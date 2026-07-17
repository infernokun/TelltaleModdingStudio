using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Engine;
using TelltaleModding.Core.Games;
using TelltaleModding.Core.Projects;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// Main window logic: pick a game, browse an archive, extract assets, manage the
/// mod project's swaps, and build the minimal patch archive.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly string? _enginePath;
    private ModProject _project = new();
    private ArchiveListing? _listing;
    private CancellationTokenSource? _runCts;

    public MainWindowViewModel()
    {
        _enginePath = EngineLocator.TryLocate();
        Games = new ObservableCollection<GameEntry>(GameDatabase.Games);

        EngineStatus = _enginePath is null
            ? $"ttarchext not found — set {EngineLocator.PathEnvVar} or place it in engine/ttarchext/"
            : !EngineLocator.CanExecute(_enginePath)
                ? "Found ttarchext.exe, but it needs Windows — build the native engine (make -C engine/ttarchext) to work here"
                : $"Engine ready: {_enginePath}";
    }

    /// <summary>Set by the view so pickers can be shown; null in unit tests.</summary>
    public IDialogService? Dialogs { get; set; }

    // ---- Game selection -------------------------------------------------

    public ObservableCollection<GameEntry> Games { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameKindDisplay))]
    [NotifyCanExecuteChangedFor(nameof(ListArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildPatchCommand))]
    private GameEntry? _selectedGame;

    public string GameKindDisplay => SelectedGame is null
        ? ""
        : SelectedGame.Kind == ArchiveKind.Ttarch2 ? ".ttarch2 era" : ".ttarch era";

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        if (value is not null)
            _project.GameNum = value.GameNum;
    }

    // ---- Archive browsing -----------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ListArchiveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractAllCommand))]
    private string? _archivePath;

    [ObservableProperty]
    private string _archiveInfoDisplay = "";

    [ObservableProperty]
    private string _entryFilter = "";

    public ObservableCollection<ArchiveEntryViewModel> Entries { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSwapFromEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractSelectedCommand))]
    private ArchiveEntryViewModel? _selectedEntry;

    partial void OnEntryFilterChanged(string value) => ApplyEntryFilter();

    private void ApplyEntryFilter()
    {
        Entries.Clear();
        if (_listing is null)
            return;
        foreach (var entry in _listing.Files)
        {
            if (string.IsNullOrWhiteSpace(EntryFilter) ||
                entry.Name.Contains(EntryFilter, StringComparison.OrdinalIgnoreCase) ||
                entry.Category.ToString().Contains(EntryFilter, StringComparison.OrdinalIgnoreCase))
            {
                Entries.Add(new ArchiveEntryViewModel(entry));
            }
        }
    }

    [RelayCommand]
    private async Task OpenArchiveAsync()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.OpenFileAsync("Select Telltale archive",
            ("Telltale archives", new[] { "*.ttarch", "*.ttarch2" }),
            ("All files", new[] { "*" }));
        if (path is null) return;

        ArchivePath = path;
        try
        {
            var info = ArchiveInspector.Inspect(path);
            ArchiveInfoDisplay = info.Description;

            // Warn when the chosen game's era does not match the archive container.
            if (SelectedGame is not null && SelectedGame.Kind != info.Kind && info.Magic != ArchiveMagic.Unknown)
                AppendLog($"⚠ Archive is {info.Kind} but '{SelectedGame.Name}' uses {SelectedGame.Kind} — check the game selection.");
        }
        catch (Exception ex)
        {
            ArchiveInfoDisplay = $"Could not inspect: {ex.Message}";
        }

        if (CanRunEngine && SelectedGame is not null)
            await ListArchiveAsync();
    }

    public bool CanRunEngine => _enginePath is not null && EngineLocator.CanExecute(_enginePath);

    private bool CanListArchive() => CanRunEngine && SelectedGame is not null && ArchivePath is not null;

    [RelayCommand(CanExecute = nameof(CanListArchive))]
    private async Task ListArchiveAsync()
    {
        await RunEngineAsync(async (runner, progress, ct) =>
        {
            AppendLog($"Listing {Path.GetFileName(ArchivePath)} …");
            _listing = await runner.ListAsync(SelectedGame!.GameNum, ArchivePath!, null, progress, ct);
            ApplyEntryFilter();
            AppendLog($"{_listing.Count} files in archive.");
        });
    }

    private bool CanExtractAll() => CanListArchive();

    [RelayCommand(CanExecute = nameof(CanExtractAll))]
    private async Task ExtractAllAsync()
    {
        if (Dialogs is null) return;
        var outDir = await Dialogs.OpenFolderAsync("Select output folder");
        if (outDir is null) return;

        var options = new ExtractOptions { MetaExtract = MetaExtract };
        await RunEngineAsync(async (runner, progress, ct) =>
        {
            var result = await runner.ExtractAsync(SelectedGame!.GameNum, ArchivePath!, outDir, options, progress, ct);
            AppendLog(result.Success ? "Extraction complete." : $"Extraction failed (exit code {result.ExitCode}).");
        });
    }

    private bool CanExtractSelected() => CanListArchive() && SelectedEntry is not null;

    [RelayCommand(CanExecute = nameof(CanExtractSelected))]
    private async Task ExtractSelectedAsync()
    {
        if (Dialogs is null || SelectedEntry is null) return;
        var outDir = await Dialogs.OpenFolderAsync("Select output folder");
        if (outDir is null) return;

        // Use the entry's file name as a wildcard filter so only it is extracted.
        var options = new ExtractOptions
        {
            Filter = Path.GetFileName(SelectedEntry.Name),
            MetaExtract = MetaExtract,
        };
        await RunEngineAsync(async (runner, progress, ct) =>
        {
            var result = await runner.ExtractAsync(SelectedGame!.GameNum, ArchivePath!, outDir, options, progress, ct);
            AppendLog(result.Success
                ? $"Extracted {SelectedEntry.Name}."
                : $"Extraction failed (exit code {result.ExitCode}).");
        });
    }

    /// <summary>Extract meta contents (D3DTX→DDS, AUD→OGG). Off by default because
    /// meta-extracted files must not be rebuilt into archives.</summary>
    [ObservableProperty]
    private bool _metaExtract;

    // ---- Mod project ----------------------------------------------------

    [ObservableProperty]
    private string _projectName = "Untitled Mod";

    partial void OnProjectNameChanged(string value) => _project.Name = value;

    public ObservableCollection<SwapViewModel> Swaps { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSwapCommand))]
    private SwapViewModel? _selectedSwap;

    private bool CanAddSwapFromEntry() => SelectedEntry is not null;

    [RelayCommand(CanExecute = nameof(CanAddSwapFromEntry))]
    private async Task AddSwapFromEntryAsync()
    {
        if (Dialogs is null || SelectedEntry is null) return;
        var replacement = await Dialogs.OpenFileAsync(
            $"Choose replacement for {SelectedEntry.Name}",
            ("All files", new[] { "*" }));
        if (replacement is null) return;

        AddSwap(SelectedEntry.Name, replacement);
    }

    /// <summary>Adds a swap row (also used by drag-drop and tests).</summary>
    public void AddSwap(string targetAssetName, string replacementPath)
    {
        var swap = new AssetSwap
        {
            TargetAssetName = targetAssetName,
            ReplacementFilePath = replacementPath,
        };
        _project.Swaps.Add(swap);
        Swaps.Add(new SwapViewModel(swap));
        BuildPatchCommand.NotifyCanExecuteChanged();
        AppendLog($"Swap added: {targetAssetName} ← {Path.GetFileName(replacementPath)}");
    }

    private bool CanRemoveSwap() => SelectedSwap is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSwap))]
    private void RemoveSwap()
    {
        if (SelectedSwap is null) return;
        _project.Swaps.Remove(SelectedSwap.Swap);
        Swaps.Remove(SelectedSwap);
        BuildPatchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Save mod project",
            SuggestedProjectFileName(), ("Telltale mod project", new[] { "*.ttmod" }));
        if (path is null) return;

        _project.Name = ProjectName;
        _project.ReferenceArchivePath = ArchivePath;
        _project.Save(path);
        AppendLog($"Project saved: {path}");
    }

    [RelayCommand]
    private async Task LoadProjectAsync()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.OpenFileAsync("Open mod project",
            ("Telltale mod project", new[] { "*.ttmod" }), ("All files", new[] { "*" }));
        if (path is null) return;

        try
        {
            LoadProjectFrom(ModProject.Load(path));
            AppendLog($"Project loaded: {path}");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not load project: {ex.Message}");
        }
    }

    /// <summary>Applies a loaded project to the UI state (exposed for tests).</summary>
    public void LoadProjectFrom(ModProject project)
    {
        _project = project;
        ProjectName = project.Name;
        SelectedGame = project.Game;
        if (!string.IsNullOrEmpty(project.ReferenceArchivePath) && File.Exists(project.ReferenceArchivePath))
        {
            ArchivePath = project.ReferenceArchivePath;
            try { ArchiveInfoDisplay = ArchiveInspector.Inspect(project.ReferenceArchivePath).Description; }
            catch { /* purely informational */ }
        }

        Swaps.Clear();
        foreach (var swap in project.Swaps)
            Swaps.Add(new SwapViewModel(swap));
        BuildPatchCommand.NotifyCanExecuteChanged();
    }

    private string SuggestedProjectFileName()
    {
        var safe = string.Join("_", ProjectName.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Trim();
        return (safe.Length == 0 ? "mod" : safe) + ".ttmod";
    }

    // ---- Patch build ----------------------------------------------------

    private bool CanBuildPatch() => CanRunEngine && SelectedGame is not null && Swaps.Any(s => s.Enabled);

    [RelayCommand(CanExecute = nameof(CanBuildPatch))]
    private async Task BuildPatchAsync()
    {
        if (Dialogs is null || SelectedGame is null) return;

        var suggested = PatchBuilder.ConventionalPatchName(SelectedGame.Kind);
        var output = await Dialogs.SaveFileAsync("Save patch archive as", suggested,
            ("Telltale archive", new[] { "*.ttarch", "*.ttarch2" }));
        if (output is null) return;

        _project.Name = ProjectName;
        _project.ReferenceArchivePath = ArchivePath;

        var issues = _project.Validate();
        if (issues.Count > 0)
        {
            foreach (var issue in issues)
                AppendLog($"✗ {issue}");
            return;
        }

        await RunEngineAsync(async (runner, progress, ct) =>
        {
            var builder = new PatchBuilder(runner);
            var result = await builder.BuildAsync(_project, new PatchBuildRequest(output), progress, ct);
            AppendLog(result.Success
                ? $"✓ Patch built: {result.OutputArchivePath} ({result.StagedFileCount} file(s), version {result.Version}). " +
                  "Drop it in the game's archive folder."
                : "✗ Patch build failed — see output above. If the game crashes with the patch, retry with compatibility mode.");
        });
    }

    // ---- Preview --------------------------------------------------------

    [ObservableProperty]
    private Bitmap? _previewBitmap;

    [ObservableProperty]
    private string _previewStatus = "Select a .dds file to preview it here.";

    [RelayCommand]
    private async Task PreviewFileAsync()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.OpenFileAsync("Preview texture",
            ("DDS textures", new[] { "*.dds" }), ("All files", new[] { "*" }));
        if (path is null) return;
        ShowPreview(path);
    }

    /// <summary>Loads a DDS preview for the given file (also used when a swap row is selected).</summary>
    public void ShowPreview(string path)
    {
        var (bitmap, error) = DdsPreview.TryLoad(path);
        PreviewBitmap = bitmap;
        PreviewStatus = bitmap is not null
            ? $"{Path.GetFileName(path)} — {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}"
            : $"No preview: {error}";
    }

    partial void OnSelectedSwapChanged(SwapViewModel? value)
    {
        if (value is not null &&
            value.ReplacementFilePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(value.ReplacementFilePath))
        {
            ShowPreview(value.ReplacementFilePath);
        }
    }

    // ---- Engine plumbing ------------------------------------------------

    [ObservableProperty]
    private string _engineStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _log = "";

    private void AppendLog(string line) =>
        Log += (Log.Length > 0 ? Environment.NewLine : "") + line;

    [RelayCommand]
    private void ClearLog() => Log = "";

    private bool CanCancelRun() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private void CancelRun() => _runCts?.Cancel();

    private async Task RunEngineAsync(
        Func<TtarchRunner, IProgress<string>, CancellationToken, Task> operation)
    {
        if (_enginePath is null)
        {
            AppendLog("Engine not found — cannot run.");
            return;
        }

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<string>(AppendLog);
        try
        {
            await operation(new TtarchRunner(_enginePath), progress, _runCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        catch (TtarchException ex)
        {
            AppendLog($"Engine error: {ex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"Unexpected error: {ex.Message}");
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            IsRunning = false;
        }
    }
}
