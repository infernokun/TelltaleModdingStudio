using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>One agent of a chore shown in the Assets preview: its name, a count of what it does, and its rows.</summary>
public sealed record ChoreTrackViewModel(string Agent, string Summary, IReadOnlyList<ChoreRowViewModel> Rows);

/// <summary>One resource of a chore: when it plays, what it is, and its spans as fractions of the chore.</summary>
public sealed record ChoreRowViewModel(string Time, string Kind, string Name, string Detail,
    IReadOnlyList<(double Start, double End)> Spans, bool IsVoice, bool IsGesture)
{
    public string ToolTip => Detail.Length == 0 ? Name : $"{Name}\n{Detail}";
}

/// <summary>Turns a chore into the per-agent timeline the Assets tab shows.</summary>
public static class ChorePreview
{
    public sealed record Result(string Info, IReadOnlyList<string> Details, IReadOnlyList<ChoreTrackViewModel> Tracks);

    public static Result Build(GameCatalog catalog, string fileName, ChoreFile chore)
    {
        var names = new ChoreNames(catalog);
        var lines = names.LinesFor(fileName);

        double length = Math.Max(chore.Length, 0.001);
        var tracks = ChoreTimeline.Build(chore, r => names.NameOf(r, lines)).Select(track =>
        {
            var rows = track.Items.Select(item =>
            {
                var (name, details) = ChoreTimeline.Describe(chore, item, lines);
                var spans = item.Blocks.Select(b => (b.Start / length, b.End / length)).ToList();
                return new ChoreRowViewModel(ChoreTimeline.Span(item.Start, item.End), ChoreTimeline.Label(item.Kind), name,
                    string.Join(" · ", details), spans, item.Kind == ChoreItemKind.VoiceLine, item.Kind == ChoreItemKind.Gesture);
            }).ToList();
            return new ChoreTrackViewModel(track.Agent, ChoreTimeline.Summarize(track.Items), rows);
        }).ToList();

        int voices = chore.Resources.Count(r => r.Type == ChoreFile.LanguageResType);
        int animations = chore.Resources.Count(r => r.Type == ChoreFile.AnimationType);
        var info = $"{chore.Length:0.0} s · {chore.Agents.Count} agents · {animations} animations" +
                   (voices > 0 ? $" · {voices} voice lines" : "");
        var detailLines = new List<string>();
        if (chore.SceneFile.Length > 0)
            detailLines.Add($"scene    {chore.SceneFile}");
        detailLines.Add($"length   {chore.Length:0.00} s, {chore.Resources.Count} resources");
        return new Result(info, detailLines, tracks);
    }
}
