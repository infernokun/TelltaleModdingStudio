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

    /// <summary>
    /// Scans a material payload for texture symbols known to <paramref name="textureIndex"/>
    /// (symbol → file path) and picks the most diffuse-looking one, skipping
    /// normal/detail/spec maps by name. Returns null when nothing matches.
    /// </summary>
    public static string? PickDiffuseTexture(byte[] payload, IReadOnlyDictionary<ulong, string> textureIndex)
    {
        string? fallback = null;
        var seen = new HashSet<ulong>();
        for (int i = 0; i + 8 <= payload.Length; i++)
        {
            ulong symbol = BitConverter.ToUInt64(payload, i);
            if (!textureIndex.TryGetValue(symbol, out var path) || !seen.Add(symbol))
                continue;
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
}
