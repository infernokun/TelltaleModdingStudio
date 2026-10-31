using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>The Dialogue camera that follows a speaker. Sample: Sasha's skeleton.</summary>
public class SpeakerCameraTests
{
    private const string Skeleton = "sk55_sasha.skl";

    private static SkeletonFile Load()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(Skeleton)), out var skeleton, out var error), error);
        return skeleton!;
    }

    [SampleFact(Skeleton)]
    public void Frames_the_head_and_shoulders_from_the_front_of_the_rest_pose()
    {
        var skeleton = Load();
        var rest = skeleton.BindPoseWorld();
        var camera = SpeakerCamera.Build(skeleton, _ => rest, 2)!;
        var shot = camera.At(1);

        float top = rest.Max(m => m.Translation.Y), bottom = rest.Min(m => m.Translation.Y);
        Assert.InRange(shot.Target.Y, bottom + (top - bottom) * 0.7f, top);
        Assert.InRange(shot.Height, (top - bottom) * 0.1f, (top - bottom) * 0.5f);
        Assert.Equal(0f, shot.Yaw, 0.05f);
    }

    [SampleFact(Skeleton)]
    public void Turns_with_a_character_who_turns()
    {
        var skeleton = Load();
        var rest = skeleton.BindPoseWorld();
        var quarter = Matrix4x4.CreateRotationY(MathF.PI / 2);
        var turned = rest.Select(m => m * quarter).ToArray();

        // Facing front for the first second, then a quarter turn: the camera comes round with them.
        var camera = SpeakerCamera.Build(skeleton, t => t < 1 ? rest : turned, 4)!;
        Assert.Equal(0f, camera.At(0).Yaw, 0.05f);
        Assert.Equal(MathF.PI / 2, camera.At(4).Yaw, 0.05f);
        var front = camera.At(0).Target;
        var side = camera.At(4).Target;
        Assert.Equal(new Vector2(front.Z, -front.X).Length(), new Vector2(side.X, side.Z).Length(), 0.02f);
    }
}
