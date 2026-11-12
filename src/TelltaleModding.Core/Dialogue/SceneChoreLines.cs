using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Dialogue;

/// <summary>
/// Where a scene's voice lines play in its chores: the chores its dialog names, plus the
/// chores named after the scene. A chore places a line by the symbol of <c>&lt;id&gt;.lang</c>.
/// </summary>
public sealed class SceneChoreLines
{
    private readonly Dictionary<ulong, ChoreLine> _bySymbol = new();
    private readonly Dictionary<uint, List<ChoreFile>> _exchanges = new();
    private readonly Dictionary<string, int> _sceneFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of chores read for the scene.</summary>
    public int ChoreCount { get; private set; }

    /// <summary>The scene files the chores are staged in, most used first.</summary>
    public IReadOnlyList<string> SceneFiles =>
        _sceneFiles.OrderByDescending(s => s.Value).Select(s => s.Key).ToList();

    /// <summary>Indexes the voice lines of <paramref name="chore"/>. Earlier chores win.</summary>
    public void Add(ChoreFile chore)
    {
        ChoreCount++;
        if (chore.SceneFile.Length > 0)
            _sceneFiles[chore.SceneFile] = _sceneFiles.GetValueOrDefault(chore.SceneFile) + 1;
        foreach (var line in ChorePerformance.Lines(chore))
            _bySymbol.TryAdd(line.Chore.Resources[line.Resource].Name, line);
    }

    /// <summary>Records that the dialog plays the lines <paramref name="lineIds"/> with <paramref name="chore"/>.</summary>
    public void AddExchange(IEnumerable<uint> lineIds, ChoreFile chore)
    {
        foreach (var id in lineIds)
        {
            if (!_exchanges.TryGetValue(id, out var chores))
                _exchanges[id] = chores = new List<ChoreFile>(1);
            if (!chores.Contains(chore))
                chores.Add(chore);
        }
    }

    /// <summary>Where <paramref name="line"/> plays, checking its exchange's chores first.</summary>
    public ChoreLine? Find(DialogueEntry line)
    {
        var symbols = VoiceSymbols(line);
        if (_exchanges.TryGetValue(line.Line.Id, out var chores))
        {
            foreach (var chore in chores)
                foreach (var symbol in symbols)
                    if (ChorePerformance.FindLine(chore, symbol) is { } found)
                        return found;
        }
        foreach (var symbol in symbols)
            if (_bySymbol.TryGetValue(symbol, out var found))
                return found;
        return null;
    }

    /// <summary>The symbol a chore places line <paramref name="id"/> by.</summary>
    public static ulong LineSymbol(uint id) => TelltaleCrc64.HashSymbol($"{id}.lang");

    /// <summary>
    /// The symbols a chore may place <paramref name="line"/> by: its own, then that of the
    /// line whose recording it reuses.
    /// </summary>
    public static IReadOnlyList<ulong> VoiceSymbols(DialogueEntry line)
    {
        var own = line.Line.NameSymbol != 0 ? line.Line.NameSymbol : LineSymbol(line.Line.Id);
        if (line.Audio is { } audio && uint.TryParse(Path.GetFileNameWithoutExtension(audio.Name), out var voiced) &&
            voiced != line.Line.Id)
            return new[] { own, LineSymbol(voiced) };
        return new[] { own };
    }

    /// <summary>
    /// Reads the chores of <paramref name="scene"/>: those its dialog plays, then those named after it.
    /// <paramref name="chores"/> is every <c>.chore</c> in the game.
    /// </summary>
    public static SceneChoreLines Build(GameCatalog catalog, IReadOnlyList<ArchiveEntryRef> chores, string scene)
    {
        var index = new SceneChoreLines();
        var read = new Dictionary<string, ChoreFile?>(StringComparer.OrdinalIgnoreCase);

        ChoreFile? Load(ArchiveEntryRef entry)
        {
            if (read.TryGetValue(entry.Name, out var cached))
                return cached;
            ChoreFile? chore = null;
            try
            {
                if (!ChoreFile.TryRead(catalog.Read(entry), out chore, out _))
                    chore = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
            }
            read[entry.Name] = chore;
            if (chore is not null)
                index.Add(chore);
            return chore;
        }

        if (catalog.Find(scene + ".dlog") is { } dlogEntry && TryReadDialog(catalog, dlogEntry) is { } dialog)
        {
            foreach (var node in dialog.Nodes)
            {
                if (node.ChoreSymbol == 0 || catalog.FindBySymbol(node.ChoreSymbol) is not { } entry ||
                    !entry.Name.EndsWith(".chore", StringComparison.OrdinalIgnoreCase) || Load(entry) is not { } chore)
                    continue;
                if (node.Kind == DlogNodeKind.Exchange)
                    index.AddExchange(node.LineIds, chore);
            }
        }

        var prefix = scene + "_";
        foreach (var entry in chores)
        {
            if (!entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            Load(entry);
        }
        return index;
    }

    private static DlogFile? TryReadDialog(GameCatalog catalog, ArchiveEntryRef entry)
    {
        try
        {
            return DlogFile.TryRead(catalog.Read(entry), out var dialog, out _) ? dialog : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }
}
