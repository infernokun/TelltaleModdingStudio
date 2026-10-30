using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>
/// Finds the diffuse texture a material uses. Materials parsed as <see cref="MaterialData"/>
/// name it outright: the texture bound to the engine's "Material - Diffuse Texture"
/// property. Otherwise (or when that property is unset) Telltale's material payloads
/// embed texture references as 8-byte name symbols; scanning for symbols that match
/// known texture files and judging them by name recovers it.
/// </summary>
public static class MaterialTextureLocator
{
    /// <summary>Key symbol of the engine's diffuse texture material property.</summary>
    public static readonly ulong DiffuseTextureKey = TelltaleCrc64.HashSymbol("Material - Diffuse Texture");

    /// <summary>Key symbol of the engine's normal map material property.</summary>
    public static readonly ulong NormalMapKey = TelltaleCrc64.HashSymbol("Material - Normal Map Texture");

    /// <summary>Key symbol of the engine's specular map property.</summary>
    public static readonly ulong SpecularMapKey = TelltaleCrc64.HashSymbol("Material - Specular Map Texture");

    /// <summary>Key symbol of the Definitive Series' graphic-black ink texture property.</summary>
    public static readonly ulong InkTextureKey = TelltaleCrc64.HashSymbol("Material - Ink Texture");

    /// <summary>Key symbol of the detail texture property (line art or ink, by its role).</summary>
    public static readonly ulong DetailTextureKey = TelltaleCrc64.HashSymbol("Material - Detail Texture");

    /// <summary>Key symbol of the emissive (glow) texture property, e.g. Tales from the Borderlands' hologram <c>*_glow</c> maps.</summary>
    public static readonly ulong EmissiveTextureKey = TelltaleCrc64.HashSymbol("Material - Emissive Texture");

    /// <summary>Texture roles that are never a surface's color.</summary>
    private const MaterialTextureTypes NonColorTypes =
        MaterialTextureTypes.NormalMap | MaterialTextureTypes.NormalXYMap | MaterialTextureTypes.NormalDxDyMap |
        MaterialTextureTypes.ObjectNormalMap | MaterialTextureTypes.Vector2Map | MaterialTextureTypes.SingleChannelDetailMap |
        MaterialTextureTypes.PackedDetailMap | MaterialTextureTypes.SharpDetailMap | MaterialTextureTypes.LinePatternMap |
        MaterialTextureTypes.LineVisibilityMap | MaterialTextureTypes.LineGenerationStyleMap |
        MaterialTextureTypes.CubeMap | MaterialTextureTypes.VolumeMap;

    /// <summary>True when the material sets its diffuse texture property.</summary>
    public static bool HasDiffuseProperty(MaterialData material) =>
        material.Textures.Any(t => t.Key == DiffuseTextureKey && t.TextureSymbol != 0);

    /// <summary>The texture bound to the material's diffuse property, or null when unset or unknown.</summary>
    public static string? EngineDiffuse(MaterialData material, Func<ulong, string?> textureForSymbol) =>
        material.Textures.Where(t => t.Key == DiffuseTextureKey && t.TextureSymbol != 0)
            .Select(t => textureForSymbol(t.TextureSymbol)).FirstOrDefault(n => n is not null);

    /// <summary>
    /// The material's diffuse texture: its diffuse property when set and known; otherwise the
    /// most diffuse-looking texture of <paramref name="payload"/>, leaving out textures the
    /// compiled material declares as normal, flow, detail or similar maps, and shared
    /// detail atlases.
    /// </summary>
    public static string? PickDiffuseTexture(MaterialData material, byte[] payload, Func<ulong, string?> textureForSymbol)
    {
        if (EngineDiffuse(material, textureForSymbol) is { } diffuse)
            return diffuse;
        if (HasDiffuseProperty(material))
        {
            // The diffuse the material names is not shipped (generic NPCs in Tales from the
            // Borderlands). Its maps are named after it (face_nm → face), so try the stem of
            // the normal map; failing that, no texture beats painting a map on as color.
            foreach (var map in material.Textures.Where(t => t.Key == NormalMapKey && t.TextureSymbol != 0))
            {
                if (textureForSymbol(map.TextureSymbol) is not { } normal)
                    continue;
                var stem = Path.GetFileNameWithoutExtension(normal);
                foreach (var marker in NormalMapMarkers)
                {
                    if (!stem.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var color = stem[..^marker.Length];
                    if ((textureForSymbol(TelltaleCrc64.HashSymbol(color + ".d3dtx")) ??
                         textureForSymbol(TelltaleCrc64.HashSymbol(color))) is { } found)
                        return found;
                }
            }
            return null;
        }
        var excluded = material.Textures
            .Where(t => t.TextureSymbol != 0 && ((t.Types & NonColorTypes) != 0 && (t.Types & (MaterialTextureTypes.PrimaryColorMap | MaterialTextureTypes.ColorMap)) == 0 || t.Layout == 3))
            .Select(t => t.TextureSymbol)
            .ToHashSet();
        return PickDiffuseTexture(payload, symbol => excluded.Contains(symbol) ? null : textureForSymbol(symbol));
    }

    /// <summary>The material's normal map: its normal map property when set, otherwise judged by name.</summary>
    public static string? PickNormalMap(MaterialData material, byte[] payload, Func<ulong, string?> textureForSymbol)
    {
        var bound = material.Textures.Where(t => t.Key == NormalMapKey && t.TextureSymbol != 0)
            .Select(t => textureForSymbol(t.TextureSymbol)).FirstOrDefault(n => n is not null);
        // Engine placeholders (normalxy_000) mean "flat": no map.
        if (bound is not null)
            return IsEnginePlaceholder(bound) ? null : bound;
        return PickNormalMap(payload, textureForSymbol);
    }

    /// <summary>The texture bound to the material's specular map property, or null.</summary>
    public static string? PickSpecularMap(MaterialData material, Func<ulong, string?> textureForSymbol) =>
        material.Textures.Where(t => t.Key == SpecularMapKey && t.TextureSymbol != 0)
            .Select(t => textureForSymbol(t.TextureSymbol))
            .FirstOrDefault(n => n is not null && !IsEnginePlaceholder(n));

    /// <summary>The texture bound to the material's emissive texture property, or null.</summary>
    public static string? PickEmissiveMap(MaterialData material, Func<ulong, string?> textureForSymbol) =>
        material.Textures.Where(t => t.Key == EmissiveTextureKey && t.TextureSymbol != 0)
            .Select(t => textureForSymbol(t.TextureSymbol))
            .FirstOrDefault(n => n is not null && !IsEnginePlaceholder(n));

    /// <summary>
    /// The material's ink texture and how it applies: the Definitive Series' "Ink Texture"
    /// (graphic-black shadow ink); otherwise its "Detail Texture": drawn line art when the
    /// compiled role is a sharp detail map (Tales from the Borderlands), shadow ink when it is
    /// a single-channel detail map (the ported earlier seasons). Null when there is none.
    /// </summary>
    public static (string Name, InkMapKind Kind)? PickInkTexture(MaterialData material, Func<ulong, string?> textureForSymbol)
    {
        foreach (var binding in material.Textures)
        {
            if (binding.Key != InkTextureKey || binding.TextureSymbol == 0)
                continue;
            if (textureForSymbol(binding.TextureSymbol) is { } ink && !IsEnginePlaceholder(ink))
                return (ink, InkMapKind.Shadow);
        }
        foreach (var binding in material.Textures)
        {
            if (binding.Key != DetailTextureKey || binding.TextureSymbol == 0 ||
                textureForSymbol(binding.TextureSymbol) is not { } detail || IsEnginePlaceholder(detail))
                continue;
            if ((binding.Types & MaterialTextureTypes.SharpDetailMap) != 0)
                return (detail, InkMapKind.LineArt);
            if ((binding.Types & MaterialTextureTypes.SingleChannelDetailMap) != 0)
                return (detail, InkMapKind.Shadow);
        }
        return null;
    }

    /// <summary>
    /// For meshes without material data (The Wolf Among Us, Game of Thrones), the normal map
    /// and drawn line art among <paramref name="meshTextures"/> that belong to the color
    /// texture <paramref name="diffuse"/>, judged by name: <c>head</c> → <c>head_nm</c> or
    /// <c>head_brushnormal</c>, and <c>head_lines</c>, <c>head_detail</c> or the character's
    /// <c>inkLinesHead</c>.
    /// </summary>
    public static (string? Normal, string? Lines) PickLegacyMaps(string diffuse, IEnumerable<string> meshTextures)
    {
        var stem = Path.GetFileNameWithoutExtension(diffuse);
        var byStem = meshTextures.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => Path.GetFileNameWithoutExtension(t), StringComparer.OrdinalIgnoreCase);
        string? Find(params string[] suffixes) =>
            suffixes.Select(sfx => byStem.GetValueOrDefault(stem + sfx)).FirstOrDefault(t => t is not null);
        var normal = Find("_nm", "_nrm", "_norm", "_brushnormal");
        var lines = Find("_lines", "_detail", "_ink");
        if (lines is null)
        {
            // "sk54_bigby_head" draws its lines from "sk54_bigby_inkLinesHead".
            int at = stem.LastIndexOf('_');
            if (at > 0 && at < stem.Length - 1)
            {
                var part = stem[(at + 1)..];
                lines = byStem.FirstOrDefault(kv =>
                    kv.Key.StartsWith(stem[..at], StringComparison.OrdinalIgnoreCase) &&
                    kv.Key.EndsWith("inkLines" + part, StringComparison.OrdinalIgnoreCase)).Value;
            }
        }
        return (normal, lines);
    }

    private static readonly string[] NonDiffuseMarkers =
        { "_nm", "_nrm", "_norm", "_normal", "_bnm", "detail", "_spec", "_specular", "_ink", "_ao", "mask", "_env", "_lightmap", "_lm",
          "_grime", "decal", "wrinkle" };

    private static readonly string[] NormalMapMarkers = { "_nm", "_nrm", "_norm" };

    /// <summary>
    /// Scans a material payload for texture symbols known to <paramref name="textureIndex"/>
    /// (symbol → file path) and picks the most diffuse-looking one, skipping
    /// normal/detail/spec maps by name. Returns null when nothing matches.
    /// </summary>
    public static string? PickDiffuseTexture(byte[] payload, IReadOnlyDictionary<ulong, string> textureIndex) =>
        PickDiffuseTexture(payload, symbol => textureIndex.TryGetValue(symbol, out var path) ? path : null);

    /// <summary>
    /// As <see cref="PickDiffuseTexture(byte[], IReadOnlyDictionary{ulong, string})"/>, resolving
    /// symbols through <paramref name="textureForSymbol"/> (returns null for unknown symbols).
    /// </summary>
    public static string? PickDiffuseTexture(byte[] payload, Func<ulong, string?> textureForSymbol)
    {
        // Engine placeholders (solid colors filling unused slots) can be listed before
        // the real diffuse, so they only win over normal/detail/spec maps.
        string? placeholder = null, fallback = null;
        var all = EnumerateTextures(payload, textureForSymbol).ToList();
        var candidates = new List<string>();
        foreach (var path in all)
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (IsEnginePlaceholder(name))
                placeholder ??= path;
            else if (IsNonDiffuseMap(name))
                fallback ??= path;
            else
                candidates.Add(path);
        }
        if (candidates.Count <= 1)
            return candidates.FirstOrDefault() ?? placeholder ?? fallback;

        // Telltale names a material's maps after its color texture (head → head_nm,
        // head_spec, head303_damageMask…), so the color texture is the stem the most
        // other textures extend; damage/blood overlays listed earlier lose to it.
        var stems = all.Select(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant()).Distinct().ToList();
        string best = candidates[0];
        int bestScore = -1;
        foreach (var path in candidates)
        {
            var stem = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            int score = stems.Count(s => s.Length > stem.Length && s.StartsWith(stem, StringComparison.Ordinal));
            if (score > bestScore)
            {
                best = path;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>True for normal, detail, specular, mask and similar maps (judged by name).</summary>
    public static bool IsNonDiffuseMap(string textureName)
    {
        var name = Path.GetFileNameWithoutExtension(textureName);
        foreach (var marker in NonDiffuseMarkers)
        {
            for (int at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = name.IndexOf(marker, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (marker[0] != '_' || EndsWord(name, at + marker.Length))
                    return true;
            }
        }
        return false;
    }

    // A suffix marker must end its word: "_norm" in sk55_norma_body and "_spec" in a
    // Specialist's texture start longer words, not map suffixes.
    private static bool EndsWord(string name, int end) => end >= name.Length || !char.IsLower(name[end]);

    /// <summary>
    /// True for the engine's built-in fill textures (<c>color_000</c>, <c>color_EEE</c>,
    /// <c>color_array_000</c>, <c>normalxy_000</c>) that materials use for unused slots.
    /// </summary>
    public static bool IsEnginePlaceholder(string textureName)
    {
        var name = Path.GetFileNameWithoutExtension(textureName).ToLowerInvariant();
        foreach (var prefix in new[] { "color_array_", "normalxy_", "color_" })
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var code = name[prefix.Length..];
                return code.Length is >= 3 and <= 8 && code.All(Uri.IsHexDigit);
            }
        }
        return false;
    }

    /// <summary>Finds the material's tangent-space normal map, if any.</summary>
    public static string? PickNormalMap(byte[] payload, IReadOnlyDictionary<ulong, string> textureIndex) =>
        PickNormalMap(payload, symbol => textureIndex.TryGetValue(symbol, out var path) ? path : null);

    /// <summary>
    /// Finds the material's tangent-space normal map, resolving symbols through a function:
    /// the one named after the color texture (head → head_nm) when present. Wrinkle maps
    /// (headWrinkle_nm) are never used; the game blends them in only for expressions,
    /// and applied permanently they age the face.
    /// </summary>
    public static string? PickNormalMap(byte[] payload, Func<ulong, string?> textureForSymbol)
    {
        var normals = EnumerateTextures(payload, textureForSymbol)
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                return NormalMapMarkers.Any(name.Contains) && !name.Contains("wrinkle");
            })
            .ToList();
        if (normals.Count <= 1)
            return normals.FirstOrDefault();

        if (PickDiffuseTexture(payload, textureForSymbol) is { } diffuse)
        {
            var stem = Path.GetFileNameWithoutExtension(diffuse).ToLowerInvariant();
            var own = normals.FirstOrDefault(n =>
                NormalMapMarkers.Any(m => Path.GetFileNameWithoutExtension(n).ToLowerInvariant() == stem + m));
            if (own is not null)
                return own;
        }
        return normals[0];
    }

    private static IEnumerable<string> EnumerateTextures(byte[] payload, Func<ulong, string?> textureForSymbol)
    {
        var seen = new HashSet<ulong>();
        for (int i = 0; i + 8 <= payload.Length; i++)
        {
            ulong symbol = BitConverter.ToUInt64(payload, i);
            if (seen.Contains(symbol))
                continue;
            if (textureForSymbol(symbol) is { } path)
            {
                seen.Add(symbol);
                yield return path;
            }
        }
    }
}
