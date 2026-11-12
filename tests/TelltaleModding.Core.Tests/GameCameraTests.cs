using System.Numerics;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// The camera the game shows a line from. Samples (Definitive Series):
/// <c>env_lodgeExteriorAndMainRoom_cs_hideCouch_1.chore</c> and <c>adv_lodgeExteriorAndMainRoom.scene</c>.
/// </summary>
public class GameCameraTests
{
    private const string Chore = "env_lodgeExteriorAndMainRoom_cs_hideCouch_1.chore", Scene = "adv_lodgeExteriorAndMainRoom.scene";

    // About where an adult's face is in their own space.
    private static readonly Vector3 Face = new(0, 1.55f, 0.1f);

    [SampleFact(Chore, Scene)]
    public void Finds_a_camera_that_frames_a_speaker_and_looks_at_them()
    {
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample(Chore)), out var chore, out var error), error);
        var scene = SceneFile.Load(File.ReadAllBytes(SampleData.Sample(Scene)));

        var shown = ChorePerformance.Lines(chore!)
            .Select(line => (line, camera: GameCamera.Find(chore!, scene, line.Agent, line.Start, line.End, Face)))
            .Where(x => x.camera is not null)
            .ToList();
        Assert.NotEmpty(shown);

        foreach (var (line, camera) in shown)
        {
            float duration = line.End - line.Start;
            var framed = Enumerable.Range(0, 8).Select(i => duration * (i + 0.5f) / 8).Where(t => camera!.Frames(Face, t)).ToList();
            Assert.NotEmpty(framed);
            var view = camera!.At(framed[0]);
            Assert.Equal(1f, view.Forward.Length(), 1e-3f);
            Assert.InRange(view.FieldOfView, 10, 90);
            // The face is in front of the camera.
            Assert.True(Vector3.Dot(Face - view.Eye, view.Forward) > 0);
        }
    }

    [SampleFact(Chore, Scene)]
    public void A_shot_from_behind_is_not_a_shot_of_the_face()
    {
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample(Chore)), out var chore, out var error), error);
        var scene = SceneFile.Load(File.ReadAllBytes(SampleData.Sample(Scene)));
        var (line, camera) = ChorePerformance.Lines(chore!)
            .Select(l => (l, c: GameCamera.Find(chore!, scene, l.Agent, l.Start, l.End, Face)))
            .First(x => x.c is not null);

        float t = Enumerable.Range(0, 8).Select(i => (line.End - line.Start) * (i + 0.5f) / 8).First(x => camera!.Frames(Face, x));
        var eye = camera!.At(t).Eye - Face;
        float towardCamera = MathF.Atan2(eye.X, eye.Z);
        Assert.True(camera.ShowsFace(Face, towardCamera, t));
        Assert.False(camera.ShowsFace(Face, towardCamera + MathF.PI, t));
    }
}
