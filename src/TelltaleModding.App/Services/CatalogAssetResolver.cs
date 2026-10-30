using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.Services;

/// <summary>Serves a scene's files straight out of a game's archives.</summary>
public sealed class CatalogAssetResolver : IAssetResolver
{
    private readonly GameCatalog _catalog;

    public CatalogAssetResolver(GameCatalog catalog) => _catalog = catalog;

    public byte[]? TryRead(string fileName) =>
        _catalog.Find(fileName) is { } entry ? _catalog.Read(entry) : null;

    // Material payloads are scanned 8 bytes at a time, so only texture files count as hits.
    public string? TextureNameForSymbol(ulong symbol) =>
        _catalog.FindBySymbol(symbol) is { } entry &&
        entry.Name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
            ? entry.Name
            : null;

    public string? NameForSymbol(ulong symbol) => _catalog.FindBySymbol(symbol)?.Name;

    public IEnumerable<string> MeshNamesStartingWith(string prefix) =>
        _catalog.Entries
            .Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                        e.Name.EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> SkeletonNames =>
        _catalog.Characters.Where(c => c.Skeleton is not null).Select(c => c.Skeleton!.Name).Distinct();
}
