using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class ChoreFileTests
{
    private static ChoreFile Read(string sample)
    {
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample(sample)), out var chore, out var error), error);
        return chore!;
    }

    private static int IndexOf(ChoreFile chore, string name) =>
        chore.Resources.ToList().FindIndex(r => r.Name == TelltaleCrc64.HashSymbol(name));

    [SkippableFact]
    public void Reads_a_dialogue_exchange_chore()
    {
        var chore = Read("env_cave_act1_cs_hardWorld_1.chore");
        Assert.Equal("env_cave_act1_cs_hardWorld_1.chore", chore.Name);
        Assert.Equal("adv_cave.scene", chore.SceneFile);
        Assert.Equal(47.75f, chore.Length);
        Assert.Equal(150, chore.Resources.Count);
        Assert.Equal(13, chore.Agents.Count);

        // AJ turns, then crosses his arms, starting about 8.7 s in.
        int arms = IndexOf(chore, "sk63_ajStandA_toArmsX.anm");
        Assert.True(arms >= 0);
        var resource = chore.Resources[arms];
        Assert.Equal(ChoreFile.AnimationType, resource.Type);
        Assert.Equal(4, resource.Length, 0.01f);
        var block = Assert.Single(resource.Blocks);
        Assert.Equal(8.70, block.Start, 0.01f);
        Assert.Equal(11.77, block.End, 0.01f);
        Assert.Equal("AJ", chore.AgentOf(arms)!.Name);

        // Voice lines are LanguageRes resources owned by their speaker.
        var voices = chore.Resources.Select((r, i) => (r, i)).Where(x => x.r.Type == ChoreFile.LanguageResType).ToList();
        Assert.Equal(5, voices.Count);
        Assert.Contains(voices, v => chore.AgentOf(v.i)!.Name == "James");
        Assert.All(voices, v => Assert.Single(v.r.Blocks));

        // Every resource has exactly one owner.
        Assert.Equal(chore.Resources.Count, chore.Agents.Sum(a => a.Resources.Count));
    }

    [SkippableFact]
    public void Reads_a_chore_embedded_in_another()
    {
        var chore = Read("bg_forestTile_act1_csOpening_blocker1.chore");
        Assert.Equal(8, chore.Resources.Count);
        var embedded = Assert.Single(chore.Resources, r => r.EmbeddedChore is not null);
        Assert.Equal(ChoreFile.ChoreType, embedded.Type);
        Assert.StartsWith("(postFP)", embedded.EmbeddedChore!.Name);
        Assert.Equal(29f, embedded.EmbeddedChore.Length);
        Assert.Equal(6, chore.Agents.Count);
    }

    [SkippableFact]
    public void Skips_embedded_data_after_the_last_resource()
    {
        // The last resource carries style idle data; the agents follow it.
        var chore = Read("env_airportExteriorDusk_cs_gabe_52.chore");
        Assert.Equal(36, chore.Resources.Count);
        Assert.Equal(new[] { "Javier", "Gabe" }, chore.Agents.Select(a => a.Name).Take(2));
        Assert.Equal(chore.Resources.Count, chore.Agents.Sum(a => a.Resources.Count));
    }

    [SkippableTheory]
    [InlineData("chore_got_adv_asherWhitehills_asher_finalDecision.chore", 40, "sk61_idle_asherHandOnBelt.anm")]
    [InlineData("chore_wau_adv_103puddingNPie_brokenDispenser.chore", 2,
        "obj_dispenserSmokesPuddingNPieBreakable_sk54_action_bigbySmashesCigaretteMachineWithBat.anm")]
    public void Reads_older_chores(string sample, int resources, string animation)
    {
        var chore = Read(sample);
        Assert.Equal(resources, chore.Resources.Count);
        int index = IndexOf(chore, animation);
        Assert.True(index >= 0);
        Assert.Equal(ChoreFile.AnimationType, chore.Resources[index].Type);
        Assert.NotEmpty(chore.Resources[index].Blocks);
    }

    [SkippableFact]
    public void Rejects_files_that_are_not_chores()
    {
        Assert.False(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample("sk54_bigby.skl")), out _, out var error));
        Assert.NotNull(error);
        Assert.False(ChoreFile.TryRead(new byte[] { 1, 2, 3 }, out _, out _));
    }

    [SkippableFact]
    public void Lays_out_a_timeline_per_agent()
    {
        var chore = Read("env_cave_act1_cs_hardWorld_1.chore");
        var names = new[] { "sk63_ajStandA_toArmsX.anm", "aj_headEyeGesture_lookUp_add.anm" }
            .ToDictionary(TelltaleCrc64.HashSymbol);
        var tracks = ChoreTimeline.Build(chore, r => names.GetValueOrDefault(r.Name));

        // Speakers come first.
        Assert.Contains(tracks.TakeWhile(t => t.Items.Any(i => i.Kind == ChoreItemKind.VoiceLine)), t => t.Agent == "AJ");
        var aj = Assert.Single(tracks, t => t.Agent == "AJ");
        var arms = Assert.Single(aj.Items, i => i.Name == "sk63_ajStandA_toArmsX.anm");
        Assert.Equal(ChoreItemKind.Animation, arms.Kind);
        Assert.Equal(8.70, arms.Start!.Value, 0.01f);

        // Settings (no play span) lead, then items in start order.
        var timed = aj.Items.Where(i => i.Start is not null).Select(i => i.Start!.Value).ToList();
        Assert.Equal(timed.OrderBy(t => t), timed);
        Assert.All(aj.Items.Where(i => i.Name == "aj_headEyeGesture_lookUp_add.anm"), i => Assert.Equal(ChoreItemKind.Gesture, i.Kind));
        Assert.Contains(aj.Items, i => i.Kind == ChoreItemKind.Gesture);
        Assert.Equal(chore.Resources.Count, tracks.Sum(t => t.Items.Count));
    }

    [SkippableFact]
    public void Finds_a_voice_line_and_collects_its_speakers_animations()
    {
        var chore = Read("env_cave_act1_cs_hardWorld_1.chore");
        var voice = chore.Resources.First(r => r.Type == ChoreFile.LanguageResType && chore.AgentOf(chore.Resources.ToList().IndexOf(r))!.Name == "AJ");
        var line = ChorePerformance.FindLine(chore, voice.Name);
        Assert.NotNull(line);
        Assert.Equal("AJ", line!.Agent);
        Assert.True(line.End > line.Start);
        Assert.Null(ChorePerformance.FindLine(chore, 12345));

        // Names come from the game's files; here, the hashes of the names the chore uses.
        var known = new[]
        {
            "sk63_ajStandA_toArmsX.anm", "sk63_ajStandA_turnRight90.anm", "aj_headEyeGesture_lookUp_add.anm",
            "sk62_clementine400StandA_toWalk.anm", "aJ_face_blink.chore",
        }.ToDictionary(TelltaleCrc64.HashSymbol);
        // A nested chore that cannot be read (the blink here) adds nothing rather than failing.
        var clips = ChorePerformance.Collect(chore, "AJ", s => known.GetValueOrDefault(s), _ => null);

        var arms = Assert.Single(clips, c => c.Name == "sk63_ajStandA_toArmsX.anm");
        Assert.False(arms.Additive);
        Assert.Equal(8.70, arms.Start, 0.01f);
        Assert.Contains(clips, c => c.Name == "aj_headEyeGesture_lookUp_add.anm" && c.Additive);
        // Only AJ's clips: Clementine's walk stays out.
        Assert.DoesNotContain(clips, c => c.Name.Contains("clementine"));
        Assert.Empty(ChorePerformance.Collect(chore, "nobody", s => known.GetValueOrDefault(s), _ => null));
    }
}
