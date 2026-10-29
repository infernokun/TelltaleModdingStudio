using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.Core.Dialogue;

/// <summary>A dialogue line placed in its game: scene, episode and voice file.</summary>
public sealed record DialogueEntry(DialogueLine Line, string Scene, string Episode, ArchiveEntryRef Source, ArchiveEntryRef? Audio)
{
    public bool HasAudio => Audio is not null;
}

/// <summary>
/// Every line of one language across a game's <c>.landb</c> files, matched to the
/// <c>&lt;id&gt;.wav</c> voice files the game plays for them.
/// </summary>
public sealed partial class DialogueIndex
{
    public string Language { get; }

    /// <summary>The languages the game ships text for, English first.</summary>
    public IReadOnlyList<string> Languages { get; }

    public IReadOnlyList<DialogueEntry> Lines { get; }

    /// <summary>Speakers by line count, most talkative first.</summary>
    public IReadOnlyList<(string Speaker, int Lines)> Speakers { get; }

    /// <summary><c>.landb</c> files that could not be read.</summary>
    public int UnreadableFiles { get; }

    private DialogueIndex(string language, IReadOnlyList<string> languages, IReadOnlyList<DialogueEntry> lines, int unreadable)
    {
        Language = language;
        Languages = languages;
        Lines = lines;
        UnreadableFiles = unreadable;
        Speakers = lines.Where(l => l.Line.Speaker.Length > 0)
            .GroupBy(l => l.Line.Speaker, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Line.Speaker, g.Count()))
            .OrderByDescending(s => s.Item2).ThenBy(s => s.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The languages <paramref name="entries"/> has <c>.landb</c> files for, English first.</summary>
    public static IReadOnlyList<string> LanguagesOf(IEnumerable<ArchiveEntryRef> entries) =>
        entries.Where(e => IsLandb(e.Name))
            .Select(e => LandbFile.SplitName(e.Name).Language)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l.Equals("english", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Reads every <c>.landb</c> of <paramref name="language"/> (in parallel) and links
    /// each line to its voice file. Duplicate ids (lines reused by several scenes) are
    /// kept once. <paramref name="progress"/> reports files read out of the total.
    /// </summary>
    public static DialogueIndex Build(GameCatalog catalog, string language = "english",
        IProgress<(int Done, int Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var languages = LanguagesOf(catalog.Entries);
        var files = catalog.Entries
            .Where(e => IsLandb(e.Name) && LandbFile.SplitName(e.Name).Language.Equals(language, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var perFile = new ConcurrentBag<(ArchiveEntryRef File, IReadOnlyList<DialogueLine> Lines)>();
        int done = 0, unreadable = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 }, file =>
        {
            try
            {
                if (LandbFile.TryRead(catalog.Read(file), out var lines, out _))
                    perFile.Add((file, lines));
                else
                    Interlocked.Increment(ref unreadable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref unreadable);
            }
            progress?.Report((Interlocked.Increment(ref done), files.Count));
        });

        var seen = new HashSet<uint>();
        var entries = new List<DialogueEntry>();
        foreach (var (file, lines) in perFile.OrderBy(f => EpisodeOf(f.File.ArchivePath), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(f => f.File.Name, StringComparer.OrdinalIgnoreCase))
        {
            var scene = LandbFile.SplitName(file.Name).Scene;
            var episode = EpisodeOf(file.ArchivePath);
            foreach (var line in lines)
            {
                if (line.RawText.Length == 0 || !seen.Add(line.Id))
                    continue;
                entries.Add(new DialogueEntry(line, scene, episode, file, FindAudio(catalog, line)));
            }
        }
        return new DialogueIndex(language, languages, entries, unreadable);
    }

    private static ArchiveEntryRef? FindAudio(GameCatalog catalog, DialogueLine line)
    {
        foreach (var symbol in line.LinkedSymbols)
        {
            // The symbol may name the borrowed line's lip-sync clip rather than its voice.
            var linked = catalog.FindBySymbol(symbol);
            if (linked is null)
                continue;
            if (linked.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                return linked;
            var voice = catalog.Find(Path.ChangeExtension(linked.Name, ".wav"));
            if (voice is not null)
                return voice;
        }
        return catalog.Find(line.AudioFileName);
    }

    private static bool IsLandb(string name) => name.EndsWith(".landb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A short episode label from an archive name: <c>WDC_pc_WalkingDead304_data.ttarch2</c>
    /// → <c>WalkingDead304</c>, <c>Borderlands_pc_Boot_data.ttarch2</c> → <c>Boot</c>.
    /// </summary>
    public static string EpisodeOf(string archivePath)
    {
        var stem = Path.GetFileNameWithoutExtension(archivePath);
        int pc = stem.IndexOf("_pc_", StringComparison.OrdinalIgnoreCase);
        if (pc >= 0)
            stem = stem[(pc + 4)..];
        return ArchiveSuffix().Replace(stem, "");
    }

    [GeneratedRegex(@"_(data|all|uncompressed|txmesh|ms|anichore|voice|sound|lang)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveSuffix();
}
