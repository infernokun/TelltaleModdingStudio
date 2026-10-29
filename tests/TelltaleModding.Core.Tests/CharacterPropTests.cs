using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// Character agent props extracted from the games: Clementine 400 from the Definitive
/// Series (WalkingDead401_data, MSV6) and Rhys from Tales from the Borderlands (101, MSV5).
/// </summary>
public class CharacterPropTests
{
    private static readonly string[] ClementineMeshes =
    {
        "bodyLowerB", "bodyUpperB", "bodyUpperBBackpack", "eyesMouth", "hairBandage",
        "hairCap", "hairSidePony", "hands", "handsNoFinger", "head",
    };

    private static ulong Sym(string name) => TelltaleCrc64.HashSymbol(name);

    [SkippableFact]
    public void BoolTypeSymbol_MatchesTheEnginesTypeName()
    {
        // Bytes c0 d6 75 75 58 c5 04 90 precede every bool run in real props.
        Assert.Equal(0x9004C5587575D6C0UL, CharacterProp.BoolType);
    }

    [SkippableFact]
    public void Clementine400_HasSkeletonAndFullMeshList()
    {
        var prop = CharacterProp.TryParse(File.ReadAllBytes(SampleData.Sample("sk62_clementine400.prop")));

        Assert.NotNull(prop);
        Assert.True(prop!.DescribesCharacter);
        Assert.Equal(Sym("sk62_clementine400.skl"), prop.SkeletonSymbol);
        Assert.Equal(
            ClementineMeshes.Select(m => Sym($"sk62_clementine400_{m}.d3dmesh")).OrderBy(s => s),
            prop.MeshListSymbols.OrderBy(s => s));
        Assert.Contains(Sym("player.prop"), prop.ParentSymbols);
    }

    [SkippableFact]
    public void Clementine400_TopLevelVisibilityIsTheDefaultOutfit()
    {
        var prop = CharacterProp.TryParse(File.ReadAllBytes(SampleData.Sample("sk62_clementine400.prop")))!;

        var on = ClementineMeshes.Where(m => prop.IsMeshVisible($"sk62_clementine400_{m}") == true).ToArray();
        var off = ClementineMeshes.Where(m => prop.IsMeshVisible($"sk62_clementine400_{m}.d3dmesh") == false).ToArray();

        Assert.Equal(new[] { "bodyLowerB", "bodyUpperB", "eyesMouth", "hairCap", "hands", "head" }, on);
        // The nested style sets ("bandageOnHead", "headHatless", "handsMissingFinger") flip
        // hairBandage / hairSidePony / handsNoFinger on, but they are not the defaults.
        Assert.Equal(new[] { "bodyUpperBBackpack", "hairBandage", "hairSidePony", "handsNoFinger" }, off);
    }

    [SkippableFact]
    public void Rhys_ListsAlternatesButOnlyShowsHisOwnBody()
    {
        var prop = CharacterProp.TryParse(File.ReadAllBytes(SampleData.Sample("sk54_rhys.prop")))!;

        Assert.Equal(Sym("sk54_rhys.skl"), prop.SkeletonSymbol);
        Assert.Contains(Sym("sk54_rhysFuture_body.d3dmesh"), prop.MeshListSymbols);
        Assert.Null(prop.MeshSymbol); // "D3D Mesh" is present but empty
        Assert.True(prop.IsMeshVisible("sk54_rhys_body"));
        Assert.True(prop.IsMeshVisible("sk54_rhys_templePortOff"));
        Assert.False(prop.IsMeshVisible("sk54_rhys_templePortOn"));
        Assert.False(prop.IsMeshVisible("sk54_rhysFuture_body"));
        Assert.Null(prop.IsMeshVisible("sk54_rhys_notAMesh"));
    }

    [SkippableFact]
    public void VisibilityKey_IsTheEnginesMeshVisibleKey() =>
        Assert.Equal(Sym("Mesh sk62_clementine400_head - Visible"), CharacterProp.VisibilityKey("sk62_clementine400_head"));

    [SkippableFact]
    public void NonPropertySetBytes_AreRejectedOrEmpty()
    {
        Assert.Null(CharacterProp.TryParse(new byte[8]));
        var mesh = CharacterProp.TryParse(File.ReadAllBytes(SampleData.TinyMeshSample));
        Assert.False(mesh?.DescribesCharacter ?? false);
    }
}
