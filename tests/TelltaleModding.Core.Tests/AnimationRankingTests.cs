using TelltaleModding.Core.Catalog;

namespace TelltaleModding.Core.Tests;

public class AnimationRankingTests
{
    [SkippableTheory]
    [InlineData("sasha103_sk55_action_fionaRhysTryToMoveDyingSasha", "sasha103", true)]
    [InlineData("idle_sashaHandsHips", "sasha", true)]
    [InlineData("action_clementine400AjCloseDoor", "clementine400", true)]
    [InlineData("clementine400_jog", "clementine400", true)]
    // Someone else's clip from a scene with the character: named performer-first.
    [InlineData("action_fionaRhysTryToMoveDyingSasha", "sasha", false)]
    [InlineData("action_fionaKissSashaHand", "sasha", false)]
    // A longer name that merely starts with the token is a different character.
    [InlineData("idle_sashay", "sasha", false)]
    public void Character_performs_a_clip_only_when_their_name_starts_a_segment(string clip, string token, bool expected) =>
        Assert.Equal(expected, GameCatalog.PerformsClip(clip, token));
}
