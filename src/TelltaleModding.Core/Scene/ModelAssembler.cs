using System.Numerics;
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
/// and skeletons are cached, so re-assembling with a part toggled is quick.
/// Not thread-safe: use one assembler per resolver from one thread at a time.
/// </summary>
public sealed class ModelAssembler
{
    private readonly IAssetResolver _assets;
    private readonly Dictionary<string, (D3dMeshFile? Mesh, string? Error)> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DdsImage?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkeletonFile?> _skeletons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, MaterialData?> _materialFiles = new();

    public ModelAssembler(IAssetResolver assets) => _assets = assets;

    /// <summary>
    /// Assembles the named meshes into one model. <paramref name="preferredSkeleton"/>
    /// (a .skl file name) is used when it fits; otherwise the skeleton covering the
    /// most bones is chosen.
    /// </summary>
    public AssembledModel Build(IReadOnlyList<string> meshNames, string? preferredSkeleton = null)
    {
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
            // A colour texture (real or an engine solid-colour fill), not just maps. The
            // Season 3-era eye "glass" layer has a colour texture, but it is a solid black
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
                // in a material colour. Effects with a mask and colour are drawn that way;
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
                        details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · effect layer: {layer.Name}");
                        continue;
                    }
                    details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · hidden (texture-less overlay: material has no diffuse texture)");
                    continue;
                }

                // A material with no colour texture (none at all, or only a normal map,
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
                float opacity = data is null ? 1f : BatchOpacity(mesh, part, batch, data, VertexColors);
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
                var shading = data is not null ? MaterialShading.FromMaterial(data) : MaterialShading.Default;
                string? specularName = data is not null ? MaterialTextureLocator.PickSpecularMap(data, textureLookup) : null;
                var ink = data is not null ? MaterialTextureLocator.PickInkTexture(data, textureLookup) : null;
                if (mesh.MaterialHandles.Count == 0 && diffuseName is not null)
                {
                    // Older meshes carry no material data: their maps are named after the colour texture.
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
                shading = shading with
                {
                    SpecularLayout = specular is null ? SpecularMapLayout.None : ClassifySpecularMap(specular),
                    InkKind = inkImage is null ? InkMapKind.None : ink!.Value.Kind,
                    // Line art is read from alpha (below one half = ink), whatever the texture's
                    // format: placeholder detail maps are opaque with no colour, which must mean
                    // "no lines". Shadow ink keeps its strokes in colour.
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
                batchNormalMaps.Add(DecodeTexture(normalName));
                batchBlendModes.Add(data?.BlendMode);
                batchSpecular.Add(specular);
                batchInk.Add(inkImage);
                batchParams.Add(shading);
                details.Add($"  batch {b}: {batch.PrimitiveCount:N0} tris · " +
                            (diffuse is not null ? diffuseName : diffuseName is null ? "no texture" : $"{diffuseName} (could not decode)") +
                            (gradientNote is null ? "" : $" (coloured through {gradientNote})") +
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

        return new AssembledModel
        {
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
            _assets.TryRead(name) is { } bytes && MetaStreamHeader.TryParse(bytes, out var meta, out _) && meta is not null)
        {
            int size = Math.Min(meta.DefaultSectionSize, bytes.Length - meta.DefaultSectionOffset);
            if (size > 0)
                data = MaterialData.TryParse(bytes.AsSpan(meta.DefaultSectionOffset, size).ToArray());
        }
        _materialFiles[symbol] = data;
        return data;
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
    /// colour's alpha and (on Tales from the Borderlands-era meshes, whose shaders multiply
    /// it in) the alpha of the batch's vertex colours (tinted lenses are 27-85%; the clear
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

    /// <summary>
    /// The texture with its alpha scaled by <paramref name="opacity"/> (cached). Tiny textures
    /// (the engine's 4×4 solid-colour fills) are widened to 8×8 so a viewer judging
    /// translucency by pixel count does not take them for compression noise.
    /// </summary>
    private DdsImage TranslucentTexture(string fileName, DdsImage image, float opacity)
    {
        var key = $"{fileName}|opacity {opacity:F3}";
        if (_textures.TryGetValue(key, out var cached))
            return cached!;
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
        var result = ImageFromRgba(width, height, px) ?? image;
        _textures[key] = result;
        return result;
    }

    private static readonly ulong GradientBiasKey = TelltaleCrc64.HashSymbol("Material - Gradient Bias");

    /// <summary>
    /// Season 4 zombies store shading, not colour, in their diffuse texture's red channel;
    /// the material colours it through a gradient strip (a few texels wide, one palette
    /// per band of rows, the band picked by its "Gradient Bias"). Returns the coloured
    /// texture, or null when the material has no gradient.
    /// </summary>
    private (DdsImage Image, string Gradient)? GradientMapped(
        string diffuseName, DdsImage diffuse, MaterialData material, Func<ulong, string?> textureLookup)
    {
        // Only a shading texture (red carries the image, green/blue are masks) is recoloured.
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
    /// For an effect material (e.g. a bleeding wound): its colour property shown through its
    /// mask texture's alpha, or null when it has no such colour and mask.
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

    private static string ColorText(Vector4 c) => $"#{(int)(Math.Clamp(c.X, 0, 1) * 255):X2}{(int)(Math.Clamp(c.Y, 0, 1) * 255):X2}{(int)(Math.Clamp(c.Z, 0, 1) * 255):X2}";

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
        if (_skeletons.TryGetValue(fileName, out var cached))
            return cached;
        var bytes = _assets.TryRead(fileName);
        SkeletonFile? skeleton = bytes is not null && SkeletonFile.TryLoad(bytes, out var skl, out _) ? skl : null;
        return _skeletons[fileName] = skeleton;
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
    /// Season 4 specular maps leave red and green almost empty (metal masks) and keep gloss in
    /// blue and intensity in alpha; older ones are a grey intensity image.
    /// </summary>
    public static SpecularMapLayout ClassifySpecularMap(DdsImage image)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (int i = 0; i < image.Rgba.Length; i += 4 * 37, n++)
        {
            r += image.Rgba[i];
            g += image.Rgba[i + 1];
            b += image.Rgba[i + 2];
        }
        if (n == 0)
            return SpecularMapLayout.Intensity;
        return (r + g) / (2 * n) < 24 && b / n > 2 * Math.Max(1, (r + g) / (2 * n)) ? SpecularMapLayout.Packed : SpecularMapLayout.Intensity;
    }

    /// <summary>
    /// The ink texture's UV set: set 3 on Definitive Series meshes, the detail-map stream on
    /// legacy meshes (The Wolf Among Us). Tales from the Borderlands paints its line-art detail
    /// maps over the colour texture's layout, so it uses the main set (its set 1 is partly unset).
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
