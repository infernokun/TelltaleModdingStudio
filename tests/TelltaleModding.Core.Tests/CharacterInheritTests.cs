using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.Core.Tests;

public class CharacterInheritTests
{
    private static CharacterPart Part(string mesh, string slot, bool on) =>
        new(new ArchiveEntryRef("x.ttarch2", mesh, 1), Path.GetFileNameWithoutExtension(mesh), slot, on);

    private static readonly ArchiveEntryRef Skeleton = new("x.ttarch2", "sk54_rhys.skl", 1);

    private static CharacterEntry Base() => new("sk54_rhys", "Rhys", null, Skeleton, new[]
    {
        Part("sk54_rhys_body.d3dmesh", "Upper body", true),
        Part("sk54_rhys_templePortOff.d3dmesh", "Accessory", true),
        Part("sk54_rhys_templePortOn.d3dmesh", "Accessory", false),
        Part("sk54_rhys_hairHat.d3dmesh", "Hair", true),
    });

    [SkippableFact]
    public void A_variant_on_the_same_body_borrows_parts_for_slots_it_leaves_empty()
    {
        var hacking = new CharacterEntry("sk54_rhys_hacking", "Rhys", "Hacking", Skeleton, new[]
        {
            Part("sk54_rhys_body.d3dmesh", "Upper body", true),
            Part("sk54_rhys_hairNoHat.d3dmesh", "Hair", true),
        });
        var result = CharacterGrouping.InheritBaseParts(new[] { Base(), hacking });

        var parts = result[1].Parts;
        Assert.Contains(parts, p => p.Mesh.Name == "sk54_rhys_templePortOff.d3dmesh" && p.DefaultOn);
        // Only the base's default for the empty slot, and nothing for a slot the variant fills.
        Assert.DoesNotContain(parts, p => p.Mesh.Name == "sk54_rhys_templePortOn.d3dmesh");
        Assert.DoesNotContain(parts, p => p.Mesh.Name == "sk54_rhys_hairHat.d3dmesh");
        Assert.Same(result[0], result[0]);
    }

    [SkippableFact]
    public void A_variant_with_its_own_body_is_left_alone()
    {
        var future = new CharacterEntry("sk54_rhysFuture", "Rhys", "Future", Skeleton, new[]
        {
            Part("sk54_rhysFuture_body.d3dmesh", "Upper body", true),
        });
        var result = CharacterGrouping.InheritBaseParts(new[] { Base(), future });
        Assert.Single(result[1].Parts);
    }
}
