using System.Text;
using System.Text.RegularExpressions;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>What a chore resource is, in the terms a reader of the timeline cares about.</summary>
public enum ChoreItemKind
{
    Animation,
    /// <summary>An additive animation layered on top (<c>_add.anm</c>): breaths, head turns, gestures.</summary>
    Gesture,
    Chore,
    VoiceLine,
    /// <summary>Data the chore carries itself, such as a procedural look-at.</summary>
    Embedded,
    /// <summary>A resource with no play span: a style guide, a prop or the scene it sets up.</summary>
    Setting,
}

/// <summary>One resource on a chore's timeline, with the spans it plays in (none for settings, which apply throughout).</summary>
public sealed record ChoreTimelineItem(int Index, ChoreItemKind Kind, string Name, IReadOnlyList<ChoreBlock> Blocks, int Priority)
{
    public float? Start => Blocks.Count == 0 ? null : Blocks.Min(b => b.Start);
    public float? End => Blocks.Count == 0 ? null : Blocks.Max(b => b.End);
    public bool Looping => Blocks.Any(b => b.Looping);
}

/// <summary>The resources of one agent (a character, camera, light...), in play order.</summary>
public sealed record ChoreTimelineTrack(string Agent, IReadOnlyList<ChoreTimelineItem> Items);

/// <summary>A chore laid out per agent for reading: what plays, for whom, and when.</summary>
public static partial class ChoreTimeline
{
    private static readonly ulong StyleIdleType = TelltaleCrc64.HashSymbol("StyleIdleTransitionsRes");

    /// <param name="nameOf">
    /// A readable name for a resource, or null to show its symbol. Data stored in the chore
    /// that has no name is named after its type and owner (see <see cref="EmbeddedName"/>).
    /// </param>
    public static IReadOnlyList<ChoreTimelineTrack> Build(ChoreFile chore, Func<ChoreResource, string?> nameOf)
    {
        var owner = new Dictionary<int, string>();
        foreach (var agent in chore.Agents)
            foreach (int index in agent.Resources)
                owner.TryAdd(index, agent.Name);

        var tracks = new Dictionary<string, List<ChoreTimelineItem>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (int i = 0; i < chore.Resources.Count; i++)
        {
            var resource = chore.Resources[i];
            var agent = owner.GetValueOrDefault(i, "(no agent)");
            if (!tracks.TryGetValue(agent, out var items))
            {
                tracks[agent] = items = new List<ChoreTimelineItem>();
                order.Add(agent);
            }
            var name = nameOf(resource) ??
                       (resource.Embedded && resource.EmbeddedChore is null ? EmbeddedName(resource.Type, agent) : $"0x{resource.Name:X16}");
            var kind = KindOf(resource, name);
            items.Add(new ChoreTimelineItem(i, kind, name, resource.Blocks, resource.Priority));
        }

        // Characters first (the agents with voice lines or animations), then everything else.
        return order
            .Select(a => new ChoreTimelineTrack(a, tracks[a]
                .OrderBy(t => t.Start is null ? 0 : 1).ThenBy(t => t.Start).ThenBy(t => t.Index).ToList()))
            .OrderByDescending(t => t.Items.Any(i => i.Kind == ChoreItemKind.VoiceLine))
            .ThenByDescending(t => t.Items.Count(i => i.Kind is ChoreItemKind.Animation or ChoreItemKind.Gesture))
            .ToList();
    }

    public static ChoreItemKind KindOf(ChoreResource resource, string name)
    {
        if (resource.Type == ChoreFile.LanguageResType)
            return ChoreItemKind.VoiceLine;
        if (resource.Type == ChoreFile.ChoreType)
            return ChoreItemKind.Chore;
        if (resource.Type == ChoreFile.AnimationType)
            return Path.GetFileNameWithoutExtension(name).EndsWith("_add", StringComparison.OrdinalIgnoreCase)
                ? ChoreItemKind.Gesture
                : ChoreItemKind.Animation;
        if (resource.Embedded && resource.Type != StyleIdleType)
            return ChoreItemKind.Embedded;
        return resource.Blocks.Count == 0 ? ChoreItemKind.Setting : ChoreItemKind.Embedded;
    }

    public static string Label(ChoreItemKind kind) => kind switch
    {
        ChoreItemKind.Animation => "Animation",
        ChoreItemKind.Gesture => "Gesture",
        ChoreItemKind.Chore => "Chore",
        ChoreItemKind.VoiceLine => "Voice line",
        ChoreItemKind.Embedded => "Procedural",
        _ => "Setting",
    };

    /// <summary>
    /// A name for data stored in the chore, whose own name is a symbol no file has: what
    /// its type does, and for a look-at target (agent <c>obj_lookAtClementineEyes</c>) whose
    /// eyes or head it marks.
    /// </summary>
    public static string EmbeddedName(ulong type, string agent)
    {
        var typeName = KnownTypeNames.Resolve(type);
        switch (typeName)
        {
            case "PointOfInterestBlocking":
                var target = LookAtTarget().Match(agent);
                if (!target.Success)
                    return "point of interest";
                var who = Words(target.Groups["who"].Value, lower: false);
                return target.Groups["part"].Success
                    ? $"look at {who} ({target.Groups["part"].Value.ToLowerInvariant()})"
                    : $"look at {who}";
            case "Procedural_LookAt":
                return "look-at";
            case "ProceduralEyes":
                return "eye movement";
            case "StyleIdleTransitionsRes":
                return "style idle";
            case "ParticleInverseKinematics":
                return "particle IK";
            case "BlendCameraResource":
                return "camera blend";
            case null or "Animation" or "Chore" or "LanguageRes":
                return "data stored in the chore";
            default:
                return Words(typeName, lower: true);
        }
    }

    [GeneratedRegex("^obj_lookAt(?<who>.+?)(?<part>Eyes|Head|Face|Body|Chest|Hand)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LookAtTarget();

    // "ClementineFlashback" to "Clementine Flashback"; lowered, "CameraSelect" to "camera select".
    // Runs of capitals ("AJ", "IK") stay as they are.
    private static string Words(string name, bool lower)
    {
        var words = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '_')
            {
                words.Append(' ');
                continue;
            }
            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]))
                words.Append(' ');
            bool acronym = char.IsUpper(c) && (i + 1 < name.Length && char.IsUpper(name[i + 1]) || i > 0 && char.IsUpper(name[i - 1]));
            words.Append(lower && !acronym ? char.ToLowerInvariant(c) : c);
        }
        return string.Join(' ', words.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// What a timeline row shows: its name (one missing from the game's files becomes
    /// "unnamed" and its kind) and the notes that go with it, such as the line id of a
    /// voice line or how many spans it plays in.
    /// </summary>
    public static (string Name, IReadOnlyList<string> Details) Describe(ChoreFile chore, ChoreTimelineItem item,
        IReadOnlyDictionary<ulong, DialogueLine> lines)
    {
        var resource = chore.Resources[item.Index];
        var details = new List<string>();
        // A name missing from the game's files is shown by its kind, with the symbol as a note.
        var name = item.Name;
        if (name.StartsWith("0x", StringComparison.Ordinal))
        {
            details.Add($"symbol {name}");
            name = $"unnamed {Label(item.Kind).ToLowerInvariant()}";
        }
        if (item.Kind == ChoreItemKind.VoiceLine && lines.TryGetValue(resource.Name, out var line))
            details.Add($"line {line.Id}");
        if (resource.Embedded && resource.EmbeddedChore is null)
            details.Add($"type {KnownTypeNames.Resolve(resource.Type) ?? $"0x{resource.Type:X16}"}");
        if (item.Blocks.Count > 1)
            details.Add($"{item.Blocks.Count} spans");
        if (item.Looping)
            details.Add("loops");
        if (!resource.Enabled)
            details.Add("disabled");
        if (resource.EmbeddedChore is { } nested)
            details.Add($"embedded · {nested.Resources.Count} resources");
        return (name, details);
    }

    /// <summary>
    /// When a row plays: "3.4–7.4s", or "throughout" with no span. Spans can begin before
    /// the chore does, so a move is already under way at 0 s; one that ends by then is a "lead-in".
    /// </summary>
    public static string Span(float? start, float? end) => (start, end) switch
    {
        (null, _) or (_, null) => "throughout",
        (_, <= 0) => "lead-in",
        ({ } s, { } e) => $"{Math.Max(0, s):0.0}–{e:0.0}s",
    };

    /// <summary>A count of what an agent does: "2 voice lines · 14 animations · 1 setting".</summary>
    public static string Summarize(IReadOnlyList<ChoreTimelineItem> items)
    {
        var parts = new List<string>();
        void Add(ChoreItemKind kind, string one, string many)
        {
            int n = items.Count(i => i.Kind == kind);
            if (n > 0)
                parts.Add(n == 1 ? $"1 {one}" : $"{n} {many}");
        }
        Add(ChoreItemKind.VoiceLine, "voice line", "voice lines");
        Add(ChoreItemKind.Animation, "animation", "animations");
        Add(ChoreItemKind.Gesture, "gesture", "gestures");
        Add(ChoreItemKind.Chore, "chore", "chores");
        Add(ChoreItemKind.Embedded, "procedural", "procedural");
        Add(ChoreItemKind.Setting, "setting", "settings");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The timeline as plain text, with the rows the Assets preview shows: a heading per
    /// agent, then a line per resource with when it plays, its kind, its name and notes.
    /// A resource that plays more than once lists every span.
    /// </summary>
    public static string ToText(ChoreFile chore, Func<ChoreResource, string?> nameOf, IReadOnlyDictionary<ulong, DialogueLine> lines)
    {
        var text = new StringBuilder();
        text.Append(chore.Name).Append('\n');
        if (chore.SceneFile.Length > 0)
            text.Append("scene    ").Append(chore.SceneFile).Append('\n');
        text.Append($"length   {chore.Length:0.00} s, {chore.Resources.Count} resources, {chore.Agents.Count} agents\n");
        foreach (var track in Build(chore, nameOf))
        {
            text.Append('\n').Append(track.Agent);
            var summary = Summarize(track.Items);
            if (summary.Length > 0)
                text.Append("  (").Append(summary).Append(')');
            text.Append('\n');
            foreach (var item in track.Items)
            {
                var (name, details) = Describe(chore, item, lines);
                var notes = details.Where(d => !d.EndsWith(" spans", StringComparison.Ordinal)).ToList();
                if (item.Blocks.Count > 1)
                    notes.Add("at " + string.Join(", ", item.Blocks.Select(b => Span(b.Start, b.End))));
                text.Append($"  {Span(item.Start, item.End),-14} {Label(item.Kind),-11} {name}");
                if (notes.Count > 0)
                    text.Append("  [").Append(string.Join("; ", notes)).Append(']');
                text.Append('\n');
            }
        }
        return text.ToString();
    }
}
