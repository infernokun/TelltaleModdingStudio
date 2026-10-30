using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class ChoreTimelineTests
{
    private static readonly ulong PointOfInterest = TelltaleCrc64.HashSymbol("PointOfInterestBlocking");

    private static ChoreFile Read(string sample)
    {
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample(sample)), out var chore, out var error), error);
        return chore!;
    }

    [Theory]
    [InlineData("obj_lookAtClementineEyes", "look at Clementine (eyes)")]
    [InlineData("obj_lookAtAJEyes", "look at AJ (eyes)")]
    [InlineData("obj_lookAtClementineFlashbackEyes", "look at Clementine Flashback (eyes)")]
    [InlineData("obj_lookAtZombieTrainStationFemaleEyes", "look at Zombie Train Station Female (eyes)")]
    [InlineData("obj_lookAtLee", "look at Lee")]
    [InlineData("Clementine", "point of interest")]
    public void Names_a_look_at_target_after_its_agent(string agent, string expected) =>
        Assert.Equal(expected, ChoreTimeline.EmbeddedName(PointOfInterest, agent));

    [Theory]
    [InlineData("StyleIdleTransitionsRes", "style idle")]
    [InlineData("Procedural_LookAt", "look-at")]
    [InlineData("ProceduralEyes", "eye movement")]
    [InlineData("BlendCameraResource", "camera blend")]
    [InlineData("ParticleInverseKinematics", "particle IK")]
    // Other known types are spelled out from their name.
    [InlineData("CameraSelect", "camera select")]
    [InlineData("PropertySet", "property set")]
    public void Names_embedded_data_by_its_type(string type, string expected) =>
        Assert.Equal(expected, ChoreTimeline.EmbeddedName(TelltaleCrc64.HashSymbol(type), "Clementine"));

    [Fact]
    public void Embedded_data_of_an_unknown_type_says_where_it_lives()
    {
        Assert.Equal("data stored in the chore", ChoreTimeline.EmbeddedName(0, "Bigby"));
        Assert.Equal("data stored in the chore", ChoreTimeline.EmbeddedName(0x1234_5678_9ABC_DEF0, "obj_lookAtBigbyEyes"));
    }

    [Theory]
    [InlineData(null, null, "throughout")]
    [InlineData(-1.15f, 0f, "lead-in")]
    [InlineData(-0.1f, 0.74f, "0.0–0.7s")]
    [InlineData(8.7f, 11.77f, "8.7–11.8s")]
    public void Formats_when_a_row_plays(float? start, float? end, string expected) =>
        Assert.Equal(expected, ChoreTimeline.Span(start, end));

    [SkippableFact]
    public void Names_procedural_rows_of_a_real_chore()
    {
        var chore = Read("env_cave_act1_cs_hardWorld_1.chore");
        var tracks = ChoreTimeline.Build(chore, _ => null);

        var target = Assert.Single(Assert.Single(tracks, t => t.Agent == "obj_lookAtClementineEyes").Items);
        Assert.Equal("look at Clementine (eyes)", target.Name);
        Assert.Equal(ChoreItemKind.Embedded, target.Kind);

        var aj = Assert.Single(tracks, t => t.Agent == "AJ");
        var idle = Assert.Single(aj.Items, i => i.Name == "style idle");
        Assert.Equal(ChoreItemKind.Embedded, idle.Kind);
        Assert.Equal(3, idle.Blocks.Count);
        Assert.Contains(tracks.Single(t => t.Agent == "Clementine").Items, i => i.Name == "particle IK");

        // Every embedded resource of this chore has a known type, so none is left as plain data.
        Assert.DoesNotContain(tracks.SelectMany(t => t.Items), i => i.Name == "data stored in the chore");
        var (name, details) = ChoreTimeline.Describe(chore, target, new Dictionary<ulong, DialogueLine>());
        Assert.Equal("look at Clementine (eyes)", name);
        Assert.Contains("type PointOfInterestBlocking", details);
    }

    [SkippableFact]
    public void Writes_a_chore_as_a_text_timeline()
    {
        var chore = Read("env_cave_act1_cs_hardWorld_1.chore");
        var files = new[] { "sk63_ajStandA_toArmsX.anm", "aJ_face_blink.chore", "adv_cave.scene" }
            .ToDictionary(TelltaleCrc64.HashSymbol);
        // The first of AJ's lines, as the scene's subtitle database would give it.
        int ajIndex = chore.Agents.Single(a => a.Name == "AJ").Resources
            .First(i => chore.Resources[i].Type == ChoreFile.LanguageResType && chore.Resources[i].Blocks[0].Start < 4);
        var voice = chore.Resources[ajIndex];
        var lines = new Dictionary<ulong, DialogueLine>
        {
            [voice.Name] = new DialogueLine(396764596, "AJ", "Killing Lilly made us safe.", Array.Empty<ulong>()) { NameSymbol = voice.Name },
        };

        var text = ChoreTimeline.ToText(chore, r => ChoreNames.NameOf(r, lines, files.GetValueOrDefault), lines);
        var rows = text.Split('\n');

        Assert.Equal("env_cave_act1_cs_hardWorld_1.chore", rows[0]);
        Assert.Equal("scene    adv_cave.scene", rows[1]);
        Assert.Equal("length   47.75 s, 150 resources, 13 agents", rows[2]);
        // A heading per agent with what it does, then its rows.
        Assert.Contains(rows, r => r.StartsWith("AJ  (3 voice lines · ", StringComparison.Ordinal));
        Assert.Contains("  3.4–7.4s       Voice line  Aj: \"Killing Lilly made us safe.\"  [line 396764596]", rows);
        Assert.Contains("  8.7–11.8s      Animation   sk63_ajStandA_toArmsX.anm", rows);
        // Every span of a resource that plays more than once.
        Assert.Contains(rows, r => r.Contains("aJ_face_blink.chore  [at 3.0–3.6s, 8.4–8.9s]"));
        Assert.Contains(rows, r => r.Contains("Procedural  style idle  [type StyleIdleTransitionsRes; at 3.1–3.6s, 4.6–5.1s, 6.7–8.2s]"));
        Assert.Contains("  throughout     Procedural  look at AJ (eyes)  [type PointOfInterestBlocking]", rows);
        Assert.Contains(rows, r => r.Contains("unnamed setting  [symbol 0x"));
        // One row per resource.
        Assert.Equal(chore.Resources.Count, rows.Count(r => r.StartsWith("  ", StringComparison.Ordinal)));
    }
}
