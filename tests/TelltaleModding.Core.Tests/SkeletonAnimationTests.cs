using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

public class SkeletonFileTests
{
    private static SkeletonFile Load(string name)
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var skl, out var error), error);
        return skl!;
    }

    [Fact]
    public void Parses_character_skeleton()
    {
        var skeleton = Load("sk54_felix.skl");
        Assert.Equal(111, skeleton.Bones.Count);

        var root = skeleton.Bones[0];
        Assert.Equal(-1, root.ParentIndex);
        // Root's mirror is itself and it sits ~0.88 units up — sanity from real data.
        Assert.InRange(root.LocalPosition.Y, 0.8f, 1.0f);

        // Every non-root bone's parent index is valid and precedes it.
        for (int i = 1; i < skeleton.Bones.Count; i++)
            Assert.InRange(skeleton.Bones[i].ParentIndex, 0, i - 1);
    }

    [Fact]
    public void Rejects_mesh_files()
    {
        Assert.False(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.MeshSample), out _, out var error));
        Assert.Contains("Skeleton", error);
    }
}

public class AnimationFileTests
{
    [Fact]
    public void Parses_skeletal_animation_and_identifies_its_rig()
    {
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk54_action_rhysWakeUpDragged.anm")),
            out var anim, out var error), error);

        Assert.Equal(5, anim!.Version);
        Assert.InRange(anim.Length, 1f, 30f);
        Assert.Equal(9, anim.Channels.Count);
        Assert.True(anim.IsSkeletal);
        Assert.Contains(anim.Channels, c => c.TypeName == "CompressedSkeletonPoseKeys");
        Assert.Contains(anim.Channels, c => c.TypeName == "KeyframedValue<Transform>");

        // The pose payload embeds the rig's bones: nearly all of Rhys's joints appear.
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_rhys.skl")), out var rhys, out _));
        var found = anim.FindBoneSymbols(rhys!.JointSymbols);
        Assert.True(found.Count > rhys.Bones.Count * 0.9,
            $"expected >90% of {rhys.Bones.Count} joints, found {found.Count}");

        // And a non-matching skeleton scores far lower.
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_felix.skl")), out var felix, out _));
        Assert.True(anim.FindBoneSymbols(felix!.JointSymbols).Count < found.Count);
    }
}
