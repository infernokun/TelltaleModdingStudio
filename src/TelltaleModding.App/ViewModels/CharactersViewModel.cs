using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The character viewer: browse a game's characters, dress them from their parts,
/// and play their animations.
/// </summary>
public sealed partial class CharactersViewModel : ViewModelBase
{
    private GameCatalog? _catalog;
    private IAssetResolver? _assets;
    private ModelAssembler? _assembler;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly List<CharacterItemViewModel> _allCharacters = new();
    private List<AnimationItemViewModel> _allAnimations = new();
    private string? _preferredSkeleton;
    private int _buildGeneration, _clipGeneration;

    private AssembledModel? _model;
    private AnimationPlayer? _player;
    private (AnimationFile Clip, string Name)? _clip;

    /// <summary>How the viewport shades (shared by the workspaces; set by the shell).</summary>
    public ViewportLookViewModel Look { get; set; } = new();

    private readonly DispatcherTimer _playTimer;
    private readonly Stopwatch _clock = new();
    private bool _timeFromTimer;

    public CharactersViewModel()
    {
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playTimer.Tick += (_, _) => AdvancePlayback();
        InitFace();
    }

    /// <summary>
    /// Raised on the UI thread when a model is ready to render; the flag says whether
    /// the camera should frame it afresh (new character) or stay put (outfit change).
    /// </summary>
    public event Action<AssembledModel, bool>? ModelReady;

    private bool _frameNextModel = true;

    /// <summary>Raised when the viewport should show nothing.</summary>
    public event Action? ModelCleared;

    /// <summary>
    /// Raised during playback/scrubbing with the frame's skinning matrices (the viewport
    /// skins on the GPU); null returns the model to its rest pose.
    /// </summary>
    public event Action<Matrix4x4[]?>? PoseReady;

    /// <summary>Raised when the camera should frame the model again.</summary>
    public event Action? ResetViewRequested;

    // ---- Sources --------------------------------------------------------

    [ObservableProperty]
    private bool _hasSource;

    [ObservableProperty]
    private string _sourceName = "";

    /// <summary>Shows a game's characters.</summary>
    public void Load(GameCatalog catalog)
    {
        Reset();
        _catalog = catalog;
        _assets = new CatalogAssetResolver(catalog);
        _assembler = new ModelAssembler(_assets);
        UseFaceSource(catalog, _assets, catalog.Entries.Select(e => e.Name));
        SourceName = catalog.Install.DisplayName;
        HasSource = true;
        foreach (var entry in catalog.Characters)
            _allCharacters.Add(new CharacterItemViewModel(entry));
        ApplyCharacterFilter();
        CharacterSummary = $"{_allCharacters.Count:N0} characters";
    }

    /// <summary>
    /// Shows loose extracted files: the given meshes as one model, with every
    /// animation in their folder (used by the <c>--preview-mesh</c> debug flag).
    /// </summary>
    public void LoadLoose(IReadOnlyList<string> meshPaths, string? playAnimationPath = null)
    {
        Reset();
        var folder = Path.GetDirectoryName(meshPaths[0])!;
        var resolver = new FolderAssetResolver(folder);
        _assets = resolver;
        _assembler = new ModelAssembler(resolver);
        UseFaceSource(null, resolver, resolver.FileNames);
        SourceName = folder;
        HasSource = true;

        var slot = new OutfitSlotViewModel("Parts");
        foreach (var path in meshPaths)
        {
            var name = Path.GetFileName(path);
            slot.Parts.Add(new OutfitPartViewModel(name, Path.GetFileNameWithoutExtension(name), true, ScheduleRebuild));
        }
        Outfit.Add(slot);
        Title = Path.GetFileNameWithoutExtension(meshPaths[0]);
        Face.SetCharacter(Title, Title);
        Subtitle = $"{meshPaths.Count} loose mesh{(meshPaths.Count == 1 ? "" : "es")}";

        _allAnimations = resolver.FileNames
            .Where(n => n.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new AnimationItemViewModel(Path.GetFileNameWithoutExtension(n), n, true,
                () => resolver.TryRead(n) ?? Array.Empty<byte>()))
            .ToList();
        ApplyAnimationFilter();

        _ = RebuildAndPlayAsync(playAnimationPath);
    }

    private async Task RebuildAndPlayAsync(string? animationPath)
    {
        await RebuildModelAsync();
        if (animationPath is null)
            return;
        var name = Path.GetFileName(animationPath);
        SelectedAnimation = _allAnimations.FirstOrDefault(a => a.FileName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? new AnimationItemViewModel(Path.GetFileNameWithoutExtension(name), name, true, () => File.ReadAllBytes(animationPath));
    }

    /// <summary>Stops playback and forgets the source, its characters and the model.</summary>
    public void Clear() => Reset();

    private void Reset()
    {
        StopPlayback(renderRest: false);
        _catalog = null;
        _assets = null;
        _assembler = null;
        _model = null;
        _player = null;
        _clip = null;
        Face.Clear();
        _allCharacters.Clear();
        _allAnimations.Clear();
        Characters.Clear();
        Outfit.Clear();
        Animations.Clear();
        Details.Clear();
        SelectedCharacter = null;
        HasModel = false;
        HasSource = false;
        Error = null;
        Title = Subtitle = Stats = ClipName = ClipInfo = "";
        Duration = 0;
        _frameNextModel = true;
        ModelCleared?.Invoke();
    }

    // ---- Character list -------------------------------------------------

    public ObservableCollection<CharacterItemViewModel> Characters { get; } = new();

    [ObservableProperty]
    private string _characterFilter = "";

    [ObservableProperty]
    private string _characterSummary = "";

    partial void OnCharacterFilterChanged(string value) => ApplyCharacterFilter();

    private void ApplyCharacterFilter()
    {
        var terms = CharacterFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Characters.Clear();
        foreach (var item in _allCharacters)
        {
            if (terms.All(t => item.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                Characters.Add(item);
        }
    }

    [ObservableProperty]
    private CharacterItemViewModel? _selectedCharacter;

    partial void OnSelectedCharacterChanged(CharacterItemViewModel? value)
    {
        if (value is null || _catalog is null)
            return;
        StopPlayback(renderRest: false);
        _clip = null;
        ClipName = ClipInfo = "";
        Duration = 0;
        var entry = value.Entry;
        Title = entry.DisplayName;
        Subtitle = entry.Variant is null ? entry.Id : $"{entry.Variant} · {entry.Id}";
        _preferredSkeleton = entry.Skeleton?.Name;
        Face.SetCharacter(entry.Id, entry.DisplayName);

        // Outfit: parts grouped by slot, in a stable body order.
        Outfit.Clear();
        bool anyDefault = entry.Parts.Any(p => p.DefaultOn);
        var seenSlots = new HashSet<string>();
        foreach (var group in entry.Parts.GroupBy(p => p.Slot).OrderBy(g => SlotOrder(g.Key)))
        {
            var slot = new OutfitSlotViewModel(group.Key);
            foreach (var part in group.OrderBy(p => p.PartName, StringComparer.OrdinalIgnoreCase))
            {
                // Without default data from the game, wear the first part of each slot.
                bool on = anyDefault ? part.DefaultOn : seenSlots.Add(group.Key);
                slot.Parts.Add(new OutfitPartViewModel(part.Mesh.Name, part.PartName, on, ScheduleRebuild));
            }
            Outfit.Add(slot);
        }

        _allAnimations = _catalog.AnimationsFor(entry)
            .Select(a => new AnimationItemViewModel(a.DisplayName, a.File.Name, a.NamedForCharacter,
                () => _catalog!.ReadAnimation(a.File)))
            .ToList();
        AnimationFilter = "";
        ApplyAnimationFilter();
        _frameNextModel = true;
        _ = RebuildModelAsync();
    }

    /// <summary>
    /// The meshes a character wears by default: the game's own defaults, or else the
    /// first part of each slot (the same choice the Outfit tab starts from).
    /// </summary>
    public static IEnumerable<string> DefaultOutfitMeshes(CharacterEntry entry)
    {
        bool anyDefault = entry.Parts.Any(p => p.DefaultOn);
        foreach (var group in entry.Parts.GroupBy(p => p.Slot).OrderBy(g => SlotOrder(g.Key)))
        {
            var parts = group.OrderBy(p => p.PartName, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var part in anyDefault ? parts.Where(p => p.DefaultOn) : parts.Take(1))
                yield return part.Mesh.Name;
        }
    }

    private static int SlotOrder(string slot) => slot switch
    {
        "Head" => 0, "Face" => 1, "Hair" => 2, "Upper body" => 3, "Lower body" => 4,
        "Hands" => 5, "Feet" => 6, "Accessory" => 7, _ => 8,
    };

    // ---- Outfit -----------------------------------------------------------

    public ObservableCollection<OutfitSlotViewModel> Outfit { get; } = new();

    // Several quick toggles collapse into one rebuild.
    private async void ScheduleRebuild()
    {
        _rebuildCts?.Cancel();
        var cts = _rebuildCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(180, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RebuildModelAsync();
    }

    private CancellationTokenSource? _rebuildCts;

    [RelayCommand]
    private void DefaultOutfit()
    {
        if (SelectedCharacter is { } current)
            OnSelectedCharacterChanged(current);
    }

    // ---- Model ------------------------------------------------------------

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _stats = "";

    [ObservableProperty]
    private bool _hasModel;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = "";

    [ObservableProperty]
    private string? _error;

    /// <summary>One line per mesh/batch: textures matched, hidden layers, skeleton.</summary>
    public ObservableCollection<string> Details { get; } = new();

    private async Task RebuildModelAsync()
    {
        if (_assembler is null)
            return;
        var meshes = Outfit.SelectMany(s => s.Parts).Where(p => p.IsOn).Select(p => p.MeshName).ToList();
        int generation = ++_buildGeneration;
        if (meshes.Count == 0)
        {
            _model = null;
            _player = null;
            _ = Face.SetModelAsync(null);
            HasModel = false;
            Error = "Turn on at least one part in the Outfit tab.";
            ModelCleared?.Invoke();
            return;
        }

        IsLoading = true;
        LoadingText = $"Loading {Title}…";
        Error = null;
        AssembledModel? model = null;
        string? error = null;
        var assembler = _assembler;
        await _buildLock.WaitAsync();
        try
        {
            model = await Task.Run(() => assembler.Build(meshes, _preferredSkeleton));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            error = ex.Message;
        }
        finally
        {
            _buildLock.Release();
        }
        if (generation != _buildGeneration)
            return; // a newer outfit/character was requested meanwhile
        IsLoading = false;

        if (model is null)
        {
            _model = null;
            _player = null;
            _ = Face.SetModelAsync(null);
            HasModel = false;
            Error = error;
            ModelCleared?.Invoke();
            return;
        }

        _model = model;
        HasModel = true;
        Stats = $"{model.Geometry.Positions.Length:N0} verts · {model.TriangleCount:N0} tris · " +
                $"{model.TexturesUsed.Count} texture{(model.TexturesUsed.Count == 1 ? "" : "s")}" +
                (model.Skeleton is null ? "" : $" · {model.Skeleton.Bones.Count} bones");
        Details.Clear();
        foreach (var line in model.Details)
            Details.Add(line);
        if (model.Failures.Count > 0)
            Error = $"{model.Failures.Count} part{(model.Failures.Count == 1 ? "" : "s")} could not be loaded. See Details.";

        _player = model.CanAnimate ? new AnimationPlayer(model.Geometry, model.Skeleton!) : null;
        _ = Face.SetModelAsync(model);
        ModelReady?.Invoke(model, _frameNextModel);
        _frameNextModel = false;

        // Keep the current clip (and time) across outfit changes.
        if (_player is not null && _clip is { } clip)
        {
            _player.Load(clip.Clip, clip.Name);
            RenderFrame((float)CurrentTime);
        }
        else if (_player is null)
        {
            StopPlayback(renderRest: false);
        }
        PlayPauseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ResetView() => ResetViewRequested?.Invoke();

    /// <summary>Raised when the camera should frame the character's face.</summary>
    public event Action? FaceViewRequested;

    [RelayCommand]
    private void FaceView() => FaceViewRequested?.Invoke();

    /// <summary>Raised with a character's name to list what they say.</summary>
    public event Action<string>? LinesRequested;

    [RelayCommand]
    private void ShowLines()
    {
        if (SelectedCharacter is { } character)
            LinesRequested?.Invoke(character.Name);
    }

    // ---- Animations -------------------------------------------------------

    public ObservableCollection<AnimationItemViewModel> Animations { get; } = new();

    [ObservableProperty]
    private string _animationFilter = "";

    [ObservableProperty]
    private bool _onlyCharacterClips = true;

    [ObservableProperty]
    private string _animationSummary = "";

    partial void OnAnimationFilterChanged(string value) => ApplyAnimationFilter();

    partial void OnOnlyCharacterClipsChanged(bool value) => ApplyAnimationFilter();

    private void ApplyAnimationFilter()
    {
        var terms = AnimationFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Animations.Clear();
        int ownClips = _allAnimations.Count(a => a.IsCharacterClip);
        foreach (var item in _allAnimations)
        {
            if (OnlyCharacterClips && ownClips > 0 && !item.IsCharacterClip)
                continue;
            if (terms.All(t => item.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                Animations.Add(item);
        }
        AnimationSummary = _allAnimations.Count == 0
            ? "No animations found for this character."
            : $"{Animations.Count:N0} of {_allAnimations.Count:N0} clips" +
              (ownClips > 0 && ownClips < _allAnimations.Count ? $" · {ownClips:N0} named for {Title}" : "");
    }

    [ObservableProperty]
    private AnimationItemViewModel? _selectedAnimation;

    [ObservableProperty]
    private string _clipName = "";

    [ObservableProperty]
    private string _clipInfo = "";

    partial void OnSelectedAnimationChanged(AnimationItemViewModel? value)
    {
        if (value is not null)
            _ = LoadClipAsync(value);
    }

    private async Task LoadClipAsync(AnimationItemViewModel item)
    {
        int generation = ++_clipGeneration;
        ClipName = item.Name;
        if (_player is null)
        {
            ClipInfo = _model is null ? "Load a model first." : "This model has no skeleton to animate.";
            return;
        }

        ClipInfo = "Loading…";
        AnimationFile? clip = null;
        string? error = null;
        await Task.Run(() =>
        {
            try
            {
                if (!AnimationFile.TryLoad(item.Load(), out clip, out error))
                    clip = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                error = ex.Message;
            }
        });
        if (generation != _clipGeneration || _player is null)
            return;
        if (clip is null)
        {
            ClipInfo = $"Can't read this clip: {error}";
            return;
        }

        _player.Load(clip, item.FileName);
        if (!_player.HasMotion)
        {
            ClipInfo = "This clip doesn't move any of this skeleton's bones.";
            _clip = null;
            StopPlayback(renderRest: true);
            return;
        }
        _clip = (clip, item.FileName);
        ClipInfo = _player.ClipDescription;
        Duration = _player.Length;
        SetTime(0);
        RenderFrame(0);
        Play();
    }

    // ---- Playback ---------------------------------------------------------

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _currentTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(HasClip))]
    private double _duration;

    [ObservableProperty]
    private double _speed = 1.0;

    [ObservableProperty]
    private bool _loop = true;

    public IReadOnlyList<double> Speeds { get; } = new[] { 0.25, 0.5, 1.0, 1.5, 2.0 };

    public bool HasClip => Duration > 0;

    public string TimeText => $"{CurrentTime:0.00} / {Duration:0.00}s";

    private bool CanPlayPause() => _player is not null && _clip is not null;

    [RelayCommand(CanExecute = nameof(CanPlayPause))]
    private void PlayPause()
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    [RelayCommand]
    private void Stop() => StopPlayback(renderRest: true);

    private void Play()
    {
        if (_player is null || _clip is null)
            return;
        if (CurrentTime >= Duration - 1e-3)
            SetTime(0);
        IsPlaying = true;
        _clock.Restart();
        _playTimer.Start();
        PlayPauseCommand.NotifyCanExecuteChanged();
    }

    private void Pause()
    {
        IsPlaying = false;
        _playTimer.Stop();
    }

    private void StopPlayback(bool renderRest)
    {
        Pause();
        SetTime(0);
        if (renderRest && _model is not null)
        {
            if (Face.IsActive)
                RenderFrame(0); // the face stays posed
            else
                PoseReady?.Invoke(null);
        }
    }

    private void AdvancePlayback()
    {
        if (_player is null || _clip is null || Duration <= 0)
        {
            Pause();
            return;
        }
        double elapsed = _clock.Elapsed.TotalSeconds * Speed;
        _clock.Restart();
        double t = CurrentTime + elapsed;
        if (t >= Duration)
        {
            if (Loop)
            {
                t %= Duration;
            }
            else
            {
                t = Duration;
                Pause();
            }
        }
        SetTime(t);
        RenderFrame((float)t);
    }

    private void SetTime(double t)
    {
        _timeFromTimer = true;
        CurrentTime = t;
        _timeFromTimer = false;
    }

    // Scrubbing the timeline poses the model at the dragged time.
    partial void OnCurrentTimeChanged(double value)
    {
        if (!_timeFromTimer && _clip is not null)
            RenderFrame((float)value);
    }

    private void RenderFrame(float time)
    {
        if (_player is null)
            return;
        PoseReady?.Invoke(FaceSkinMatrices(time) ?? _player.SkinMatrices(time));
    }
}

/// <summary>One row of the character list.</summary>
public sealed class CharacterItemViewModel
{
    public CharacterItemViewModel(CharacterEntry entry)
    {
        Entry = entry;
        SearchText = $"{entry.DisplayName} {entry.Variant} {entry.Id}";
        var words = entry.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }

    public CharacterEntry Entry { get; }
    public string Name => Entry.DisplayName;
    public string? Variant => Entry.Variant;
    public bool HasVariant => !string.IsNullOrEmpty(Entry.Variant);
    public string Initials { get; }
    public string PartsText => $"{Entry.Parts.Count} part{(Entry.Parts.Count == 1 ? "" : "s")}";
    public string SearchText { get; }
}

/// <summary>A body slot in the outfit tab (Head, Hair, …) and its interchangeable parts.</summary>
public sealed class OutfitSlotViewModel
{
    public OutfitSlotViewModel(string slot) => Slot = slot;
    public string Slot { get; }
    public ObservableCollection<OutfitPartViewModel> Parts { get; } = new();
}

/// <summary>One wearable mesh.</summary>
public sealed partial class OutfitPartViewModel : ViewModelBase
{
    private readonly Action _changed;

    public OutfitPartViewModel(string meshName, string partName, bool isOn, Action changed)
    {
        MeshName = meshName;
        Name = Prettify(partName);
        _isOn = isOn;
        _changed = changed;
    }

    public string MeshName { get; }
    public string Name { get; }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _changed();

    /// <summary>"bodyUpperBBackpack" → "Body upper B backpack".</summary>
    internal static string Prettify(string partName)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char c in partName.Replace('_', ' '))
        {
            if (c == ' ' || (char.IsUpper(c) && current.Length > 0))
            {
                if (current.Length > 0)
                    words.Add(current.ToString());
                current.Clear();
                if (c == ' ')
                    continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        if (words.Count == 0)
            return partName;
        var text = string.Join(" ", words.Select(w => w.Length > 1 ? w.ToLowerInvariant() : w));
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

/// <summary>One clip in the animation list.</summary>
public sealed class AnimationItemViewModel
{
    public AnimationItemViewModel(string name, string fileName, bool isCharacterClip, Func<byte[]> load)
    {
        Name = name;
        FileName = fileName;
        IsCharacterClip = isCharacterClip;
        Load = load;
    }

    public string Name { get; }
    public string FileName { get; }
    public bool IsCharacterClip { get; }

    /// <summary>Reads the clip's bytes (may run on a background thread).</summary>
    public Func<byte[]> Load { get; }
}
