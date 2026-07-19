namespace TelltaleModding.Core.Meshes;

/// <summary>
/// Finds the diffuse texture a material uses. Telltale's compiled material payloads
/// embed texture references as 8-byte name symbols; scanning for symbols that match
/// known texture files recovers them without parsing the full property set.
/// </summary>
public static class MaterialTextureLocator
{
    private static readonly string[] NonDiffuseMarkers =
        { "_nm", "_nrm", "_norm", "_detail", "_spec", "_ink", "_ao", "_mask", "_env", "_lightmap", "_lm" };

    private static readonly string[] NormalMapMarkers = { "_nm", "_nrm", "_norm" };

    /// <summary>
    /// Scans a material payload for texture symbols known to <paramref name="textureIndex"/>
    /// (symbol → file path) and picks the most diffuse-looking one, skipping
    /// normal/detail/spec maps by name. Returns null when nothing matches.
    /// </summary>
    public static string? PickDiffuseTexture(byte[] payload, IReadOnlyDictionary<ulong, string> textureIndex)
    {
        string? fallback = null;
        foreach (var path in EnumerateTextures(payload, textureIndex))
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (NonDiffuseMarkers.Any(name.Contains))
            {
                fallback ??= path;
                continue;
            }
            return path;
        }
        return fallback;
    }

    /// <summary>Finds the material's tangent-space normal map, if any.</summary>
    public static string? PickNormalMap(byte[] payload, IReadOnlyDictionary<ulong, string> textureIndex)
    {
        foreach (var path in EnumerateTextures(payload, textureIndex))
        {
            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (NormalMapMarkers.Any(name.Contains))
                return path;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateTextures(
        byte[] payload, IReadOnlyDictionary<ulong, string> textureIndex)
    {
        var seen = new HashSet<ulong>();
        for (int i = 0; i + 8 <= payload.Length; i++)
        {
            ulong symbol = BitConverter.ToUInt64(payload, i);
            if (textureIndex.TryGetValue(symbol, out var path) && seen.Add(symbol))
                yield return path;
        }
    }
}
