using System.Collections.Concurrent;
using System.Numerics;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Scene;

/// <summary>What a scene agent is, judged by the property files it inherits from.</summary>
public enum SceneAgentKind
{
    /// <summary>Location geometry (<c>adv_*</c> meshes: walls, floors, terrain).</summary>
    Environment,

    /// <summary>A placed object with meshes (furniture, doors, vehicles…).</summary>
    Prop,

    /// <summary>A person or creature (inherits <c>character.prop</c>); placed by chores at run time.</summary>
    Character,

    /// <summary>Particle emitters and other effect cards.</summary>
    Effect,

    /// <summary>In-world interface pieces (buttons, text, markers).</summary>
    Interface,

    Camera,
    Light,
    Audio,

    /// <summary>A group node other agents are attached to.</summary>
    Group,

    /// <summary>Walk boxes, triggers, logic and anything else that does not render.</summary>
    Other,
}

/// <summary>A scene agent resolved through its property files: what it is, what it renders and where.</summary>
public sealed class SceneObject
{
    public required SceneAgent Agent { get; init; }

    public required SceneAgentKind Kind { get; init; }

    /// <summary>Every mesh the agent lists (alternates included), as file names; unresolved symbols are left out.</summary>
    public required IReadOnlyList<string> Meshes { get; init; }

    /// <summary>The meshes shown by default ("Mesh X - Visible" keys; meshes without a key are shown).</summary>
    public required IReadOnlyList<string> VisibleMeshes { get; init; }

    /// <summary>Mesh symbols that matched no file.</summary>
    public int UnresolvedMeshes { get; init; }

    /// <summary>The agent's skeleton file, if it has one.</summary>
    public string? Skeleton { get; init; }

    /// <summary>Property files the agent inherits from, nearest first.</summary>
    public required IReadOnlyList<string> PropertyFiles { get; init; }

    /// <summary>Render scale ("Render Global Scale" × "Render Axis Scale"), applied before the transform.</summary>
    public Vector3 RenderScale { get; init; } = Vector3.One;

    /// <summary>World transform (row-vector convention), composed through the attachment chain.</summary>
    public Matrix4x4 World { get; init; } = Matrix4x4.Identity;

    /// <summary>The agent's own property set followed by its property files, nearest first (for inherited lookups).</summary>
    public IReadOnlyList<ScenePropertySet> PropertyChain { get; init; } = Array.Empty<ScenePropertySet>();

    public string Name => Agent.Name;

    public bool HasMeshes => VisibleMeshes.Count > 0;

    /// <summary>True when the scene shows it at the start ("Scene: Start Visibility").</summary>
    public bool StartVisible => Agent.StartVisible;

    /// <summary>
    /// Shown when the environment is first assembled: visible environment and prop meshes.
    /// Characters (posed by chores at run time, so they stand at the origin), effects and
    /// interface pieces start hidden.
    /// </summary>
    public bool ShownByDefault => HasMeshes && StartVisible && Kind is SceneAgentKind.Environment or SceneAgentKind.Prop;

    public Vector3 WorldPosition => World.Translation;

    public override string ToString() => $"{Name} ({Kind})";
}

/// <summary>A scene with every agent resolved.</summary>
public sealed class SceneLayout
{
    public required SceneFile Scene { get; init; }

    /// <summary>One entry per agent, in the scene's order.</summary>
    public required IReadOnlyList<SceneObject> Objects { get; init; }

    public int MeshReferences => Objects.Sum(o => o.Meshes.Count + o.UnresolvedMeshes);

    public int ResolvedMeshReferences => Objects.Sum(o => o.Meshes.Count);
}

/// <summary>Progress of an environment build.</summary>
public readonly record struct SceneBuildProgress(string Message, int Done, int Total);

/// <summary>An environment assembled into one renderable model.</summary>
public sealed class AssembledScene
{
    /// <summary>
    /// The merged model. Its geometry's bounds are the framing bounds (outliers such as sky
    /// domes and far-off cards trimmed); see <see cref="FullBoundsMin"/> for everything.
    /// </summary>
    public required AssembledModel Model { get; init; }

    /// <summary>For each batch of the geometry, the index of the <see cref="SceneLayout.Objects"/> entry it came from.</summary>
    public required IReadOnlyList<int> BatchObjects { get; init; }

    /// <summary>The objects included, by layout index.</summary>
    public required IReadOnlyList<int> IncludedObjects { get; init; }

    public required Vector3 FullBoundsMin { get; init; }
    public required Vector3 FullBoundsMax { get; init; }

    /// <summary>Mesh instances placed (one mesh used by three agents counts three times).</summary>
    public int MeshInstances { get; init; }

    public int TriangleCount => Model.TriangleCount;

    public int TextureCount => Model.TexturesUsed.Count;

    /// <summary>Baked lightmaps (a second UV set and per-batch atlases); null when no placed mesh has one.</summary>
    public SceneLightmaps? Lightmaps { get; init; }
}

/// <summary>Options for <see cref="SceneAssembler"/>.</summary>
public sealed record SceneAssemblerOptions
{
    /// <summary>Textures larger than this (either side) are box-filtered down to fit, to bound memory. 0 keeps full size.</summary>
    public int MaxTextureSize { get; init; } = 1024;

    /// <summary>Decode normal maps too.</summary>
    public bool NormalMaps { get; init; } = true;

    /// <summary>How many meshes or textures to decode at once.</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>Read baked lightmaps (UVs and atlases) into <see cref="AssembledScene.Lightmaps"/>.</summary>
    public bool Lightmaps { get; init; } = true;
}

/// <summary>
/// Resolves a scene's agents through their property files (meshes, skeleton, render scale,
/// kind) and assembles the chosen ones into one model, each mesh placed with its agent's
/// world transform. Skinned meshes are placed in their bind pose. Decoded meshes, textures,
/// property files and skeletons are cached, so rebuilding with a different selection is
/// quick. Use one assembler from one thread at a time.
/// </summary>
public sealed class SceneAssembler
{
    private static readonly ulong KeyMeshList = TelltaleCrc64.HashSymbol("D3D Mesh List");
    private static readonly ulong KeyMesh = TelltaleCrc64.HashSymbol("D3D Mesh");
    private static readonly ulong KeySkeleton = TelltaleCrc64.HashSymbol("Skeleton File");
    private static readonly ulong KeyGlobalScale = TelltaleCrc64.HashSymbol("Render Global Scale");
    private static readonly ulong KeyAxisScale = TelltaleCrc64.HashSymbol("Render Axis Scale");
    private static readonly ulong BlendModeSymbol = TelltaleCrc64.HashSymbol("BlendMode");

    private readonly ISceneAssetResolver _assets;
    private readonly SceneAssemblerOptions _options;
    private readonly ConcurrentDictionary<ulong, (string? Name, ScenePropertySet? Set)> _props = new();
    private readonly ConcurrentDictionary<string, MeshPart?> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DdsImage?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DdsImage?> _opaqueTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<ulong, Matrix4x4>?> _skeletonNodes = new(StringComparer.OrdinalIgnoreCase);

    public SceneAssembler(ISceneAssetResolver assets, SceneAssemblerOptions? options = null)
    {
        _assets = assets;
        _options = options ?? new SceneAssemblerOptions();
    }

    // ---- Resolving agents ---------------------------------------------------

    /// <summary>Resolves every agent of <paramref name="scene"/> through its property files.</summary>
    public SceneLayout Describe(SceneFile scene)
    {
        var chains = scene.Agents.Select(PropertyChain).ToList();
        string? SkeletonOf(SceneAgent agent) =>
            FirstValue(chains[agent.Index], s => s.GetSymbol(KeySkeleton)) is { } symbol ? _assets.NameForSymbol(symbol) : null;

        // A node offset is the parent skeleton's bind-pose transform of the node the child rides on.
        Matrix4x4? NodeOffset(SceneAgent child) =>
            child.Parent is { } parent && SkeletonOf(parent) is { } skeleton &&
            SkeletonNodes(skeleton) is { } nodes && nodes.TryGetValue(child.ParentNode, out var node)
                ? node
                : null;

        var objects = new List<SceneObject>(scene.Agents.Count);
        foreach (var agent in scene.Agents)
        {
            var chain = chains[agent.Index];
            var files = chain.Skip(1).Select(c => c.Name!).ToList();
            var symbols = First(chain, s => s.GetSymbolList(KeyMeshList) is { Count: > 0 } list ? list : null)
                ?? (FirstValue(chain, s => s.GetSymbol(KeyMesh)) is { } single ? new[] { single } : Array.Empty<ulong>());
            var meshes = new List<string>();
            int unresolved = 0;
            foreach (var symbol in symbols.Distinct())
            {
                if (_assets.NameForSymbol(symbol) is { } name && name.EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase))
                    meshes.Add(name);
                else
                    unresolved++;
            }
            var visible = meshes.Where(m =>
            {
                var key = CharacterProp.VisibilityKey(m[..^".d3dmesh".Length]);
                return FirstValue(chain, s => s.GetBool(key)) ?? true;
            }).ToList();

            float globalScale = FirstValue(chain, s => s.GetFloat(KeyGlobalScale)) ?? 1f;
            var axisScale = FirstValue(chain, s => s.GetVector3(KeyAxisScale)) ?? Vector3.One;
            var scale = axisScale * globalScale;
            if (!(Math.Abs(scale.X) > 1e-4f && Math.Abs(scale.Y) > 1e-4f && Math.Abs(scale.Z) > 1e-4f))
                scale = Vector3.One;

            objects.Add(new SceneObject
            {
                Agent = agent,
                Kind = Classify(agent, files, meshes),
                Meshes = meshes,
                VisibleMeshes = visible,
                UnresolvedMeshes = unresolved,
                Skeleton = SkeletonOf(agent),
                PropertyFiles = files,
                RenderScale = scale,
                World = SceneFile.WorldTransform(agent, NodeOffset),
                PropertyChain = chain.Select(c => c.Set).ToList(),
            });
        }
        return new SceneLayout { Scene = scene, Objects = objects };
    }

    /// <summary>Judges what an agent is from its inherited property files and meshes.</summary>
    public static SceneAgentKind Classify(SceneAgent agent, IReadOnlyCollection<string> propertyFiles, IReadOnlyCollection<string> meshes)
    {
        bool Has(string file) => propertyFiles.Any(f => f.Equals(file, StringComparison.OrdinalIgnoreCase));
        bool Any(Func<string, bool> test) => propertyFiles.Any(f => test(f.ToLowerInvariant()));

        if (Has("obj_dummy.prop") || Has("dummy.prop"))
            return SceneAgentKind.Other;
        if (Has("scene.prop") || Has("module_scene.prop"))
            return SceneAgentKind.Other;
        if (Has("character.prop"))
            return SceneAgentKind.Character;
        if (Has("module_camera.prop"))
            return SceneAgentKind.Camera;
        if (Any(f => f.Contains("particle") || f.StartsWith("fx_", StringComparison.Ordinal)))
            return meshes.Count > 0 ? SceneAgentKind.Effect : SceneAgentKind.Other;
        if (Any(f => f.StartsWith("ui_", StringComparison.Ordinal) || f.StartsWith("module_text", StringComparison.Ordinal)))
            return SceneAgentKind.Interface;
        if (meshes.Count > 0 && (agent.Name.EndsWith("Glow", StringComparison.OrdinalIgnoreCase) ||
                                 meshes.All(m => Path.GetFileNameWithoutExtension(m).EndsWith("Glow", StringComparison.OrdinalIgnoreCase))))
            return SceneAgentKind.Effect;
        if (meshes.Count > 0)
        {
            bool environment = agent.Name.StartsWith("adv_", StringComparison.OrdinalIgnoreCase) ||
                               meshes.All(m => m.StartsWith("adv_", StringComparison.OrdinalIgnoreCase));
            return environment ? SceneAgentKind.Environment : SceneAgentKind.Prop;
        }
        if (Any(f => f.Contains("light") && !f.Contains("enlighten") && !f.Contains("lightprobe")))
            return SceneAgentKind.Light;
        if (Any(f => f.Contains("sound") || f.Contains("audio") || f.Contains("music")))
            return SceneAgentKind.Audio;
        if (Has("group.prop"))
            return SceneAgentKind.Group;
        return SceneAgentKind.Other;
    }

    /// <summary>The agent's own set followed by its property files, depth-first in inheritance order.</summary>
    private List<(string? Name, ScenePropertySet Set)> PropertyChain(SceneAgent agent)
    {
        var chain = new List<(string?, ScenePropertySet)> { (null, agent.Properties) };
        var seen = new HashSet<ulong>();
        void Visit(ScenePropertySet set, int depth)
        {
            if (depth > 16)
                return;
            foreach (var symbol in set.Parents)
            {
                if (!seen.Add(symbol))
                    continue;
                var (name, parent) = LoadProp(symbol);
                if (parent is null)
                    continue;
                chain.Add((name, parent));
                Visit(parent, depth + 1);
            }
        }
        Visit(agent.Properties, 0);
        return chain;
    }

    private (string? Name, ScenePropertySet? Set) LoadProp(ulong symbol) => _props.GetOrAdd(symbol, s =>
    {
        var name = _assets.NameForSymbol(s);
        if (name is null || !name.EndsWith(".prop", StringComparison.OrdinalIgnoreCase))
            return (name, null);
        try
        {
            return (name, _assets.TryRead(name) is { } bytes ? ScenePropertySet.TryParse(bytes) : null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (name, null);
        }
    });

    private static T? First<T>(List<(string? Name, ScenePropertySet Set)> chain, Func<ScenePropertySet, T?> get) where T : class
    {
        foreach (var (_, set) in chain)
            if (get(set) is { } value)
                return value;
        return null;
    }

    private static T? FirstValue<T>(List<(string? Name, ScenePropertySet Set)> chain, Func<ScenePropertySet, T?> get) where T : struct
    {
        foreach (var (_, set) in chain)
            if (get(set) is { } value)
                return value;
        return null;
    }

    /// <summary>Bind-pose model-space transform of every node of a skeleton, by joint symbol.</summary>
    private Dictionary<ulong, Matrix4x4>? SkeletonNodes(string skeletonName) => _skeletonNodes.GetOrAdd(skeletonName, name =>
    {
        if (_assets.TryRead(name) is not { } bytes || !SkeletonFile.TryLoad(bytes, out var skeleton, out _))
            return null;
        var world = skeleton!.BindPoseWorld();
        var nodes = new Dictionary<ulong, Matrix4x4>();
        for (int i = 0; i < skeleton.Bones.Count; i++)
            nodes.TryAdd(skeleton.Bones[i].JointSymbol, world[i]);
        return nodes;
    });

    // ---- Building -----------------------------------------------------------

    /// <summary>
    /// Assembles the objects <paramref name="include"/> selects (default:
    /// <see cref="SceneObject.ShownByDefault"/>) into one model.
    /// </summary>
    public AssembledScene Build(SceneLayout layout, Func<SceneObject, bool>? include = null,
        IProgress<SceneBuildProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        include ??= o => o.ShownByDefault;
        var chosen = Enumerable.Range(0, layout.Objects.Count)
            .Where(i => layout.Objects[i].HasMeshes && include(layout.Objects[i]))
            .ToList();
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelism),
            CancellationToken = cancellationToken,
        };

        // 1. Decode every distinct mesh.
        var meshNames = chosen.SelectMany(i => layout.Objects[i].VisibleMeshes)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int done = 0;
        progress?.Report(new SceneBuildProgress("Loading meshes", 0, meshNames.Count));
        Parallel.ForEach(meshNames, parallel, name =>
        {
            LoadPart(name);
            int n = Interlocked.Increment(ref done);
            progress?.Report(new SceneBuildProgress($"Loading meshes: {name}", n, meshNames.Count));
        });

        // 2. Decode every texture they use.
        var textureNames = meshNames.Select(n => _parts.GetValueOrDefault(n)).Where(p => p is not null)
            .SelectMany(p => p!.Diffuse.Concat(_options.NormalMaps ? p.Normal : Enumerable.Empty<string?>()))
            .Where(n => n is not null).Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        done = 0;
        progress?.Report(new SceneBuildProgress("Loading textures", 0, textureNames.Count));
        Parallel.ForEach(textureNames, parallel, name =>
        {
            DecodeTexture(name);
            int n = Interlocked.Increment(ref done);
            progress?.Report(new SceneBuildProgress($"Loading textures: {name}", n, textureNames.Count));
        });

        // 2b. Decode the lightmaps of meshes that carry lightmap UVs.
        if (_options.Lightmaps)
        {
            var lightmapNames = meshNames.Select(n => _parts.GetValueOrDefault(n))
                .Where(p => p?.LightmapUv is not null && p.Lightmap is not null).Select(p => p!.Lightmap!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Parallel.ForEach(lightmapNames, parallel, name => DecodeLightmap(name));
        }

        // 3. Place each mesh instance.
        progress?.Report(new SceneBuildProgress("Placing meshes", 0, chosen.Count));
        var parts = new List<MeshGeometry>();
        var batchObjects = new List<int>();
        var batchTextures = new List<DdsImage?>();
        var batchNormals = new List<DdsImage?>();
        var lightmapUvs = new List<Vector2[]?>();
        var batchLightmaps = new List<DdsImage?>();
        var details = new List<string>();
        var texturesUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<(string, string)>();
        int instances = 0;
        foreach (int index in chosen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var obj = layout.Objects[index];
            var transform = Matrix4x4.CreateScale(obj.RenderScale) * obj.World;
            foreach (var meshName in obj.VisibleMeshes)
            {
                var part = _parts.GetValueOrDefault(meshName);
                if (part is null || part.Geometry is null)
                {
                    failures.Add((meshName, part?.Error ?? "not found"));
                    details.Add($"{obj.Name}: {meshName}: {part?.Error ?? "not found"}");
                    continue;
                }
                var batches = part.KeptBatches(obj.Kind == SceneAgentKind.Character);
                if (batches.Count == 0)
                    continue;
                parts.Add(Transform(part.Geometry, batches.Select(b => part.Geometry.Batches[b]).ToList(), transform));
                var lightmap = _options.Lightmaps && part.LightmapUv is not null && part.Lightmap is { } lm ? DecodeLightmap(lm) : null;
                lightmapUvs.Add(lightmap is not null ? part.LightmapUv : null);
                foreach (int b in batches)
                {
                    batchObjects.Add(index);
                    var diffuse = part.Diffuse[b] is { } d ? DiffuseFor(d, part.Blended[b]) : null;
                    if (diffuse is not null)
                        texturesUsed.Add(part.Diffuse[b]!);
                    batchTextures.Add(diffuse);
                    batchNormals.Add(_options.NormalMaps && part.Normal[b] is { } nm ? _textures.GetValueOrDefault(nm) : null);
                    batchLightmaps.Add(lightmap);
                }
                instances++;
                details.Add($"{obj.Name}: {meshName}: {part.Description}");
            }
            progress?.Report(new SceneBuildProgress("Placing meshes", parts.Count, chosen.Count));
        }

        if (parts.Count == 0)
            throw new InvalidDataException(failures.Count > 0
                ? $"No mesh could be loaded: {string.Join("; ", failures.Take(5).Select(f => $"{f.Item1}: {f.Item2}"))}"
                : "The scene has no visible meshes.");

        progress?.Report(new SceneBuildProgress("Merging", 0, 1));
        var merged = MeshGeometry.Combine(parts);
        var (focusMin, focusMax) = FocusBounds(merged.Positions);
        var geometry = merged with { BoundsMin = focusMin, BoundsMax = focusMax };

        return new AssembledScene
        {
            Model = new AssembledModel
            {
                Geometry = geometry,
                BatchTextures = batchTextures,
                BatchNormalMaps = batchNormals,
                Details = details,
                TexturesUsed = texturesUsed.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                Failures = failures,
                BoneSymbols = Array.Empty<ulong>(),
            },
            BatchObjects = batchObjects,
            IncludedObjects = chosen,
            FullBoundsMin = merged.BoundsMin,
            FullBoundsMax = merged.BoundsMax,
            MeshInstances = instances,
            Lightmaps = batchLightmaps.Any(l => l is not null) ? new SceneLightmaps
            {
                Uv2 = parts.Zip(lightmapUvs, (p, uv) => uv ?? new Vector2[p.Positions.Length]).SelectMany(uv => uv).ToArray(),
                BatchLightmaps = batchLightmaps,
                Intensity = LightmapIntensity(layout),
            } : null,
        };
    }

    /// <summary>
    /// Bounds that frame the bulk of the scene: the 1st-99th percentile of vertex positions
    /// on each axis, widened a little, so a sky dome or a far-off card does not shrink the view.
    /// </summary>
    public static (Vector3 Min, Vector3 Max) FocusBounds(IReadOnlyList<Vector3> positions)
    {
        if (positions.Count == 0)
            return (Vector3.Zero, Vector3.Zero);
        int step = Math.Max(1, positions.Count / 200_000);
        int n = (positions.Count + step - 1) / step;
        var xs = new float[n];
        var ys = new float[n];
        var zs = new float[n];
        for (int i = 0, k = 0; i < positions.Count && k < n; i += step, k++)
        {
            xs[k] = positions[i].X;
            ys[k] = positions[i].Y;
            zs[k] = positions[i].Z;
        }
        Array.Sort(xs);
        Array.Sort(ys);
        Array.Sort(zs);
        int lo = (int)(n * 0.01), hi = Math.Max(lo, (int)(n * 0.99) - 1);
        var min = new Vector3(xs[lo], ys[lo], zs[lo]);
        var max = new Vector3(xs[hi], ys[hi], zs[hi]);
        var pad = (max - min) * 0.05f;
        return (min - pad, max + pad);
    }

    /// <summary>Copies the given batches' vertices through a transform (skinning data dropped).</summary>
    private static MeshGeometry Transform(MeshGeometry source, IReadOnlyList<MeshBatch> batches, Matrix4x4 transform)
    {
        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        int count = source.Positions.Length;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var tangents = new Vector4[count];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            var p = Vector3.Transform(source.Positions[i], transform);
            positions[i] = p;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            var n = Vector3.TransformNormal(source.Normals[i], normalMatrix);
            float nl = n.Length();
            normals[i] = nl > 1e-8f ? n / nl : Vector3.Zero;
            var t = source.Tangents[i];
            var tt = Vector3.TransformNormal(new Vector3(t.X, t.Y, t.Z), transform);
            float tl = tt.Length();
            tangents[i] = tl > 1e-8f ? new Vector4(tt / tl, t.W) : Vector4.Zero;
        }
        if (count == 0)
            min = max = Vector3.Zero;
        return new MeshGeometry(positions, normals, source.TexCoords, tangents, source.Indices, batches, min, max);
    }

    // ---- Meshes and textures -------------------------------------------------

    /// <summary>A decoded mesh with each batch's chosen textures.</summary>
    private sealed class MeshPart
    {
        public MeshGeometry? Geometry { get; init; }
        public string? Error { get; init; }
        public string Description { get; init; } = "";
        public string?[] Diffuse { get; init; } = Array.Empty<string?>();
        public string?[] Normal { get; init; } = Array.Empty<string?>();

        /// <summary>Batches that are clear overlays (eye wetness, cornea), hidden on characters.</summary>
        public bool[] Overlay { get; init; } = Array.Empty<bool>();

        /// <summary>Batches whose material sets a blend mode (its colour texture's alpha is opacity).</summary>
        public bool[] Blended { get; init; } = Array.Empty<bool>();

        /// <summary>Batches drawn with a light-card texture (glows, beams), additive in the game, so left out.</summary>
        public bool[] LightCard { get; init; } = Array.Empty<bool>();

        /// <summary>The baked lightmap atlas listed in the mesh's texture slots, if any.</summary>
        public string? Lightmap { get; init; }

        /// <summary>Lightmap UVs, one per vertex; null when the mesh has none.</summary>
        public Vector2[]? LightmapUv { get; init; }

        public List<int> KeptBatches(bool hideOverlays) =>
            Enumerable.Range(0, Geometry?.Batches.Count ?? 0).Where(b => !LightCard[b] && (!hideOverlays || !Overlay[b])).ToList();
    }

    private MeshPart? LoadPart(string name) => _parts.GetOrAdd(name, n =>
    {
        byte[]? bytes;
        try
        {
            bytes = _assets.TryRead(n);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new MeshPart { Error = ex.Message };
        }
        if (bytes is null)
            return new MeshPart { Error = "file not found" };
        if (!D3dMeshFile.TryLoad(bytes, out var mesh, out var error))
            return new MeshPart { Error = error };
        MeshGeometry geometry;
        try
        {
            geometry = mesh!.GetGeometry();
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return new MeshPart { Error = ex.Message };
        }

        Func<ulong, string?> lookup = _assets.TextureNameForSymbol;
        var payloads = mesh.MaterialHandles
            .Select(h => mesh.MaterialResources.FirstOrDefault(r => r.NameSymbol == h).Payload)
            .ToList();
        var diffuseByMaterial = payloads
            .Select(p => p is null ? null : MaterialTextureLocator.PickDiffuseTexture(p, lookup))
            .ToList();
        bool HasColor(string? diffuse) =>
            diffuse is not null && !MaterialTextureLocator.IsNonDiffuseMap(diffuse) && !ModelAssembler.IsClearEyeLayer(diffuse);
        bool anyColored = diffuseByMaterial.Any(HasColor);
        // Baked environments list their lightmap atlas (laid out on a second UV set) in the
        // mesh's texture slots; the colour texture is then only in the material.
        string? Slot(int slot) =>
            slot >= 0 && slot < mesh.TextureSymbols.Count && lookup(mesh.TextureSymbols[slot]) is { } texture &&
            !IsLightmap(texture, n)
                ? texture
                : null;

        int count = geometry.Batches.Count;
        var diffuse = new string?[count];
        var normal = new string?[count];
        var overlay = new bool[count];
        var blended = new bool[count];
        var lightCard = new bool[count];
        for (int b = 0; b < count; b++)
        {
            var batch = geometry.Batches[b];
            int material = batch.MaterialIndex;
            bool hasPayload = material >= 0 && material < payloads.Count && payloads[material] is not null;
            overlay[b] = hasPayload && !HasColor(diffuseByMaterial[material]) && anyColored;
            blended[b] = hasPayload && ContainsSymbol(payloads[material]!, BlendModeSymbol);
            diffuse[b] = Slot(batch.DiffuseTextureIndex)
                ?? (hasPayload ? diffuseByMaterial[material] : null)
                ?? (batch.DiffuseTextureIndex < 0 ? Slot(0) : null);
            if (diffuse[b] is { } d && ModelAssembler.IsClearEyeLayer(d))
                overlay[b] = true;
            lightCard[b] = diffuse[b] is { } card && IsLightCard(card, batch.PrimitiveCount);
            normal[b] = hasPayload ? MaterialTextureLocator.PickNormalMap(payloads[material]!, lookup) : null;
        }
        return new MeshPart
        {
            Geometry = geometry with { BlendWeights = null, BlendIndices = null, BonePalettes = null },
            Description = mesh.Describe(),
            Lightmap = mesh.TextureSymbols.Select(lookup).FirstOrDefault(t => t is not null && IsLightmap(t, n)),
            LightmapUv = SceneLightmapReader.ReadUvs(bytes, mesh),
            Diffuse = diffuse,
            Normal = normal,
            Overlay = overlay,
            Blended = blended,
            LightCard = lightCard,
        };
    });

    /// <summary>
    /// The colour texture for a batch. Without a blend mode in its material, a texture whose
    /// alpha is mostly partial (a specular or gloss mask, not a cut-out) is drawn opaque, so
    /// doors and walls do not turn see-through; glass keeps its alpha.
    /// </summary>
    private DdsImage? DiffuseFor(string name, bool blended)
    {
        var found = _textures.GetValueOrDefault(name);
        if (found is not { } image || blended || name.Contains("glass", StringComparison.OrdinalIgnoreCase))
            return found;
        return _opaqueTextures.GetOrAdd(name, key =>
        {
            var rgba = image.Rgba;
            int partial = 0, total = rgba.Length / 4;
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] is >= 16 and <= 240)
                    partial++;
            if (partial < total * 0.15)
                return image;
            var opaque = (byte[])rgba.Clone();
            for (int i = 3; i < opaque.Length; i += 4)
                opaque[i] = 255;
            return DdsImage.TryDecode(WrapRgba(opaque, image.Width, image.Height), out var result, out _) && result is not null ? result : image;
        });
    }

    private static bool ContainsSymbol(byte[] payload, ulong symbol)
    {
        Span<byte> pattern = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(pattern, symbol);
        return payload.AsSpan().IndexOf(pattern) >= 0;
    }

    /// <summary>
    /// True for textures of light cards (glows, beams and light quads such as
    /// <c>env_arenaUnderOrangeLightA</c> on a few quads), which the game draws additively; drawn
    /// opaque they would cover the room.
    /// </summary>
    public static bool IsLightCard(string textureName, int triangles)
    {
        var stem = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        if (stem.Contains("glow") || stem.Contains("lightbeam") || stem.Contains("lightshaft") ||
            stem.Contains("lightray") || stem.Contains("godray") || stem.Contains("lightcone") || stem.Contains("lensflare"))
            return true;
        // "...OrangeLight", "...LightA": a texture named for the light itself, on a few quads
        // (a lamp post textured "streetLight" is real geometry).
        if (triangles > 16)
            return false;
        int at = stem.LastIndexOf("light", StringComparison.Ordinal);
        return at > 0 && stem.Length - (at + 5) <= 1 && stem[(at + 5)..].All(char.IsLetter);
    }

    /// <summary>
    /// True for a baked lightmap: named after its mesh with a numeric suffix
    /// (<c>adv_arenaHangar_meshesA_000</c> for <c>adv_arenaHangar_meshesA</c>) or marked as one.
    /// </summary>
    public static bool IsLightmap(string textureName, string meshName)
    {
        var stem = Path.GetFileNameWithoutExtension(textureName);
        var meshStem = Path.GetFileNameWithoutExtension(meshName);
        if (stem.Length > meshStem.Length + 1 && stem.StartsWith(meshStem, StringComparison.OrdinalIgnoreCase) &&
            stem[meshStem.Length] == '_' && stem[(meshStem.Length + 1)..].All(char.IsDigit))
            return true;
        return stem.Contains("lightmap", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith("_lm", StringComparison.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<string, DdsImage?> _lightmaps = new(StringComparer.OrdinalIgnoreCase);

    private DdsImage? DecodeLightmap(string name) => _lightmaps.GetOrAdd(name, n =>
    {
        try
        {
            return _assets.TryRead(n) is { } bytes ? SceneLightmapReader.Decode(bytes, _options.MaxTextureSize) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    });

    /// <summary>The scene's "HDR lightmaps Intensity" (1 when unset).</summary>
    private static float LightmapIntensity(SceneLayout layout)
    {
        var scene = layout.Objects.FirstOrDefault(o => o.Name.Equals(layout.Scene.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var set in scene?.PropertyChain ?? Array.Empty<ScenePropertySet>())
            if (set.GetFloat(SceneLightKeys.HdrLightmapsIntensity) is { } value)
                return value;
        return 1;
    }

    private DdsImage? DecodeTexture(string name) => _textures.GetOrAdd(name, n =>
    {
        try
        {
            if (_assets.TryRead(n) is not { } bytes ||
                !D3dtxTexture.TryLoad(bytes, out var texture, out _) ||
                !texture!.TryToDds(out var dds, out _) ||
                !DdsImage.TryDecode(dds!, out var image, out _))
                return null;
            var rgba = image!.Rgba;
            int width = image.Width, height = image.Height;
            // Alpha-only textures sample as black with the value as alpha.
            if (texture.SurfaceFormat == T3SurfaceFormat.A8)
            {
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    rgba[i + 3] = rgba[i];
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = 0;
                }
            }
            int cap = _options.MaxTextureSize;
            if (cap <= 0 || (width <= cap && height <= cap))
                return image;
            while (width > cap || height > cap)
                (rgba, width, height) = HalveImage(rgba, width, height);
            return DdsImage.TryDecode(WrapRgba(rgba, width, height), out var smaller, out _) ? smaller : image;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    });

    /// <summary>Box-filters an RGBA image to half size (a side of 1 stays 1).</summary>
    private static (byte[] Rgba, int Width, int Height) HalveImage(byte[] rgba, int width, int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Min(height - 1, y * 2), y1 = Math.Min(height - 1, y * 2 + 1);
            for (int x = 0; x < w; x++)
            {
                int x0 = Math.Min(width - 1, x * 2), x1 = Math.Min(width - 1, x * 2 + 1);
                for (int c = 0; c < 4; c++)
                {
                    int sum = rgba[(y0 * width + x0) * 4 + c] + rgba[(y0 * width + x1) * 4 + c] +
                              rgba[(y1 * width + x0) * 4 + c] + rgba[(y1 * width + x1) * 4 + c];
                    result[(y * w + x) * 4 + c] = (byte)((sum + 2) / 4);
                }
            }
        }
        return (result, w, h);
    }

    /// <summary>Wraps RGBA pixels in an uncompressed 32-bit DDS so they can be decoded into a <see cref="DdsImage"/>.</summary>
    private static byte[] WrapRgba(byte[] rgba, int width, int height)
    {
        var dds = new byte[128 + rgba.Length];
        void Put(int at, uint value) => BitConverter.TryWriteBytes(dds.AsSpan(at), value);
        Put(0, 0x20534444);          // "DDS "
        Put(4, 124);                 // header size
        Put(8, 0x100F);              // caps | height | width | pitch | pixel format
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(20, (uint)(width * 4));
        Put(76, 32);                 // pixel format size
        Put(80, 0x41);               // RGB | alpha pixels
        Put(88, 32);
        Put(92, 0x000000FF);
        Put(96, 0x0000FF00);
        Put(100, 0x00FF0000);
        Put(104, 0xFF000000);
        Put(108, 0x1000);            // texture
        rgba.CopyTo(dds, 128);
        return dds;
    }
}
