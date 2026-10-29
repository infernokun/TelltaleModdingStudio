using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class FaceRigTests
{
    private static (MeshGeometry Geometry, SkeletonFile Skeleton) Bigby()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_bigby.skl")), out var skl, out var error), error);
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_bigby_head.d3dmesh")), out var mesh, out error), error);
        return (mesh!.GetGeometry(), skl!);
    }

    private static AnimationFile Clip(string name)
    {
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var clip, out var error), error);
        return clip!;
    }

    private static FaceRig Rig(out SkeletonFile skeleton)
    {
        var (geometry, skl) = Bigby();
        skeleton = skl;
        var rig = new FaceRig(geometry, skl);
        rig.SetMouthShapes(new Dictionary<string, AnimationFile>
        {
            ["default"] = Clip("face_bigby_phoneme_default.anm"),
            ["AA"] = Clip("face_bigby_phoneme_AA.anm"),
            ["MM"] = Clip("face_bigby_phoneme_MM.anm"),
        });
        return rig;
    }

    private static float Angle(Quaternion a, Quaternion b) =>
        2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0f, 1f)) * 180f / MathF.PI;

    private static void AssertSameWorld(Matrix4x4[] expected, Matrix4x4[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Vector3.Distance(expected[i].Translation, actual[i].Translation) < 1e-5f, $"bone {i} moved");
    }

    [SkippableFact]
    public void Mouth_weights_past_one_are_normalised()
    {
        var rig = Rig(out _);
        rig.SetMouthWeight("AA", 1f);
        rig.SetMouthWeight("MM", 0.6f);
        var weights = rig.MouthWeightsAt(0);
        Assert.Equal(1f, weights.Values.Sum(), 1e-5f);
        Assert.Equal(1f / 1.6f, weights["AA"], 1e-5f);

        rig.ClearMouth();
        rig.SetMouthWeight("AA", 0.3f);
        Assert.Equal(0.3f, rig.MouthWeightsAt(0)["AA"], 1e-5f);
    }

    [SkippableFact]
    public void Line_keys_drive_the_mouth_over_time()
    {
        var rig = Rig(out _);
        rig.SetLine(new[] { new PhonemeKey(1f, TelltaleCrc64.HashSymbol("AA"), 0.2f, 0.4f, 0.2f, 1f) });
        Assert.Empty(rig.MouthWeightsAt(0.5f));
        Assert.Equal(1f, rig.MouthWeightsAt(1.4f)["AA"], 1e-4f);
        Assert.False(rig.IsNeutral);
    }

    [SkippableFact]
    public void Neutral_rig_keeps_the_body_pose()
    {
        var rig = Rig(out var skeleton);
        Assert.True(rig.IsNeutral);
        AssertSameWorld(skeleton.BindPoseWorld(), rig.Pose(0));

        var body = new AnimationPlayer(rig.Geometry, skeleton);
        body.Load(Clip("sk54_bigbyCrouch_weightShiftAShort.anm"), "sk54_bigbyCrouch_weightShiftAShort.anm");
        rig.Body = body;
        AssertSameWorld(body.Pose(0.3f), rig.Pose(0.3f));
    }

    [SkippableFact]
    public void Expression_at_strength_zero_is_the_base_pose()
    {
        var rig = Rig(out var skeleton);
        rig.SetExpression(Clip("face_bigby_face_browsGestureUp.anm"), "face_bigby_face_browsGestureUp.anm");
        Assert.NotEmpty(rig.Expression!.Bones);

        rig.ExpressionStrength = 0f;
        Assert.True(rig.IsNeutral);
        AssertSameWorld(skeleton.BindPoseWorld(), rig.Pose(0));

        rig.ExpressionStrength = 1f;
        var bone = skeleton.Bones.First(b => rig.Expression.Bones.TryGetValue(b.JointSymbol, out var e) &&
                                              Angle(e.Rot, b.LocalRotation) > 1f);
        var full = rig.SampleLocal(bone, 0);
        Assert.Equal(0f, Angle(full.Rot, rig.Expression.Bones[bone.JointSymbol].Rot), 0.01f);
        rig.ExpressionStrength = 0.5f;
        var half = rig.SampleLocal(bone, 0);
        Assert.Equal(Angle(bone.LocalRotation, full.Rot) / 2f, Angle(bone.LocalRotation, half.Rot), 0.05f);
    }

    [SkippableFact]
    public void Open_mouth_shape_moves_the_mesh()
    {
        var rig = Rig(out _);
        int n = rig.Geometry.Positions.Length;
        var rest = new Vector3[n];
        var open = new Vector3[n];
        var normals = new Vector3[n];
        rig.Evaluate(0, rest, normals);
        Assert.True(rest.Zip(rig.Geometry.Positions).All(p => Vector3.Distance(p.First, p.Second) < 1e-5f));

        rig.SetMouthWeight("AA", 1f);
        rig.Evaluate(0, open, normals);
        float most = rest.Zip(open).Max(p => Vector3.Distance(p.First, p.Second));
        Assert.InRange(most, 0.005f, 0.05f);
    }

    [SkippableFact]
    public void Manual_offset_rotates_only_the_chosen_bone()
    {
        var rig = Rig(out var skeleton);
        var brows = rig.SamplePose(Clip("face_bigby_face_browsGestureUp.anm"), "brows");
        var faces = FaceBones.Find(skeleton, rig.DrivenBones.Concat(brows.Bones.Keys));
        var jaw = faces.Single(b => b.Label == "Jaw");
        var bone = skeleton.Bones[jaw.Index];

        rig.SetBoneOffset(jaw.Joint, new Vector3(20f, 0, 0));
        var local = rig.SampleLocal(bone, 0);
        Assert.Equal(20f, Angle(bone.LocalRotation, local.Rot), 0.05f);
        // About the bone's own X axis.
        var turn = Quaternion.Conjugate(bone.LocalRotation) * local.Rot;
        Assert.True(MathF.Abs(turn.X) > 0.17f && MathF.Abs(turn.Y) < 1e-4f && MathF.Abs(turn.Z) < 1e-4f);

        var rest = skeleton.BindPoseWorld();
        var posed = rig.Pose(0);
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            bool below = false;
            for (int p = i; p >= 0 && !below; p = skeleton.Bones[p].ParentIndex)
                below = p == jaw.Index;
            if (!below)
                Assert.True(Vector3.Distance(rest[i].Translation, posed[i].Translation) < 1e-6f, $"bone {i} moved");
        }
        Assert.Contains(Enumerable.Range(0, skeleton.Bones.Count), i => skeleton.Bones[i].ParentIndex == jaw.Index &&
            Vector3.Distance(rest[i].Translation, posed[i].Translation) > 1e-3f);

        rig.SetBoneOffset(jaw.Joint, Vector3.Zero);
        Assert.True(rig.IsNeutral);
    }

    [SkippableFact]
    public void Additive_gesture_layers_onto_the_pose_underneath()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk55_sasha.skl")), out var skeleton, out var error), error);
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk55_sasha.d3dmesh")), out var mesh, out error), error);
        var rig = new FaceRig(mesh!.GetGeometry(), skeleton!);
        const string name = "sk55_sashaHandsHips_bobLeft_add.anm";
        rig.SetGesture(Clip(name), name);
        Assert.True(rig.HasGesture);
        Assert.True(rig.GestureIsAdditive);

        var player = new AnimationPlayer(rig.Geometry, skeleton!);
        player.Load(Clip(name), name);
        float t = rig.GestureLength * 0.4f;
        rig.GestureTime = t;
        var body = new AnimationPlayer(rig.Geometry, skeleton!);
        body.Load(Clip("sk55_idle_sashaHandsHips.anm"), "sk55_idle_sashaHandsHips.anm");
        var bone = skeleton!.Bones.Where(b => player.Animates(b.JointSymbol) && body.Animates(b.JointSymbol))
            .OrderByDescending(b => Angle(player.SampleLocal(b, t).Rot, b.LocalRotation)).First();
        Assert.True(Angle(player.SampleLocal(bone, t).Rot, bone.LocalRotation) > 1f);

        // Over the rest pose the gesture plays as the clip does.
        Assert.Equal(0f, Angle(player.SampleLocal(bone, t).Rot, rig.SampleLocal(bone, 0).Rot), 0.01f);

        // Over a body clip it adds the same turn on top of the body's pose.
        rig.Body = body;
        var under = body.SampleLocal(bone, 0.5f);
        var layered = rig.SampleLocal(bone, 0.5f);
        var gestureTurn = Quaternion.Conjugate(bone.LocalRotation) * player.SampleLocal(bone, t).Rot;
        Assert.True(Angle(under.Rot, bone.LocalRotation) > 0.5f);
        Assert.Equal(0f, Angle(gestureTurn, Quaternion.Conjugate(under.Rot) * layered.Rot), 0.05f);

        // Half weight, half the turn.
        rig.GestureWeight = 0.5f;
        var half = rig.SampleLocal(bone, 0.5f);
        Assert.Equal(Angle(Quaternion.Identity, gestureTurn) / 2f, Angle(under.Rot, half.Rot), 0.05f);
    }

    [SkippableFact]
    public void Reset_returns_to_neutral()
    {
        var rig = Rig(out var skeleton);
        rig.SetExpression(Clip("face_bigby_phoneme_default_angryA.anm"), "x");
        rig.SetMouthWeight("MM", 1);
        rig.SetBoneOffset(skeleton.Bones[60].JointSymbol, new Vector3(5, 0, 0));
        Assert.False(rig.IsNeutral);
        rig.ResetToNeutral();
        Assert.True(rig.IsNeutral);
        Assert.Equal(3, rig.MouthShapes.Count);
    }

    [SkippableFact]
    public void Exports_a_face_pose_as_a_single_frame_clip()
    {
        SampleData.Require("sk54_bigby_head.d3dmesh", "sk54_bigby.skl");
        var model = new ModelAssembler(new FolderAssetResolver(SampleData.SamplesDir)).Build(new[] { "sk54_bigby_head.d3dmesh" }, "sk54_bigby.skl");
        Assert.True(model.CanAnimate);
        var rig = new FaceRig(model.Geometry, model.Skeleton!);
        rig.SetMouthShapes(new Dictionary<string, AnimationFile> { ["AA"] = Clip("face_bigby_phoneme_AA.anm") });
        rig.SetMouthWeight("AA", 1);
        var pose = model.Skeleton!.Bones.ToDictionary(b => b.JointSymbol, b => rig.SampleLocal(b, 0));

        using var stream = new MemoryStream();
        Export.GltfExporter.WriteGlb(model, stream, new[]
        {
            new Export.GltfClip("face pose", new AnimationPlayer(model.Geometry, model.Skeleton!)) { Pose = pose },
        });
        var glb = stream.ToArray();
        int jsonLength = BitConverter.ToInt32(glb, 12);
        using var json = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        var root = json.RootElement;
        var animation = root.GetProperty("animations")[0];
        Assert.Equal("face pose", animation.GetProperty("name").GetString());
        Assert.Equal(model.Skeleton.Bones.Count * 2, animation.GetProperty("channels").GetArrayLength());
        // One key per channel (read out first: a failure message must not touch the disposed document).
        var keyCounts = animation.GetProperty("samplers").EnumerateArray()
            .Select(s => root.GetProperty("accessors")[s.GetProperty("input").GetInt32()].GetProperty("count").GetInt32())
            .ToList();
        Assert.All(keyCounts, count => Assert.Equal(1, count));
    }

    [SkippableFact]
    public void Indexes_facial_clips_by_character()
    {
        var files = new[]
        {
            "rhys_phoneme_default.anm", "rhys_phoneme_AA.anm", "rhys_phoneme_EE.anm", "rhys_phoneme_AA_smugA.anm",
            "rhys_phoneme_default_smugA.anm", "rhys_phonemes_smugA.anm", "rhys_face_moodSmugA.anm", "rhys_face_moodHappyA.anm",
            "rhys_phoneme_AA_moodAngryC.anm", "rhys_headGesture_nodYes.anm", "rhys_headGesture_nodYes_add.anm",
            "rhys_face_blink_add.anm", "rhys_face_default.anm", "clementine400_phoneme_AH_angryA.anm",
            "clementine400_phoneme_AA_normalA.anm", "clementine400_phoneme_AA_angryA.anm", "sk54_rhys.skl", "obj_cup.anm",
        };
        var index = FaceClipSet.Index(files);
        Assert.Equal(new[] { "clementine400", "rhys" }, index.Keys.OrderBy(k => k));

        var rhys = index["rhys"];
        Assert.Equal(new[] { "AA", "EE" }, rhys.MouthShapes);
        Assert.True(rhys.HasNeutralMouth);
        Assert.Equal(new[] { "angryC", "happyA", "smugA" }, rhys.Moods.Select(m => m.Key));
        var smug = rhys.Mood("smugA")!;
        Assert.Equal("rhys_phonemes_smugA.anm", smug.ExpressionClip);
        Assert.Equal("Smug A", smug.Label);
        Assert.Equal("rhys_phoneme_AA_smugA.anm", rhys.MouthClip("AA", smug));
        Assert.Equal("rhys_phoneme_EE.anm", rhys.MouthClip("EE", smug));
        Assert.Equal("moodAngryC", rhys.Mood("angryC")!.PhonemeSuffix);
        Assert.Equal("rhys_face_moodHappyA.anm", rhys.Mood("happyA")!.ExpressionClip);
        Assert.Same(smug, rhys.FindMood(new[] { "lookAt", "smugA" }));

        Assert.Equal(2, rhys.Gestures.Count);
        var nod = rhys.Gestures.Single(g => g.Kind == FaceGestureKind.Head);
        Assert.Equal(("rhys_headGesture_nodYes_add.anm", true, "Nod yes"), (nod.FileName, nod.IsAdditive, nod.Label));

        // Without neutral poses the plain mood stands in.
        var clem = index["clementine400"];
        Assert.False(clem.HasNeutralMouth);
        Assert.Equal(new[] { "AA", "AH" }, clem.MouthShapes);
        Assert.Equal("clementine400_phoneme_AA_normalA.anm", clem.MouthClip("AA", null));

        Assert.Equal("rhys", FaceClipSet.MatchPrefix(index, "sk54_rhys", "Rhys"));
        Assert.Equal("clementine400", FaceClipSet.MatchPrefix(index, "sk62_clementine400", "Clementine"));
        Assert.Null(FaceClipSet.MatchPrefix(index, "sk54_vaughn", "Vaughn"));
    }

    [SkippableFact]
    public void Groups_face_bones_by_name_or_by_where_they_sit()
    {
        var rig = Rig(out var skeleton);
        rig.SetExpression(Clip("face_bigby_face_browsGestureUp.anm"), "brows");
        var bones = FaceBones.Find(skeleton, rig.DrivenBones);
        Assert.Contains(bones, b => b.Group == FaceBoneGroup.JawMouth && b.Label == "Jaw");
        Assert.Contains(bones, b => b.Group == FaceBoneGroup.Brows && b.Label.Contains("(left)"));
        Assert.Contains(bones, b => b.Group == FaceBoneGroup.Lips);
        Assert.Equal(bones.Count, bones.Select(b => b.Label).Distinct().Count());
        Assert.All(bones, b => Assert.StartsWith("bone_", b.Name));

        Assert.Equal(FaceBoneGroup.Brows, FaceBones.GroupOf("browOuter_L"));
        Assert.Equal(FaceBoneGroup.Eyes, FaceBones.GroupOf("eyeLidUpper_R"));
        Assert.Null(FaceBones.GroupOf("head"));
        Assert.Equal("Brow outer (left)", FaceBones.Label("browOuter_L"));
    }
}
