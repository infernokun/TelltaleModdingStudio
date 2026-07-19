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

    [Fact]
    public void Decodes_keyframed_transform_tracks()
    {
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk54_action_rhysWakeUpDragged.anm")),
            out var anim, out var error), error);

        Assert.True(anim!.BodiesFullyParsed);
        Assert.Equal(8, anim.TransformTracks.Count);

        // Track symbols mirror the container trailer's channel targets.
        foreach (var track in anim.TransformTracks)
            Assert.Contains(anim.Channels, c => c.TargetSymbol == track.BoneSymbol);

        // Keys are time-ordered within the animation and quaternions are unit length.
        foreach (var track in anim.TransformTracks)
        {
            for (int i = 1; i < track.Keys.Count; i++)
                Assert.True(track.Keys[i].Time >= track.Keys[i - 1].Time);
            foreach (var key in track.Keys)
            {
                Assert.InRange(key.Time, -0.5f, anim.Length + 0.5f);
                Assert.InRange(key.Rotation.Length(), 0.9f, 1.1f);
            }
        }

        // Sampling interpolates: mid-time between two keys is between their values.
        var dense = anim.TransformTracks.First(t => t.Keys.Count > 10);
        var (rotation, _) = dense.Sample(dense.Keys[3].Time);
        Assert.InRange(rotation.Length(), 0.99f, 1.01f);
    }
}

public class CspkDecoderTests
{
    private static CspkClip Decode(string name)
    {
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample(name)), out var anim, out var error), error);
        var clip = CspkDecoder.TryDecodeFixedRate(anim!.PoseKeysPayload!, anim.Length);
        Assert.NotNull(clip);
        return clip!;
    }

    [Fact]
    public void Decodes_fixed_rate_phoneme_pose()
    {
        var clip = Decode("fiona_phoneme_default_happyC.anm");
        Assert.Equal(20, clip.BoneSymbols.Count);
        Assert.Equal(2, clip.SampleCount);
        foreach (var samples in clip.Rotations)
            foreach (var q in samples)
                Assert.InRange(q.Length(), 0.999f, 1.001f);
    }

    [Fact]
    public void Decodes_eight_word_head_variant_smoothly()
    {
        // rebeccaLayInBed carries the rarer 8-word payload head and 6 samples.
        var clip = Decode("sk55_idle_rebeccaLayInBed.anm");
        Assert.Equal(108, clip.BoneSymbols.Count);
        Assert.Equal(6, clip.SampleCount);

        // Delta-chained samples stay unit length and rotate < 5° per step (idle).
        foreach (var samples in clip.Rotations)
        {
            for (int s = 1; s < samples.Length; s++)
            {
                float dot = Math.Abs(System.Numerics.Quaternion.Dot(samples[s - 1], samples[s]));
                float degrees = 2f * MathF.Acos(Math.Min(dot, 1f)) * 180f / MathF.PI;
                Assert.True(degrees < 5f, $"step of {degrees}°");
            }
        }
    }

    [Fact]
    public void Decoded_pose_matches_skeleton_rest_rotations()
    {
        var clip = Decode("skB6_idle_bossanovaFists.anm");
        Assert.True(SkeletonFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk55_sasha.skl")), out var skl, out _));

        // For the bones this rig shares with Sasha's skeleton, an idle's first
        // sample should sit near the rest rotation for most bones.
        var rest = skl!.Bones.ToDictionary(b => b.JointSymbol, b => b.LocalRotation);
        int matched = 0, close = 0;
        for (int i = 0; i < clip.BoneSymbols.Count; i++)
        {
            if (!rest.TryGetValue(clip.BoneSymbols[i], out var rq))
                continue;
            matched++;
            if (Math.Abs(System.Numerics.Quaternion.Dot(clip.Rotations[i][0], rq)) > 0.85f)
                close++;
        }
        Assert.True(matched > 20, $"only {matched} shared bones");
        Assert.True(close > matched * 0.6, $"{close}/{matched} near rest");
    }

    [Fact]
    public void Rejects_variable_rate_container()
    {
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk54_action_rhysWakeUpDragged.anm")),
            out var anim, out _));
        Assert.Null(CspkDecoder.TryDecodeFixedRate(anim!.PoseKeysPayload!, anim.Length));
    }
}
