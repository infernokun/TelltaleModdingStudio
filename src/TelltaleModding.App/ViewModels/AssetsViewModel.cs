using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Audio;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;
using TelltaleModding.Core.Scripts;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// Every file in the open game: search, filter by kind, preview, export, or mark a
/// file for replacement in the mod project.
/// </summary>
public sealed partial class AssetsViewModel : ViewModelBase
{
    private const int MaxResults = 2000;

    private GameCatalog? _catalog;
    private ModelAssembler? _assembler;
    private GameAudio? _audio;
    private CancellationTokenSource? _searchCts;
    private int _previewGeneration;

    public AssetsViewModel()
    {
        Categories = new[] { new CategoryOption("All files", null) }
            .Concat(new[]
            {
                ("Models", AssetCategory.Model), ("Textures", AssetCategory.Texture),
                ("Animations", AssetCategory.Animation), ("Skeletons", AssetCategory.Skeleton),
                ("Scenes & props", AssetCategory.Scene), ("Performances", AssetCategory.Performance),
                ("Audio", AssetCategory.Audio), ("Scripts", AssetCategory.Script), ("Other", AssetCategory.Other),
            }.Select(c => new CategoryOption(c.Item1, c.Item2)))
            .ToList();
        _selectedCategory = Categories[0];
    }

    /// <summary>Set by the shell so pickers can be shown.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Raised to preview a model in this workspace's viewport.</summary>
    public event Action<AssembledModel>? MeshPreviewReady;

    /// <summary>Raised to read a script in the Scripts workspace.</summary>
    public event Action<string>? ScriptRequested;

    /// <summary>Raised when the user wants to replace a file in their mod.</summary>
    public event Action<string>? SwapRequested;

    /// <summary>Raised with a short status message for the status bar.</summary>
    public event Action<string>? StatusMessage;

    [ObservableProperty]
    private bool _hasSource;

    public void Load(GameCatalog catalog, ModelAssembler? models = null)
    {
        _catalog = catalog;
        _assembler = models ?? new ModelAssembler(new CatalogAssetResolver(catalog));
        _audio = new GameAudio(catalog);
        HasSource = true;
        Query = "";
        SelectedCategory = Categories[0];
        ClearPreview();
        _ = SearchAsync();
    }

    public void Clear()
    {
        _catalog = null;
        _assembler = null;
        _audio = null;
        HasSource = false;
        Results.Clear();
        ClearPreview();
    }

    // ---- Search -----------------------------------------------------------

    public IReadOnlyList<CategoryOption> Categories { get; }

    [ObservableProperty]
    private CategoryOption _selectedCategory;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _resultSummary = "";

    public ObservableCollection<AssetRowViewModel> Results { get; } = new();

    partial void OnQueryChanged(string value) => RestartSearchDebounce();

    partial void OnSelectedCategoryChanged(CategoryOption value) => RestartSearchDebounce();

    // Typing quickly collapses into one search.
    private async void RestartSearchDebounce()
    {
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(200, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SearchAsync();
    }

    private CancellationTokenSource? _debounceCts;

    private async Task SearchAsync()
    {
        if (_catalog is null)
            return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var category = SelectedCategory.Category;
        var entries = _catalog.Entries;

        List<ArchiveEntryRef> hits;
        int total;
        try
        {
            (hits, total) = await Task.Run(() =>
            {
                var found = new List<ArchiveEntryRef>();
                int count = 0;
                foreach (var entry in entries)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (category is { } c && AssetClassifier.Classify(entry.Name) != c)
                        continue;
                    if (!terms.All(t => entry.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                        Path.GetFileName(entry.ArchivePath).Contains(t, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (++count <= MaxResults)
                        found.Add(entry);
                }
                return (found, count);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts != _searchCts)
            return;

        Results.Clear();
        foreach (var hit in hits)
            Results.Add(new AssetRowViewModel(hit));
        ResultSummary = total == 0
            ? "No files match."
            : total > MaxResults
                ? $"Showing {MaxResults:N0} of {total:N0} matches. Refine the search."
                : $"{total:N0} file{(total == 1 ? "" : "s")}";
        ExportResultsCommand.NotifyCanExecuteChanged();
    }

    // ---- Preview ----------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportAsDdsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportAsPngCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReplaceInModCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenScriptCommand))]
    private AssetRowViewModel? _selected;

    /// <summary>Plays the selected sound file.</summary>
    public AudioPlayerViewModel Player { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportAsWavCommand))]
    private bool _isAudioPreview;

    [ObservableProperty]
    private bool _isScriptPreview;

    [ObservableProperty]
    private bool _isChorePreview;

    /// <summary>The selected chore's timeline, one entry per agent.</summary>
    public ObservableCollection<ChoreTrackViewModel> ChoreTracks { get; } = new();

    /// <summary>The sounds in the selected file (several for a bank).</summary>
    public ObservableCollection<AudioClip> PreviewClips { get; } = new();

    [ObservableProperty]
    private AudioClip? _selectedClip;

    public bool HasSeveralClips => PreviewClips.Count > 1;

    partial void OnSelectedClipChanged(AudioClip? value)
    {
        if (value is not null && value != Player.Clip)
            _ = Player.LoadAsync(() => value, autoPlay: Player.IsPlaying);
    }

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private bool _isMeshPreview;

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private string _previewTitle = "";

    [ObservableProperty]
    private string _previewInfo = "";

    public ObservableCollection<string> PreviewDetails { get; } = new();

    partial void OnSelectedChanged(AssetRowViewModel? value)
    {
        if (value is not null)
            _ = PreviewAsync(value.Entry);
    }

    private void ClearPreview()
    {
        PreviewImage = null;
        IsMeshPreview = false;
        IsAudioPreview = false;
        IsScriptPreview = false;
        IsChorePreview = false;
        ChoreTracks.Clear();
        Player.Unload();
        PreviewClips.Clear();
        SelectedClip = null;
        OnPropertyChanged(nameof(HasSeveralClips));
        HasPreview = false;
        PreviewTitle = PreviewInfo = "";
        PreviewDetails.Clear();
    }

    private async Task PreviewAsync(ArchiveEntryRef entry)
    {
        if (_catalog is null)
            return;
        int generation = ++_previewGeneration;
        var catalog = _catalog;
        var assembler = _assembler!;
        PreviewTitle = entry.Name;
        PreviewInfo = "Loading…";
        PreviewDetails.Clear();
        HasPreview = true;
        IsChorePreview = false;
        ChoreTracks.Clear();

        var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        IsScriptPreview = LuaScriptReader.IsScriptFile(entry.Name);
        if (TelltaleAudio.IsAudioFile(entry.Name) && _audio is { } audio)
        {
            await PreviewAudioAsync(audio, entry, generation);
            return;
        }
        IsAudioPreview = false;
        Player.Unload();
        PreviewClips.Clear();
        var result = await Task.Run(() => BuildPreview(catalog, assembler, entry, ext));
        if (generation != _previewGeneration)
            return;

        PreviewImage = result.Image is null ? null : DdsPreview.ToBitmap(result.Image);
        IsMeshPreview = result.Model is not null;
        PreviewInfo = result.Info;
        foreach (var line in result.Details)
            PreviewDetails.Add(line);
        if (result.Chore is { } tracks)
        {
            foreach (var track in tracks)
                ChoreTracks.Add(track);
            IsChorePreview = true;
        }
        if (result.Model is not null)
            MeshPreviewReady?.Invoke(result.Model);
    }

    private async Task PreviewAudioAsync(GameAudio audio, ArchiveEntryRef entry, int generation)
    {
        PreviewImage = null;
        IsMeshPreview = false;
        IsAudioPreview = true;
        PreviewClips.Clear();
        try
        {
            var clips = await Task.Run(() => audio.LoadClips(entry));
            if (generation != _previewGeneration)
                return;
            foreach (var clip in clips)
                PreviewClips.Add(clip);
            OnPropertyChanged(nameof(HasSeveralClips));
            var first = clips[0];
            PreviewInfo = (clips.Count > 1 ? $"{clips.Count:N0} sounds · " : "") +
                          $"{first.Codec} · {first.Duration.TotalSeconds:0.0} s · {FormatSize(entry.Size)}";
            SelectedClip = first;
            await Player.LoadAsync(() => first, autoPlay: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            if (generation != _previewGeneration)
                return;
            IsAudioPreview = false;
            Player.Unload();
            PreviewInfo = $"{FormatSize(entry.Size)} · {ex.Message}";
        }
    }

    private sealed record PreviewResult(string Info, DdsImage? Image, AssembledModel? Model, IReadOnlyList<string> Details,
        IReadOnlyList<ChoreTrackViewModel>? Chore = null);

    private static PreviewResult BuildPreview(GameCatalog catalog, ModelAssembler assembler, ArchiveEntryRef entry, string ext)
    {
        try
        {
            string size = FormatSize(entry.Size);
            switch (ext)
            {
                case ".d3dtx":
                {
                    var bytes = catalog.Read(entry);
                    if (!D3dtxTexture.TryLoad(bytes, out var tex, out var error))
                        return new($"{size} · {error}", null, null, Array.Empty<string>());
                    DdsImage? image = tex!.TryToDds(out var dds, out var ddsError) &&
                                      DdsImage.TryDecode(dds!, out var decoded, out ddsError) ? decoded : null;
                    return new($"{tex.Describe()} · {size}" + (image is null ? $" · no preview: {ddsError}" : ""),
                        image, null, Array.Empty<string>());
                }
                case ".d3dmesh":
                {
                    AssembledModel model;
                    lock (assembler)
                        model = assembler.Build(new[] { entry.Name });
                    return new($"{model.Geometry.Positions.Length:N0} verts · {model.TriangleCount:N0} tris · {size}",
                        null, model, model.Details);
                }
                case ".skl":
                {
                    var bytes = catalog.Read(entry);
                    return SkeletonFile.TryLoad(bytes, out var skl, out var error)
                        ? new($"{skl!.Bones.Count} bones · {size}", null, null, Array.Empty<string>())
                        : new($"{size} · {error}", null, null, Array.Empty<string>());
                }
                case ".anm":
                {
                    var bytes = catalog.Read(entry);
                    return AnimationFile.TryLoad(bytes, out var anim, out var error)
                        ? new($"{anim!.Describe()} · {size}", null, null, Array.Empty<string>())
                        : new($"{size} · {error}", null, null, Array.Empty<string>());
                }
                case ".chore":
                {
                    if (!ChoreFile.TryRead(catalog.Read(entry), out var chore, out var error))
                        return new($"{size} · {error}", null, null, Array.Empty<string>());
                    var preview = ChorePreview.Build(catalog, entry.Name, chore!);
                    return new($"{preview.Info} · {size}", null, null, preview.Details, preview.Tracks);
                }
                case ".lua" or ".lenc" or ".luac":
                {
                    var script = LuaScriptReader.Read(catalog.Read(entry), entry.Name);
                    var info = script.Kind is LuaScriptKind.Source or LuaScriptKind.Unknown
                        ? $"{script.VersionLabel} · {size}"
                        : $"{script.VersionLabel} · {script.FunctionCount:N0} functions · {size}";
                    return new(info, null, null, script.FunctionNames.Take(40).ToList());
                }
                default:
                {
                    var bytes = catalog.Read(entry);
                    var info = MetaStreamHeader.TryParse(bytes, out var header, out _)
                        ? $"{header!.Describe()} · {size}"
                        : size;
                    return new(info, null, null, Array.Empty<string>());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return new($"Could not read: {ex.Message}", null, null, Array.Empty<string>());
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    // ---- Export -----------------------------------------------------------

    private bool HasSelection() => Selected is not null && _catalog is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ExportSelectedAsync()
    {
        if (Dialogs is null || Selected is null || _catalog is null)
            return;
        var name = Path.GetFileName(Selected.Entry.Name);
        var path = await Dialogs.SaveFileAsync("Export file", name, ("All files", new[] { "*" }));
        if (path is null)
            return;
        var entry = Selected.Entry;
        var catalog = _catalog;
        await Task.Run(() => File.WriteAllBytes(path, catalog.Read(entry)));
        StatusMessage?.Invoke($"Exported {name}");
    }

    private bool IsTextureSelected() =>
        HasSelection() && Selected!.Entry.Name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(IsTextureSelected))]
    private async Task ExportAsDdsAsync()
    {
        if (Dialogs is null || Selected is null || _catalog is null)
            return;
        var name = Path.GetFileNameWithoutExtension(Selected.Entry.Name) + ".dds";
        var path = await Dialogs.SaveFileAsync("Export texture as DDS", name, ("DDS texture", new[] { "*.dds" }));
        if (path is null)
            return;
        var entry = Selected.Entry;
        var catalog = _catalog;
        var error = await Task.Run(() =>
        {
            if (!D3dtxTexture.TryLoad(catalog.Read(entry), out var tex, out var loadError))
                return loadError;
            if (!tex!.TryToDds(out var dds, out var ddsError))
                return ddsError;
            File.WriteAllBytes(path, dds!);
            return null;
        });
        StatusMessage?.Invoke(error is null ? $"Exported {name}" : $"Could not export: {error}");
    }

    [RelayCommand(CanExecute = nameof(IsTextureSelected))]
    private async Task ExportAsPngAsync()
    {
        if (Dialogs is null || Selected is null || _catalog is null)
            return;
        var name = Path.GetFileNameWithoutExtension(Selected.Entry.Name) + ".png";
        var path = await Dialogs.SaveFileAsync("Export texture as PNG", name, ("PNG image", new[] { "*.png" }));
        if (path is null)
            return;
        var entry = Selected.Entry;
        var catalog = _catalog;
        try
        {
            await Task.Run(() => File.WriteAllBytes(path, TextureToPng(catalog.Read(entry))));
            StatusMessage?.Invoke($"Exported {name}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
        {
            StatusMessage?.Invoke($"Could not export: {ex.Message}");
        }
    }

    private static byte[] TextureToPng(byte[] d3dtx)
    {
        if (!D3dtxTexture.TryLoad(d3dtx, out var tex, out var error) ||
            !tex!.TryToDds(out var dds, out error) || !DdsImage.TryDecode(dds!, out var image, out error))
            throw new InvalidDataException(error);
        return PngWriter.Encode(image!);
    }

    /// <summary>
    /// Bulk export converts to everyday formats: textures to PNG, sounds to MP3/Ogg
    /// (bank sounds into a folder per bank), scripts to decompiled Lua, subtitle
    /// databases to a tab-separated transcript, chores to a text timeline. Off: files
    /// are written as stored.
    /// </summary>
    [ObservableProperty]
    private bool _convertOnExport = true;

    // Writes one entry into folder, converted when possible; returns false if it fell back to the raw file.
    private static bool ExportConverted(GameCatalog catalog, GameAudio audio, ChoreNames choreNames, ArchiveEntryRef entry, string folder)
    {
        var name = Path.GetFileName(entry.Name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var data = catalog.Read(entry);
        try
        {
            switch (AssetClassifier.Classify(name))
            {
                case AssetCategory.Texture when name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase):
                    File.WriteAllBytes(Path.Combine(folder, stem + ".png"), TextureToPng(data));
                    return true;
                case AssetCategory.Audio when TelltaleAudio.IsAudioFile(name):
                {
                    if (!TelltaleAudio.TryLoad(data, name, audio.Setups, out var clips, out var error) || clips.Count == 0)
                        throw new InvalidDataException(error);
                    if (clips.Count == 1)
                    {
                        File.WriteAllBytes(Path.Combine(folder, stem + clips[0].NativeExtension), clips[0].NativeFile);
                        return true;
                    }
                    var bankFolder = Directory.CreateDirectory(Path.Combine(folder, stem)).FullName;
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var clip in clips)
                    {
                        var clipName = SafeFileName(clip.Name);
                        for (int n = 2; !used.Add(clipName); n++)
                            clipName = $"{SafeFileName(clip.Name)} ({n})";
                        File.WriteAllBytes(Path.Combine(bankFolder, clipName + clip.NativeExtension), clip.NativeFile);
                    }
                    return true;
                }
                case AssetCategory.Script:
                    File.WriteAllText(Path.Combine(folder, stem + ".lua"), LuaScriptReader.Read(data, name).Decompiled);
                    return true;
                case AssetCategory.Other when name.EndsWith(".landb", StringComparison.OrdinalIgnoreCase):
                {
                    if (!LandbFile.TryRead(data, out var lines, out var error))
                        throw new InvalidDataException(error);
                    var text = new System.Text.StringBuilder("id\tspeaker\ttext\n");
                    foreach (var line in lines)
                        text.Append(line.Id).Append('\t').Append(line.Speaker).Append('\t')
                            .Append(line.RawText.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
                    File.WriteAllText(Path.Combine(folder, stem + ".tsv"), text.ToString());
                    return true;
                }
                case AssetCategory.Performance when name.EndsWith(".chore", StringComparison.OrdinalIgnoreCase):
                {
                    if (!ChoreFile.TryRead(data, out var chore, out var error))
                        throw new InvalidDataException(error);
                    File.WriteAllText(Path.Combine(folder, stem + ".txt"), choreNames.Text(name, chore!));
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
        }
        File.WriteAllBytes(Path.Combine(folder, name), data);
        return false;
    }

    private bool HasResults() => Results.Count > 0 && _catalog is not null;

    [RelayCommand(CanExecute = nameof(HasResults))]
    private async Task ExportResultsAsync()
    {
        if (Dialogs is null || _catalog is null)
            return;
        var folder = await Dialogs.OpenFolderAsync($"Export {Results.Count:N0} files to…");
        if (folder is null)
            return;
        var entries = Results.Select(r => r.Entry).ToList();
        var catalog = _catalog;
        var audio = _audio!;
        bool convert = ConvertOnExport;
        // Shared across the export so each subtitle database is read once.
        var choreNames = new ChoreNames(catalog);
        var progress = new Progress<int>(n => StatusMessage?.Invoke($"Exporting… {n:N0}/{entries.Count:N0}"));
        int failed = await Task.Run(() =>
        {
            int done = 0, errors = 0;
            foreach (var entry in entries)
            {
                try
                {
                    if (convert)
                        ExportConverted(catalog, audio, choreNames, entry, folder);
                    else
                        File.WriteAllBytes(Path.Combine(folder, Path.GetFileName(entry.Name)), catalog.Read(entry));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    errors++;
                }
                if (++done % 50 == 0)
                    ((IProgress<int>)progress).Report(done);
            }
            return errors;
        });
        StatusMessage?.Invoke($"Exported {entries.Count - failed:N0} files to {folder}" +
                              (failed > 0 ? $" ({failed} failed)" : ""));
    }

    [RelayCommand(CanExecute = nameof(IsAudioPreview))]
    private async Task ExportAsWavAsync()
    {
        if (Dialogs is null || SelectedClip is not { } clip)
            return;
        var name = SafeFileName(PreviewClips.Count > 1 ? clip.Name : Path.GetFileNameWithoutExtension(Selected?.Entry.Name ?? clip.Name)) + ".wav";
        var path = await Dialogs.SaveFileAsync("Export sound as WAV", name, ("WAV audio", new[] { "*.wav" }));
        if (path is null)
            return;
        try
        {
            await Task.Run(() => File.WriteAllBytes(path, clip.ToWav()));
            StatusMessage?.Invoke($"Exported {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusMessage?.Invoke($"Could not export: {ex.Message}");
        }
    }

    // Bank sound names can carry an event GUID ("door_open {af4ed549-…}").
    private static string SafeFileName(string name)
    {
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\{[0-9a-fA-F-]+\}", "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "sound" : name;
    }

    private bool IsScriptSelected() => HasSelection() && LuaScriptReader.IsScriptFile(Selected!.Entry.Name);

    [RelayCommand(CanExecute = nameof(IsScriptSelected))]
    private void OpenScript()
    {
        if (Selected is not null)
            ScriptRequested?.Invoke(Selected.Entry.Name);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ReplaceInMod()
    {
        if (Selected is not null)
            SwapRequested?.Invoke(Path.GetFileName(Selected.Entry.Name));
    }
}

public sealed record CategoryOption(string Label, AssetCategory? Category);

/// <summary>One file in the asset search results.</summary>
public sealed class AssetRowViewModel
{
    public AssetRowViewModel(ArchiveEntryRef entry)
    {
        Entry = entry;
        Kind = AssetClassifier.Classify(entry.Name).ToString();
    }

    public ArchiveEntryRef Entry { get; }
    public string Name => Entry.Name;
    public string Kind { get; }
    public string SizeText => AssetsViewModel.FormatSize(Entry.Size);
    public string ArchiveName => Path.GetFileName(Entry.ArchivePath);
}
