using TelltaleModding.Core.Export;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class BoneNamesTests
{
    [Fact]
    public void Bundled_list_names_bones_beyond_the_shared_rig()
    {
        Assert.True(BoneNames.Count > 19_000);
        foreach (var name in new[] { "index1_L", "pinky2_R", "foot_L", "bootLace", "abbieCarmichael_eyeLidLower_L" })
            Assert.Equal(name, BoneNames.TryGet(TelltaleCrc64.HashSymbol(name)));
    }

    [Fact]
    public void Shared_rig_names_keep_their_spelling_and_stay_standard()
    {
        Assert.Equal("head", BoneNames.TryGet(TelltaleCrc64.HashSymbol("head")));
        Assert.Equal("eye_L", BoneNames.TryGetStandard(TelltaleCrc64.HashSymbol("eye_L")));
        // Names only the bundled list knows are not read as face-rig names.
        Assert.Null(BoneNames.TryGetStandard(TelltaleCrc64.HashSymbol("index1_L")));
    }

    [Fact]
    public void Unknown_bones_are_named_after_their_symbol()
    {
        Assert.Null(BoneNames.TryGet(0x0123456789ABCDEF));
        Assert.Equal("bone_0123456789ABCDEF", BoneNames.For(0x0123456789ABCDEF));
    }

    [Fact]
    public void Parses_names_and_explicit_symbols()
    {
        var entries = BoneNames.Parse(new[]
        {
            "# comment", "", "  shoeLace_L  ", "// another comment",
            "E0E3EEDD9B630B88 strap", "0xE0E3EEDD9B630B89 = buckle", "bone_E0E3EEDD9B630B8A,sole",
            "not a symbol here",
        }).ToList();
        Assert.Equal(new (ulong?, string)[]
        {
            (null, "shoeLace_L"),
            (0xE0E3EEDD9B630B88, "strap"),
            (0xE0E3EEDD9B630B89, "buckle"),
            (0xE0E3EEDD9B630B8A, "sole"),
            (null, "not a symbol here"),
        }, entries);
    }

    [Fact]
    public void User_names_add_to_the_list_and_explicit_symbols_rename()
    {
        // Symbols no real skeleton uses, so other tests never see these names.
        const ulong renamed = 0x7E57_0000_0000_0001;
        var name = $"testOnlyBone{Guid.NewGuid():N}";
        var path = Path.Combine(Path.GetTempPath(), $"bonenames-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(path, new[] { name, $"{renamed:X16} myStrap", "head" });
        try
        {
            Assert.Equal(2, BoneNames.LoadFile(path));
            Assert.Equal(name, BoneNames.TryGet(TelltaleCrc64.HashSymbol(name)));
            Assert.Equal("myStrap", BoneNames.TryGet(renamed));
            Assert.Equal("head", BoneNames.TryGet(TelltaleCrc64.HashSymbol("head")));
            Assert.Equal(0, BoneNames.LoadFile(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [SampleFact("sk55_sasha.skl")]
    public void Names_every_bone_of_a_sample_skeleton()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk55_sasha.skl")), out var skeleton, out var error), error);
        Assert.All(skeleton!.Bones, b => Assert.NotNull(BoneNames.TryGet(b.JointSymbol)));
    }

    [SampleFact("sk54_felix.skl")]
    public void Face_rig_reads_only_the_shared_rig_names()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_felix.skl")), out var skeleton, out var error), error);
        // Face bones found by name use the shared rig name.
        foreach (var bone in FaceBones.Find(skeleton!))
        {
            if (BoneNames.TryGetStandard(bone.Joint) is { } standard)
                Assert.Equal(standard, bone.Name);
        }
    }
}
