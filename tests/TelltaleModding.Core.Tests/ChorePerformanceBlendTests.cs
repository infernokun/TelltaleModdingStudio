using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// How a performance blends body clips that play at once: by priority, then by weight.
/// </summary>
public class ChorePerformanceBlendTests
{
    private const string Skeleton = "sk55_sasha.skl", Mesh = "sk55_sasha.d3dmesh";
    private const string Idle = "sk55_idle_sashaHandsHips.anm", Walk = "sk55_move_sashaWalk.anm";

    private static AnimationFile Clip(string sample)
    {
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(sample)), out var clip, out var error), error);
        return clip!;
    }

    private static PerformanceClip Spec(string name, float start, int priority, float weight) =>
        new(name, start, 100, false, 1, priority, false,
            new ResourceControl(start, 100, 100, 1, null, new ChoreCurve([new ChoreCurveKey(0, weight, 2)]), null));

    private static (SkeletonFile Skeleton, MeshGeometry Geometry) Rig()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(Skeleton)), out var skeleton, out var error), error);
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample(Mesh)), out var mesh, out error), error);
        return (skeleton!, mesh!.GetGeometry());
    }

    // Bones both clips turn, with each clip's rotation for them.
    private static List<(SkeletonBone Bone, Quaternion Idle, Quaternion Walk)> Shared(SkeletonFile skeleton, MeshGeometry geometry, float idleTime, float walkTime)
    {
        var idle = new AnimationPlayer(geometry, skeleton);
        idle.Load(Clip(Idle), Idle);
        var walk = new AnimationPlayer(geometry, skeleton);
        walk.Load(Clip(Walk), Walk);
        var shared = new List<(SkeletonBone, Quaternion, Quaternion)>();
        foreach (var bone in skeleton.Bones)
        {
            if (bone.ParentIndex < 0 || !idle.Animates(bone.JointSymbol) || !walk.Animates(bone.JointSymbol))
                continue;
            var a = idle.SampleLocal(bone, idleTime).Rot;
            var b = walk.SampleLocal(bone, walkTime).Rot;
            if (Angle(a, b) > 5)
                shared.Add((bone, a, b));
        }
        Assert.NotEmpty(shared);
        return shared;
    }

    [SampleFact(Skeleton, Mesh, Idle, Walk)]
    public void A_higher_priority_clip_wins_even_when_a_lower_one_starts_later()
    {
        var (skeleton, geometry) = Rig();
        var performance = new ChorePerformance(geometry, skeleton,
            [(Spec(Walk, 0, 10, 1), Clip(Walk)), (Spec(Idle, 0.5f, 1, 1), Clip(Idle))]);

        foreach (var (bone, _, walk) in Shared(skeleton, geometry, 0.5f, 1))
        {
            var rot = performance.SampleLocal(bone, 1, (bone.LocalPosition, bone.LocalRotation)).Rot;
            Assert.True(Angle(rot, walk) < 0.5f, $"bone {bone.JointSymbol:x} is {Angle(rot, walk):0.0} degrees off the walk");
        }
    }

    [SampleFact(Skeleton, Mesh, Idle, Walk)]
    public void Clips_of_one_priority_average_then_cover_the_pose_under_them_by_their_largest_weight()
    {
        var (skeleton, geometry) = Rig();
        var performance = new ChorePerformance(geometry, skeleton,
            [(Spec(Walk, 0, 5, 0.5f), Clip(Walk)), (Spec(Idle, 0, 5, 0.5f), Clip(Idle))]);

        // Half of each, and the pair counts half over the rest pose.
        foreach (var (bone, idle, walk) in Shared(skeleton, geometry, 1, 1))
        {
            var expected = Quaternion.Slerp(bone.LocalRotation, Quaternion.Slerp(walk, idle, 0.5f), 0.5f);
            var rot = performance.SampleLocal(bone, 1, (bone.LocalPosition, bone.LocalRotation)).Rot;
            Assert.True(Angle(rot, expected) < 0.5f, $"bone {bone.JointSymbol:x} is {Angle(rot, expected):0.0} degrees off");
        }
    }

    [SampleFact("sk56_clementine.skl", "clementine_headGesture_lookDown_add.anm", "clementine200_headGesture_lookUp_add.chore")]
    public void A_head_gesture_limited_to_the_head_groups_leaves_the_brows_alone()
    {
        // The look-down frowns, but the chore limits it to the head so the brows stay put.
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk56_clementine.skl")), out var skeleton, out var error), error);
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample("clementine200_headGesture_lookUp_add.chore")), out var chore, out error), error);
        const string Gesture = "clementine_headGesture_lookDown_add.anm";
        var spec = new PerformanceClip(Gesture, 0, 10, false, 1, 1, true,
            new ResourceControl(0, 10, 10, 1, null, new ChoreCurve([new ChoreCurveKey(0, 1, 2)]), null));
        var geometry = new MeshGeometry([], [], [], [], [], [], default, default);
        var free = new ChorePerformance(geometry, skeleton!, [(spec, Clip(Gesture))]);
        var limited = new ChorePerformance(geometry, skeleton!, [(spec with { Groups = [chore!.Resources[0].GroupInclude!] }, Clip(Gesture))]);

        float Turn(ChorePerformance performance, ulong joint)
        {
            var bone = skeleton!.Bones.First(b => b.JointSymbol == joint);
            return Angle(performance.SampleLocal(bone, 1, (bone.LocalPosition, bone.LocalRotation)).Rot, bone.LocalRotation);
        }
        const ulong Head = 0x65d5ebe568bb61ab, Brow = 0x9e480b44a3feb64c;
        Assert.True(Turn(free, Brow) > 10);
        Assert.True(Turn(limited, Brow) < 0.01f);
        Assert.True(Turn(limited, Head) > 2);
        Assert.Equal(Turn(free, Head), Turn(limited, Head), 0.01f);
    }

    private static float Angle(Quaternion a, Quaternion b) =>
        2 * MathF.Acos(MathF.Min(1, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180 / MathF.PI;
}
