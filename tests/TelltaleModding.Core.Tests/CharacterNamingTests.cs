using TelltaleModding.Core.Catalog;

namespace TelltaleModding.Core.Tests;

public class CharacterNamingTests
{
    [SkippableTheory]
    [InlineData("sk62_clementine400", "sk62_")]
    [InlineData("skB0_claptrap_body.d3dmesh", "skb0_")]
    [InlineData("SK54_rhys.skl", "sk54_")]
    [InlineData("obj_backpackClem400.skl", null)]
    [InlineData("adv_forest_meshesA.skl", null)]
    [InlineData("clementine400_phoneme_aa.anm", null)]
    public void FamilyPrefix_RecognisesSkeletonFamilies(string name, string? expected) =>
        Assert.Equal(expected, CharacterNaming.FamilyPrefix(name));

    [SkippableTheory]
    [InlineData("bodyUpperBBackpack", "body|Upper|B|Backpack")]
    [InlineData("handsomeJackAI102", "handsome|Jack|AI|102")]
    [InlineData("rhys_hacking", "rhys|hacking")]
    [InlineData("eyesMouth", "eyes|Mouth")]
    [InlineData("zer0", "zer0")]
    [InlineData("gortysPhase2", "gortys|Phase|2")]
    public void SplitWords_SplitsCamelSnakeAndDigits(string text, string expected) =>
        Assert.Equal(expected.Split('|'), CharacterNaming.SplitWords(text));

    [SkippableTheory]
    [InlineData("clementine", "Clementine")]
    [InlineData("handsomeJackAI", "Handsome Jack AI")]
    [InlineData("battleDroid_hologram", "Battle Droid Hologram")]
    [InlineData("zer0", "Zer0")]
    [InlineData("drSpara", "Dr Spara")]
    public void Prettify_MakesReadableNames(string text, string expected) =>
        Assert.Equal(expected, CharacterNaming.Prettify(text));

    [SkippableFact]
    public void ComputeNames_GroupsVariantsUnderOnePerson()
    {
        var names = CharacterNaming.ComputeNames(new[]
        {
            "sk62_clementine400", "sk62_clementine402", "sk56_clementine200", "sk62_clementineCredits",
            "sk54_rhys", "sk54_rhysFuture", "sk54_rhys_hacking", "sk54_rhys105",
            "skB0_battleDroid", "skB0_battleDroid_hologram_fakeShell",
            "skB5_zer0", "sk54_john", "sk54_johnny",
            "sk54_bigby", "sk54_bigbywolfout",
        });

        Assert.Equal(("Clementine", "400"), names["sk62_clementine400"]);
        Assert.Equal(("Clementine", "402"), names["sk62_clementine402"]);
        Assert.Equal(("Clementine", "200"), names["sk56_clementine200"]);
        Assert.Equal(("Clementine", "Credits"), names["sk62_clementineCredits"]);
        Assert.Equal(("Rhys", null), names["sk54_rhys"]);
        Assert.Equal(("Rhys", "Future"), names["sk54_rhysFuture"]);
        Assert.Equal(("Rhys", "Hacking"), names["sk54_rhys_hacking"]);
        Assert.Equal(("Rhys", "105"), names["sk54_rhys105"]);
        Assert.Equal(("Battle Droid", "Hologram Fake Shell"), names["skB0_battleDroid_hologram_fakeShell"]);
        Assert.Equal(("Zer0", null), names["skB5_zer0"]);
        // "johnny" is not "john" + "ny": no word boundary and the suffix is too short.
        Assert.Equal(("Johnny", null), names["sk54_johnny"]);
        // All-lowercase ids still group under a known person.
        Assert.Equal(("Bigby", "Wolfout"), names["sk54_bigbywolfout"]);
    }

    [SkippableTheory]
    [InlineData("head", CharacterSlots.Head)]
    [InlineData("headBandaged", CharacterSlots.Head)]
    [InlineData("eyesMouth", CharacterSlots.Face)]
    [InlineData("eyeRobotIris", CharacterSlots.Face)]
    [InlineData("teeth", CharacterSlots.Face)]
    [InlineData("hairCap", CharacterSlots.Hair)]
    [InlineData("hairSidePony", CharacterSlots.Hair)]
    [InlineData("bodyUpperB", CharacterSlots.UpperBody)]
    [InlineData("bodyUpperBBackpack", CharacterSlots.UpperBody)]
    [InlineData("body", CharacterSlots.UpperBody)]
    [InlineData("torsoLords", CharacterSlots.UpperBody)]
    [InlineData("bodyLowerB", CharacterSlots.LowerBody)]
    [InlineData("legL", CharacterSlots.LowerBody)]
    [InlineData("hands", CharacterSlots.Hands)]
    [InlineData("handsNoFinger", CharacterSlots.Hands)]
    [InlineData("bootsMuddy", CharacterSlots.Feet)]
    [InlineData("accessoriesBackpack", CharacterSlots.Accessory)]
    [InlineData("glasses", CharacterSlots.Accessory)]
    [InlineData("maskPsycho", CharacterSlots.Accessory)]
    [InlineData("swordSheath", CharacterSlots.Accessory)]
    [InlineData("antenna", CharacterSlots.Other)]
    public void ClassifySlot_UsesLeadingRegionThenKeywords(string partName, string expected) =>
        Assert.Equal(expected, CharacterNaming.ClassifySlot(partName));

    [SkippableFact]
    public void ClassifySlot_OnlyReturnsKnownSlots()
    {
        foreach (var name in new[] { "x", "templePortOff", "Future_body", "sasha", "clementine105_haircut" })
            Assert.Contains(CharacterNaming.ClassifySlot(name), CharacterSlots.All);
    }

    [SkippableTheory]
    [InlineData("sk62_clementine400_hairCap", "sk62_clementine400", "sk62_clementine400", "hairCap")]
    [InlineData("sk54_rhys_body", "sk54_rhys_hacking", "sk54_rhys", "body")]
    [InlineData("sk54_rhysFuture_body", "sk54_rhys", "sk54_rhys", "Future_body")]
    [InlineData("sk55_sasha", "sk55_sasha", "sk55_sasha", "sasha")]
    [InlineData("sk56_clementine105_body", "sk56_clementine100", "sk56_clementine100", "clementine105_body")]
    public void PartName_IsRelativeToTheCharacter(string meshStem, string id, string skeleton, string expected) =>
        Assert.Equal(expected, CharacterNaming.PartName(meshStem, id, skeleton));
}
