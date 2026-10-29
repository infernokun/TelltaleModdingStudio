using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>An asset resolver that can also name any file from its symbol (scenes reference props and meshes that way).</summary>
public interface ISceneAssetResolver : IAssetResolver
{
    /// <summary>The file a symbol (CRC64 of its name, with extension) refers to, or null when unknown.</summary>
    new string? NameForSymbol(ulong symbol);
}

/// <summary>Serves a scene's files out of a game catalog. Thread-safe.</summary>
public sealed class SceneAssetResolver : ISceneAssetResolver
{
    private readonly GameCatalog _catalog;

    public SceneAssetResolver(GameCatalog catalog) => _catalog = catalog;

    public byte[]? TryRead(string fileName) =>
        _catalog.Find(fileName) is { } entry ? _catalog.Read(entry) : null;

    // Material payloads are scanned 8 bytes at a time, so only texture files count as hits.
    public string? TextureNameForSymbol(ulong symbol) =>
        _catalog.FindBySymbol(symbol) is { } entry &&
        entry.Name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase)
            ? entry.Name
            : null;

    public string? NameForSymbol(ulong symbol) => _catalog.FindBySymbol(symbol)?.Name;

    public IEnumerable<string> SkeletonNames =>
        _catalog.Characters.Where(c => c.Skeleton is not null).Select(c => c.Skeleton!.Name).Distinct();
}

/// <summary>
/// Serves a scene's files from a folder of loose files (searched
/// recursively), naming files by symbol. <paramref name="aliases"/> maps game file names to
/// the names they are stored under (e.g. renamed samples).
/// </summary>
public sealed class SceneFolderAssetResolver : ISceneAssetResolver
{
    private readonly Dictionary<string, string> _pathByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, string> _nameBySymbol = new();
    private readonly Dictionary<ulong, string> _textureBySymbol = new();

    public SceneFolderAssetResolver(string folder, IReadOnlyDictionary<string, string>? aliases = null)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            Add(Path.GetFileName(path), path);
        if (aliases is not null)
            foreach (var (gameName, storedName) in aliases)
                if (_pathByName.TryGetValue(storedName, out var path))
                    Add(gameName, path);
    }

    private void Add(string name, string path)
    {
        _pathByName.TryAdd(name, path);
        _nameBySymbol.TryAdd(TelltaleCrc64.HashSymbol(name), name);
        if (name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase))
        {
            _textureBySymbol.TryAdd(TelltaleCrc64.HashSymbol(name), name);
            _textureBySymbol.TryAdd(TelltaleCrc64.HashSymbol(Path.GetFileNameWithoutExtension(name)), name);
        }
    }

    public byte[]? TryRead(string fileName) =>
        _pathByName.TryGetValue(fileName, out var path) ? File.ReadAllBytes(path) : null;

    public string? TextureNameForSymbol(ulong symbol) => _textureBySymbol.GetValueOrDefault(symbol);

    public string? NameForSymbol(ulong symbol) => _nameBySymbol.GetValueOrDefault(symbol);

    public IEnumerable<string> SkeletonNames =>
        _pathByName.Keys.Where(n => n.EndsWith(".skl", StringComparison.OrdinalIgnoreCase));
}
