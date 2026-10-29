using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

public class AssemblyAuditFixTests
{
    [SkippableTheory]
    [InlineData("sk55_lee_head_nm.d3dtx")]
    [InlineData("sk55_lee_head_norm.d3dtx")]
    [InlineData("sk55_lee_head_normal.d3dtx")]
    [InlineData("sk55_lee_head_spec.d3dtx")]
    [InlineData("sk55_lee_head_specular.d3dtx")]
    [InlineData("sk55_lee_head_nmDetail.d3dtx")]
    [InlineData("sk54_lee_head303_damageMask.d3dtx")]
    public void Map_suffixes_are_not_diffuse(string name) => Assert.True(MaterialTextureLocator.IsNonDiffuseMap(name));

    [SkippableTheory]
    [InlineData("sk55_norma_bodyLower.d3dtx")]
    [InlineData("sk55_norma_head.d3dtx")]
    [InlineData("sk61_specialist_body.d3dtx")]
    [InlineData("sk54_crane_specs.d3dtx")]
    public void Words_that_start_with_a_marker_are_diffuse(string name) =>
        Assert.False(MaterialTextureLocator.IsNonDiffuseMap(name));

    [SkippableFact]
    public void A_scripted_head_choice_gets_the_plainest_head()
    {
        var parts = new (string, string)[]
        {
            ("headGoatee", CharacterSlots.Head), ("headClean", CharacterSlots.Head),
            ("eyes", CharacterSlots.Face), ("torso", CharacterSlots.UpperBody),
            ("hairHelmet", CharacterSlots.Hair), ("hairBuzz", CharacterSlots.Hair),
        };
        var on = new[] { false, false, true, true, false, false };
        Assert.True(CharacterGrouping.PickHeadIfScripted(parts, on));
        Assert.Equal(new[] { false, true, true, true, false, true }, on);
    }

    [SkippableFact]
    public void A_head_carried_by_a_whole_body_part_is_left_alone()
    {
        var parts = new (string, string)[]
        {
            ("headA", CharacterSlots.Head), ("eyes", CharacterSlots.Face), ("tourGuide", CharacterSlots.Other),
        };
        var on = new[] { false, true, true };
        Assert.False(CharacterGrouping.PickHeadIfScripted(parts, on));
        Assert.False(on[0]);
    }

    [SkippableFact]
    public void A_head_in_the_body_with_its_hair_on_is_left_alone()
    {
        var parts = new (string, string)[]
        {
            ("headCap", CharacterSlots.Head), ("eyesMouth", CharacterSlots.Face),
            ("hairA", CharacterSlots.Hair), ("bodyA", CharacterSlots.UpperBody),
        };
        var on = new[] { false, true, true, true };
        Assert.False(CharacterGrouping.PickHeadIfScripted(parts, on));
        Assert.False(on[0]);
    }
}
