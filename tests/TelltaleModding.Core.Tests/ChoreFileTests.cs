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

        // AJ crosses his arms about 8.7 s in.
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
        // The agents come after the last resource's style idle data.
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

    [Fact]
    public void Curves_hold_their_ends_ease_flat_keys_and_step()
    {
        var linear = new ChoreCurve(new[] { new ChoreCurveKey(1, 0, 2), new ChoreCurveKey(3, 1, 2) });
        Assert.Equal(0f, linear.Evaluate(0));
        Assert.Equal(0.25f, linear.Evaluate(1.5f), 1e-4f);
        Assert.Equal(1f, linear.Evaluate(9));

        var flat = new ChoreCurve(new[] { new ChoreCurveKey(0, 0, 4), new ChoreCurveKey(1, 1, 4) });
        Assert.Equal(0.5f, flat.Evaluate(0.5f), 1e-4f);
        Assert.True(flat.Evaluate(0.25f) < 0.25f);

        var stepped = new ChoreCurve(new[] { new ChoreCurveKey(0, 0.3f, 1), new ChoreCurveKey(1, 1, 1) });
        Assert.Equal(0.3f, stepped.Evaluate(0.9f));
    }

    [Fact]
    public void A_nested_resource_follows_the_chore_that_plays_it()
    {
        // The outer chore holds its nested chore at half way, at 60%, from 2 s to 4 s.
        var outer = new ResourceControl(2, 4, 10, 1,
            Time: new ChoreCurve(new[] { new ChoreCurveKey(2, 0.5f, 2) }),
            Contribution: new ChoreCurve(new[] { new ChoreCurveKey(2, 0.6f, 2) }), Parent: null);
        var inner = new ResourceControl(4, 8, 3, 1, Time: null,
            Contribution: new ChoreCurve(new[] { new ChoreCurveKey(4, 0.5f, 2) }), Parent: outer);

        var (time, weight) = inner.At(3, hold: false);
        Assert.Equal(1f, time, 1e-4f);                 // 5 s into the nested chore, 1 s into its clip
        Assert.Equal(0.3f, weight, 1e-4f);
        Assert.Equal(0f, inner.At(5, hold: false).Weight);
        Assert.Equal(0.3f, inner.At(5, hold: true).Weight, 1e-4f);
        Assert.Equal(0f, inner.At(1, hold: true).Weight);
    }

    [SkippableFact]
    public void Reads_each_resources_time_and_contribution_curves()
    {
        var chore = Read("env_civilWarMemorialCenterMorning_cs_saritaBitten_15.chore");
        var wide = chore.Resources[IndexOf(chore, "clementine200_face_eyesWide_add.anm")];

        // Clementine's eyes widen to about half strength, then relax.
        Assert.Equal(0f, wide.Contribution!.Evaluate(0.5f));
        Assert.Equal(0.4755f, wide.Contribution.Evaluate(1.3f), 1e-3f);
        Assert.Equal(0f, wide.Contribution.Evaluate(2.5f));
        Assert.Equal(0f, wide.TimeCurve!.Evaluate(0.589f), 1e-3f);
        Assert.Equal(1f, wide.TimeCurve.Evaluate(1.98f), 1e-3f);
        Assert.Equal(1.4f, wide.Length, 1e-3f);
    }

    [SkippableFact]
    public void Reads_the_camera_moves_a_chore_keys()
    {
        // The chore turns the camera rig and zooms from 35 to 25 degrees and back.
        var chore = Read("env_lodgeExteriorAndMainRoom_cs_hideCouch_1.chore");
        ChoreResource Keyed(string agent) => chore.Agents.First(a => a.Name == agent).Resources
            .Select(i => chore.Resources[i]).First(r => r.Transform is not null || r.FieldOfView is not null);

        var rig = Keyed("cam_station1_parent").Transform!;
        Assert.Equal(3, rig.Keys.Count);
        Assert.Equal(21f, rig.Keys[0].Time, 1e-3f);
        Assert.Equal(11.61f, rig.Keys[0].Position.X, 1e-2f);
        Assert.Equal(5.897f, rig.Keys[0].Position.Z, 1e-2f);
        var (rotation, position) = rig.Evaluate(30);
        Assert.Equal(12.367f, position.X, 1e-2f);
        Assert.Equal(1f, rotation.Length(), 1e-3f);

        var zoom = Keyed("cam_station1").FieldOfView!;
        Assert.Equal(35f, zoom.Evaluate(10), 1e-3f);
        Assert.Equal(25f, zoom.Evaluate(22), 1e-3f);
        Assert.Equal(35f, zoom.Evaluate(26), 1e-3f);
    }

    [SkippableFact]
    public void Reads_the_resource_groups_a_resource_is_limited_to()
    {
        // The glance moves the head and eyes but not the face.
        var chore = Read("clementine200_headGesture_lookUp_add.chore");
        var groups = chore.Resources[0].GroupInclude!;
        Assert.Equal(6, groups.Count);
        Assert.Equal(-1f, groups[0x6533c11bb1c71cb4]);
        Assert.Equal(1f, groups[0x16fc55ce88d82bdd]);
        Assert.Equal(1f, groups[0x600e10e7529b1ff2]);
        Assert.Null(Read("clementine200_headturns_zero.chore").Resources[0].GroupInclude);
    }

    [SkippableFact]
    public void A_chore_at_zero_contribution_adds_nothing_while_a_held_glance_does()
    {
        SampleData.Require("clementine200_headturns_zero.chore", "clementine200_headGesture_lookUp_add.chore");
        var chore = Read("env_civilWarMemorialCenterMorning_cs_saritaBitten_15.chore");
        var known = new[]
        {
            "clementine200_headturns_zero.chore", "clementine200_headGesture_lookUp_add.chore",
            "clementine200_headGesture_lookUp_add.anm", "clementine200_eyes_lookUp_add.anm",
        }.ToDictionary(TelltaleCrc64.HashSymbol);
        var clips = ChorePerformance.Collect(chore, "Clementine", s => known.GetValueOrDefault(s),
            name => Path.Combine(SampleData.SamplesDir, name) is var path && File.Exists(path) &&
                    ChoreFile.TryRead(File.ReadAllBytes(path), out var c, out _) ? c : null);

        // "Head turns zero" plays the glances with no weight at all.
        var zeroed = clips.Where(c => c.Name == "clementine200_eyes_lookUp_add.anm" && c.Priority == 100).ToList();
        Assert.NotEmpty(zeroed);
        Assert.All(zeroed, c => Assert.Equal(0f, c.Control!.At(1.5f, hold: false).Weight));

        // The glance is held near its peak.
        var glance = Assert.Single(clips, c => c.Name == "clementine200_headGesture_lookUp_add.anm" && c.Priority == 1);
        var (time, weight) = glance.Control!.At(2f, hold: false);
        Assert.Equal(0.77f, weight, 0.02f);
        Assert.InRange(time, 1.1f, 1.3f);
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

        // Settings come first, then items in start order.
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

        // Names the chore uses, by hash.
        var known = new[]
        {
            "sk63_ajStandA_toArmsX.anm", "sk63_ajStandA_turnRight90.anm", "aj_headEyeGesture_lookUp_add.anm",
            "sk62_clementine400StandA_toWalk.anm", "aJ_face_blink.chore",
        }.ToDictionary(TelltaleCrc64.HashSymbol);
        // A nested chore that can't be read adds nothing.
        var clips = ChorePerformance.Collect(chore, "AJ", s => known.GetValueOrDefault(s), _ => null);

        var arms = Assert.Single(clips, c => c.Name == "sk63_ajStandA_toArmsX.anm");
        Assert.False(arms.Additive);
        Assert.Equal(8.70, arms.Start, 0.01f);
        Assert.Contains(clips, c => c.Name == "aj_headEyeGesture_lookUp_add.anm" && c.Additive);
        // Only AJ's clips.
        Assert.DoesNotContain(clips, c => c.Name.Contains("clementine"));
        Assert.Empty(ChorePerformance.Collect(chore, "nobody", s => known.GetValueOrDefault(s), _ => null));
    }
}
