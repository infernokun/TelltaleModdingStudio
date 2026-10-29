using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.Core.Tests;

public class CharacterPoolTests
{
    private static CharacterEntry Character(params (string Name, string Slot, bool On)[] parts) =>
        new("sk54_test", "Test", null, null,
            parts.Select(p => new CharacterPart(new ArchiveEntryRef("a.ttarch2", $"sk54_test_{p.Name}.d3dmesh", 1), p.Name, p.Slot, p.On)).ToArray())
        { DefaultsFromGameData = true };

    private static string[] Worn(CharacterEntry c) => c.Parts.Where(p => p.DefaultOn).Select(p => p.PartName).ToArray();

    [SkippableFact]
    public void Generic_zombie_pool_keeps_one_plain_part_per_slot_and_side()
    {
        // sk54_zombieAverage's prop marks its whole random pool visible.
        var zombie = Character(
            ("headUniqueA", "Head", true), ("headC", "Head", true), ("headCJaw", "Head", true), ("headB", "Head", true),
            ("hairStraggly", "Hair", true), ("hairSpikey", "Hair", true), ("hairLongA", "Hair", true),
            ("torsoVest", "Upper body", true), ("torsoJacketA", "Upper body", true),
            ("armRightstump", "Upper body", true), ("armRightFull", "Upper body", true), ("armRightFullFracture", "Upper body", true),
            ("armLeftstump", "Upper body", true), ("armLeftFull", "Upper body", true),
            ("legsShortsA", "Lower body", true), ("legsLongPantsA", "Lower body", true),
            ("handRightA", "Hands", true), ("handLeftA", "Hands", true));

        var worn = Worn(CharacterGrouping.ThinRandomPools(zombie));

        Assert.Equal(new[] { "headC", "hairLongA", "torsoVest", "armRightFull", "armLeftFull", "legsShortsA", "handRightA", "handLeftA" }, worn);
    }

    [SkippableFact]
    public void Ordinary_split_outfits_are_left_as_the_game_data_says()
    {
        // Lee: a body and two separate arms; Clementine 203: neck halves and a shirt over her body.
        var lee = Character(("body", "Upper body", true), ("armR", "Upper body", true), ("armL", "Upper body", true));
        var clementine = Character(
            ("headHat", "Head", true), ("neckL", "Head", true), ("neckR", "Head", true),
            ("armLSleeveDown", "Upper body", true), ("armRSleeveDown", "Upper body", true),
            ("body", "Upper body", true), ("shirt", "Upper body", true), ("bodyAlt", "Upper body", false));

        Assert.Same(lee, CharacterGrouping.ThinRandomPools(lee));
        Assert.Same(clementine, CharacterGrouping.ThinRandomPools(clementine));
    }
}
