using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Engine;
using TelltaleModding.Core.Games;
using TelltaleModding.Core.Projects;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The mod project: which files to replace and with what, saved as a <c>.ttmod</c>,
/// and the build of a minimal patch archive the game loads over its originals.
/// </summary>
public sealed partial class ModToolsViewModel : ViewModelBase
{
    private readonly string? _enginePath;
    private ModProject _project = new();
    private CancellationTokenSource? _runCts;

    public ModToolsViewModel()
    {
        _enginePath = EngineLocator.TryLocate();
        Games = new ObservableCollection<GameEntry>(GameDatabase.Games);
        EngineStatus = _enginePath is null
            ? $"Patch builder not found. Set {EngineLocator.PathEnvVar} or place ttarchext in engine/ttarchext/"
            : !EngineLocator.CanExecute(_enginePath)
                ? "The patch builder needs Windows here. Build the native engine (make -C engine/ttarchext)"
                : "Patch builder ready";
    }

    /// <summary>Set by the shell so pickers can be shown.</summary>
    public IDialogService? Dialogs { get; set; }

    // ---- Target game ------------------------------------------------------

    public ObservableCollection<GameEntry> Games { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildPatchCommand))]
    private GameEntry? _selectedGame;

    /// <summary>One of the game's real archives; patches are built in its format version.</summary>
    [ObservableProperty]
    private string? _referenceArchivePath;

    partial void OnSelectedGameChanged(GameEntry? value)
    {
        if (value is not null)
            _project.GameNum = value.GameNum;
    }

    /// <summary>Targets the game the user opened in the library (unless the project chose another).</summary>
    public void UseGame(GameEntry game, string? referenceArchive)
    {
        if (Swaps.Count > 0 && SelectedGame is not null && SelectedGame != game)
            return;
        SelectedGame = game;
        ReferenceArchivePath = referenceArchive;
    }

    // ---- Swaps ------------------------------------------------------------

    [ObservableProperty]
    private string _projectName = "Untitled Mod";

    public ObservableCollection<SwapViewModel> Swaps { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSwapCommand))]
    private SwapViewModel? _selectedSwap;

    /// <summary>File name typed in the "replace" box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSwapCommand))]
    private string _newTargetName = "";

    public bool HasSwaps => Swaps.Count > 0;

    private bool CanAddSwap() => !string.IsNullOrWhiteSpace(NewTargetName);

    [RelayCommand(CanExecute = nameof(CanAddSwap))]
    private async Task AddSwapAsync()
    {
        await AddSwapForAsync(NewTargetName.Trim());
        NewTargetName = "";
    }

    /// <summary>Asks for a replacement file for <paramref name="targetAssetName"/> and adds the swap.</summary>
    public async Task AddSwapForAsync(string targetAssetName)
    {
        if (Dialogs is null)
            return;
        var replacement = await Dialogs.OpenFileAsync($"Choose a replacement for {targetAssetName}",
            ("All files", new[] { "*" }));
        if (replacement is null)
            return;
        AddSwap(targetAssetName, replacement);
    }

    /// <summary>Adds a swap row (also used by tests).</summary>
    public void AddSwap(string targetAssetName, string replacementPath)
    {
        var existing = Swaps.FirstOrDefault(s => s.TargetAssetName.Equals(targetAssetName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.ReplacementFilePath = replacementPath;
        }
        else
        {
            var swap = new AssetSwap { TargetAssetName = targetAssetName, ReplacementFilePath = replacementPath };
            _project.Swaps.Add(swap);
            Swaps.Add(Track(new SwapViewModel(swap)));
        }
        OnPropertyChanged(nameof(HasSwaps));
        BuildPatchCommand.NotifyCanExecuteChanged();
        AppendLog($"Swap: {targetAssetName} ← {Path.GetFileName(replacementPath)}");
    }

    private bool CanRemoveSwap() => SelectedSwap is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSwap))]
    private void RemoveSwap()
    {
        if (SelectedSwap is null)
            return;
        _project.Swaps.Remove(SelectedSwap.Swap);
        Swaps.Remove(SelectedSwap);
        OnPropertyChanged(nameof(HasSwaps));
        BuildPatchCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NewProject()
    {
        LoadProjectFrom(new ModProject { GameNum = SelectedGame?.GameNum ?? -1 });
        ReferenceArchivePath = _project.ReferenceArchivePath ?? ReferenceArchivePath;
    }

    [RelayCommand]
    private async Task SaveProjectAsync()
    {
        if (Dialogs is null)
            return;
        var path = await Dialogs.SaveFileAsync("Save mod project", SuggestedProjectFileName(),
            ("Telltale mod project", new[] { "*.ttmod" }));
        if (path is null)
            return;
        _project.Name = ProjectName;
        _project.ReferenceArchivePath = ReferenceArchivePath;
        _project.Save(path);
        AppendLog($"Project saved: {path}");
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (Dialogs is null)
            return;
        var path = await Dialogs.OpenFileAsync("Open mod project",
            ("Telltale mod project", new[] { "*.ttmod" }), ("All files", new[] { "*" }));
        if (path is null)
            return;
        try
        {
            LoadProjectFrom(ModProject.Load(path));
            AppendLog($"Project opened: {path}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            AppendLog($"Could not open project: {ex.Message}");
        }
    }

    /// <summary>Applies a loaded project to the UI state (exposed for tests).</summary>
    public void LoadProjectFrom(ModProject project)
    {
        _project = project;
        ProjectName = project.Name;
        if (project.Game is { } game)
            SelectedGame = game;
        if (!string.IsNullOrEmpty(project.ReferenceArchivePath))
            ReferenceArchivePath = project.ReferenceArchivePath;
        Swaps.Clear();
        foreach (var swap in project.Swaps)
            Swaps.Add(Track(new SwapViewModel(swap)));
        OnPropertyChanged(nameof(HasSwaps));
        BuildPatchCommand.NotifyCanExecuteChanged();
    }

    // Ticking a swap on or off changes whether there is anything to build.
    private SwapViewModel Track(SwapViewModel swap)
    {
        swap.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwapViewModel.Enabled))
                BuildPatchCommand.NotifyCanExecuteChanged();
        };
        return swap;
    }

    private string SuggestedProjectFileName()
    {
        var safe = string.Join("_", ProjectName.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Trim();
        return (safe.Length == 0 ? "mod" : safe) + ".ttmod";
    }

    // ---- Patch build --------------------------------------------------------

    [ObservableProperty]
    private string _engineStatus;

    public bool CanRunEngine => _enginePath is not null && EngineLocator.CanExecute(_enginePath);

    private bool CanBuildPatch() => CanRunEngine && SelectedGame is not null && Swaps.Any(s => s.Enabled) && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanBuildPatch))]
    private async Task BuildPatchAsync()
    {
        if (Dialogs is null || SelectedGame is null)
            return;
        var output = await Dialogs.SaveFileAsync("Save patch archive as",
            PatchBuilder.ConventionalPatchName(SelectedGame.Kind),
            ("Telltale archive", new[] { "*.ttarch", "*.ttarch2" }));
        if (output is null)
            return;

        _project.Name = ProjectName;
        _project.ReferenceArchivePath = ReferenceArchivePath;
        var issues = _project.Validate();
        if (issues.Count > 0)
        {
            foreach (var issue in issues)
                AppendLog($"✗ {issue}");
            return;
        }

        IsRunning = true;
        _runCts = new CancellationTokenSource();
        try
        {
            var builder = new PatchBuilder(new TtarchRunner(_enginePath!));
            var result = await builder.BuildAsync(_project, new PatchBuildRequest(output),
                new Progress<string>(AppendLog), _runCts.Token);
            AppendLog(result.Success
                ? $"✓ Patch built: {result.OutputArchivePath} ({result.StagedFileCount} file(s), version {result.Version}). " +
                  "Drop it in the game's archive folder."
                : "✗ Patch build failed. See the log. If the game crashes with the patch, retry with compatibility mode.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("Cancelled.");
        }
        catch (Exception ex) when (ex is TtarchException or IOException or InvalidOperationException)
        {
            AppendLog($"✗ {ex.Message}");
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            IsRunning = false;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelBuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildPatchCommand))]
    private bool _isRunning;

    private bool CanCancelBuild() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancelBuild))]
    private void CancelBuild() => _runCts?.Cancel();

    [ObservableProperty]
    private string _log = "";

    private void AppendLog(string line) => Log += (Log.Length > 0 ? Environment.NewLine : "") + line;

    [RelayCommand]
    private void ClearLog() => Log = "";
}
