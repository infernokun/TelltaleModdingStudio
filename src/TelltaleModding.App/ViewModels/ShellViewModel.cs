using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>The app's workspaces.</summary>
public enum Workspace { Home, Characters, Scenes, Dialogue, Scripts, Assets, Mod, Settings }

/// <summary>
/// The main window: the game library, the open game, and the workspaces
/// (Home, Characters, Scenes, Dialogue, Scripts, Assets, Mod) that share it.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private GameCatalog? _catalog;
    private CancellationTokenSource? _openCts;

    public ShellViewModel() : this(AppSettings.Load()) { }

    public ShellViewModel(AppSettings settings)
    {
        _settings = settings;
        Preferences = settings.Preferences;
        UserPreferences.Current = Preferences;
        Look.Preferences = Preferences;
        Settings = new SettingsViewModel(Preferences, Look);
        Preferences.PropertyChanged += (_, e) =>
        {
            ApplyPreferences(e.PropertyName);
            _settings.Save();
        };
        ApplyPreferences(null);
        Look.Mode = settings.ViewportLook ?? (settings.ViewportGameLook ? RenderMode.GameLook : RenderMode.Studio);
        Characters.Look = Scenes.Look = Dialogue.Look = Look;
        Look.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewportLookViewModel.Mode) || _settings.ViewportLook == Look.Mode)
                return;
            _settings.ViewportLook = Look.Mode;
            _settings.ViewportGameLook = Look.Mode != RenderMode.Studio;
            _settings.Save();
        };
        Assets.SwapRequested += name =>
        {
            CurrentWorkspace = Workspace.Mod;
            _ = Mod.AddSwapForAsync(name);
        };
        Assets.StatusMessage += message => StatusText = message;
        Assets.ScriptRequested += name =>
        {
            CurrentWorkspace = Workspace.Scripts;
            _ = Scripts.OpenAsync(name);
        };
        Characters.LinesRequested += name =>
        {
            CurrentWorkspace = Workspace.Dialogue;
            Dialogue.ShowLinesOf(name);
        };
        Dialogue.StatusMessage += message => StatusText = message;
        Characters.StatusMessage += message => StatusText = message;
        // The Face tab lip-syncs voice lines from the dialogue index once it is read.
        Characters.Face.DialogueSource = () => Dialogue.Index;
        Dialogue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DialogueViewModel.IsIndexing) && !Dialogue.IsIndexing)
                Characters.Face.RefreshLines();
        };
        Scripts.StatusMessage += message => StatusText = message;
        Scenes.StatusMessage += message => StatusText = message;
    }

    public CharactersViewModel Characters { get; } = new();
    public ScenesViewModel Scenes { get; } = new();
    public DialogueViewModel Dialogue { get; } = new();
    public ScriptsViewModel Scripts { get; } = new();
    public AssetsViewModel Assets { get; } = new();
    public ModToolsViewModel Mod { get; } = new();

    /// <summary>How the 3D viewports shade; the style follows the open game.</summary>
    public ViewportLookViewModel Look { get; } = new();

    /// <summary>The Settings page's choices (saved with the app settings).</summary>
    public UserPreferences Preferences { get; }

    public SettingsViewModel Settings { get; }

    // Pushes preferences into the workspaces (all of them, or the one that changed).
    private void ApplyPreferences(string? changed)
    {
        bool All(string name) => changed is null || changed == name;
        var p = Preferences;
        if (All(nameof(UserPreferences.LoopClips))) Characters.Loop = p.LoopClips;
        if (All(nameof(UserPreferences.PlaybackSpeed))) Characters.Speed = p.PlaybackSpeed;
        if (All(nameof(UserPreferences.Volume))) AudioOutputs.Shared.Volume = (float)(p.Volume / 100.0);
        if (All(nameof(UserPreferences.VoicedOnly))) Dialogue.VoicedOnly = p.VoicedOnly;
        if (All(nameof(UserPreferences.ContinuousPlayback))) Dialogue.AutoPlayNext = p.ContinuousPlayback;
        if (All(nameof(UserPreferences.ShowSpeaker))) Dialogue.ShowSpeaker = p.ShowSpeaker;
        if (All(nameof(UserPreferences.SubtitleLanguage))) Dialogue.PreferredLanguage = p.SubtitleLanguage;
        if (All(nameof(UserPreferences.ScriptsDecompiled)))
            Scripts.ViewMode = p.ScriptsDecompiled ? ScriptViewMode.Decompiled : ScriptViewMode.Disassembly;
        if (All(nameof(UserPreferences.SceneLighting))) Scenes.UseSceneLighting = p.SceneLighting;
        if (All(nameof(UserPreferences.SceneTextureSize)) || All(nameof(UserPreferences.SceneLightmaps)))
            Scenes.SetAssemblyOptions(p.SceneTextureSize, p.SceneLightmaps);
        if (All(nameof(UserPreferences.ConvertOnExport))) Assets.ConvertOnExport = p.ConvertOnExport;
    }

    private IDialogService? _dialogs;

    /// <summary>Set by the view so pickers can be shown; null in unit tests.</summary>
    public IDialogService? Dialogs
    {
        get => _dialogs;
        set
        {
            _dialogs = value;
            Assets.Dialogs = value;
            Settings.Dialogs = value;
            Characters.Dialogs = value;
            Dialogue.Dialogs = value;
            Scripts.Dialogs = value;
            Scenes.Dialogs = value;
            Mod.Dialogs = value;
        }
    }

    // ---- Workspaces -------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsSettings), nameof(IsCharacters), nameof(IsScenes), nameof(IsDialogue), nameof(IsScripts), nameof(IsAssets), nameof(IsMod))]
    private Workspace _currentWorkspace = Workspace.Home;

    // Leaving the Dialogue workspace pauses its line or track; Play resumes it.
    partial void OnCurrentWorkspaceChanged(Workspace oldValue, Workspace newValue)
    {
        if (oldValue == Workspace.Dialogue && newValue != Workspace.Dialogue)
            Dialogue.Player.Pause();
        if (newValue == Workspace.Settings)
            Settings.Refresh();
    }

    public bool IsHome { get => CurrentWorkspace == Workspace.Home; set { if (value) CurrentWorkspace = Workspace.Home; } }
    public bool IsCharacters { get => CurrentWorkspace == Workspace.Characters; set { if (value) CurrentWorkspace = Workspace.Characters; } }
    public bool IsSettings { get => CurrentWorkspace == Workspace.Settings; set { if (value) CurrentWorkspace = Workspace.Settings; } }

    [RelayCommand]
    private void OpenSettings() => CurrentWorkspace = CurrentWorkspace == Workspace.Settings ? Workspace.Home : Workspace.Settings;

    public bool IsScenes { get => CurrentWorkspace == Workspace.Scenes; set { if (value) CurrentWorkspace = Workspace.Scenes; } }
    public bool IsDialogue { get => CurrentWorkspace == Workspace.Dialogue; set { if (value) CurrentWorkspace = Workspace.Dialogue; } }
    public bool IsScripts { get => CurrentWorkspace == Workspace.Scripts; set { if (value) CurrentWorkspace = Workspace.Scripts; } }
    public bool IsAssets { get => CurrentWorkspace == Workspace.Assets; set { if (value) CurrentWorkspace = Workspace.Assets; } }
    public bool IsMod { get => CurrentWorkspace == Workspace.Mod; set { if (value) CurrentWorkspace = Workspace.Mod; } }

    [RelayCommand]
    private void OpenHome() => CurrentWorkspace = Workspace.Home;

    // ---- Library ----------------------------------------------------------

    /// <summary>Games the user added.</summary>
    public ObservableCollection<GameCardViewModel> Games { get; } = new();

    /// <summary>Telltale games found in Steam libraries that are not in the library yet.</summary>
    public ObservableCollection<GameCardViewModel> Detected { get; } = new();

    [ObservableProperty]
    private bool _isScanning;

    public bool HasGames => Games.Count > 0;

    [ObservableProperty]
    private GameCardViewModel? _activeGame;

    [ObservableProperty]
    private string _activeGameName = "No game open";

    /// <summary>Restores the library, looks for installed games, and reopens the last game.</summary>
    public async Task InitializeAsync()
    {
        foreach (var folder in _settings.GameFolders.ToList())
            Games.Add(new GameCardViewModel(folder));
        OnPropertyChanged(nameof(HasGames));
        await Task.WhenAll(Games.Select(DescribeAsync));

        _ = ScanForGamesAsync();
        if (Preferences.ReopenLastGame && _settings.LastGameFolder is { } last &&
            Games.FirstOrDefault(g => g.Folder.Equals(last, StringComparison.OrdinalIgnoreCase)) is { } card)
        {
            await OpenGameAsync(card);
        }
    }

    private static async Task DescribeAsync(GameCardViewModel card)
    {
        var (install, error) = await Task.Run(() =>
        {
            try
            {
                var detected = GameInstall.Detect(card.Folder, out var detectError);
                return (detected, detectError);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ((GameInstall?)null, ex.Message);
            }
        });
        card.Install = install;
        card.Name = install?.DisplayName ?? Path.GetFileName(card.Folder);
        card.Status = install is null ? error ?? "Not a supported Telltale game" : $"{install.ArchivePaths.Count} archives";
    }

    [RelayCommand]
    private async Task ScanForGamesAsync()
    {
        IsScanning = true;
        try
        {
            var folders = await Task.Run(SteamLibraryScanner.FindTelltaleInstalls);
            Detected.Clear();
            foreach (var folder in folders)
            {
                if (Games.Any(g => SameFolder(g.Folder, folder)))
                    continue;
                var card = new GameCardViewModel(folder);
                await DescribeAsync(card);
                if (card.Install is not null)
                    Detected.Add(card);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task AddGameFolderAsync()
    {
        if (Dialogs is null)
            return;
        var folder = await Dialogs.OpenFolderAsync("Select a Telltale game's install folder");
        if (folder is not null)
            await AddAndOpenAsync(folder);
    }

    [RelayCommand]
    private async Task AddDetectedAsync(GameCardViewModel? card)
    {
        if (card is null)
            return;
        Detected.Remove(card);
        await AddAndOpenAsync(card.Folder);
    }

    /// <summary>Adds a game folder to the library (if new) and opens it.</summary>
    public async Task AddAndOpenAsync(string folder)
    {
        var card = Games.FirstOrDefault(g => SameFolder(g.Folder, folder));
        if (card is null)
        {
            card = new GameCardViewModel(folder);
            await DescribeAsync(card);
            if (card.Install is null)
            {
                StatusText = $"{folder}: {card.Status}";
                return;
            }
            Games.Add(card);
            OnPropertyChanged(nameof(HasGames));
            _settings.GameFolders.Add(folder);
            _settings.Save();
        }
        await OpenGameAsync(card);
    }

    [RelayCommand]
    private void RemoveGame(GameCardViewModel? card)
    {
        if (card is null)
            return;
        Games.Remove(card);
        OnPropertyChanged(nameof(HasGames));
        _settings.GameFolders.RemoveAll(f => SameFolder(f, card.Folder));
        if (_settings.LastGameFolder is { } last && SameFolder(last, card.Folder))
            _settings.LastGameFolder = null;
        _settings.Save();
        if (card == ActiveGame)
            CloseGame();
    }

    [RelayCommand]
    private async Task OpenGameAsync(GameCardViewModel? card)
    {
        if (card is null || card.IsBusy)
            return;
        if (card.Install is null)
            await DescribeAsync(card);
        if (card.Install is not { } install)
        {
            StatusText = $"{card.Folder}: {card.Status}";
            return;
        }

        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        card.IsBusy = true;
        IsBusy = true;
        StatusText = $"Opening {install.DisplayName}…";
        ProgressValue = 0;
        var progress = new Progress<CatalogProgress>(p =>
        {
            StatusText = p.Message;
            ProgressValue = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
            IsProgressIndeterminate = p.Total <= 0;
        });
        try
        {
            var catalog = await GameCatalog.OpenAsync(install, ArchiveOpener.For(install), progress, cts.Token);
            if (cts != _openCts)
            {
                catalog.Dispose();
                return;
            }
            _catalog?.Dispose();
            _catalog = catalog;
            foreach (var g in Games)
                g.IsActive = g == card;
            ActiveGame = card;
            ActiveGameName = install.DisplayName;
            Look.Style = GameLookStyle.ForGame(install.Game.GameNum);
            card.Status = $"{catalog.Characters.Count:N0} characters · {catalog.Entries.Count:N0} files";
            Characters.Load(catalog);
            Dialogue.Load(catalog);
            Scripts.Load(catalog);
            Scenes.Load(catalog);
            Assets.Load(catalog);
            Mod.UseGame(install.Game, install.ArchivePaths.FirstOrDefault());
            _settings.LastGameFolder = card.Folder;
            _settings.Save();
            StatusText = $"{install.DisplayName} · {card.Status}";
            if (CurrentWorkspace == Workspace.Home)
                CurrentWorkspace = Enum.TryParse<Workspace>(Preferences.StartWorkspace, out var start) &&
                                   start is not (Workspace.Home or Workspace.Settings)
                    ? start
                    : Workspace.Characters;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or UnauthorizedAccessException or DllNotFoundException)
        {
            StatusText = $"Could not open {install.DisplayName}: {ex.Message}";
        }
        finally
        {
            card.IsBusy = false;
            if (cts == _openCts)
            {
                IsBusy = false;
                IsProgressIndeterminate = false;
            }
        }
    }

    private void CloseGame()
    {
        _catalog?.Dispose();
        _catalog = null;
        ActiveGame = null;
        ActiveGameName = "No game open";
        Dialogue.Clear();
        Scripts.Clear();
        Scenes.Clear();
        Assets.Clear();
        CurrentWorkspace = Workspace.Home;
    }

    /// <summary>Closes the open game and releases its archives and caches.</summary>
    public void Dispose()
    {
        _openCts?.Cancel();
        CloseGame();
        Characters.Clear();
        // The static would otherwise keep this shell (and every view bound to it) alive.
        if (ReferenceEquals(UserPreferences.Current, Preferences))
            UserPreferences.Current = new();
    }

    /// <summary>Shows loose extracted meshes (debug flags <c>--preview-mesh</c> / <c>--play-anim</c>).</summary>
    public void OpenLooseFiles(IReadOnlyList<string> meshPaths, string? animationPath)
    {
        Characters.LoadLoose(meshPaths, animationPath);
        ActiveGameName = Path.GetFileName(Path.GetDirectoryName(meshPaths[0])) ?? "Loose files";
        CurrentWorkspace = Workspace.Characters;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    // ---- Status bar -------------------------------------------------------

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [RelayCommand]
    private void CancelOpen()
    {
        _openCts?.Cancel();
        _openCts = null;
        IsBusy = false;
        StatusText = "Cancelled.";
    }
}

/// <summary>A game in the library (or found on this PC).</summary>
public sealed partial class GameCardViewModel : ViewModelBase
{
    public GameCardViewModel(string folder)
    {
        Folder = folder;
        _name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
    }

    public string Folder { get; }

    public GameInstall? Install { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    private string _name;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Monogram for the card's tile, e.g. "WD" for The Walking Dead.</summary>
    public string Initials
    {
        get
        {
            var words = Name.Replace(":", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !w.Equals("The", StringComparison.OrdinalIgnoreCase) && char.IsLetter(w[0]))
                .ToList();
            return words.Count == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        }
    }
}
