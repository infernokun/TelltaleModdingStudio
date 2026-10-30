using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// Names a chore's resources from a game's files: voice lines by speaker and text from
/// the English databases that go with the chore, everything else by its file name.
/// Databases are read once and kept, so one instance can name many chores (an export);
/// it is not safe to share between threads.
/// </summary>
public sealed class ChoreNames
{
    private readonly GameCatalog _catalog;
    private List<(string Scene, ArchiveEntryRef Entry)>? _databases;
    private readonly Dictionary<string, IReadOnlyList<DialogueLine>> _read = new(StringComparer.OrdinalIgnoreCase);

    public ChoreNames(GameCatalog catalog) => _catalog = catalog;

    /// <summary>
    /// The voice lines a chore can name, by name symbol: those of the English databases
    /// whose scene name starts the chore's name (env_cave_act1.landb for
    /// env_cave_act1_cs_hardWorld_1.chore).
    /// </summary>
    public IReadOnlyDictionary<ulong, DialogueLine> LinesFor(string choreFileName)
    {
        _databases ??= _catalog.Entries
            .Where(e => e.Name.EndsWith(".landb", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Split: LandbFile.SplitName(e.Name), Entry: e))
            .Where(x => x.Split.Language.Equals("english", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Split.Scene, x.Entry))
            .ToList();
        var stem = Path.GetFileNameWithoutExtension(choreFileName);
        var lines = new Dictionary<ulong, DialogueLine>();
        foreach (var (scene, entry) in _databases)
        {
            if (!stem.StartsWith(scene, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_read.TryGetValue(entry.Name, out var read))
            {
                read = Array.Empty<DialogueLine>();
                try
                {
                    if (LandbFile.TryRead(_catalog.Read(entry), out var parsed, out _))
                        read = parsed;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                }
                _read[entry.Name] = read;
            }
            foreach (var line in read)
                lines.TryAdd(line.NameSymbol, line);
        }
        return lines;
    }

    /// <summary>A resource's name from the game's files, or null when none has it.</summary>
    public string? NameOf(ChoreResource resource, IReadOnlyDictionary<ulong, DialogueLine> lines) =>
        NameOf(resource, lines, symbol => _catalog.FindBySymbol(symbol)?.Name);

    /// <summary>
    /// A resource's name: a voice line as <c>Speaker: "text"</c>, a nested chore by its own
    /// name, anything else by the file <paramref name="fileName"/> finds for its symbol.
    /// </summary>
    public static string? NameOf(ChoreResource resource, IReadOnlyDictionary<ulong, DialogueLine> lines, Func<ulong, string?> fileName)
    {
        if (resource.Type == ChoreFile.LanguageResType && lines.TryGetValue(resource.Name, out var line))
            return line.Speaker.Length == 0 ? $"\"{line.Text}\"" : $"{TitleCase(line.Speaker)}: \"{line.Text}\"";
        if (resource.EmbeddedChore is { } nested)
            return nested.Name;
        return fileName(resource.Name);
    }

    /// <summary>The chore's timeline as text (see <see cref="ChoreTimeline.ToText"/>), named from the game's files.</summary>
    public string Text(string choreFileName, ChoreFile chore)
    {
        var lines = LinesFor(choreFileName);
        return ChoreTimeline.ToText(chore, r => NameOf(r, lines), lines);
    }

    // Speakers are stored in capitals ("CLEMENTINE").
    private static string TitleCase(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
}
