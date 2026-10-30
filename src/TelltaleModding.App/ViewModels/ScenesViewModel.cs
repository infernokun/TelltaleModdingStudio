using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Export;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The game's locations: pick a scene to assemble its environment (every visible agent's
/// meshes placed where the scene puts them), browse and toggle its agents, and export it.
/// </summary>
public sealed partial class ScenesViewModel : ViewModelBase
{
    private const string AllEpisodes = "All episodes";

    private GameCatalog? _catalog;
    private SceneAssembler? _assembler;
    private List<SceneRowViewModel> _allScenes = new();
    private SceneLayout? _layout;
    private AssembledScene? _built;
    private HashSet<int> _builtObjects = new();
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _visibilityCts;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private bool _bulkToggle;
    private bool _restoringSelection;
    private SceneRowViewModel? _loadedScene;
    private SceneAssetResolver? _resolver;

    /// <summary>Set by the shell so pickers can be shown.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>How the viewport shades (shared by the workspaces; set by the shell).</summary>
    public ViewportLookViewModel Look { get; set; } = new();

    /// <summary>Raised with a short status message for the status bar.</summary>
    public event Action<string>? StatusMessage;

    /// <summary>Raised to show an assembled environment; the flag asks to keep the camera where it is.</summary>
    public event Action<AssembledScene, bool>? SceneReady;

    /// <summary>Raised to show or hide batches of the shown environment (null shows all).</summary>
    public event Action<IReadOnlyList<bool>?>? BatchVisibilityChanged;

    /// <summary>Raised to frame the environment again.</summary>
    public event Action? ResetViewRequested;

    /// <summary>Raised when the viewport should be emptied.</summary>
    public event Action? SceneCleared;

    /// <summary>Raised with the rig the viewport should light the scene with (null = studio lighting).</summary>
    public event Action<LightRig?>? LightingChanged;

    [ObservableProperty]
    private bool _hasSource;

    private SceneAssemblerOptions _assemblyOptions = new();

    /// <summary>
    /// Texture detail (largest side, 0 = full) and lightmaps for scenes assembled from now
    /// on; the open game's cache is rebuilt so the next scene picks them up.
    /// </summary>
    public void SetAssemblyOptions(int maxTextureSize, bool lightmaps)
    {
        var options = _assemblyOptions with { MaxTextureSize = maxTextureSize, Lightmaps = lightmaps };
        if (options == _assemblyOptions)
            return;
        _assemblyOptions = options;
        if (_resolver is not null)
            _assembler = new SceneAssembler(_resolver, _assemblyOptions);
    }

    public void Load(GameCatalog catalog)
    {
        Clear();
        _catalog = catalog;
        _resolver = new SceneAssetResolver(catalog);
        _assembler = new SceneAssembler(_resolver, _assemblyOptions);
        _allScenes = catalog.Entries
            .Where(e => e.Name.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
            .Select(e => new SceneRowViewModel(e))
            .OrderBy(r => r.IsLocation ? 0 : 1)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Episodes.Clear();
        Episodes.Add(AllEpisodes);
        foreach (var episode in _allScenes.Select(s => s.Episode).Distinct().OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            Episodes.Add(episode);
        SelectedEpisode = AllEpisodes;
        HasSource = true;
        ApplyFilter();
    }

    public void Clear()
    {
        _loadCts?.Cancel();
        _visibilityCts?.Cancel();
        _catalog = null;
        _assembler = null;
        _resolver = null;
        _allScenes = new();
        Scenes.Clear();
        Episodes.Clear();
        HasSource = false;
        _loadedScene = null;
        SelectedScene = null;
        ClearScene();
    }

    // ---- Scene list -----------------------------------------------------------

    public ObservableCollection<SceneRowViewModel> Scenes { get; } = new();

    public ObservableCollection<string> Episodes { get; } = new();

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string? _selectedEpisode;

    /// <summary>Also list menus and other non-location scenes.</summary>
    [ObservableProperty]
    private bool _includeOtherScenes;

    [ObservableProperty]
    private string _sceneSummary = "";

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedEpisodeChanged(string? value) => ApplyFilter();

    partial void OnIncludeOtherScenesChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var episode = SelectedEpisode is null or AllEpisodes ? null : SelectedEpisode;
        var hits = _allScenes.Where(s =>
                (IncludeOtherScenes || s.IsLocation) &&
                (episode is null || s.Episode == episode) &&
                terms.All(t => s.DisplayName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                               s.FileName.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // Refilling the list clears the selection; put it back without reloading the scene.
        var keep = SelectedScene;
        _restoringSelection = true;
        Scenes.Clear();
        foreach (var hit in hits)
            Scenes.Add(hit);
        SelectedScene = keep is not null && hits.Contains(keep) ? keep : null;
        _restoringSelection = false;
        int total = _allScenes.Count(s => IncludeOtherScenes || s.IsLocation);
        SceneSummary = hits.Count == total ? $"{total:N0} scenes" : $"{hits.Count:N0} of {total:N0}";
    }

    [ObservableProperty]
    private SceneRowViewModel? _selectedScene;

    partial void OnSelectedSceneChanged(SceneRowViewModel? value)
    {
        if (value is not null && !_restoringSelection && value != _loadedScene)
            _ = LoadSceneAsync(value);
    }

    // ---- Loading ----------------------------------------------------------------

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = "";

    [ObservableProperty]
    private double _loadingProgress;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportGltfCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetViewCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowDefaultCommand))]
    private bool _hasScene;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private string _stats = "";

    [ObservableProperty]
    private string? _error;

    /// <summary>Meshes that could not be loaded, and similar notes.</summary>
    public ObservableCollection<string> Problems { get; } = new();

    private async Task LoadSceneAsync(SceneRowViewModel row)
    {
        if (_catalog is not { } catalog || _assembler is not { } assembler)
            return;
        var resolver = _resolver;
        _loadCts?.Cancel();
        _visibilityCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var token = cts.Token;

        _loadedScene = row;
        ClearScene();
        Title = row.DisplayName;
        Subtitle = $"{row.FileName} · {row.Episode}";
        IsLoading = true;
        IsProgressIndeterminate = true;
        LoadingText = "Reading scene…";
        var progress = new Progress<SceneBuildProgress>(p =>
        {
            if (cts != _loadCts)
                return;
            LoadingText = p.Total > 0 ? $"{p.Message.Split(':')[0]} ({p.Done:N0}/{p.Total:N0})" : p.Message;
            IsProgressIndeterminate = p.Total <= 0;
            LoadingProgress = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        });

        try
        {
            await _buildLock.WaitAsync(token);
            try
            {
                var (layout, built, lighting) = await Task.Run(() =>
                {
                    var scene = SceneFile.Load(catalog.Read(row.Entry));
                    token.ThrowIfCancellationRequested();
                    var described = assembler.Describe(scene);
                    token.ThrowIfCancellationRequested();
                    AssembledScene? model = described.Objects.Any(o => o.ShownByDefault)
                        ? assembler.Build(described, null, progress, token)
                        : null;
                    var lights = SceneLighting.Build(described, resolver,
                        model?.Model.Geometry.BoundsMin, model?.Model.Geometry.BoundsMax);
                    return (described, model, lights);
                }, token);
                if (cts != _loadCts)
                    return;
                ShowLayout(layout, built, lighting);
            }
            finally
            {
                _buildLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            if (cts == _loadCts)
            {
                Error = $"Could not load {row.FileName}: {ex.Message}";
                StatusMessage?.Invoke(Error);
            }
        }
        finally
        {
            if (cts == _loadCts)
                IsLoading = false;
        }
    }

    private void ShowLayout(SceneLayout layout, AssembledScene? built, SceneLighting lighting)
    {
        _layout = layout;
        _built = built;
        _builtObjects = built?.IncludedObjects.ToHashSet() ?? new HashSet<int>();

        _bulkToggle = true;
        Agents.Clear();
        _allAgents = layout.Objects.Select(o => new SceneAgentRowViewModel(o, this) { IsOn = _builtObjects.Contains(o.Agent.Index) }).ToList();
        Kinds.Clear();
        foreach (var group in _allAgents.Where(a => a.CanToggle).GroupBy(a => a.Object.Kind).OrderBy(g => g.Key))
            Kinds.Add(new SceneKindViewModel(group.Key, group.ToList(), this));
        _bulkToggle = false;
        ApplyAgentFilter();
        ShowLights(lighting);

        Problems.Clear();
        if (built is not null)
        {
            foreach (var (mesh, error) in built.Model.Failures.DistinctBy(f => f.Mesh))
                Problems.Add($"{mesh}: {error}");
            SceneReady?.Invoke(built, false);
            HasScene = true;
        }
        else
        {
            Error = "This scene places no visible meshes.";
        }
        UpdateStats();
        StatusMessage?.Invoke($"{Title}: {Stats}");
    }

    private void ClearScene()
    {
        _layout = null;
        _built = null;
        _builtObjects.Clear();
        _allAgents = new();
        Agents.Clear();
        Kinds.Clear();
        Problems.Clear();
        HasScene = false;
        Title = Subtitle = Stats = "";
        Error = null;
        ShowLights(null);
        SceneCleared?.Invoke();
    }

    // ---- Agents -----------------------------------------------------------------

    private List<SceneAgentRowViewModel> _allAgents = new();

    public ObservableCollection<SceneAgentRowViewModel> Agents { get; } = new();

    public ObservableCollection<SceneKindViewModel> Kinds { get; } = new();

    [ObservableProperty]
    private string _agentFilter = "";

    /// <summary>List only agents that render something.</summary>
    [ObservableProperty]
    private bool _onlyAgentsWithMeshes = true;

    [ObservableProperty]
    private string _agentSummary = "";

    partial void OnAgentFilterChanged(string value) => ApplyAgentFilter();

    partial void OnOnlyAgentsWithMeshesChanged(bool value) => ApplyAgentFilter();

    private void ApplyAgentFilter()
    {
        var terms = AgentFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _allAgents.Where(a =>
                (!OnlyAgentsWithMeshes || a.CanToggle) &&
                terms.All(t => a.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                               a.KindLabel.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                               a.Object.VisibleMeshes.Any(m => m.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        Agents.Clear();
        foreach (var hit in hits)
            Agents.Add(hit);
        AgentSummary = $"{hits.Count:N0} of {_allAgents.Count:N0} agents";
    }

    internal void OnAgentToggled()
    {
        if (_bulkToggle)
            return;
        foreach (var kind in Kinds)
            kind.Refresh();
        _ = ApplyVisibilityAsync();
    }

    internal void SetKind(IReadOnlyList<SceneAgentRowViewModel> agents, bool on)
    {
        _bulkToggle = true;
        foreach (var agent in agents)
            agent.IsOn = on;
        _bulkToggle = false;
        OnAgentToggled();
    }

    /// <summary>
    /// Shows the agents that are switched on: hides batches when everything wanted is already
    /// assembled, otherwise re-assembles (decoded meshes and textures are cached).
    /// </summary>
    private async Task ApplyVisibilityAsync()
    {
        if (_layout is not { } layout || _assembler is not { } assembler)
            return;
        _visibilityCts?.Cancel();
        var cts = _visibilityCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var wanted = _allAgents.Where(a => a.IsOn && a.CanToggle).Select(a => a.Object.Agent.Index).ToHashSet();
        if (_built is { } built && wanted.IsSubsetOf(_builtObjects))
        {
            var shown = built.BatchObjects.Select(wanted.Contains).ToArray();
            BatchVisibilityChanged?.Invoke(shown.All(s => s) ? null : shown);
            UpdateStats();
            return;
        }
        if (wanted.Count == 0)
            return;

        IsLoading = true;
        IsProgressIndeterminate = false;
        LoadingText = "Assembling…";
        var progress = new Progress<SceneBuildProgress>(p =>
        {
            if (cts != _visibilityCts)
                return;
            LoadingText = $"{p.Message.Split(':')[0]} ({p.Done:N0}/{p.Total:N0})";
            LoadingProgress = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        });
        try
        {
            await _buildLock.WaitAsync(cts.Token);
            try
            {
                var rebuilt = await Task.Run(
                    () => assembler.Build(layout, o => wanted.Contains(o.Agent.Index), progress, cts.Token), cts.Token);
                if (cts != _visibilityCts || layout != _layout)
                    return;
                _built = rebuilt;
                _builtObjects = rebuilt.IncludedObjects.ToHashSet();
                Problems.Clear();
                foreach (var (mesh, error) in rebuilt.Model.Failures.DistinctBy(f => f.Mesh))
                    Problems.Add($"{mesh}: {error}");
                // A scene that showed nothing at the start (jackAI.scene, whose agents start
                // hidden) is framed when the first agent is switched on.
                bool first = !HasScene;
                Error = null;
                SceneReady?.Invoke(rebuilt, !first);
                HasScene = true;
                UpdateStats();
                if (first)
                    StatusMessage?.Invoke($"{Title}: {Stats}");
            }
            finally
            {
                _buildLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidDataException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            if (cts == _visibilityCts)
                IsLoading = false;
        }
    }

    private void UpdateStats()
    {
        if (_layout is not { } layout)
            return;
        int agents = layout.Objects.Count;
        if (_built is not { } built)
        {
            Stats = $"{agents:N0} agents · nothing to show";
            return;
        }
        var on = _allAgents.Where(a => a.IsOn && a.CanToggle).Select(a => a.Object.Agent.Index).ToHashSet();
        on.IntersectWith(_builtObjects);
        var geometry = built.Model.Geometry;
        long triangles = 0;
        var textures = new HashSet<object>();
        for (int b = 0; b < geometry.Batches.Count; b++)
        {
            if (!on.Contains(built.BatchObjects[b]))
                continue;
            triangles += geometry.Batches[b].PrimitiveCount;
            if (built.Model.BatchTextures[b] is { } texture)
                textures.Add(texture);
        }
        int meshes = on.Sum(i => layout.Objects[i].VisibleMeshes.Count);
        Stats = $"{agents:N0} agents · {meshes:N0} meshes · {triangles:N0} triangles · {textures.Count:N0} textures";
    }

    // ---- Lights -----------------------------------------------------------------

    private SceneLighting? _lighting;
    private List<SceneLightRowViewModel> _allLights = new();
    private bool _bulkLights;

    /// <summary>The scene's lights shown in the list (see <see cref="ShowAllLights"/>).</summary>
    public ObservableCollection<SceneLightRowViewModel> Lights { get; } = new();

    /// <summary>Light the scene with its own lights (false: the studio rig).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseStudioLighting))]
    private bool _useSceneLighting = true;

    /// <summary>The inverse of <see cref="UseSceneLighting"/>, for the radio buttons.</summary>
    public bool UseStudioLighting
    {
        get => !UseSceneLighting;
        set => UseSceneLighting = !value;
    }

    /// <summary>Also list character rig lights, effect lights and lights for the sky dome or foliage.</summary>
    [ObservableProperty]
    private bool _showAllLights;

    [ObservableProperty]
    private bool _hasLights;

    [ObservableProperty]
    private string _lightSummary = "";

    /// <summary>The rig the viewport is lit with; null for studio lighting.</summary>
    public LightRig? CurrentRig { get; private set; }

    partial void OnUseSceneLightingChanged(bool value) => UpdateLighting();

    partial void OnShowAllLightsChanged(bool value) => ApplyLightFilter();

    private void ShowLights(SceneLighting? lighting)
    {
        _lighting = lighting;
        _bulkLights = true;
        _allLights = lighting?.Lights.Select(l => new SceneLightRowViewModel(l, this) { IsOn = SceneLighting.IsOnByDefault(l) }).ToList()
                     ?? new List<SceneLightRowViewModel>();
        _bulkLights = false;
        HasLights = _allLights.Count > 0;
        ApplyLightFilter();
        UpdateLighting();
    }

    private void ApplyLightFilter()
    {
        Lights.Clear();
        foreach (var row in _allLights.Where(r => ShowAllLights || r.IsMain)
                     .OrderByDescending(r => r.IsOn).ThenByDescending(r => r.Light.Importance))
            Lights.Add(row);
    }

    internal void OnLightToggled()
    {
        if (!_bulkLights)
            UpdateLighting();
    }

    /// <summary>Back to the lights the scene starts with.</summary>
    [RelayCommand]
    private void DefaultLights()
    {
        _bulkLights = true;
        foreach (var row in _allLights)
            row.IsOn = SceneLighting.IsOnByDefault(row.Light);
        _bulkLights = false;
        UpdateLighting();
    }

    private void UpdateLighting()
    {
        LightRig? rig = null;
        if (UseSceneLighting && _lighting is { } lighting && _allLights.Count > 0)
            rig = lighting.BuildRig(_allLights.Where(r => r.IsOn).Select(r => r.Light));
        CurrentRig = rig;
        int on = _allLights.Count(r => r.IsOn);
        LightSummary = _allLights.Count == 0
            ? "No lights in this scene"
            : $"{_allLights.Count:N0} lights · {on:N0} on" + (rig is null ? "" : $" · {rig.Lights.Count} drawn");
        LightingChanged?.Invoke(rig);
    }

    // ---- Commands ---------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasScene))]
    private void ResetView() => ResetViewRequested?.Invoke();

    /// <summary>Back to the scene's own start: visible environment and props only.</summary>
    [RelayCommand(CanExecute = nameof(HasScene))]
    private void ShowDefault()
    {
        _bulkToggle = true;
        foreach (var agent in _allAgents)
            agent.IsOn = agent.Object.ShownByDefault;
        _bulkToggle = false;
        OnAgentToggled();
    }

    [RelayCommand(CanExecute = nameof(HasScene))]
    private async Task ExportGltfAsync()
    {
        if (Dialogs is null || _layout is not { } layout || _assembler is not { } assembler || SelectedScene is not { } row)
            return;
        var stem = Path.GetFileNameWithoutExtension(row.FileName);
        var path = await Dialogs.SaveFileAsync("Export environment", stem + ".glb", ("glTF binary", new[] { "*.glb" }));
        if (path is null)
            return;
        var wanted = _allAgents.Where(a => a.IsOn && a.CanToggle).Select(a => a.Object.Agent.Index).ToHashSet();
        IsLoading = true;
        IsProgressIndeterminate = true;
        LoadingText = "Exporting…";
        try
        {
            await _buildLock.WaitAsync();
            try
            {
                var summary = await Task.Run(() =>
                {
                    var model = _built is { } built && wanted.SetEquals(_builtObjects)
                        ? built
                        : assembler.Build(layout, o => wanted.Contains(o.Agent.Index));
                    return GltfExporter.WriteGlb(model.Model, path, options: new GltfExportOptions
                    {
                        Name = stem,
                        IncludeNormalMaps = Services.UserPreferences.Current.GltfNormalMaps,
                    });
                });
                StatusMessage?.Invoke(
                    $"Exported {Path.GetFileName(path)}: {summary.Primitives:N0} primitives, {summary.Textures:N0} textures, {summary.Bytes / (1024.0 * 1024.0):0.#} MB");
            }
            finally
            {
                _buildLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Error = $"Export failed: {ex.Message}";
            StatusMessage?.Invoke(Error);
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>One scene file in the list.</summary>
public sealed class SceneRowViewModel
{
    public SceneRowViewModel(ArchiveEntryRef entry)
    {
        Entry = entry;
        FileName = entry.Name;
        DisplayName = SceneNaming.DisplayName(entry.Name);
        Episode = SceneNaming.Episode(entry.ArchivePath);
        IsLocation = SceneNaming.IsLocation(entry.Name);
    }

    public ArchiveEntryRef Entry { get; }
    public string FileName { get; }
    public string DisplayName { get; }
    public string Episode { get; }
    public bool IsLocation { get; }
    public string SizeText => Entry.Size < 1024 * 1024 ? $"{Entry.Size / 1024.0:0} KB" : $"{Entry.Size / (1024.0 * 1024.0):0.0} MB";
}

/// <summary>One agent of the loaded scene, with a visibility toggle when it has meshes.</summary>
public sealed partial class SceneAgentRowViewModel : ObservableObject
{
    private readonly ScenesViewModel _owner;

    public SceneAgentRowViewModel(SceneObject obj, ScenesViewModel owner)
    {
        Object = obj;
        _owner = owner;
    }

    public SceneObject Object { get; }
    public string Name => Object.Name;
    public string KindLabel => SceneKindViewModel.KindName(Object.Kind);
    public bool CanToggle => Object.HasMeshes;

    public string Detail
    {
        get
        {
            var p = Object.WorldPosition;
            var meshes = Object.VisibleMeshes.Count switch
            {
                0 => "no meshes",
                1 => Object.VisibleMeshes[0],
                int n => $"{n} meshes",
            };
            return $"{meshes} · ({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})" + (Object.StartVisible ? "" : " · hidden at start");
        }
    }

    public string ToolTip =>
        string.Join("\n", new[] { Object.Name, $"Kind: {KindLabel}", Object.Agent.ParentName is { } parent ? $"Attached to: {parent}" : null }
            .Concat(Object.VisibleMeshes)
            .Concat(Object.PropertyFiles.Take(3).Select(f => $"Inherits {f}"))
            .Where(s => s is not null));

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _owner.OnAgentToggled();
}

/// <summary>A kind of agent (environment, props, characters…) with a toggle for all of them.</summary>
public sealed partial class SceneKindViewModel : ObservableObject
{
    private readonly IReadOnlyList<SceneAgentRowViewModel> _agents;
    private readonly ScenesViewModel _owner;
    private bool _refreshing;

    public SceneKindViewModel(SceneAgentKind kind, IReadOnlyList<SceneAgentRowViewModel> agents, ScenesViewModel owner)
    {
        Kind = kind;
        _agents = agents;
        _owner = owner;
        Refresh();
    }

    public SceneAgentKind Kind { get; }
    public string Label => $"{KindName(Kind)} ({_agents.Count})";

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value)
    {
        if (!_refreshing)
            _owner.SetKind(_agents, value);
    }

    /// <summary>On when any agent of the kind is on.</summary>
    internal void Refresh()
    {
        _refreshing = true;
        IsOn = _agents.Any(a => a.IsOn);
        _refreshing = false;
    }

    public static string KindName(SceneAgentKind kind) => kind switch
    {
        SceneAgentKind.Environment => "Environment",
        SceneAgentKind.Prop => "Props",
        SceneAgentKind.Character => "Characters",
        SceneAgentKind.Effect => "Effects",
        SceneAgentKind.Interface => "Interface",
        SceneAgentKind.Camera => "Cameras",
        SceneAgentKind.Light => "Lights",
        SceneAgentKind.Audio => "Audio",
        SceneAgentKind.Group => "Groups",
        _ => "Other",
    };
}

/// <summary>One light of the loaded scene, with a toggle.</summary>
public sealed partial class SceneLightRowViewModel : ObservableObject
{
    private readonly ScenesViewModel _owner;

    public SceneLightRowViewModel(SceneLight light, ScenesViewModel owner)
    {
        Light = light;
        _owner = owner;
        var c = light.Color;
        float max = MathF.Max(1e-4f, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
        byte Channel(float v) => (byte)Math.Clamp(MathF.Round(MathF.Pow(Math.Max(0, v / max), 1 / 2.2f) * 255), 0, 255);
        Swatch = new SolidColorBrush(Color.FromRgb(Channel(c.X), Channel(c.Y), Channel(c.Z)));
    }

    public SceneLight Light { get; }
    public string Name => Light.Name;

    /// <summary>The light's hue at full brightness.</summary>
    public IBrush Swatch { get; }

    /// <summary>Lights the location itself (not a character rig, effect, sky dome or foliage light).</summary>
    public bool IsMain => Light.AffectsMainGeometry && !Light.IsCharacterLight && !SceneLighting.LooksLikeEffectLight(Light.Name);

    public string TypeLabel => Light.Type switch
    {
        SceneLightType.Directional => "Directional",
        SceneLightType.Point => "Point",
        SceneLightType.Spot => "Spot",
        SceneLightType.Ambient => "Ambient",
        _ => "Sky ambient",
    };

    public string IntensityText => $"{Light.Intensity:0.##}";

    public string Detail
    {
        get
        {
            var parts = new List<string> { TypeLabel };
            if (Light.Range > 0 && Light.Type != SceneLightType.Directional)
                parts.Add($"range {Light.Range:0.#}");
            if (Light.Type == SceneLightType.Spot)
                parts.Add($"cone {Light.SpotOuterDegrees:0}\u00b0");
            if (Light.IsCharacterLight)
                parts.Add("characters");
            if (!Light.Enabled)
                parts.Add("off at start");
            return string.Join(" \u00b7 ", parts);
        }
    }

    public string ToolTip
    {
        get
        {
            var p = Light.Position;
            var d = Light.Direction;
            return string.Join("\n", new[]
            {
                Light.Name,
                Detail + $" \u00b7 intensity {Light.Intensity:0.###}",
                $"Position ({p.X:0.##}, {p.Y:0.##}, {p.Z:0.##})",
                Light.Type is SceneLightType.Directional or SceneLightType.Spot ? $"Direction ({d.X:0.##}, {d.Y:0.##}, {d.Z:0.##})" : null,
                Light.AffectsMainGeometry ? null : "Lights only part of the scene (sky dome, foliage, effects)",
                Light.LightsLightmapped ? null : "Not applied to lightmapped surfaces",
            }.Where(s => s is not null));
        }
    }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => _owner.OnLightToggled();
}
