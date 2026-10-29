using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.Core.Tests;

public class CharacterGroupingTests
{
    private const string Archive = @"C:\game\Archives\WDC_pc_WalkingDead401_txmesh.ttarch2";

    private static readonly string[] ClementineParts =
    {
        "head", "eyesMouth", "hairCap", "hairBandage", "hairSidePony",
        "bodyUpperB", "bodyUpperBBackpack", "bodyLowerB", "hands", "handsNoFinger",
    };

    private static ArchiveEntryRef E(string name, long size = 100) => new(Archive, name, size);

    private static List<ArchiveEntryRef> ClementineEntries() =>
        ClementineParts.Select(p => E($"sk62_clementine400_{p}.d3dmesh"))
            .Append(E("sk62_clementine400.skl"))
            .Append(E("sk62_clementine400.prop"))
            .Append(E("obj_backpackClem400.skl"))
            .Append(E("obj_backpackClem400.d3dmesh"))
            .ToList();

    [SkippableFact]
    public void PropDefinedCharacter_UsesTheGamesDefaultOutfit()
    {
        var prop = CharacterProp.TryParse(File.ReadAllBytes(SampleData.Sample("sk62_clementine400.prop")))!;
        var characters = CharacterGrouping.Build(ClementineEntries(),
            new Dictionary<string, CharacterProp> { ["sk62_clementine400.prop"] = prop });

        var clem = Assert.Single(characters);
        Assert.Equal("sk62_clementine400", clem.Id);
        Assert.Equal("Clementine", clem.DisplayName);
        Assert.Equal("400", clem.Variant);
        Assert.Equal("sk62_clementine400.skl", clem.Skeleton!.Name);
        Assert.True(clem.DefaultsFromGameData);
        Assert.Equal(10, clem.Parts.Count);
        Assert.Equal(
            new[] { "bodyLowerB", "bodyUpperB", "eyesMouth", "hairCap", "hands", "head" },
            clem.DefaultParts.Select(p => p.PartName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(CharacterSlots.Hair, clem.Parts.Single(p => p.PartName == "hairSidePony").Slot);
    }

    [SkippableFact]
    public void SkeletonWithoutProp_FallsBackToNamesAndHeuristic()
    {
        // Same files, no prop read: the heuristic must land on the same outfit.
        var characters = CharacterGrouping.Build(ClementineEntries(), new Dictionary<string, CharacterProp>());

        var clem = Assert.Single(characters); // obj_ skeletons are props, not characters
        Assert.False(clem.DefaultsFromGameData);
        Assert.Equal(
            new[] { "bodyLowerB", "bodyUpperB", "eyesMouth", "hairCap", "hands", "head" },
            clem.DefaultParts.Select(p => p.PartName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [SkippableFact]
    public void HeuristicDefaults_PicksPlainestPartPerSlot()
    {
        var parts = new (string, string)[]
        {
            ("head", CharacterSlots.Head), ("headDamageNoseRBloody", CharacterSlots.Head),
            ("teeth", CharacterSlots.Face), ("eyes", CharacterSlots.Face), ("teethWolf", CharacterSlots.Face), // one per kind
            ("bodyNoHat", CharacterSlots.UpperBody), ("body", CharacterSlots.UpperBody),
            ("glasses", CharacterSlots.Accessory),
        };

        var on = CharacterGrouping.HeuristicDefaults(parts);

        Assert.Equal(new[] { true, false, true, true, false, false, true, false }, on);
    }

    [SkippableFact]
    public void HeuristicDefaults_WholeMeshCharacterIsShown()
    {
        var on = CharacterGrouping.HeuristicDefaults(new[] { ("sasha", CharacterSlots.Other) });
        Assert.Equal(new[] { true }, on);
    }

    [SkippableFact]
    public void SameNameInTwoFamilies_IsDisambiguatedByFamily()
    {
        var entries = new[]
        {
            E("sk54_hyperionSuit.skl"), E("sk54_hyperionSuit_body.d3dmesh"),
            E("sk55_hyperionSuit.skl"), E("sk55_hyperionSuit_body.d3dmesh"),
        };

        var characters = CharacterGrouping.Build(entries, new Dictionary<string, CharacterProp>());

        Assert.Equal(new[] { "sk54", "sk55" }, characters.Select(c => c.Variant));
        Assert.All(characters, c => Assert.Equal("Hyperion Suit", c.DisplayName));
    }

    [SkippableFact]
    public void Characters_AreSortedByNameThenVariant()
    {
        var entries = new[]
        {
            E("sk62_clementine402.skl"), E("sk62_clementine402_head.d3dmesh"),
            E("sk56_clementine200.skl"), E("sk56_clementine200_head.d3dmesh"),
            E("sk54_ben.skl"), E("sk54_ben_body.d3dmesh"),
            E("sk62_clementine400.skl"), E("sk62_clementine400_head.d3dmesh"),
        };

        var characters = CharacterGrouping.Build(entries, new Dictionary<string, CharacterProp>());

        Assert.Equal(new[] { "Ben", "Clementine 200", "Clementine 400", "Clementine 402" },
            characters.Select(c => (c.DisplayName + " " + c.Variant).Trim()));
    }

    [SkippableFact]
    public void SelectCharacterPropCandidates_SkipsMaterialAndUnprefixedProps()
    {
        var entries = new[]
        {
            E("sk62_clementine400.prop"), E("sk62_clementine400_head_m.prop"), E("obj_capClementine400.prop"),
            E("sk54_rhys_hacking.prop"), E("module_renderable.prop"), E("sk62_huge.prop", 5_000_000),
        };

        var names = CharacterGrouping.SelectCharacterPropCandidates(entries).Select(e => e.Name).ToArray();

        Assert.Equal(new[] { "sk62_clementine400.prop", "sk54_rhys_hacking.prop" }, names);
    }
}
