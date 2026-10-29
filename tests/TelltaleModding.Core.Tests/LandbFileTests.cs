using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Dialogue;

namespace TelltaleModding.Core.Tests;

public class LandbFileTests
{
    private static IReadOnlyList<DialogueLine> Read(string sample)
    {
        Assert.True(LandbFile.TryRead(File.ReadAllBytes(SampleData.Sample(sample)), out var lines, out var error), error);
        return lines;
    }

    [SkippableFact]
    public void Reads_speakers_and_text_from_an_msv6_database()
    {
        var lines = Read("landb_tftb_death_english.landb");
        var first = lines[0];
        Assert.Equal(362135553u, first.Id);
        Assert.Equal("STRANGER", first.Speaker);
        Assert.Equal("[v-o][confused] And then you died? You sure you want to stick with that story?", first.RawText);
        Assert.Equal("And then you died? You sure you want to stick with that story?", first.Text);
        Assert.Equal(new[] { "v-o", "confused" }, first.Directions);
        Assert.Equal("362135553.wav", first.AudioFileName);
        Assert.Contains(lines, l => l.Speaker == "RHYS" && l.Text.StartsWith("Okay, okay, maybe I didn't"));
        Assert.Contains(lines, l => l.Speaker == "FIONA");
        Assert.Equal(lines.Count, lines.Select(l => l.Id).Distinct().Count());
    }

    [SkippableFact]
    public void Reads_an_msv5_database()
    {
        var lines = Read("landb_wau_env_trollbar_fight_english.landb");
        Assert.Contains(lines, l => l.Speaker == "GRENDEL" && l.Text == "You got some fuckin' nerve comin' down here like this.");
        Assert.All(lines, l => Assert.NotEqual(0u, l.Id));
    }

    [SkippableFact]
    public void Reads_curly_brace_directions()
    {
        var lines = Read("landb_wdc_crawfordautoshop_lee_molly_english.landb");
        var line = Assert.Single(lines, l => l.RawText == "{thinkingA}Something you need?");
        Assert.Equal("MOLLY", line.Speaker);
        Assert.Equal("Something you need?", line.Text);
        Assert.Equal(new[] { "thinkingA" }, line.Directions);
        Assert.Contains(lines, l => l.Speaker == "LEE");
    }

    [SkippableFact]
    public void Rejects_files_that_are_not_language_databases()
    {
        Assert.False(LandbFile.TryRead(File.ReadAllBytes(SampleData.Sample("sk54_bigby.skl")), out _, out var error));
        Assert.NotNull(error);
        Assert.False(LandbFile.TryRead(new byte[] { 1, 2, 3 }, out _, out _));
    }

    [SkippableTheory]
    [InlineData("env_heliosoffice_english.landb", "env_heliosoffice", "english")]
    [InlineData("env_richmondsafehouse_traditional_chinese.landb", "env_richmondsafehouse", "traditional_chinese")]
    [InlineData("env_richmondsafehouse_chinese.landb", "env_richmondsafehouse", "chinese")]
    [InlineData("ui_text.landb", "ui_text", "english")]
    public void Splits_scene_and_language(string file, string scene, string language)
    {
        Assert.Equal((scene, language), LandbFile.SplitName(file));
    }

    [SkippableTheory]
    [InlineData(@"C:\Games\WDC\Archives\WDC_pc_WalkingDead304_data.ttarch2", "WalkingDead304")]
    [InlineData(@"F:\TFTB\Archives\Borderlands_pc_Borderlands101_voice.ttarch2", "Borderlands101")]
    [InlineData(@"D:\GoT\Archives\GameOfThrones_pc_DT_Boot_EP6_all.ttarch2", "DT_Boot_EP6")]
    public void Labels_episodes_from_archive_names(string archive, string episode)
    {
        Assert.Equal(episode, DialogueIndex.EpisodeOf(archive));
    }

    [SkippableFact]
    public void Lists_languages_english_first()
    {
        var entries = new[] { "a_french.landb", "a_english.landb", "b_arabic.landb", "x.d3dmesh" }
            .Select(n => new ArchiveEntryRef("x.ttarch2", n, 1));
        Assert.Equal(new[] { "english", "arabic", "french" }, DialogueIndex.LanguagesOf(entries));
    }
}
