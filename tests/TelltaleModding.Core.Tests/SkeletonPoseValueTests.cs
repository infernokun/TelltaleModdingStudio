using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// Uncompressed skeleton poses, which The Walking Dead: Michonne uses for most clips. Samples:
/// <c>spv_michonne_phoneme_AA_angryA.anm</c> and <c>spv_alex_headGesture_lookDown_add.anm</c>.
/// </summary>
public class SkeletonPoseValueTests
{
    private const string MouthSample = "spv_michonne_phoneme_AA_angryA.anm";
    private const string GestureSample = "spv_alex_headGesture_lookDown_add.anm";

    private static AnimationFile Clip(string sample)
    {
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(sample)), out var clip, out var error), error);
        return clip!;
    }

    [SampleFact(MouthSample)]
    public void Reads_a_held_mouth_pose()
    {
        var clip = Clip(MouthSample);
        var pose = clip.SkeletonPose!;

        Assert.True(clip.IsSkeletal);
        Assert.True(clip.BodiesFullyParsed);
        Assert.False(pose.IsAdditive);
        Assert.Equal(18, pose.BoneSymbols.Count);
        Assert.Equal(1, pose.SampleCount);
        var jaw = pose.Rotations[0][0];
        Assert.Equal(-0.0756f, jaw.X, 1e-3f);
        Assert.Equal(0.9971f, jaw.W, 1e-3f);
        // Translations are relative to the rest length, so they stay close to 1.
        Assert.All(pose.Translations, t => Assert.Equal(1f, t[0].Length(), 0.1f));
        Assert.Equal(pose.BoneSymbols.ToHashSet(), clip.FindBoneSymbols(pose.BoneSymbols.ToHashSet()));
    }

    [SampleFact(GestureSample)]
    public void Reads_an_additive_gesture_over_time()
    {
        var pose = Clip(GestureSample).SkeletonPose!;

        Assert.True(pose.IsAdditive);
        Assert.Equal(36, pose.BoneSymbols.Count);
        Assert.Equal(53, pose.SampleCount);
        Assert.Equal(0f, pose.SampleTimes[0]);
        Assert.Equal(2.333f, pose.SampleTimes[^1], 1e-3f);
        Assert.True(pose.SampleTimes.Zip(pose.SampleTimes.Skip(1)).All(p => p.First < p.Second));
        // Additive values are offsets, so the first one is no change.
        Assert.Equal(1f, Math.Abs(Quaternion.Dot(Quaternion.Identity, pose.Rotations[0][0])), 1e-4f);
        Assert.Equal(0f, pose.Translations[0][0].Length(), 1e-4f);
    }
}
