using System.Numerics;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Export;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Scene;

/// <summary>Where a scene's files come from: a game catalog or a loose folder of extracted files.</summary>
public interface IAssetResolver
{
    /// <summary>Reads a file by its name, or null when absent.</summary>
    byte[]? TryRead(string fileName);

    /// <summary>
    /// The texture file a symbol refers to, or null when unknown. The Definitive Series hashes
    /// names with the extension, older games without.
    /// </summary>
    string? TextureNameForSymbol(ulong symbol);

    /// <summary>The file a symbol refers to, or null when unknown.</summary>
    string? NameForSymbol(ulong symbol) => null;

    /// <summary>Names of every skeleton (.skl) available.</summary>
    IEnumerable<string> SkeletonNames { get; }

    /// <summary>Names of the meshes whose names start with <paramref name="prefix"/>.</summary>
    IEnumerable<string> MeshNamesStartingWith(string prefix) => Array.Empty<string>();
}

/// <summary>A model assembled from one or more meshes, ready to render.</summary>
public sealed class AssembledModel
{
    public required MeshGeometry Geometry { get; init; }

    /// <summary>Diffuse texture per batch of <see cref="Geometry"/> (null where none matched).</summary>
    public required IReadOnlyList<DdsImage?> BatchTextures { get; init; }

    /// <summary>Normal map per batch (null where none matched or it could not be decoded).</summary>
    public required IReadOnlyList<DdsImage?> BatchNormalMaps { get; init; }

    /// <summary>Main-pass blend mode per batch (null where the mesh does not say).</summary>
    public IReadOnlyList<MaterialBlendMode?> BatchBlendModes { get; init; } = Array.Empty<MaterialBlendMode?>();

    /// <summary>Specular map per batch (null where none). <see cref="BatchMaterialParams"/> says how to read it.</summary>
    public IReadOnlyList<DdsImage?> BatchSpecularMaps { get; init; } = Array.Empty<DdsImage?>();

    /// <summary>Ink or line-art texture per batch (null where none). <see cref="BatchMaterialParams"/> says how it applies.</summary>
    public IReadOnlyList<DdsImage?> BatchInkTextures { get; init; } = Array.Empty<DdsImage?>();

    /// <summary>Per-vertex texture coordinates for the ink textures, or null when not assembled.</summary>
    public Vector2[]? InkTexCoords { get; init; }

    /// <summary>Shading values per batch from its material (null where unknown; may be empty).</summary>
    public IReadOnlyList<MaterialShading?> BatchMaterialParams { get; init; } = Array.Empty<MaterialShading?>();

    /// <summary>Emissive texture per batch (null where none glows). Empty when nothing glows.</summary>
    public IReadOnlyList<DdsImage?> BatchEmissiveMaps { get; init; } = Array.Empty<DdsImage?>();

    /// <summary>The agent <c>.prop</c> whose render settings were applied, or null when none was found.</summary>
    public string? AgentProp { get; init; }

    /// <summary>The render settings of <see cref="AgentProp"/> (already applied to the batches).</summary>
    public AgentRenderSettings Agent { get; init; } = AgentRenderSettings.Default;

    /// <summary>Human-readable breakdown: one line per mesh and batch.</summary>
    public required IReadOnlyList<string> Details { get; init; }

    /// <summary>Texture files that were matched and decoded.</summary>
    public required IReadOnlyList<string> TexturesUsed { get; init; }

    /// <summary>Meshes that could not be loaded, with the reason.</summary>
    public required IReadOnlyList<(string Mesh, string Error)> Failures { get; init; }

    /// <summary>Union of the bones the meshes are skinned to.</summary>
    public required IReadOnlyList<ulong> BoneSymbols { get; init; }

    /// <summary>The skeleton the model is bound to, when one was found.</summary>
    public SkeletonFile? Skeleton { get; init; }

    public string? SkeletonName { get; init; }

    /// <summary>How many of <see cref="BoneSymbols"/> the skeleton contains.</summary>
    public int SkeletonBonesMatched { get; init; }

    public int TriangleCount => Geometry.Batches.Sum(b => b.PrimitiveCount);

    /// <summary>True when the model can be animated with <see cref="AnimationPlayer"/>.</summary>
    public bool CanAnimate => Skeleton is not null && Geometry.IsSkinned && Geometry.BonePalettes is not null;
}

/// <summary>
/// Builds renderable models from meshes, matching textures and merging the parts into one
/// geometry. Decoded assets are cached within a memory budget. Thread-safe: calls run one at a time.
/// </summary>
public sealed class ModelAssembler
{
    private readonly IAssetResolver _assets;
    private readonly object _gate = new();
    private readonly SizeBoundedCache<string, (D3dMeshFile? Mesh, string? Error)> _meshes;
    private readonly SizeBoundedCache<string, DdsImage?> _textures;
    private readonly Dictionary<string, SkeletonFile?> _skeletons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, MaterialData?> _materialFiles = new();

    /// <summary>The default budget for decoded meshes and textures, based on this machine's memory.</summary>
    public static long DefaultCacheBytes { get; } = CacheBudget.For(1 / 16.0, 256L << 20, 1L << 30);

    /// <param name="assets">Where meshes, textures and skeletons are read from.</param>
    /// <param name="cacheBytes">Budget for decoded meshes and textures (default <see cref="DefaultCacheBytes"/>); textures get three quarters of it.</param>
    public ModelAssembler(IAssetResolver assets, long? cacheBytes = null)
    {
        _assets = assets;
        long budget = cacheBytes ?? DefaultCacheBytes;
        _textures = new(budget / 4 * 3, image => image!.Rgba.LongLength + 64, StringComparer.OrdinalIgnoreCase);
        _meshes = new(budget / 4, entry => (entry.Mesh?.DataSize ?? 0) + 256, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assembles the named meshes into one model. <paramref name="preferredSkeleton"/> is used
    /// when it fits, else the skeleton covering the most bones. The agent's render settings from
    /// <paramref name="agentProp"/> are applied over the materials.
    /// </summary>
    public AssembledModel Build(IReadOnlyList<string> meshNames, string? preferredSkeleton = null, string? agentProp = null)
    {
        lock (_gate)
            return BuildLocked(meshNames, preferredSkeleton, agentProp);
    }

    private AssembledModel BuildLocked(IReadOnlyList<string> meshNames, string? preferredSkeleton, string? agentPropName)
    {
        var (agentProp, agent) = FindAgent(meshNames, preferredSkeleton, agentPropName);
        var agentTint = agent.LinearTint;
        var agentGlow = agent.LinearGlow;
        var batchEmissive = new List<DdsImage?>();
        var parts = new List<MeshGeometry>();
        var batchTextures = new List<DdsImage?>();
        var batchNormalMaps = new List<DdsImage?>();
        var batchBlendModes = new List<MaterialBlendMode?>();
        var batchSpecular = new List<DdsImage?>();
        var batchInk = new List<DdsImage?>();
        var batchParams = new List<MaterialShading?>();
        var inkUvs = new List<Vector2[]>();
        var details = new List<string>();
        var texturesUsed = new List<string>();
        var failures = new List<(string, string)>();
        var loaded = new List<D3dMeshFile>();

        Func<ulong, string?> textureLookup = _assets.TextureNameForSymbol;

        foreach (var name in meshNames)
        {
            var (mesh, error) = LoadMesh(name);
            if (mesh is null)
            {
                failures.Add((name, error ?? "not found"));
                details.Add($"{name}: {error}");
                continue;
            }
            MeshGeometry part;
            try
            {
                part = mesh.GetGeometry();
            }
            catch (InvalidDataException ex)
            {
                failures.Add((name, ex.Message));
                details.Add($"{name}: {ex.Message}");
                continue;
            }
            loaded.Add(mesh);

            var payloadByMaterial = mesh.MaterialHandles
                .Select(h => mesh.MaterialResources.FirstOrDefault(r => r.NameSymbol == h).Payload)
                .ToList();
            var dataByMaterial = payloadByMaterial
                .Select(p => p is null ? null : MaterialData.TryParse(p))
                .ToList();
            var inheritedByMaterial = dataByMaterial
                .Select(d => d is null ? null : InheritedDiffuse(d, textureLookup))
                .ToList();
            var diffuseByMaterial = payloadByMaterial
                .Select((p, i) => p is null ? null
                    : inheritedByMaterial[i] is { } inherited ? inherited
                    : dataByMaterial[i] is { } data ? MaterialTextureLocator.PickDiffuseTexture(data, p, textureLookup)
                    : MaterialTextureLocator.PickDiffuseTexture(p, textureLookup))
                .ToList();
            // Sometimes only a diffuse named after the mesh ships. Used only when nothing else matched.
            if (diffuseByMaterial.All(d => d is null) && TextureNamedAfter(mesh.Name, textureLookup) is { } named)
            {
                for (int i = 0; i < diffuseByMaterial.Count; i++)
                    if (dataByMaterial[i] is { } d && MaterialTextureLocator.HasDiffuseProperty(d))
                        diffuseByMaterial[i] = named;
            }
            // A model whose textures never shipped borrows them from a variant with the same UV layout.
            BorrowedTextures? borrowed = null;
            if (diffuseByMaterial.All(d => d is null) &&
                dataByMaterial.Any(d => d is not null && MaterialTextureLocator.HasDiffuseProperty(d)))
            {
                borrowed = BorrowFromVariant(mesh, part, textureLookup);
                if (borrowed is not null)
                    foreach (var (i, diffuse) in borrowed.Diffuse)
                        diffuseByMaterial[i] = diffuse;
            }
            // A real color texture, not just maps. The black eye "glass" layer counts as none.
            bool HasColor(string? diffuse) =>
                diffuse is not null && !MaterialTextureLocator.IsNonDiffuseMap(diffuse) && !IsClearEyeLayer(diffuse);
            bool anyColoredMaterial = diffuseByMaterial.Any(HasColor);
            // Materials that bind the engine's diffuse property, so their textures are known.
            var engineDiffuse = dataByMaterial
                .Select((d, i) => d is not null && (MaterialTextureLocator.HasDiffuseProperty(d) || inheritedByMaterial[i] is not null))
                .ToList();
            bool anyEngineDiffuse = engineDiffuse.Any(x => x);

            DdsImage? Texture(string? fileName)
            {
                var image = DecodeTexture(fileName);
                if (image is not null && !texturesUsed.Contains(fileName!, StringComparer.OrdinalIgnoreCase))
                    texturesUsed.Add(fileName!);
                return image;
            }
            string? SlotTexture(int slot) =>
                slot >= 0 && slot < mesh.TextureSymbols.Count ? textureLookup(mesh.TextureSymbols[slot]) : null;

            details.Add($"{mesh.Name}: {mesh.Describe()}");
            if (borrowed is not null)
                details.Add($"  the game ships none of its textures; borrowed from {borrowed.From}, built on the same UV layout");
            Vector4[]? vertexColors = null;
            bool vertexColorsRead = false;
            Vector4[]? VertexColors()
            {
                if (!vertexColorsRead)
                {
                    vertexColorsRead = true;
                    try
                    {
                        vertexColors = mesh.GetVertexColors(0) is { } colors && colors.Length == part.Positions.Length ? colors : null;
                    }
                    catch (InvalidDataException)
                    {
                    }
                }
                return vertexColors;
            }
            var kept = new List<MeshBatch>();
            for (int b = 0; b < part.Batches.Count; b++)
            {
                var batch = part.Batches[b];
                int material = batch.MaterialIndex;
                bool hasPayload = material >= 0 && material < payloadByMaterial.Count && payloadByMaterial[material] is not null;
                var data = hasPayload ? dataByMaterial[material] : null;

                // A material without a diffuse property is an overlay layer. Masked color
                // effects are drawn; anything else would hide what is underneath.
                if (data is not null && !engineDiffuse[material] && (anyEngineDiffuse || data.IsBlended) &&
                    batch.DiffuseTextureIndex < 0)
                {
                    if (TintedMaskLayer(data, textureLookup) is { } layer)
                    {
                        kept.Add(batch);
                        batchTextures.Add(layer.Image);
                        batchNormalMaps.Add(null);
                        batchBlendModes.Add(data.BlendMode);
                        batchSpecular.Add(null);
                        batchInk.Add(null);
                        batchParams.Add(null);
                        batchEmissive.Add(null);
                        details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · effect layer: {layer.Name}");
                        continue;
                    }
                    details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · hidden (texture-less overlay: material has no diffuse texture)");
                    continue;
                }

                // No color texture while its siblings have one: a clear overlay shell, so skip it.
                if (hasPayload && !engineDiffuse[material] && !HasColor(diffuseByMaterial[material]) && anyColoredMaterial)
                {
                    details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · hidden (texture-less overlay, e.g. eye wetness)");
                    continue;
                }

                string? diffuseName = SlotTexture(batch.DiffuseTextureIndex)
                    ?? (hasPayload ? diffuseByMaterial[material] : null)
                    // Fall back to slot 0 for single-texture meshes with sparse indices.
                    ?? (batch.DiffuseTextureIndex < 0 ? SlotTexture(0) : null);
                if (diffuseName is not null && IsClearEyeLayer(diffuseName))
                {
                    details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · hidden (clear eye layer, {diffuseName})");
                    continue;
                }
                float opacity = (data is null ? 1f : BatchOpacity(mesh, part, batch, data, VertexColors)) * agent.Opacity;
                if (opacity < 1f / 255f)
                {
                    details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · hidden (fully transparent, {diffuseName ?? "no texture"})");
                    continue;
                }
                kept.Add(batch);
                string? normalName = !hasPayload ? null
                    : data is not null ? MaterialTextureLocator.PickNormalMap(data, payloadByMaterial[material]!, textureLookup)
                    : MaterialTextureLocator.PickNormalMap(payloadByMaterial[material]!, textureLookup);
                normalName ??= borrowed?.Normal.GetValueOrDefault(material);
                var shading = data is not null ? MaterialShading.FromMaterial(data) : MaterialShading.Default;
                string? specularName = data is not null ? MaterialTextureLocator.PickSpecularMap(data, textureLookup) : null;
                var ink = data is not null ? MaterialTextureLocator.PickInkTexture(data, textureLookup) : null;
                if (mesh.MaterialHandles.Count == 0 && diffuseName is not null)
                {
                    // Older meshes carry no material data: their maps are named after the color texture.
                    var (legacyNormal, legacyLines) = MaterialTextureLocator.PickLegacyMaps(diffuseName,
                        mesh.TextureSymbols.Select(textureLookup).OfType<string>());
                    normalName ??= legacyNormal;
                    if (legacyLines is not null)
                        // 8-bit alpha means drawn lines; anything else is shadow ink.
                        ink = (legacyLines, DecodeTexture(legacyLines) is not null && _alphaTextures.Contains(legacyLines)
                            ? InkMapKind.LineArt : InkMapKind.Shadow);
                }
                var specular = DecodeTexture(specularName);
                var inkImage = ink is { } inkMap ? DecodeTexture(inkMap.Name) : null;
                // 8-bit alpha detail maps are always line art; read as shadow ink they paint everything black.
                if (inkImage is not null && _alphaTextures.Contains(ink!.Value.Name))
                    ink = (ink.Value.Name, InkMapKind.LineArt);
                // Emissive maps glow only in the agent's emission color.
                string? emissiveName = data is not null && agentGlow != Vector3.Zero
                    ? MaterialTextureLocator.PickEmissiveMap(data, textureLookup) : null;
                var emissive = Texture(emissiveName);
                shading = shading with
                {
                    DiffuseColor = shading.DiffuseColor * agentTint,
                    EmissiveColor = emissive is null ? Vector3.Zero : agentGlow,
                    SelfIllumination = agentGlow,
                    DrawAsOneLayer = agent.DrawAsOneLayer,
                    SpecularLayout = specular is null ? SpecularMapLayout.None : ClassifySpecularMap(specular),
                    InkKind = inkImage is null ? InkMapKind.None : ink!.Value.Kind,
                    // Line art is read from alpha, so opaque placeholder maps mean no lines.
                    InkInAlpha = inkImage is not null &&
                                 (ink!.Value.Kind == InkMapKind.LineArt || _alphaTextures.Contains(ink.Value.Name)),
                    Opacity = opacity,
                };
                var diffuse = Texture(diffuseName);
                // An opaque pass ignores texture alpha, except legacy ports that alpha-test.
                if (diffuse is not null && data?.BlendMode == MaterialBlendMode.Normal && !IsLegacyPort(data))
                    diffuse = OpaqueTexture(diffuseName!, diffuse);
                string? gradientNote = null;
                if (diffuse is not null && data is not null && GradientMapped(diffuseName!, diffuse, data, textureLookup) is { } mapped)
                    (diffuse, gradientNote) = mapped;
                if (diffuse is not null && opacity < 1f)
                    diffuse = TranslucentTexture(diffuseName!, diffuse, opacity);
                batchTextures.Add(diffuse);
                batchNormalMaps.Add(DecodeNormalMap(normalName));
                batchBlendModes.Add(data?.BlendMode);
                batchSpecular.Add(specular);
                batchInk.Add(inkImage);
                batchParams.Add(shading);
                batchEmissive.Add(emissive);
                details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · " +
                            (diffuse is not null ? diffuseName
                                : diffuseName is not null ? $"{diffuseName} (could not decode)"
                                // Some materials name textures the game doesn't ship.
                                : data is not null && MaterialTextureLocator.HasDiffuseProperty(data) ? "no texture (its diffuse texture is not in the game files)"
                                : "no texture") +
                            (gradientNote is null ? "" : $" (colored through {gradientNote})") +
                            (emissive is null ? "" : $" (glows through {emissiveName})") +
                            (opacity < 1f ? $" ({opacity:P0} opaque)" : ""));
            }
            parts.Add(kept.Count == part.Batches.Count ? part : part with { Batches = kept });
            inkUvs.Add(InkTexCoords(mesh, part));
        }

        if (parts.Count == 0)
            throw new InvalidDataException(failures.Count > 0
                ? $"No mesh could be loaded: {string.Join("; ", failures.Select(f => $"{f.Item1}: {f.Item2}"))}"
                : "No meshes given.");

        var bones = loaded.SelectMany(m => m.BoneSymbols).Distinct().ToList();
        var (skeleton, skeletonName, matched) = bones.Count > 0
            ? PickSkeleton(bones, preferredSkeleton)
            : (null, null, 0);
        if (skeleton is not null)
        {
            // Bind unskinned parts to the nearest bone so they move with the rest.
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].IsSkinned || parts[i].Positions.Length == 0)
                    continue;
                if (NearestBone(skeleton, bones, parts[i]) is { } bone)
                {
                    parts[i] = parts[i].BindRigid(bone);
                    details.Add($"{loaded[i].Name}: unskinned, bound rigidly to bone {bone:x16}");
                }
            }
        }
        var geometry = MeshGeometry.Combine(parts);
        if (skeleton is not null && matched < bones.Count)
        {
            // Bones the skeleton lacks move with the nearest bone it has.
            var standIns = MeshSkinner.StandInBones(geometry, skeleton);
            if (standIns.Count > 0)
                details.Add($"Not in {skeletonName}, moved with the nearest bone: " + string.Join(", ",
                    standIns.Select(s => $"{BoneNames.For(s.Key)} with {BoneNames.For(skeleton.Bones[s.Value].JointSymbol)}")));
        }
        if (bones.Count > 0)
            details.Insert(0, skeleton is null
                ? $"Skeleton: none found for {bones.Count} bones"
                : $"Skeleton: {skeletonName} · {matched}/{bones.Count} bones matched");
        if (agentProp is not null && !agent.IsDefault)
        {
            var notes = new List<string>();
            if (agent.DiffuseColor != Vector3.One)
                notes.Add(agent.IsEffect
                    ? $"tinted {ColorText(new Vector4(agent.DiffuseColor, 1f))}"
                    : $"its color {ColorText(new Vector4(agent.DiffuseColor, 1f))} left out (it grades the character for a scene)");
            if (agent.EmissionColor != Vector3.Zero)
                notes.Add($"glowing {ColorText(new Vector4(agent.EmissionColor, 1f))}");
            if (agent.Opacity < 1f)
                notes.Add($"{agent.Opacity:P0} opaque");
            if (agent.DrawAsOneLayer)
                notes.Add("see-through as one layer");
            if (notes.Count > 0)
                details.Insert(bones.Count > 0 ? 1 : 0, $"Agent: {agentProp} · {string.Join(", ", notes)}");
        }

        return new AssembledModel
        {
            BatchEmissiveMaps = batchEmissive.Any(e => e is not null) ? batchEmissive : Array.Empty<DdsImage?>(),
            AgentProp = agentProp,
            Agent = agent,
            Geometry = geometry,
            BatchTextures = batchTextures,
            BatchNormalMaps = batchNormalMaps,
            BatchBlendModes = batchBlendModes,
            BatchSpecularMaps = batchSpecular,
            BatchInkTextures = batchInk,
            BatchMaterialParams = batchParams,
            InkTexCoords = inkUvs.Sum(u => u.Length) == geometry.Positions.Length ? inkUvs.SelectMany(u => u).ToArray() : null,
            Details = details,
            TexturesUsed = texturesUsed,
            Failures = failures,
            BoneSymbols = bones,
            Skeleton = skeleton,
            SkeletonName = skeletonName,
            SkeletonBonesMatched = matched,
        };
    }

    /// <summary>
    /// True for the shared clear cornea layers over the eyeballs. Their textures are solid
    /// black stand-ins that would black out the eyes.
    /// </summary>
    public static bool IsClearEyeLayer(string textureName) =>
        textureName.Contains("SharedParts", StringComparison.OrdinalIgnoreCase) &&
        (textureName.Contains("eyesGlass", StringComparison.OrdinalIgnoreCase) ||
         textureName.Contains("eyeLens", StringComparison.OrdinalIgnoreCase));

    /// <summary>The bone in <paramref name="skinnedBones"/> nearest the center of <paramref name="part"/>.</summary>
    private static ulong? NearestBone(SkeletonFile skeleton, IReadOnlyList<ulong> skinnedBones, MeshGeometry part)
    {
        var centre = Vector3.Zero;
        foreach (var p in part.Positions)
            centre += p;
        centre /= part.Positions.Length;
        var world = skeleton.BindPoseWorld();
        var allowed = skinnedBones.Count > 0 ? skinnedBones.ToHashSet() : null;
        ulong? best = null;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            if (allowed is not null && !allowed.Contains(skeleton.Bones[i].JointSymbol))
                continue;
            float d = Vector3.DistanceSquared(world[i].Translation, centre);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = skeleton.Bones[i].JointSymbol;
            }
        }
        return best;
    }

    private static readonly ulong LegacyMaterialModule = TelltaleCrc64.HashSymbol("module_legacy_material.prop");

    /// <summary>True for materials ported from the older engine (they alpha-test in the opaque pass).</summary>
    private static bool IsLegacyPort(MaterialData material) => material.Parents.Contains(LegacyMaterialModule);

    /// <summary>
    /// The diffuse texture a material inherits from a parent <c>*_m.prop</c> when it sets
    /// none itself, or null.
    /// </summary>
    private string? InheritedDiffuse(MaterialData material, Func<ulong, string?> textureLookup, int depth = 0)
    {
        if (depth > 3 || MaterialTextureLocator.HasDiffuseProperty(material))
            return null;
        foreach (var parent in material.Parents)
        {
            if (MaterialFile(parent) is not { } data)
                continue;
            if (MaterialTextureLocator.EngineDiffuse(data, textureLookup) is { } diffuse)
                return MaterialTextureLocator.IsEnginePlaceholder(diffuse) ? null : diffuse;
            if (InheritedDiffuse(data, textureLookup, depth + 1) is { } inherited)
                return inherited;
        }
        return null;
    }

    /// <summary>Textures borrowed from variant meshes, by material index.</summary>
    private sealed record BorrowedTextures(string From, Dictionary<int, string?> Diffuse, Dictionary<int, string?> Normal);

    // Borrows textures from a variant mesh by matching vertices with the same place and UV.
    // Later variants go first, since the first is often an effect version.
    private BorrowedTextures? BorrowFromVariant(D3dMeshFile mesh, MeshGeometry part, Func<ulong, string?> textureLookup)
    {
        var stem = Path.GetFileNameWithoutExtension(mesh.Name);
        var candidates = _assets.MeshNamesStartingWith(stem)
            .Where(n => !Path.GetFileNameWithoutExtension(n).Equals(stem, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        if (candidates.Count == 0)
            return null;

        // A few sample vertices per material.
        var samples = new Dictionary<int, List<int>>();
        foreach (var batch in part.Batches)
        {
            if (!samples.TryGetValue(batch.MaterialIndex, out var list))
                samples[batch.MaterialIndex] = list = new List<int>();
            for (int k = 0; k < 3 && k < batch.PrimitiveCount; k++)
                list.Add(part.Indices[batch.StartIndex + k * 3]);
        }

        var diffuse = new Dictionary<int, string?>();
        var normal = new Dictionary<int, string?>();
        var from = new List<string>();
        foreach (var name in candidates)
        {
            if (samples.Keys.All(diffuse.ContainsKey))
                break;
            var (other, _) = LoadMesh(name);
            if (other is null)
                continue;
            MeshGeometry geometry;
            try
            {
                geometry = other.GetGeometry();
            }
            catch (InvalidDataException)
            {
                continue;
            }
            var batchOf = new int[geometry.Positions.Length];
            Array.Fill(batchOf, -1);
            for (int b = 0; b < geometry.Batches.Count; b++)
            {
                var batch = geometry.Batches[b];
                for (int k = 0; k < batch.PrimitiveCount * 3 && batch.StartIndex + k < geometry.Indices.Length; k++)
                    batchOf[geometry.Indices[batch.StartIndex + k]] = b;
            }
            var payloads = other.MaterialHandles
                .Select(h => other.MaterialResources.FirstOrDefault(r => r.NameSymbol == h).Payload)
                .ToList();
            bool used = false;
            foreach (var (material, vertices) in samples)
            {
                if (diffuse.ContainsKey(material))
                    continue;
                foreach (int v in vertices)
                {
                    int match = FindVertex(geometry, part.Positions[v], part.TexCoords[v]);
                    if (match < 0 || batchOf[match] < 0)
                        continue;
                    int m = geometry.Batches[batchOf[match]].MaterialIndex;
                    if (m < 0 || m >= payloads.Count || payloads[m] is not { } payload || MaterialData.TryParse(payload) is not { } data ||
                        MaterialTextureLocator.PickDiffuseTexture(data, payload, textureLookup) is not { } texture)
                        continue;
                    diffuse[material] = texture;
                    normal[material] = MaterialTextureLocator.PickNormalMap(data, payload, textureLookup);
                    used = true;
                    break;
                }
            }
            if (used)
                from.Add(name);
        }
        return diffuse.Count == 0 ? null : new BorrowedTextures(string.Join(", ", from), diffuse, normal);
    }

    // The vertex at the same place with the same UV, or -1.
    private static int FindVertex(MeshGeometry geometry, Vector3 position, Vector2 uv)
    {
        for (int i = 0; i < geometry.Positions.Length; i++)
            if (Vector3.DistanceSquared(geometry.Positions[i], position) < 4e-6f &&
                Vector2.DistanceSquared(geometry.TexCoords[i], uv) < 1e-4f)
                return i;
        return -1;
    }

    // The texture named after a mesh, dropping trailing words until one is found, or null.
    private static string? TextureNamedAfter(string meshName, Func<ulong, string?> textureLookup)
    {
        var stem = Path.GetFileNameWithoutExtension(meshName);
        int partStart = stem.LastIndexOf('_') + 1;
        while (stem.Length > partStart)
        {
            if ((textureLookup(TelltaleCrc64.HashSymbol(stem + ".d3dtx")) ?? textureLookup(TelltaleCrc64.HashSymbol(stem))) is { } found &&
                !MaterialTextureLocator.IsNonDiffuseMap(found))
                return found;
            int cut = stem.Length - 1;
            while (cut > partStart && !char.IsUpper(stem[cut]))
                cut--;
            if (cut <= partStart)
                break;
            stem = stem[..cut];
        }
        return null;
    }

    /// <summary>Reads a material .prop by its symbol through the resolver (cached), or null.</summary>
    private MaterialData? MaterialFile(ulong symbol)
    {
        if (_materialFiles.TryGetValue(symbol, out var cached))
            return cached;
        MaterialData? data = null;
        if (_assets.NameForSymbol(symbol) is { } name && name.EndsWith(".prop", StringComparison.OrdinalIgnoreCase) &&
            _assets.TryRead(name) is { } bytes)
            data = PropertySet(bytes);
        _materialFiles[symbol] = data;
        return data;
    }

    /// <summary>The property set of a .prop file's bytes, or null.</summary>
    private static MaterialData? PropertySet(byte[] bytes)
    {
        if (!MetaStreamHeader.TryParse(bytes, out var meta, out _) || meta is null)
            return null;
        int size = Math.Min(meta.DefaultSectionSize, bytes.Length - meta.DefaultSectionOffset);
        return size > 0 ? MaterialData.TryParse(bytes.AsSpan(meta.DefaultSectionOffset, size).ToArray()) : null;
    }

    private readonly Dictionary<string, (HashSet<ulong> Meshes, AgentRenderSettings Settings)?> _agents =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The agent .prop the meshes belong to and its render settings. Only a .prop that lists
    /// one of the meshes counts, so another agent's settings don't color this outfit.
    /// </summary>
    private (string? Name, AgentRenderSettings Settings) FindAgent(
        IReadOnlyList<string> meshNames, string? preferredSkeleton, string? named)
    {
        var candidates = new List<string>();
        if (named is not null)
            candidates.Add(named);
        if (preferredSkeleton is not null)
            candidates.Add(Path.GetFileNameWithoutExtension(preferredSkeleton) + ".prop");
        // Props name their meshes with or without the extension.
        var meshes = meshNames
            .SelectMany(m => new[] { TelltaleCrc64.HashSymbol(m), TelltaleCrc64.HashSymbol(Path.GetFileNameWithoutExtension(m)) })
            .ToHashSet();
        foreach (var name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_agents.TryGetValue(name, out var agent))
                _agents[name] = agent = ReadAgent(name);
            if (agent is { } found && found.Meshes.Overlaps(meshes))
                return (name, found.Settings);
        }
        return (null, AgentRenderSettings.Default);
    }

    private (HashSet<ulong> Meshes, AgentRenderSettings Settings)? ReadAgent(string propName)
    {
        if (_assets.TryRead(propName) is not { } bytes || CharacterProp.TryParse(bytes) is not { } prop ||
            PropertySet(bytes) is not { } properties)
            return null;
        var meshes = prop.MeshListSymbols.ToHashSet();
        if (prop.MeshSymbol is { } single)
            meshes.Add(single);
        return (meshes, AgentRenderSettings.FromProperties(properties, MaterialFile));
    }

    /// <summary>The texture with every pixel opaque (cached), or the texture itself when it already is.</summary>
    private DdsImage OpaqueTexture(string fileName, DdsImage image)
    {
        var key = fileName + "|opaque";
        if (_textures.TryGetValue(key, out var cached))
            return cached!;
        bool translucent = false;
        for (int i = 3; i < image.Rgba.Length && !translucent; i += 4)
            translucent = image.Rgba[i] != 255;
        DdsImage result = image;
        if (translucent)
        {
            var px = (byte[])image.Rgba.Clone();
            for (int i = 3; i < px.Length; i += 4)
                px[i] = 255;
            result = ImageFromRgba(image.Width, image.Height, px) ?? image;
        }
        _textures[key] = result;
        return result;
    }

    /// <summary>
    /// How opaque an alpha-blended batch is, from material alpha, diffuse alpha and, on older
    /// meshes, a uniform vertex alpha. 1 for anything not alpha-blended.
    /// </summary>
    private static float BatchOpacity(D3dMeshFile mesh, MeshGeometry part, MeshBatch batch, MaterialData material,
        Func<Vector4[]?> vertexColors)
    {
        if (material.BlendMode is not (MaterialBlendMode.Alpha or MaterialBlendMode.AlphaAlphaTest))
            return 1f;
        float opacity = Math.Clamp(material.Floats.GetValueOrDefault(MaterialShading.AlphaKey, 1f), 0f, 1f);
        if (material.Colors.TryGetValue(MaterialShading.DiffuseColorKey, out var tint))
            opacity *= Math.Clamp(tint.W, 0f, 1f);
        if (mesh.PlatformAttributes.Count == 0 && mesh.LegacyComponents.Count == 0 && vertexColors() is { } colors)
        {
            float min = 1f, max = 0f;
            int end = Math.Min(part.Indices.Length, batch.StartIndex + batch.PrimitiveCount * 3);
            for (int i = batch.StartIndex; i < end; i++)
            {
                int v = part.Indices[i];
                if ((uint)v >= (uint)colors.Length)
                    continue;
                min = Math.Min(min, colors[v].W);
                max = Math.Max(max, colors[v].W);
            }
            if (min <= max && max - min < 2.5f / 255f)
                opacity *= max;
        }
        return opacity;
    }

    /// <summary>The texture with its alpha scaled by <paramref name="opacity"/> (cached); see <see cref="WithOpacity"/>.</summary>
    private DdsImage TranslucentTexture(string fileName, DdsImage image, float opacity)
    {
        var key = $"{fileName}|opacity {opacity:F3}";
        if (_textures.TryGetValue(key, out var cached))
            return cached!;
        var result = WithOpacity(image, opacity);
        _textures[key] = result;
        return result;
    }

    /// <summary>
    /// A copy of the texture with its alpha scaled by <paramref name="opacity"/>. Tiny textures
    /// are widened to 8×8 so the viewer doesn't take their alpha for compression noise.
    /// </summary>
    internal static DdsImage WithOpacity(DdsImage image, float opacity)
    {
        int scale = image.Width * image.Height < 64 ? (int)Math.Ceiling(8.0 / Math.Max(1, Math.Min(image.Width, image.Height))) : 1;
        int width = image.Width * scale, height = image.Height * scale;
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int from = ((y / scale) * image.Width + x / scale) * 4, to = (y * width + x) * 4;
                px[to] = image.Rgba[from];
                px[to + 1] = image.Rgba[from + 1];
                px[to + 2] = image.Rgba[from + 2];
                px[to + 3] = (byte)MathF.Round(image.Rgba[from + 3] * opacity);
            }
        return ImageFromRgba(width, height, px) ?? image;
    }

    private static readonly ulong GradientBiasKey = TelltaleCrc64.HashSymbol("Material - Gradient Bias");

    /// <summary>
    /// Colors a shading-only diffuse (red channel) through the material's gradient strip, with
    /// the row picked by "Gradient Bias". Null when the material has no gradient.
    /// </summary>
    private (DdsImage Image, string Gradient)? GradientMapped(
        string diffuseName, DdsImage diffuse, MaterialData material, Func<ulong, string?> textureLookup)
    {
        // Only recolor shading textures, where red carries the image.
        long red = 0, other = 0;
        for (int i = 0; i < diffuse.Rgba.Length; i += 4 * 61)
        {
            red += diffuse.Rgba[i];
            other += Math.Max(diffuse.Rgba[i + 1], diffuse.Rgba[i + 2]);
        }
        if (other * 2 > red)
            return null;
        foreach (var binding in material.Textures)
        {
            if (binding.Key == MaterialTextureLocator.DiffuseTextureKey || binding.TextureSymbol == 0 ||
                textureLookup(binding.TextureSymbol) is not { } gradientName ||
                !Path.GetFileNameWithoutExtension(gradientName).Contains("gradient", StringComparison.OrdinalIgnoreCase) ||
                DecodeTexture(gradientName) is not { Width: <= 16, Height: >= 2 } gradient)
                continue;
            float bias = Math.Clamp(material.Floats.GetValueOrDefault(GradientBiasKey), 0f, 1f);
            int row = (int)MathF.Round(bias * (gradient.Height - 1));
            var key = $"{diffuseName}|{gradientName}|{row}";
            if (!_textures.TryGetValue(key, out var mapped))
            {
                var px = (byte[])diffuse.Rgba.Clone();
                int w = gradient.Width;
                for (int i = 0; i < px.Length; i += 4)
                {
                    float u = px[i] / 255f * (w - 1);
                    int c0 = (int)u, c1 = Math.Min(c0 + 1, w - 1);
                    float f = u - c0;
                    int g0 = (row * w + c0) * 4, g1 = (row * w + c1) * 4;
                    for (int k = 0; k < 3; k++)
                        px[i + k] = (byte)(gradient.Rgba[g0 + k] * (1 - f) + gradient.Rgba[g1 + k] * f);
                }
                mapped = ImageFromRgba(diffuse.Width, diffuse.Height, px);
                _textures[key] = mapped;
            }
            if (mapped is not null)
                return (mapped, gradientName);
        }
        return null;
    }

    /// <summary>An effect material's color shown through its mask texture's alpha, or null.</summary>
    private (DdsImage Image, string Name)? TintedMaskLayer(MaterialData material, Func<ulong, string?> textureLookup)
    {
        var colors = material.Colors.Values
            .Where(c => c.W > 0 && !(c.X == c.Y && c.Y == c.Z && (c.X <= 0 || c.X >= 1)))
            .ToList();
        if (colors.Count != 1)
            return null;
        var color = colors[0];
        foreach (var binding in material.Textures)
        {
            if (binding.TextureSymbol == 0 || textureLookup(binding.TextureSymbol) is not { } maskName ||
                !Path.GetFileNameWithoutExtension(maskName).Contains("mask", StringComparison.OrdinalIgnoreCase))
                continue;
            var key = $"{maskName}|tint {color}";
            if (_textures.TryGetValue(key, out var cached))
                return cached is null ? null : (cached, $"{maskName} tinted {ColorText(color)}");
            DdsImage? layer = null;
            if (DecodeTexture(maskName) is { } mask)
            {
                var px = new byte[mask.Rgba.Length];
                byte r = (byte)(Math.Clamp(color.X, 0, 1) * 255), g = (byte)(Math.Clamp(color.Y, 0, 1) * 255),
                     bl = (byte)(Math.Clamp(color.Z, 0, 1) * 255);
                int covered = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    px[i] = r; px[i + 1] = g; px[i + 2] = bl;
                    px[i + 3] = (byte)(mask.Rgba[i + 3] * Math.Clamp(color.W, 0, 1));
                    if (px[i + 3] > 8) covered++;
                }
                // Skip masks with no or full coverage.
                if (covered > 0 && covered < px.Length / 4)
                    layer = ImageFromRgba(mask.Width, mask.Height, px);
            }
            _textures[key] = layer;
            if (layer is not null)
                return (layer, $"{maskName} tinted {ColorText(color)}");
        }
        return null;
    }

    internal static string ColorText(Vector4 c) => $"#{(int)(Math.Clamp(c.X, 0, 1) * 255):X2}{(int)(Math.Clamp(c.Y, 0, 1) * 255):X2}{(int)(Math.Clamp(c.Z, 0, 1) * 255):X2}";

    /// <summary>Wraps RGBA pixels as an uncompressed 32-bit DDS and decodes it into an image.</summary>
    private static DdsImage? ImageFromRgba(int width, int height, byte[] rgba)
    {
        var dds = new byte[128 + rgba.Length];
        void Put(int at, uint value) => BitConverter.TryWriteBytes(dds.AsSpan(at), value);
        Put(0, 0x20534444);                   // "DDS "
        Put(4, 124);                          // header size
        Put(8, 0x100F);                       // caps | height | width | pitch | pixel format
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(20, (uint)(width * 4));
        Put(76, 32);                          // pixel format size
        Put(80, 0x41);                        // RGB | alpha pixels
        Put(88, 32);
        Put(92, 0x000000FF);
        Put(96, 0x0000FF00);
        Put(100, 0x00FF0000);
        Put(104, 0xFF000000);
        Put(108, 0x1000);                     // texture
        rgba.CopyTo(dds, 128);
        return DdsImage.TryDecode(dds, out var image, out _) ? image : null;
    }

    /// <summary>Loads a skeleton by file name through the resolver (cached), or null.</summary>
    public SkeletonFile? LoadSkeleton(string fileName)
    {
        lock (_gate)
        {
            if (_skeletons.TryGetValue(fileName, out var cached))
                return cached;
            var bytes = _assets.TryRead(fileName);
            SkeletonFile? skeleton = bytes is not null && SkeletonFile.TryLoad(bytes, out var skl, out _) ? skl : null;
            return _skeletons[fileName] = skeleton;
        }
    }

    private (D3dMeshFile? Mesh, string? Error) LoadMesh(string name)
    {
        if (_meshes.TryGetValue(name, out var cached))
            return cached;
        var bytes = _assets.TryRead(name);
        var result = bytes is null
            ? (null, "file not found")
            : D3dMeshFile.TryLoad(bytes, out var mesh, out var error) ? (mesh, null) : ((D3dMeshFile?)null, error);
        return _meshes[name] = result;
    }

    /// <summary>
    /// Tells packed specular maps (masks in red and green, gloss in blue) from plain intensity
    /// ones. Blue is weighed against red alone, since green can cover a lot.
    /// </summary>
    public static SpecularMapLayout ClassifySpecularMap(DdsImage image)
    {
        long r = 0, b = 0, n = 0;
        for (int i = 0; i < image.Rgba.Length; i += 4 * 37, n++)
        {
            r += image.Rgba[i];
            b += image.Rgba[i + 2];
        }
        if (n == 0)
            return SpecularMapLayout.Intensity;
        if (r / n < 32 && b / n > 2 * Math.Max(1, r / n))
            return SpecularMapLayout.Packed;
        // Maps with alpha keep gloss there; BC1 maps are intensity only.
        return image.Format is DdsPixelFormat.Bc3 or DdsPixelFormat.Bc2 ? SpecularMapLayout.ColorGloss : SpecularMapLayout.Intensity;
    }

    /// <summary>
    /// The ink texture's UV set: set 3 on Definitive Series meshes, the detail-map stream on
    /// legacy meshes, and the main set otherwise.
    /// </summary>
    private static Vector2[] InkTexCoords(D3dMeshFile mesh, MeshGeometry part)
    {
        try
        {
            if (mesh.PlatformAttributes.Count > 0 && mesh.GetTexCoords(3) is { } ink && ink.Length == part.TexCoords.Length)
                return ink;
            if (mesh.LegacyComponents.Count > 0 && mesh.GetTexCoords(1) is { } detail && detail.Length == part.TexCoords.Length)
                return detail;
        }
        catch (InvalidDataException)
        {
        }
        return part.TexCoords;
    }

    /// <summary>Textures decoded from 8-bit alpha: their value is in the alpha channel.</summary>
    private readonly HashSet<string> _alphaTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Swizzles of the textures decoded so far (null for textures without one).</summary>
    private readonly Dictionary<string, IReadOnlyList<byte>?> _swizzles = new(StringComparer.OrdinalIgnoreCase);

    // A normal map with its channels swizzled to where the viewer reads them.
    private DdsImage? DecodeNormalMap(string? fileName)
    {
        if (fileName is null)
            return null;
        var key = fileName + "|normal";
        if (_textures.TryGetValue(key, out var cached))
            return cached;
        var image = DecodeTexture(fileName);
        if (image is not null)
            image = NormalMapSwizzle.Apply(image, _swizzles.GetValueOrDefault(fileName));
        return _textures[key] = image;
    }

    private DdsImage? DecodeTexture(string? fileName)
    {
        if (fileName is null)
            return null;
        if (_textures.TryGetValue(fileName, out var cached))
            return cached;
        DdsImage? image = null;
        if (_assets.TryRead(fileName) is { } bytes &&
            D3dtxTexture.TryLoad(bytes, out var tex, out _) &&
            tex!.TryToDds(out var dds, out _) &&
            DdsImage.TryDecode(dds!, out var decoded, out _))
        {
            image = decoded;
            _swizzles[fileName] = tex.Swizzle;
            // The GPU samples A8 textures as black with the value in alpha.
            if (tex.SurfaceFormat == T3SurfaceFormat.A8)
            {
                _alphaTextures.Add(fileName);
                var px = image!.Rgba;
                for (int i = 0; i < px.Length; i += 4)
                {
                    px[i + 3] = px[i];
                    px[i] = px[i + 1] = px[i + 2] = 0;
                }
            }
        }
        return _textures[fileName] = image;
    }

    private (SkeletonFile? Skeleton, string? Name, int Matched) PickSkeleton(IReadOnlyList<ulong> bones, string? preferred)
    {
        if (preferred is not null && LoadSkeleton(preferred) is { } wanted)
        {
            int hit = bones.Count(wanted.JointSymbols.Contains);
            if (hit > 0)
                return (wanted, preferred, hit);
        }

        // Otherwise take the skeleton covering the most bones.
        (SkeletonFile? Skeleton, string? Name, int Matched) best = (null, null, 0);
        foreach (var name in _assets.SkeletonNames)
        {
            if (LoadSkeleton(name) is not { } skeleton)
                continue;
            int hit = bones.Count(skeleton.JointSymbols.Contains);
            if (hit > best.Matched)
                best = (skeleton, name, hit);
            if (hit == bones.Count)
                break;
        }
        return best;
    }
}

/// <summary>Resolves assets from a folder of loose extracted files (searched recursively).</summary>
public sealed class FolderAssetResolver : IAssetResolver
{
    private readonly Dictionary<string, string> _pathByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, string> _textureBySymbol = new();
    private readonly Dictionary<ulong, string> _nameBySymbol = new();

    public FolderAssetResolver(string folder)
    {
        Folder = folder;
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(path);
            _pathByName.TryAdd(name, path);
            _nameBySymbol.TryAdd(TelltaleCrc64.HashSymbol(name), name);
            if (name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase))
            {
                _textureBySymbol.TryAdd(TelltaleCrc64.HashSymbol(name), name);
                _textureBySymbol.TryAdd(TelltaleCrc64.HashSymbol(Path.GetFileNameWithoutExtension(name)), name);
            }
        }
    }

    public string Folder { get; }

    /// <summary>Every file name in the folder.</summary>
    public IEnumerable<string> FileNames => _pathByName.Keys;

    public byte[]? TryRead(string fileName) =>
        _pathByName.TryGetValue(fileName, out var path) ? File.ReadAllBytes(path) : null;

    public string? TextureNameForSymbol(ulong symbol) =>
        _textureBySymbol.TryGetValue(symbol, out var name) ? name : null;

    public string? NameForSymbol(ulong symbol) => _nameBySymbol.GetValueOrDefault(symbol);

    public IEnumerable<string> SkeletonNames =>
        _pathByName.Keys.Where(n => n.EndsWith(".skl", StringComparison.OrdinalIgnoreCase));
}
