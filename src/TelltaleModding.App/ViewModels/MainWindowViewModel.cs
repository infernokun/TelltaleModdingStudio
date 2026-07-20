using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Engine;
using TelltaleModding.Core.Games;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Textures;
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

    // ---- Extracted-folder explorer --------------------------------------

    [ObservableProperty]
    private string? _folderPath;

    /// <summary>Root of the explorer tree (single element so the TreeView shows the folder itself).</summary>
    public ObservableCollection<FileNodeViewModel> FolderRoots { get; } = new();

    [ObservableProperty]
    private string _folderInfo = "Open an extracted folder to browse its assets.";

    [ObservableProperty]
    private FileNodeViewModel? _selectedFolderNode;

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.OpenFolderAsync("Open extracted folder");
        if (path is null) return;
        OpenFolder(path);
    }

    /// <summary>Loads a folder into the explorer (exposed for tests and drag-drop).</summary>
    public void OpenFolder(string path)
    {
        FolderPath = path;
        _folderTreeRoot = FileNodeViewModel.ForRoot(path);
        _allFolderFiles = null;
        _textureIndex = null;   // rebuilt lazily for the new folder
        _skeletonIndex = null;
        FolderFilter = "";
        ApplyFolderFilter();
        FolderInfo = $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(path))} — click a file to inspect it.";
    }

    private FileNodeViewModel? _folderTreeRoot;
    private List<string>? _allFolderFiles;

    /// <summary>Search text for the folder explorer; matches flatten into a result list.</summary>
    [ObservableProperty]
    private string _folderFilter = "";

    partial void OnFolderFilterChanged(string value) => ApplyFolderFilter();

    private const int MaxFilterResults = 400;

    private void ApplyFolderFilter()
    {
        FolderRoots.Clear();
        if (FolderPath is null)
            return;

        if (string.IsNullOrWhiteSpace(FolderFilter))
        {
            if (_folderTreeRoot is not null)
                FolderRoots.Add(_folderTreeRoot);
            return;
        }

        _allFolderFiles ??= Directory.EnumerateFiles(FolderPath, "*", SearchOption.AllDirectories).ToList();
        int shown = 0;
        foreach (var file in _allFolderFiles)
        {
            if (!Path.GetFileName(file).Contains(FolderFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            FolderRoots.Add(FileNodeViewModel.ForSearchResult(FolderPath, file));
            if (++shown >= MaxFilterResults)
            {
                FolderRoots.Add(new FileNodeViewModel($"… more matches — refine the filter", "", isDirectory: false));
                break;
            }
        }
        FolderInfo = $"{shown} match{(shown == 1 ? "" : "es")} for \"{FolderFilter}\"";
    }

    /// <summary>
    /// Texture lookup for the open folder: Telltale references textures by the CRC64
    /// of the file name without extension, so hashing every .d3dtx name lets a mesh's
    /// texture symbols resolve straight to files.
    /// </summary>
    private Dictionary<ulong, string>? _textureIndex;

    private Dictionary<ulong, string> GetTextureIndex(string anchorFile)
    {
        if (_textureIndex is not null)
            return _textureIndex;
        var index = new Dictionary<ulong, string>();
        if (FolderPath is not null)
        {
            foreach (var tex in FindFolderAssets("*.d3dtx"))
                index[TelltaleCrc64.HashSymbol(Path.GetFileNameWithoutExtension(tex))] = tex;
        }
        else if (Path.GetDirectoryName(anchorFile) is { } root && Directory.Exists(root))
        {
            foreach (var tex in Directory.EnumerateFiles(root, "*.d3dtx", SearchOption.AllDirectories))
                index[TelltaleCrc64.HashSymbol(Path.GetFileNameWithoutExtension(tex))] = tex;
        }
        return _textureIndex = index;
    }

    partial void OnSelectedFolderNodeChanged(FileNodeViewModel? value)
    {
        ExportDdsCommand.NotifyCanExecuteChanged();
        if (value is null || value.IsDirectory || !File.Exists(value.FullPath))
            return;

        var extension = Path.GetExtension(value.FullPath).ToLowerInvariant();
        if (extension == ".dds")
        {
            ShowPreview(value.FullPath);
            FolderInfo = PreviewStatus;
            return;
        }

        if (extension == ".d3dtx")
        {
            IsMeshPreview = false;
            ShowD3dtxPreview(value.FullPath);
            return;
        }

        if (extension == ".d3dmesh")
        {
            ShowD3dMeshPreview(value.FullPath);
            return;
        }

        IsMeshPreview = false;
        FolderInfo = DescribeFolderFile(value.FullPath);
    }

    /// <summary>True while the preview pane shows the 3D viewport instead of an image.</summary>
    [ObservableProperty]
    private bool _isMeshPreview;

    /// <summary>Per-batch breakdown of the previewed model (texture assignments etc.).</summary>
    public ObservableCollection<string> ModelDetails { get; } = new();

    /// <summary>Raised when a mesh and its per-batch diffuse/normal textures are ready to render.</summary>
    public event Action<MeshGeometry, IReadOnlyList<DdsImage?>, IReadOnlyList<DdsImage?>>? MeshPreviewReady;

    /// <summary>Loads a .d3dmesh into the 3D viewport, auto-matching per-batch textures.</summary>
    public void ShowD3dMeshPreview(string path)
    {
        try
        {
            if (!D3dMeshFile.TryLoad(File.ReadAllBytes(path), out var mesh, out var error))
            {
                IsMeshPreview = false;
                FolderInfo = $"{Path.GetFileName(path)} — {error}";
                return;
            }

            var geometry = mesh!.GetGeometry();

            // Each batch names its texture slot; resolve every slot's symbol against
            // the folder's files, decoding each distinct texture once.
            var textureIndex = GetTextureIndex(path);
            var imageBySlot = new Dictionary<int, DdsImage?>();
            var matchedNames = new List<string>();
            var nameByImage = new Dictionary<DdsImage, string>();
            DdsImage? ResolveSlot(int slot)
            {
                if (slot < 0 || slot >= mesh.TextureSymbols.Count)
                    return null;
                if (imageBySlot.TryGetValue(slot, out var cached))
                    return cached;
                DdsImage? image = null;
                if (textureIndex.TryGetValue(mesh.TextureSymbols[slot], out var texPath) &&
                    D3dtxTexture.TryLoad(File.ReadAllBytes(texPath), out var tex, out _) &&
                    tex!.TryToDds(out var dds, out _) &&
                    DdsImage.TryDecode(dds!, out var decoded, out _))
                {
                    image = decoded;
                    matchedNames.Add(Path.GetFileName(texPath));
                    nameByImage[decoded!] = Path.GetFileName(texPath);
                }
                return imageBySlot[slot] = image;
            }

            // Character meshes carry no mTextures; their diffuse lives in the embedded
            // material property sets. Scan each material's payload for symbols that
            // match folder textures and classify diffuse/normal maps by name.
            DdsImage? DecodeTexture(string? path, bool track)
            {
                if (path is null ||
                    !D3dtxTexture.TryLoad(File.ReadAllBytes(path), out var tex, out _) ||
                    !tex!.TryToDds(out var dds, out _) ||
                    !DdsImage.TryDecode(dds!, out var decoded, out _))
                    return null;
                if (track)
                    matchedNames.Add(Path.GetFileName(path));
                nameByImage[decoded!] = Path.GetFileName(path);
                return decoded;
            }

            var imageByMaterial = new Dictionary<int, (DdsImage? Diffuse, DdsImage? Normal)>();
            (DdsImage? Diffuse, DdsImage? Normal) ResolveMaterialMaps(int materialIndex)
            {
                if (materialIndex < 0 || materialIndex >= mesh.MaterialHandles.Count)
                    return (null, null);
                if (imageByMaterial.TryGetValue(materialIndex, out var cached))
                    return cached;
                (DdsImage?, DdsImage?) result = (null, null);
                ulong handle = mesh.MaterialHandles[materialIndex];
                foreach (var (nameSymbol, payload) in mesh.MaterialResources)
                {
                    if (nameSymbol != handle && mesh.MaterialResources.Count != 1)
                        continue;
                    result = (
                        DecodeTexture(MaterialTextureLocator.PickDiffuseTexture(payload, textureIndex), track: true),
                        DecodeTexture(MaterialTextureLocator.PickNormalMap(payload, textureIndex), track: false));
                    break;
                }
                return imageByMaterial[materialIndex] = result;
            }

            DdsImage? ResolveMaterial(int materialIndex) => ResolveMaterialMaps(materialIndex).Diffuse;

            var batchTextures = new DdsImage?[geometry.Batches.Count];
            var batchNormalMaps = new DdsImage?[geometry.Batches.Count];
            ModelDetails.Clear();
            for (int b = 0; b < geometry.Batches.Count; b++)
            {
                var batch = geometry.Batches[b];
                batchTextures[b] = ResolveSlot(batch.DiffuseTextureIndex)
                    ?? ResolveMaterial(batch.MaterialIndex)
                    // Untextured batch but the mesh has textures: fall back to slot 0
                    // so single-texture meshes with sparse indices still render textured.
                    ?? (batch.DiffuseTextureIndex < 0 ? ResolveSlot(0) : null);
                batchNormalMaps[b] = ResolveMaterialMaps(batch.MaterialIndex).Normal;

                var textureLabel = batchTextures[b] is { } img && nameByImage.TryGetValue(img, out var n)
                    ? n : "no texture matched";
                var normalLabel = batchNormalMaps[b] is { } nm && nameByImage.TryGetValue(nm, out var nn)
                    ? $" + {nn}" : "";
                ModelDetails.Add($"Batch {b}: {batch.PrimitiveCount:N0} tris · material {batch.MaterialIndex} · {textureLabel}{normalLabel}");
            }

            // For skinned meshes, name the skeleton whose joints cover the bone palette
            // and prime the playback state.
            StopAnimation();
            Animations.Clear();
            _playbackGeometry = null;
            _playbackSkeleton = null;
            _playbackSkinner = null;
            if (mesh.BoneSymbols.Count > 0)
            {
                var match = FindBestSkeleton(mesh.BoneSymbols);
                ModelDetails.Insert(0, match is { } m
                    ? $"Skeleton: {Path.GetFileName(m.Path)} · {m.Hit}/{mesh.BoneSymbols.Count} bones matched"
                    : $"Skeleton: none found for {mesh.BoneSymbols.Count} bones (open the folder containing data\\*.skl)");
                if (match is { } hit && geometry.IsSkinned &&
                    GetSkeletonIndex().FirstOrDefault(s => s.Path == hit.Path).Skeleton is { } skeleton)
                {
                    _playbackGeometry = geometry;
                    _playbackSkeleton = skeleton;
                    try { _playbackSkinner = new MeshSkinner(geometry, skeleton); }
                    catch (ArgumentException) { }

                    // Bones whose ancestry bypasses the pelvis (child 0 of the
                    // root) form IK/helper subtrees — legs and reference frames
                    // the engine poses via runtime IK, not via the animation's
                    // local values. Hold them at rest during playback.
                    _ikSubtreeBones = new HashSet<ulong>();
                    int pelvis = skeleton.Bones.Count > 1 && skeleton.Bones[1].ParentIndex == 0 ? 1 : -1;
                    for (int i = 0; i < skeleton.Bones.Count; i++)
                    {
                        if (pelvis < 0)
                            break;
                        int a = i;
                        bool underPelvis = i == 0 || i == pelvis;
                        while (a > 0)
                        {
                            if (a == pelvis) { underPelvis = true; break; }
                            a = skeleton.Bones[a].ParentIndex;
                        }
                        if (!underPelvis)
                            _ikSubtreeBones.Add(skeleton.Bones[i].JointSymbol);
                    }
                }
            }
            FindAnimationsCommand.NotifyCanExecuteChanged();
            PlayAnimationCommand.NotifyCanExecuteChanged();

            IsMeshPreview = true;
            MeshPreviewReady?.Invoke(geometry, batchTextures, batchNormalMaps);
            var textureNote = matchedNames.Count > 0
                ? $" · {matchedNames.Count} texture{(matchedNames.Count == 1 ? "" : "s")} matched"
                : " · no matching textures in folder";
            PreviewStatus = $"{mesh.Name} — {mesh.Describe()}{textureNote}";
            FolderInfo = PreviewStatus;
        }
        catch (Exception ex)
        {
            IsMeshPreview = false;
            FolderInfo = $"Could not preview mesh: {ex.Message}";
        }
    }

    /// <summary>Converts a .d3dtx in memory and shows it in the preview pane.</summary>
    private void ShowD3dtxPreview(string path)
    {
        try
        {
            if (!D3dtxTexture.TryLoad(File.ReadAllBytes(path), out var tex, out var loadError))
            {
                FolderInfo = $"{Path.GetFileName(path)} — {loadError}";
                return;
            }

            FolderInfo = $"{Path.GetFileName(path)} — {tex!.Describe()}";
            if (tex.TryToDds(out var dds, out var ddsError) &&
                DdsImage.TryDecode(dds!, out var image, out ddsError))
            {
                PreviewBitmap = DdsPreview.ToBitmap(image!);
                PreviewStatus = $"{tex.Name} — {tex.Describe()}";
            }
            else
            {
                PreviewBitmap = null;
                PreviewStatus = $"No preview: {ddsError}";
            }
        }
        catch (Exception ex)
        {
            FolderInfo = $"Could not read file: {ex.Message}";
        }
    }

    private bool CanExportDds() =>
        SelectedFolderNode is { IsDirectory: false } node &&
        node.FullPath.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanExportDds))]
    private async Task ExportDdsAsync()
    {
        if (Dialogs is null || SelectedFolderNode is null) return;
        var source = SelectedFolderNode.FullPath;
        try
        {
            if (!D3dtxTexture.TryLoad(File.ReadAllBytes(source), out var tex, out var error) ||
                !tex!.TryToDds(out var dds, out error))
            {
                AppendLog($"✗ Cannot convert {Path.GetFileName(source)}: {error}");
                return;
            }

            var target = await Dialogs.SaveFileAsync("Export as DDS",
                Path.GetFileNameWithoutExtension(source) + ".dds",
                ("DDS textures", new[] { "*.dds" }));
            if (target is null) return;

            await File.WriteAllBytesAsync(target, dds!);
            AppendLog($"✓ Exported {Path.GetFileName(target)} ({tex.Describe()}).");
        }
        catch (Exception ex)
        {
            AppendLog($"✗ Export failed: {ex.Message}");
        }
    }

    private List<(string Path, SkeletonFile Skeleton)>? _skeletonIndex;

    /// <summary>
    /// Files matching a pattern under the open folder; when the folder itself
    /// has none, its parent is searched too — extracted games often split
    /// meshes, skeletons, and animations into sibling folders.
    /// </summary>
    private IEnumerable<string> FindFolderAssets(string pattern)
    {
        if (FolderPath is null || !Directory.Exists(FolderPath))
            yield break;
        bool any = false;
        foreach (var p in Directory.EnumerateFiles(FolderPath, pattern, SearchOption.AllDirectories))
        {
            any = true;
            yield return p;
        }
        if (any || Path.GetDirectoryName(FolderPath.TrimEnd('\\', '/')) is not { } parent
                || !Directory.Exists(parent))
            yield break;
        foreach (var p in Directory.EnumerateFiles(parent, pattern, SearchOption.AllDirectories))
            yield return p;
    }

    /// <summary>All parseable .skl files under the open folder (cached per folder).</summary>
    private List<(string Path, SkeletonFile Skeleton)> GetSkeletonIndex()
    {
        if (_skeletonIndex is not null)
            return _skeletonIndex;
        var list = new List<(string, SkeletonFile)>();
        foreach (var p in FindFolderAssets("*.skl"))
        {
            if (SkeletonFile.TryLoad(File.ReadAllBytes(p), out var skl, out _))
                list.Add((p, skl!));
        }
        return _skeletonIndex = list;
    }

    /// <summary>Best skeleton for a bone set: the one containing the most of its bones.</summary>
    private (string Path, int Hit)? FindBestSkeleton(IReadOnlyList<ulong> boneSymbols)
    {
        (string Path, int Hit)? best = null;
        foreach (var (path, skeleton) in GetSkeletonIndex())
        {
            int hit = boneSymbols.Count(skeleton.JointSymbols.Contains);
            if (hit > 0 && (best is null || hit > best.Value.Hit))
                best = (path, hit);
        }
        return best;
    }

    // ---- Animation playback ---------------------------------------------

    /// <summary>A folder animation applicable to the previewed model's skeleton.</summary>
    public sealed record AnimationItem(string Path, string Name, float Length, int TrackCount)
    {
        public string Display => $"{Name} · {Length:0.#}s · {TrackCount} bone track{(TrackCount == 1 ? "" : "s")}";
    }

    public ObservableCollection<AnimationItem> Animations { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayAnimationCommand))]
    private AnimationItem? _selectedAnimation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayAnimationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopAnimationCommand))]
    private bool _isPlaying;

    /// <summary>Raised ~30×/s during playback with skinned positions/normals.</summary>
    public event Action<System.Numerics.Vector3[], System.Numerics.Vector3[]>? DeformedFrameReady;

    private MeshGeometry? _playbackGeometry;
    private SkeletonFile? _playbackSkeleton;
    private MeshSkinner? _playbackSkinner;
    private Dictionary<ulong, TransformTrack>? _playbackTracks;
    private CspkClip? _playbackCspk;
    private Dictionary<ulong, int>? _cspkBoneIndex;
    private float _playbackLength;
    private float _playbackTime;
    private Avalonia.Threading.DispatcherTimer? _playbackTimer;
    private System.Numerics.Vector3[]? _skinPositions, _skinNormals;

    private bool CanFindAnimations() => _playbackSkeleton is not null && FolderPath is not null;

    [RelayCommand(CanExecute = nameof(CanFindAnimations))]
    private async Task FindAnimationsAsync()
    {
        var skeleton = _playbackSkeleton!;
        Animations.Clear();
        AppendLog("Scanning folder for matching animations…");
        var found = await Task.Run(() =>
        {
            var items = new List<AnimationItem>();
            foreach (var path in FindFolderAssets("*.anm"))
            {
                try
                {
                    if (!AnimationFile.TryLoad(File.ReadAllBytes(path), out var anim, out _))
                        continue;
                    // Applicable when its keyframed tracks or compressed pose
                    // channel target this skeleton's bones.
                    int matching = anim!.TransformTracks.Count(t => skeleton.JointSymbols.Contains(t.BoneSymbol));
                    if (matching == 0 && anim.FindBoneSymbols(skeleton.JointSymbols).Count == 0)
                        continue;
                    items.Add(new AnimationItem(path, Path.GetFileNameWithoutExtension(path), anim.Length, matching));
                }
                catch (IOException) { }
            }
            return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });
        foreach (var item in found)
            Animations.Add(item);
        AppendLog($"{found.Count} animation(s) match the model's skeleton.");
    }

    private bool CanPlayAnimation() => SelectedAnimation is not null && _playbackSkinner is not null && !IsPlaying;

    [RelayCommand(CanExecute = nameof(CanPlayAnimation))]
    private void PlayAnimation()
    {
        if (SelectedAnimation is null || _playbackSkinner is null || _playbackSkeleton is null)
            return;
        if (!AnimationFile.TryLoad(File.ReadAllBytes(SelectedAnimation.Path), out var anim, out var error))
        {
            AppendLog($"✗ Cannot load animation: {error}");
            return;
        }

        _playbackTracks = anim!.TransformTracks
            .Where(t => _playbackSkeleton.JointSymbols.Contains(t.BoneSymbol))
            .GroupBy(t => t.BoneSymbol)
            .ToDictionary(g => g.Key, g => g.First());
        _playbackCspk = anim.PoseKeysPayload is { } blob
            ? CspkDecoder.TryDecodeFixedRate(blob, anim.Length)
            : null;
        _cspkBoneIndex = null;
        if (_playbackCspk is { } clip)
        {
            _cspkBoneIndex = new Dictionary<ulong, int>();
            for (int i = 0; i < clip.BoneSymbols.Count; i++)
                if (clip.BoneSymbols[i] != 0)
                    _cspkBoneIndex.TryAdd(clip.BoneSymbols[i], i);
        }
        _playbackLength = Math.Max(0.05f, anim.Length);
        _playbackTime = 0f;
        _skinPositions = new System.Numerics.Vector3[_playbackGeometry!.Positions.Length];
        _skinNormals = new System.Numerics.Vector3[_playbackGeometry.Positions.Length];

        _playbackTimer ??= new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _playbackTimer.Tick -= PlaybackTick;
        _playbackTimer.Tick += PlaybackTick;
        _playbackTimer.Start();
        IsPlaying = true;
        AppendLog(_playbackCspk is { } c
            ? $"▶ {SelectedAnimation.Name} ({_playbackTracks.Count} keyframed tracks + " +
              $"compressed pose: {c.BoneSymbols.Count} bones × {c.SampleCount} samples)."
            : $"▶ {SelectedAnimation.Name} ({_playbackTracks.Count} keyframed tracks; " +
              "compressed pose channel is variable-rate — those bones hold the bind pose).");
    }

    private void PlaybackTick(object? sender, EventArgs e)
    {
        if (_playbackSkinner is null || _playbackSkeleton is null || _playbackTracks is null)
            return;
        _playbackTime = (_playbackTime + 0.033f) % _playbackLength;

        var world = _playbackSkeleton.ComposeWorld(bone =>
        {
            if (_playbackTracks.TryGetValue(bone.JointSymbol, out var track))
            {
                var (rot, pos) = track.Sample(_playbackTime);
                return (pos, rot);
            }
            // Compressed pose rotations; translations stay at rest (the CSPK
            // translation stream's semantics are not decoded yet, and bone
            // offsets barely animate in practice).
            if (_cspkBoneIndex is not null
                && _cspkBoneIndex.TryGetValue(bone.JointSymbol, out int ci)
                && !_playbackCspk!.FrameBones.Contains(ci)
                && _ikSubtreeBones?.Contains(bone.JointSymbol) != true)
            {
                var rot = _playbackCspk.SampleRotation(ci, _playbackTime);
                if (_playbackCspk.IsAdditive)
                    rot = System.Numerics.Quaternion.Normalize(
                        System.Numerics.Quaternion.Multiply(bone.LocalRotation, rot));
                return (bone.LocalPosition, rot);
            }
            return (bone.LocalPosition, bone.LocalRotation);
        });
        _playbackSkinner.Skin(world, _skinPositions!, _skinNormals!);
        if (Environment.GetEnvironmentVariable("CSPK_DUMP") is { Length: > 0 } dumpPath
            && ++_tickCount == 30)
        {
            File.WriteAllLines(dumpPath, world.Select((m, i) =>
                $"{i} {m.Translation.X:0.000} {m.Translation.Y:0.000} {m.Translation.Z:0.000}")
                .Append($"t={_playbackTime:0.000}"));
        }
        DeformedFrameReady?.Invoke(_skinPositions!, _skinNormals!);
    }

    private int _tickCount;
    private HashSet<ulong>? _ikSubtreeBones;

    /// <summary>Debug entry: selects and plays an animation file directly.</summary>
    public void DebugPlayAnimation(string path)
    {
        if (!AnimationFile.TryLoad(File.ReadAllBytes(path), out var anim, out var error))
        {
            AppendLog($"✗ {error}");
            return;
        }
        SelectedAnimation = new AnimationItem(path, Path.GetFileNameWithoutExtension(path),
            anim!.Length, anim.TransformTracks.Count);
        PlayAnimation();
    }

    private bool CanStopAnimation() => IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStopAnimation))]
    private void StopAnimation()
    {
        _playbackTimer?.Stop();
        IsPlaying = false;
        if (_playbackGeometry is not null)
            DeformedFrameReady?.Invoke(_playbackGeometry.Positions, _playbackGeometry.Normals);
    }

    /// <summary>One-line description of a file: meta-stream summary for Telltale assets, size otherwise.</summary>
    internal static string DescribeFolderFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            using var stream = File.OpenRead(path);
            var head = new byte[Math.Min(info.Length, 64 * 1024)];
            stream.ReadExactly(head);

            if (MetaStreamHeader.TryParse(head, out var header, out _, isCompleteFile: head.Length == info.Length) &&
                (head.Length < info.Length || header!.TotalSize == info.Length))
            {
                return $"{info.Name} — {header!.Describe()}";
            }
            return $"{info.Name} — {info.Length:N0} bytes";
        }
        catch (Exception ex)
        {
            return $"Could not read file: {ex.Message}";
        }
    }

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
