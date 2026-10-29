using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

public class SkeletonFileTests
{
    private static SkeletonFile Load(string name)
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var skl, out var error), error);
        return skl!;
    }

    [SkippableFact]
    public void Parses_character_skeleton()
    {
        var skeleton = Load("sk54_felix.skl");
        Assert.Equal(111, skeleton.Bones.Count);

        var root = skeleton.Bones[0];
        Assert.Equal(-1, root.ParentIndex);
        // Root's mirror is itself and it sits ~0.88 units up, a sanity check from real data.
        Assert.InRange(root.LocalPosition.Y, 0.8f, 1.0f);

        // Every non-root bone's parent index is valid and precedes it.
        for (int i = 1; i < skeleton.Bones.Count; i++)
            Assert.InRange(skeleton.Bones[i].ParentIndex, 0, i - 1);
    }

    [SkippableFact]
    public void Rejects_mesh_files()
    {
        Assert.False(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.MeshSample), out _, out var error));
        Assert.Contains("Skeleton", error);
    }

    // MSV5 skeletons (The Wolf Among Us, Game of Thrones) store their entries without
    // the mirror bone fields; reading them with those fields misaligns every bone.
    [SkippableTheory]
    [InlineData("sk54_bigby.skl", 116)]
    [InlineData("sk61_asher.skl", 136)]
    [InlineData("sk61_bannermanForresterLow.skl", 84)]
    public void Parses_msv5_skeleton_without_mirror_fields(string name, int boneCount)
    {
        var skeleton = Load(name);
        Assert.Equal(boneCount, skeleton.Bones.Count);
        Assert.Equal(-1, skeleton.Bones[0].ParentIndex);
        for (int i = 1; i < skeleton.Bones.Count; i++)
            Assert.InRange(skeleton.Bones[i].ParentIndex, 0, i - 1);

        // Unit rotations, and a standing human-sized rest pose (feet at the floor, head
        // 1.5-2 m up) with every bone close to its parent.
        Assert.All(skeleton.Bones, b => Assert.InRange(b.LocalRotation.Length(), 0.99f, 1.01f));
        var world = skeleton.BindPoseWorld();
        Assert.InRange(world.Max(m => m.Translation.Y), 1.5f, 2.0f);
        Assert.InRange(world.Min(m => m.Translation.Y), -0.1f, 0.2f);
        Assert.All(skeleton.Bones, b => Assert.True(b.LocalPosition.Length() < 1.2f, $"bone offset {b.LocalPosition}"));
    }
}

public class AnimationFileTests
{
    [SkippableFact]
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

    [SkippableFact]
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
    private static AnimationFile LoadAnim(string name)
    {
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample(name)), out var anim, out var error), error);
        return anim!;
    }

    private static CspkClip Decode(string name)
    {
        var anim = LoadAnim(name);
        bool additive = anim.Channels.Any(c => c.TypeHash == KnownAnimationTypes.CompressedSkeletonPoseKeys
                                               && (c.Flags & CspkDecoder.AdditiveFlag) != 0);
        var clip = CspkDecoder.TryDecode(anim.PoseKeysPayload!, additive);
        Assert.NotNull(clip);
        return clip!;
    }

    private static SkeletonFile LoadSkeleton(string name)
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var skl, out var error), error);
        return skl!;
    }

    /// <summary>World pose the way the engine builds it: every named slot sets its bone's local
    /// rotation and (bone-length space) translation; other bones keep their rest transform.</summary>
    private static Matrix4x4[] EnginePose(SkeletonFile skeleton, CspkClip clip, float time)
    {
        var slot = new Dictionary<ulong, int>();
        for (int i = 0; i < clip.BoneSymbols.Count; i++)
            if (clip.BoneSymbols[i] != 0)
                slot.TryAdd(clip.BoneSymbols[i], i);
        return skeleton.ComposeWorld(bone =>
            slot.TryGetValue(bone.JointSymbol, out int s)
                ? (CspkDecoder.ToLocalTranslation(clip.SampleTranslation(s, time), CspkDecoder.TranslationFrame(bone)),
                   clip.SampleRotation(s, time))
                : (bone.LocalPosition, bone.LocalRotation));
    }

    private static float Degrees(Quaternion a, Quaternion b) =>
        2f * MathF.Acos(Math.Min(Math.Abs(Quaternion.Dot(a, b)), 1f)) * 180f / MathF.PI;

    /// <summary>Angle of the pelvis→chest line from vertical, in degrees.</summary>
    private static float TorsoTilt(SkeletonFile skeleton, Matrix4x4[] world)
    {
        // The chest: the highest rest-pose bone on the body's centre line within 45 cm of the
        // pelvis (spine, not arms or head).
        var bind = skeleton.BindPoseWorld();
        const int pelvis = 1;
        int chest = Enumerable.Range(2, bind.Length - 2)
            .Where(i => Math.Abs(bind[i].Translation.X) < 0.02f
                        && bind[i].Translation.Y - bind[pelvis].Translation.Y < 0.45f)
            .MaxBy(i => bind[i].Translation.Y);
        var axis = world[chest].Translation - world[pelvis].Translation;
        return MathF.Acos(Math.Abs(axis.Y) / axis.Length()) * 180f / MathF.PI;
    }

    [SkippableFact]
    public void Decodes_phoneme_pose()
    {
        var clip = Decode("fiona_phoneme_default_happyC.anm");
        Assert.Equal(20, clip.BoneSymbols.Count);
        Assert.Equal(2, clip.SampleCount);
        Assert.False(clip.IsAdditive);
        foreach (var samples in clip.Rotations)
            foreach (var q in samples)
                Assert.InRange(q.Length(), 0.999f, 1.001f);
    }

    [SkippableFact]
    public void Slot_table_starts_at_0x80_and_names_skeleton_bones()
    {
        // The 16 bytes before the table are unused; read from 0x80, every named slot is a bone
        // of the character's skeleton (the last three are zero padding to a group of four).
        var anim = LoadAnim("sk55_idle_sashaHandsHips.anm");
        Assert.All(anim.PoseKeysPayload![0x70..0x80], b => Assert.Equal(0, b));
        var clip = Decode("sk55_idle_sashaHandsHips.anm");
        var skeleton = LoadSkeleton("sk55_sasha.skl");
        Assert.Equal(80, clip.BoneSymbols.Count);
        var named = clip.BoneSymbols.Where(s => s != 0).ToList();
        Assert.Equal(77, named.Count);
        Assert.All(named, s => Assert.Contains(s, skeleton.JointSymbols));
        Assert.Equal(skeleton.Bones[0].JointSymbol, clip.BoneSymbols[0]);
    }

    [SkippableFact]
    public void Stored_translations_map_onto_the_skeletons_rest_offsets()
    {
        // An idle barely moves bone offsets: through the bone length and retarget rotation, the
        // decoded translations land on the skeleton's own rest offsets (a few spine and
        // clavicle bones shift by a centimetre or two).
        var clip = Decode("sk55_idle_sashaHandsHips.anm");
        var skeleton = LoadSkeleton("sk55_sasha.skl");
        int near = 0, total = 0;
        for (int s = 1; s < clip.BoneSymbols.Count; s++)
        {
            if (!skeleton.BoneIndexBySymbol.TryGetValue(clip.BoneSymbols[s], out int b))
                continue;
            var bone = skeleton.Bones[b];
            var local = CspkDecoder.ToLocalTranslation(clip.Translations[s][0], CspkDecoder.TranslationFrame(bone));
            total++;
            if ((local - bone.LocalPosition).Length() < Math.Max(0.002f, 0.02f * bone.LocalPosition.Length()))
                near++;
        }
        Assert.True(near >= total * 0.85, $"only {near}/{total} bones on their rest offsets");
    }

    [SkippableFact]
    public void Rest_offsets_round_trip_through_the_translation_frame()
    {
        foreach (var bone in LoadSkeleton("sk55_sasha.skl").Bones)
        {
            var frame = CspkDecoder.TranslationFrame(bone);
            Assert.InRange(frame.Correction.Length(), 0.999f, 1.001f);
            var back = CspkDecoder.ToLocalTranslation(CspkDecoder.FromLocalTranslation(bone.LocalPosition, frame), frame);
            Assert.True((back - bone.LocalPosition).Length() < 1e-4f);
        }
    }

    [SkippableFact]
    public void Hands_on_hips_pose_puts_hands_below_the_head()
    {
        // A named pose with a distinctive silhouette: hands-on-hips must put the finger bones
        // well below the head after world composition, with the torso upright.
        var clip = Decode("sk55_idle_sashaHandsHips.anm");
        var skl = LoadSkeleton("sk55_sasha.skl");
        var world = EnginePose(skl, clip, 0f);

        // In the T-pose rest, finger bones sit far out on the X axis; find them from the bind pose.
        var bind = skl.BindPoseWorld();
        var fingers = Enumerable.Range(0, bind.Length)
            .Where(i => Math.Abs(bind[i].Translation.X) > 0.45f).ToList();
        Assert.True(fingers.Count > 10, $"only {fingers.Count} arm-extremity bones");

        float posedFingerY = fingers.Average(i => world[i].Translation.Y);
        float posedHeadY = world.Select(m => m.Translation.Y).Max();
        Assert.True(posedFingerY < 1.25f, $"fingers at {posedFingerY}: arms not lowered");
        Assert.True(posedHeadY > posedFingerY + 0.1f,
            $"head {posedHeadY} not above fingers {posedFingerY}");
        Assert.True(TorsoTilt(skl, world) < 25f);
    }

    [SkippableFact]
    public void Mixed_clip_chains_deltas_into_its_absolute_keys()
    {
        // rhysWakeUpDragged: 194 samples, 86 of them absolute keys and 108 delta samples. The
        // samples come out in time order, and the body moves smoothly from sample to sample:
        // a misread delta chain would drift every bone and jump back at the next absolute key.
        // (Single bones can turn fast: the eyelids blink by ~37° in one frame.)
        var clip = Decode("sk54_action_rhysWakeUpDragged.anm");
        Assert.Equal(194, clip.SampleCount);
        for (int s = 1; s < clip.SampleCount; s++)
            Assert.True(clip.SampleTimes[s] > clip.SampleTimes[s - 1]);
        Assert.InRange(clip.SampleTimes[^1], 6.66f, 6.67f);
        var skeleton = LoadSkeleton("sk54_rhys.skl");
        var named = Enumerable.Range(0, clip.BoneSymbols.Count).Where(i => clip.BoneSymbols[i] != 0).ToList();
        var frames = named.ToDictionary(i => i, i => CspkDecoder.TranslationFrame(
            skeleton.Bones[skeleton.BoneIndexBySymbol[clip.BoneSymbols[i]]]));
        foreach (var samples in clip.Rotations)
            Assert.All(samples, q => Assert.InRange(q.Length(), 0.999f, 1.001f));
        for (int s = 1; s < clip.SampleCount; s++)
        {
            float mean = (float)named.Average(i => Degrees(clip.Rotations[i][s - 1], clip.Rotations[i][s]));
            Assert.True(mean < 2f, $"mean step of {mean}° into sample {s}");
            Assert.All(named, i => Assert.True(Degrees(clip.Rotations[i][s - 1], clip.Rotations[i][s]) < 45f));
            Assert.All(named, i => Assert.True((CspkDecoder.ToLocalTranslation(clip.Translations[i][s], frames[i])
                - CspkDecoder.ToLocalTranslation(clip.Translations[i][s - 1], frames[i])).Length() < 0.02f));
        }
    }

    [SkippableFact]
    public void Dragged_rhys_lies_on_the_ground()
    {
        var clip = Decode("sk54_action_rhysWakeUpDragged.anm");
        var skeleton = LoadSkeleton("sk54_rhys.skl");
        foreach (float time in new[] { 0f, 3f, 6.5f })
        {
            var world = EnginePose(skeleton, clip, time);
            Assert.InRange(world[1].Translation.Y, 0f, 0.3f);      // pelvis near the ground
            Assert.True(TorsoTilt(skeleton, world) > 60f);         // body close to horizontal
        }
    }

    [SkippableFact]
    public void Dying_sasha_lies_on_the_ground()
    {
        // Sasha 103 held by Fiona while dying: on the ground, not standing.
        var clip = Decode("sk55_sasha103_sk55_idle_fionaHoldsSashaDying.anm");
        var skeleton = LoadSkeleton("sk55_sasha.skl");
        foreach (float time in new[] { 0f, 1f, 2f })
        {
            var world = EnginePose(skeleton, clip, time);
            Assert.InRange(world[1].Translation.Y, 0f, 0.2f);
            Assert.True(TorsoTilt(skeleton, world) > 60f, $"torso {TorsoTilt(skeleton, world)}° from vertical");
        }
    }

    [SkippableFact]
    public void Squat_to_run_rises_and_bends_the_knees()
    {
        // banditSquat_toRun: every sample is an absolute key (V == S == 48).
        var clip = Decode("skB4_banditSquat_toRun.anm");
        var skeleton = LoadSkeleton("skB4_banditMarauder.skl");
        Assert.Equal(48, clip.SampleCount);
        Assert.Equal(65, clip.BoneSymbols.Count(skeleton.JointSymbols.Contains));

        // Ankles: low bones out to the side whose parent (the knee) is well above them.
        var bind = skeleton.BindPoseWorld();
        var ankles = Enumerable.Range(1, bind.Length - 1)
            .Where(i => bind[i].Translation.Y is > 0.04f and < 0.2f && Math.Abs(bind[i].Translation.X) > 0.04f
                        && bind[skeleton.Bones[i].ParentIndex].Translation.Y - bind[i].Translation.Y > 0.25f)
            .ToList();
        Assert.Equal(2, ankles.Count);
        float Flexion(Matrix4x4[] w, int ankle)
        {
            int knee = skeleton.Bones[ankle].ParentIndex, hip = skeleton.Bones[knee].ParentIndex;
            var a = Vector3.Normalize(w[hip].Translation - w[knee].Translation);
            var b = Vector3.Normalize(w[ankle].Translation - w[knee].Translation);
            return 180f - MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
        }

        var start = EnginePose(skeleton, clip, 0f);
        Assert.InRange(start[1].Translation.Y, 0.6f, 0.85f);                    // crouched
        Assert.All(ankles, a => Assert.True(Flexion(start, a) > 60f));           // knees bent
        var run = Enumerable.Range(0, 9).Select(k => EnginePose(skeleton, clip, 0.6f + k * 0.1f)).ToList();
        Assert.All(run, w => Assert.InRange(w[1].Translation.Y, 0.85f, 1.05f));  // up and running
        Assert.All(ankles, a => Assert.True(run.Max(w => Flexion(w, a)) - run.Min(w => Flexion(w, a)) > 40f));
        foreach (var samples in clip.Rotations)
            for (int s = 1; s < samples.Length; s++)
                Assert.True(Degrees(samples[s - 1], samples[s]) < 45f);
    }

    [SkippableFact]
    public void Additive_clip_starts_from_identity_and_stays_small()
    {
        // The channel's additive flag marks it; its samples start as deltas from identity and
        // zero (six of them before the clip's only absolute key) and stay small offsets.
        var clip = Decode("sk55_sashaHandsHips_bobLeft_add.anm");
        Assert.True(clip.IsAdditive);
        Assert.Equal(19, clip.SampleCount);
        foreach (var samples in clip.Rotations)
            Assert.All(samples, q => Assert.True(Degrees(q, Quaternion.Identity) < 10f));
        foreach (var samples in clip.Translations)
            Assert.All(samples, t => Assert.True(t.Length() < 0.05f));
    }

    [SkippableFact]
    public void Probe_reads_the_whole_slot_table()
    {
        // The phoneme's last slot names a face bone; the quick probe must see it.
        var path = SampleData.Sample("fiona_phoneme_default_happyC.anm");
        var lastSlot = Decode("fiona_phoneme_default_happyC.anm").BoneSymbols[^1];
        Assert.Equal(0x07befe89af190afeUL, lastSlot);
        Assert.True(AnimationFile.ProbeSkeletonMatch(path, new HashSet<ulong> { lastSlot })?.Matches);
        Assert.False(AnimationFile.ProbeSkeletonMatch(path, new HashSet<ulong> { 0x1234 })?.Matches);
    }

    [SkippableFact]
    public void Rejects_a_truncated_payload()
    {
        var blob = LoadAnim("sk55_idle_sashaHandsHips.anm").PoseKeysPayload!;
        Assert.Null(CspkDecoder.TryDecode(blob[..^4]));
        Assert.Null(CspkDecoder.TryDecode(blob[..100]));
    }
}
