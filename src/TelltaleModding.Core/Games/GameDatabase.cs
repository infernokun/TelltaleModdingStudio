using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TelltaleModding.Core.Games;

/// <summary>
/// The catalogue of games ttarchext can handle, loaded once from the embedded
/// <c>games.json</c> (generated directly from ttarchext's <c>gamekeys[]</c> table
/// so the indices stay authoritative).
/// </summary>
public static class GameDatabase
{
    private static readonly Lazy<ImmutableArray<GameEntry>> _games = new(Load);

    /// <summary>All games, ordered by <see cref="GameEntry.GameNum"/>.</summary>
    public static ImmutableArray<GameEntry> Games => _games.Value;

    /// <summary>Looks up a game by its ttarchext index, or null if out of range.</summary>
    public static GameEntry? ByNumber(int gameNum) =>
        gameNum >= 0 && gameNum < Games.Length ? Games[gameNum] : null;

    /// <summary>Case-insensitive exact-name lookup.</summary>
    public static GameEntry? ByName(string name) =>
        Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Substring search over game names, ordered by index.</summary>
    public static IEnumerable<GameEntry> Search(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return Games;
        return Games.Where(g => g.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static ImmutableArray<GameEntry> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        // Resource name follows <RootNamespace>.<folder>.<file> with '/' -> '.'.
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("games.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded games.json resource not found.");

        using var stream = asm.GetManifestResourceStream(resourceName)!;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        var raw = JsonSerializer.Deserialize<List<GameEntry>>(stream, options)
                  ?? throw new InvalidOperationException("games.json deserialized to null.");

        return raw.OrderBy(g => g.GameNum).ToImmutableArray();
    }
}
