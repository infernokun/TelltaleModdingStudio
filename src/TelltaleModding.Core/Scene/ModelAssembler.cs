using System.Numerics;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Scene;

/// <summary>Where a scene's files come from: a game catalog or a loose folder of extracted files.</summary>
public interface IAssetResolver
{
    /// <summary>Reads a file by its name (e.g. <c>sk62_clementine400_head.d3dmesh</c>), or null when absent.</summary>
    byte[]? TryRead(string fileName);

    /// <summary>
    /// The texture file a symbol refers to (the CRC64 of its name, with the extension
    /// in the Definitive Series, without in older games), or null when unknown.
    /// </summary>
    string? TextureNameForSymbol(ulong symbol);

    /// <summary>
    /// The file a symbol (CRC64 of its name, with extension) refers to, or null when unknown;
    /// used to find the material a mesh's material instances.
    /// </summary>
    string? NameForSymbol(ulong symbol) => null;

    /// <summary>Names of every skeleton (.skl) available, for matching a model to its skeleton.</summary>
    IEnumerable<string> SkeletonNames { get; }

    /// <summary>Names of the meshes (.d3dmesh) whose names start with <paramref name="prefix"/>: a model's variants.</summary>
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

    /// <summary>
    /// Main-pass blend mode per batch, from the material's compiled data (null where the
    /// mesh does not say, e.g. older games).
    /// </summary>
    public IReadOnlyList<MaterialBlendMode?> BatchBlendModes { get; init; } = Array.Empty<MaterialBlendMode?>();

    /// <summary>
    /// Specular map per batch (null where the material has none). Empty when the model was
    /// assembled without material data; <see cref="BatchMaterialParams"/> says how to read it.
    /// </summary>
    public IReadOnlyList<DdsImage?> BatchSpecularMaps { get; init; } = Array.Empty<DdsImage?>();

    /// <summary>
    /// Ink or line-art texture per batch (null where none). Empty when the model was assembled
    /// without material data; <see cref="BatchMaterialParams"/> says how it applies.
    /// </summary>
    public IReadOnlyList<DdsImage?> BatchInkTextures { get; init; } = Array.Empty<DdsImage?>();

    /// <summary>
    /// Texture coordinates the ink textures are laid out on, one per vertex of
    /// <see cref="Geometry"/> (the Definitive Series' fourth UV set; the main set elsewhere),
    /// or null when the model was assembled without them.
    /// </summary>
    public Vector2[]? InkTexCoords { get; init; }

    /// <summary>Shading values per batch from its material (null where unknown; may be empty).</summary>
    public IReadOnlyList<MaterialShading?> BatchMaterialParams { get; init; } = Array.Empty<MaterialShading?>();

    /// <summary>
    /// Emissive texture per batch (null where none glows), added in the batch's
    /// <see cref="MaterialShading.EmissiveColor"/>. Empty when nothing glows.
    /// </summary>
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
/// Builds renderable models from meshes, matching each batch's textures through the
/// asset resolver and merging the parts into one geometry. Decoded meshes, textures
/// and skeletons are cached, so re-assembling with a part toggled is quick. Meshes and
/// textures are kept within a memory budget, the least recently used dropped first.
/// Thread-safe: calls from several threads run one at a time, so one assembler can serve
/// every page of an open game.
/// </summary>
public sealed class ModelAssembler
{
    private readonly IAssetResolver _assets;
    private readonly object _gate = new();
    private readonly SizeBoundedCache<string, (D3dMeshFile? Mesh, string? Error)> _meshes;
    private readonly SizeBoundedCache<string, DdsImage?> _textures;
    private readonly Dictionary<string, SkeletonFile?> _skeletons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, MaterialData?> _materialFiles = new();

    /// <summary>
    /// The default budget for decoded meshes and textures: 1/16 of this machine's memory,
    /// 256 MB to 1 GB. Browsing every character of a large game otherwise holds tens of GB.
    /// </summary>
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
    /// Assembles the named meshes into one model. <paramref name="preferredSkeleton"/>
    /// (a .skl file name) is used when it fits; otherwise the skeleton covering the
    /// most bones is chosen. The render settings of the agent the meshes belong to
    /// (<paramref name="agentProp"/>, or the <c>.prop</c> named after the skeleton, when it
    /// lists one of the meshes) are applied over the materials: see <see cref="AgentRenderSettings"/>.
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
            // The diffuse a material names is sometimes not shipped while one named after the
            // mesh is (sk55_paige_bodyUpperBitten → sk55_paige_bodyUpper, sk54_crabber_hairF).
            // Only when nothing in the mesh found a texture, so no other material is guessed over.
            if (diffuseByMaterial.All(d => d is null) && TextureNamedAfter(mesh.Name, textureLookup) is { } named)
            {
                for (int i = 0; i < diffuseByMaterial.Count; i++)
                    if (dataByMaterial[i] is { } d && MaterialTextureLocator.HasDiffuseProperty(d))
                        diffuseByMaterial[i] = named;
            }
            // A model whose textures the game never shipped (Tales from the Borderlands' plain
            // skB1_handsomeJack, unused in play) borrows them from a variant built on the same
            // UV layout, matched vertex by vertex.
            BorrowedTextures? borrowed = null;
            if (diffuseByMaterial.All(d => d is null) &&
                dataByMaterial.Any(d => d is not null && MaterialTextureLocator.HasDiffuseProperty(d)))
            {
                borrowed = BorrowFromVariant(mesh, part, textureLookup);
                if (borrowed is not null)
                    foreach (var (i, diffuse) in borrowed.Diffuse)
                        diffuseByMaterial[i] = diffuse;
            }
            // A color texture (real or an engine solid-color fill), not just maps. The
            // Season 3-era eye "glass" layer has a color texture, but it is a solid black
            // stand-in for a transparent, reflective cornea, so it counts as none.
            bool HasColor(string? diffuse) =>
                diffuse is not null && !MaterialTextureLocator.IsNonDiffuseMap(diffuse) && !IsClearEyeLayer(diffuse);
            bool anyColoredMaterial = diffuseByMaterial.Any(HasColor);
            // Materials that bind the engine's diffuse property: their textures are known, not guessed.
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

                // A material without a diffuse property, next to materials with one or
                // blended over the surface, is a layer the engine composites: the cornea
                // over the iris, or an effect such as a bleeding wound drawn through a mask
                // in a material color. Effects with a mask and color are drawn that way;
                // anything else would hide what is underneath, so it is left out.
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

                // A material with no color texture (none at all, or only a normal map,
                // e.g. the eye-wetness/cornea layer over the iris) while its siblings have
                // one is a clear overlay shell; drawn opaque it would hide what is
                // underneath, so leave it out.
                if (hasPayload && !engineDiffuse[material] && !HasColor(diffuseByMaterial[material]) && anyColoredMaterial)
                {
                    details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · hidden (texture-less overlay, e.g. eye wetness)");
                    continue;
                }

                string? diffuseName = SlotTexture(batch.DiffuseTextureIndex)
                    ?? (hasPayload ? diffuseByMaterial[material] : null)
                    // Untextured batch but the mesh has textures: fall back to slot 0 so
                    // single-texture meshes with sparse indices still render textured.
                    ?? (batch.DiffuseTextureIndex < 0 ? SlotTexture(0) : null);
                if (diffuseName is not null && IsClearEyeLayer(diffuseName))
                {
                    details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · hidden (clear eye layer, {diffuseName})");
                    continue;
                }
                float opacity = (data is null ? 1f : BatchOpacity(mesh, part, batch, data, VertexColors)) * agent.Opacity;
                if (opacity < 1f / 255f)
                {
                    // E.g. the clear cornea shells over Tales from the Borderlands eyes: drawn
                    // opaque they cover the iris in grey or black.
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
                        // Drawn lines are 8-bit alpha (dark on light); anything else is white-on-black ink.
                        ink = (legacyLines, DecodeTexture(legacyLines) is not null && _alphaTextures.Contains(legacyLines)
                            ? InkMapKind.LineArt : InkMapKind.Shadow);
                }
                var specular = DecodeTexture(specularName);
                var inkImage = ink is { } inkMap ? DecodeTexture(inkMap.Name) : null;
                // Emissive maps glow only in the agent's emission color (black by default).
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
                    // Line art is read from alpha (below one half = ink), whatever the texture's
                    // format: placeholder detail maps are opaque with no color, which must mean
                    // "no lines". Shadow ink keeps its strokes in color.
                    InkInAlpha = inkImage is not null &&
                                 (ink!.Value.Kind == InkMapKind.LineArt || _alphaTextures.Contains(ink.Value.Name)),
                    Opacity = opacity,
                };
                var diffuse = Texture(diffuseName);
                // An opaque main pass ignores the texture's alpha (often a spec or mask
                // channel); left in, the viewer would blend holes into the surface. Legacy
                // ports keep it: they alpha-test in the opaque pass.
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
                                // skB1_handsomeJack names face and body textures the game does not ship.
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
            // A part with no skinning at all would stay put while the rest animates: bind
            // it to the bone nearest its centre (among the bones the outfit is skinned to).
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
    /// True for the shared clear layers over the eyeballs: the Season 3-era "eyesGlass"
    /// and Game of Thrones "eyeLens". In the game they are transparent, reflective
    /// corneas; their textures are solid black stand-ins, so drawn opaque they black out
    /// the eyes. Character-specific lenses (a robot eye, glasses) are real and kept.
    /// </summary>
    public static bool IsClearEyeLayer(string textureName) =>
        textureName.Contains("SharedParts", StringComparison.OrdinalIgnoreCase) &&
        (textureName.Contains("eyesGlass", StringComparison.OrdinalIgnoreCase) ||
         textureName.Contains("eyeLens", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The skeleton bone (preferring those in <paramref name="skinnedBones"/>) whose
    /// bind-pose position is nearest the centre of <paramref name="part"/>'s vertices.
    /// </summary>
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
    /// The diffuse texture a material inherits when it sets none itself: some instances only
    /// name a parent <c>*_m.prop</c> that binds it (sk61_zombie400Sherak's hands and feet).
    /// Null when the material sets its own, or no parent binds a real texture.
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

    /// <summary>
    /// The texture named after a mesh, dropping trailing words of its last part until one is
    /// found (bodyUpperBitten → bodyUpper → body), or null.
    /// </summary>
    private sealed record BorrowedTextures(string From, Dictionary<int, string?> Diffuse, Dictionary<int, string?> Normal);

    // For each material of the mesh, the textures of the material that draws the same vertices
    // (same place, same UV) in a variant: a mesh whose name starts with this one's. Later
    // variants are tried first, as the first is often an effect version (skB1_handsomeJackAI
    // is broken up into cubes; AI102 and AI103 carry his plain art).
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

        // A few vertices drawn by each material, to look up in the variants.
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
    /// The agent .prop the meshes belong to and its render settings: <paramref name="named"/>,
    /// otherwise the .prop named after the skeleton, whichever lists one of the meshes (another
    /// agent's settings must not color this outfit). (null, defaults) when none does.
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
    /// How opaque an alpha-blended material's batch is: its "Material - Alpha", its diffuse
    /// color's alpha and (on Tales from the Borderlands-era meshes, whose shaders multiply
    /// it in) the alpha of the batch's vertex colors (tinted lenses are 27-85%; the clear
    /// shells over some eyes 0). 1 for anything not alpha-blended, and where the vertex alpha
    /// varies across the batch (blended terrain decals), which one value cannot express.
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
    /// (the engine's 4×4 solid-color fills) are widened to 8×8 so a viewer judging
    /// translucency by pixel count does not take them for compression noise.
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
    /// Season 4 zombies store shading, not color, in their diffuse texture's red channel;
    /// the material colors it through a gradient strip (a few texels wide, one palette
    /// per band of rows, the band picked by its "Gradient Bias"). Returns the colored
    /// texture, or null when the material has no gradient.
    /// </summary>
    private (DdsImage Image, string Gradient)? GradientMapped(
        string diffuseName, DdsImage diffuse, MaterialData material, Func<ulong, string?> textureLookup)
    {
        // Only a shading texture (red carries the image, green/blue are masks) is recolored.
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

    /// <summary>
    /// For an effect material (e.g. a bleeding wound): its color property shown through its
    /// mask texture's alpha, or null when it has no such color and mask.
    /// </summary>
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
                // A mask with no alpha coverage (or full coverage) is no shape to draw.
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
    /// Season 4 specular maps keep metal masks in red and green, gloss in blue and intensity in
    /// alpha; older ones are a grey intensity image. Red is almost empty on every packed map,
    /// but green can cover a lot (a jacket's zips and studs), so blue is weighed against red
    /// alone. Read as intensity, a packed map tints the highlights green and blue.
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
        // A map with an alpha channel keeps its gloss there; one without (BC1) is intensity only.
        return image.Format is DdsPixelFormat.Bc3 or DdsPixelFormat.Bc2 ? SpecularMapLayout.ColorGloss : SpecularMapLayout.Intensity;
    }

    /// <summary>
    /// The ink texture's UV set: set 3 on Definitive Series meshes, the detail-map stream on
    /// legacy meshes (The Wolf Among Us). Tales from the Borderlands paints its line-art detail
    /// maps over the color texture's layout, so it uses the main set (its set 1 is partly unset).
    /// </summary>
    private static Vector2[] InkTexCoords(D3dMeshFile mesh, MeshGeometry part)
    {
        try
        {
            if (mesh.PlatformAttributes.Count > 0 && mesh.GetTexCoords(3) is { } ink && ink.Length == part.TexCoords.Length)
                return ink;
            // Legacy meshes carry a dedicated detail-map stream for their ink lines.
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

    // A normal map with its channels where the viewer reads them (see NormalMapSwizzle).
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
            // Alpha-only textures (e.g. Season 2 eyelash cards) decode to grey for
            // previewing; the GPU samples them as black with the value as alpha.
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

        // No usable preferred skeleton: take the one covering the most bones (parsed
        // skeletons are cached, so repeat searches are cheap).
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
