using System.Text;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Tests;

/// <summary>Resource-set descriptions and the archive override rule built on them.</summary>
public class ArchivePriorityTests
{
    // An archive path in this OS's form (the tests compare file names).
    private static string Archive(string name) => Path.Combine(Path.GetTempPath(), "g", name);

    private static ResourceSetDescription Set(string name, int priority, string mode = "bootable",
        string destination = "<>", params string[] archives) =>
        new(name, priority, mode, $"<{name}>", destination, archives, $"_resdesc_50_{name}.lua");

    [SkippableFact]
    public void ParsesPlainLuaDescription_DefinitiveSeries()
    {
        var set = ResourceSetDescription.Parse(File.ReadAllBytes(SampleData.Sample("resdesc_wdc_WalkingDead401.lua")), "x.lua");

        Assert.NotNull(set);
        Assert.Equal("WalkingDead401", set!.Name);
        Assert.Equal(101, set.Priority);
        Assert.Equal("bootable", set.EnableMode);
        Assert.Equal("<>", set.LogicalDestination);
        Assert.Equal(new[]
        {
            "WDC_pc_WalkingDead401_anichore.ttarch2", "WDC_pc_WalkingDead401_data.ttarch2",
            "WDC_pc_WalkingDead401_ms.ttarch2", "WDC_pc_WalkingDead401_txmesh.ttarch2",
            "WDC_pc_WalkingDead401_voice.ttarch2",
        }, set.Archives);
    }

    [SkippableFact]
    public void ParsesCompiledLua51Description_SeasonTwo()
    {
        // Telltale's Lua 5.1 renumbers opcodes; the reader finds SETTABLE by its "name" store.
        var set = ResourceSetDescription.Parse(File.ReadAllBytes(SampleData.Sample("resdesc_wd2_Menu.luac")), "x.lua");

        Assert.NotNull(set);
        Assert.Equal("Menu", set!.Name);
        Assert.Equal(10, set.Priority);
        Assert.Equal("bootable", set.EnableMode);
        Assert.Contains("WalkingDead_pc_Menu_data.ttarch2", set.Archives);
    }

    [SkippableFact]
    public void ParsesCompiledLua52Description_GameOfThrones()
    {
        var set = ResourceSetDescription.Parse(File.ReadAllBytes(SampleData.Sample("resdesc_got_DT_101_P.luac")), "x.lua");

        Assert.NotNull(set);
        Assert.Equal("DT_101_P", set!.Name);
        Assert.Equal(280, set.Priority);
        Assert.Equal("constant", set.EnableMode);
        Assert.Equal("<GameOfThrones101>", set.LogicalDestination);
        Assert.Equal(new[] { "GameOfThrones_pc_DT_101_P_all.ttarch2" }, set.Archives);
    }

    [SkippableFact]
    public void Parse_ReturnsNullForScriptsWithoutASet()
    {
        var text = Encoding.ASCII.GetBytes("if rawget(_G, \"X\") then X(\"2020 RC\") end\n");
        Assert.Null(ResourceSetDescription.Parse(text, "_resdesc_50_version_Season.lua"));
    }

    [SkippableTheory]
    [InlineData("_resdesc_50_WalkingDead401.lua", true)]
    [InlineData("_resdesc_50_Boot.lenc", true)]
    [InlineData("_resourcedescriptions_500_Fables101.lenc", true)]
    [InlineData("_rescdesc_50_version_101.lua", true)]
    [InlineData("WDC_pc_WalkingDead401_data.ttarch2", false)]
    [InlineData("modinfo_graphic_black_disabler.json", false)]
    public void RecognisesDescriptionFileNames(string name, bool expected) =>
        Assert.Equal(expected, ResourceSetDescription.IsDescriptionFileName(name));

    [SkippableFact]
    public void Resolve_UsesSetPriorities_AndDropsOtherLanguages()
    {
        var sets = new[]
        {
            Set("Project", -8888, "constant", "<>", "WDC_pc_Project_data.ttarch2"),
            Set("WalkingDead401", 101, "bootable", "<>", "WDC_pc_WalkingDead401_txmesh.ttarch2"),
            Set("PC_P01_WalkingDead401", 400, "constant", "<WalkingDead401>", "WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2"),
            Set("French", 1500, "localization", "<Project>", "WDC_pc_FrenchProjectSeason1_compressed.ttarch2"),
            Set("FrenchAudio", 1500, "localization"),
            Set("ScaledWalkingDeadFrenchAudio", 1600, "constant", "<FrenchAudio>", "WD4_Fr_FrenchAudio401_uncompressed.ttarch2"),
            Set("English", 1500, "localization"),
        };
        var paths = new[]
        {
            Archive("WDC_pc_Project_data.ttarch2"), Archive("WDC_pc_WalkingDead401_txmesh.ttarch2"),
            Archive("WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2"), Archive("WDC_pc_FrenchProjectSeason1_compressed.ttarch2"),
            Archive("WD4_Fr_FrenchAudio401_uncompressed.ttarch2"), Archive("WDC_pc_Mystery_data.ttarch2"),
        };

        var resolved = ArchivePriorities.Resolve(paths, sets).ToDictionary(a => Path.GetFileName(a.Path));

        Assert.Equal(-8888, resolved["WDC_pc_Project_data.ttarch2"].Priority);
        Assert.Equal(101, resolved["WDC_pc_WalkingDead401_txmesh.ttarch2"].Priority);
        Assert.Equal(400, resolved["WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2"].Priority);
        Assert.False(resolved["WDC_pc_FrenchProjectSeason1_compressed.ttarch2"].Enabled);
        // Mounted into a French set, so disabled even though its own mode is "constant".
        Assert.False(resolved["WD4_Fr_FrenchAudio401_uncompressed.ttarch2"].Enabled);
        Assert.True(resolved["WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2"].Enabled);
        Assert.False(resolved["WDC_pc_Mystery_data.ttarch2"].FromDescription);
    }

    [SkippableFact]
    public void PrecedenceOrder_PutsWinnersLast_TiesByArchiveName()
    {
        var archives = new[]
        {
            new CatalogArchive(Archive("WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2"), 400, "P", true, true),
            new CatalogArchive(Archive("WDC_pc_WalkingDead401_txmesh.ttarch2"), 101, "E", true, true),
            new CatalogArchive(Archive("WDC_pc_WalkingDead101_txmesh.ttarch2"), 101, "E1", true, true),
            new CatalogArchive(Archive("WDC_pc_ProjectSeason4_txmesh.ttarch2"), 55, "S", true, true),
            new CatalogArchive(Archive("WDC_pc_FrenchProjectSeason4_compressed.ttarch2"), 1500, "F", false, true),
        };

        var order = ArchivePriorities.InPrecedenceOrder(archives).Select(a => Path.GetFileName(a.Path)).ToArray();

        Assert.Equal(new[]
        {
            "WDC_pc_ProjectSeason4_txmesh.ttarch2",
            "WDC_pc_WalkingDead101_txmesh.ttarch2",
            "WDC_pc_WalkingDead401_txmesh.ttarch2",
            "WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2",
        }, order);
    }

    [SkippableTheory]
    [InlineData("WDC_pc_WalkingDead401_data.ttarch2", 101)]
    [InlineData("Borderlands_pc_Borderlands105_txmesh.ttarch2", 105)]
    [InlineData("WDC_pc_ProjectSeason4_txmesh.ttarch2", 50)]
    [InlineData("WDC_pc_Menu_data.ttarch2", 20)]
    [InlineData("WDC_pc_Boot_data.ttarch2", 10)]
    [InlineData("WDC_pc_Project_data.ttarch2", -8888)]
    [InlineData("WDC_PC_ShadersGlobal.ttarch2", -9979)]
    [InlineData("WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2", 401)]
    [InlineData("Fables_pc_PC-Mac-TTG_Update_01_Fables101_all.ttarch2", 401)]
    [InlineData("WalkingDead_pc_PC_Patch_02_Project_all.ttarch2", 400)]
    public void GuessPriority_FollowsTheRealTablesShape(string name, int expected) =>
        Assert.Equal(expected, ArchivePriorities.GuessPriority(name));

    [SkippableTheory]
    [InlineData("WD4_De_GermanAudio401_uncompressed.ttarch2", true)]
    [InlineData("Borderlands_pc_french_compressed.ttarch2", true)]
    [InlineData("WDC_pc_Traditional_ChineseUISeason4_compressed.ttarch2", true)]
    [InlineData("WDC_pc_WalkingDead401_voice.ttarch2", false)]
    public void ForeignLanguageArchives_AreRecognised(string name, bool expected) =>
        Assert.Equal(expected, ArchivePriorities.IsForeignLanguageArchive(name));

    [EngineFact]
    public async Task DecryptsEncryptedDescriptionWithTtarchext()
    {
        using var dir = new TempDir();
        File.Copy(SampleData.Sample("resdesc_wdc_PC_P01_WalkingDead401_encrypted.lua"),
            Path.Combine(dir.Path, "_resdesc_50_PC_P01_WalkingDead401_01.lua"));

        var sets = await ResourceDescriptionLoader.LoadAsync(dir.Path, 67, EngineFactAttribute.FindRunnableEngine());

        var set = Assert.Single(sets);
        Assert.Equal("PC_P01_WalkingDead401", set.Name);
        Assert.Equal(400, set.Priority);
        Assert.Equal("<WalkingDead401>", set.LogicalDestination);
        Assert.Equal(new[] { "WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2" }, set.Archives);
    }
}
