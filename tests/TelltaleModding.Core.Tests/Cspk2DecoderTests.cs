using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// A fact that runs only when the given sample files are in <c>samples/</c>. To run these tests,
/// extract <c>sk62_idle_clementineHips.anm</c> (WDC_pc_WalkingDead301_anichore.ttarch2) and
/// <c>sk62_clementine.skl</c> (WDC_pc_WalkingDead301_data.ttarch2) from the Definitive Series.
/// </summary>
public sealed class SampleFactAttribute : FactAttribute
{
    public SampleFactAttribute(params string[] files)
    {
        var missing = files.Where(f => !File.Exists(Path.Combine(SampleData.SamplesDir, f))).ToList();
        if (missing.Count > 0)
            Skip = $"Sample file(s) not present: {string.Join(", ", missing)}";
    }
}

public class Cspk2DecoderTests
{
    private const string Clip = "sk62_idle_clementineHips.anm";
    private const string Rig = "sk62_clementine.skl";

    private static (PoseKeys2Clip Clip, AnimationFile Anim) Decode(string name)
    {
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var anim, out var error), error);
        Assert.NotNull(anim!.PoseKeys2Payload);
        var clip = Cspk2Decoder.TryDecode(anim.PoseKeys2Payload);
        Assert.NotNull(clip);
        return (clip!, anim);
    }

    [SampleFact(Clip)]
    public void Keys_are_time_ordered_and_span_the_clip()
    {
        var (clip, anim) = Decode(Clip);
        Assert.Equal(142, clip.BoneSymbols.Count);
        float last = 0f;
        for (int s = 0; s < clip.BoneSymbols.Count; s++)
        {
            foreach (var keys in new[] { clip.Rotations[s].Select(k => k.Time).ToArray(),
                                         clip.Translations[s].Select(k => k.Time).ToArray() })
            {
                for (int i = 1; i < keys.Length; i++)
                    Assert.True(keys[i] >= keys[i - 1]);
                if (keys.Length > 0)
                    last = Math.Max(last, keys[^1]);
            }
            foreach (var key in clip.Rotations[s])
                Assert.InRange(key.Value.Length(), 0.999f, 1.001f);
        }
        // The last key lands on the end of the animation.
        Assert.InRange(last, anim.Length - 0.05f, anim.Length + 0.05f);
    }

    [SampleFact(Clip, Rig)]
    public void Translations_follow_the_bones_rest_offsets()
    {
        // Translations are stored relative to the rest length, so an idle gives about 1.
        var (clip, _) = Decode(Clip);
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(Rig)), out var skl, out _));
        var rest = skl!.Bones.GroupBy(b => b.JointSymbol).ToDictionary(g => g.Key, g => g.First().LocalPosition);

        int compared = 0, aligned = 0;
        for (int s = 0; s < clip.BoneSymbols.Count; s++)
        {
            if (!clip.HasTranslation(s) || !rest.TryGetValue(clip.BoneSymbols[s], out var offset)
                || offset.Length() < 1e-4f)
                continue;
            compared++;
            var value = clip.SampleTranslation(s, 0f);
            float cos = Vector3.Dot(Vector3.Normalize(value), Vector3.Normalize(offset));
            if (cos > MathF.Cos(5f * MathF.PI / 180f) && value.Length() is > 0.8f and < 1.3f)
                aligned++;
        }
        Assert.True(compared > 100, $"only {compared} bones compared");
        Assert.True(aligned > compared * 0.9, $"{aligned}/{compared} translations along their rest offset");
    }

    [SampleFact(Clip, Rig)]
    public void Idle_rotations_stay_near_the_rest_pose()
    {
        // In an idle most joints stay close to the rest pose.
        var (clip, _) = Decode(Clip);
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(Rig)), out var skl, out _));
        var rest = skl!.Bones.GroupBy(b => b.JointSymbol).ToDictionary(g => g.Key, g => g.First().LocalRotation);

        var angles = new List<float>();
        for (int s = 0; s < clip.BoneSymbols.Count; s++)
        {
            if (!clip.HasRotation(s) || !rest.TryGetValue(clip.BoneSymbols[s], out var q))
                continue;
            float dot = MathF.Abs(Quaternion.Dot(clip.SampleRotation(s, 0f), q));
            angles.Add(2f * MathF.Acos(MathF.Min(dot, 1f)) * 180f / MathF.PI);
        }
        angles.Sort();
        Assert.True(angles.Count > 100);
        Assert.True(angles[angles.Count / 2] < 20f, $"median {angles[angles.Count / 2]}° from rest");
    }

    [SampleFact(Clip, Rig)]
    public void Probe_finds_the_rig_without_a_full_load()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(Rig)), out var skl, out _));
        var probe = AnimationFile.ProbeSkeletonMatch(SampleData.Sample(Clip), skl!.JointSymbols);
        Assert.NotNull(probe);
        Assert.True(probe!.Value.Matches);
    }

    [SkippableFact]
    public void Rejects_a_truncated_blob()
    {
        Assert.Null(Cspk2Decoder.TryDecode(new byte[40]));
        var blob = new byte[0x60 + 8];
        BitConverter.TryWriteBytes(blob.AsSpan(76), (ushort)4);
        BitConverter.TryWriteBytes(blob.AsSpan(80), (ulong)0x1000); // symbol table past the end
        Assert.Null(Cspk2Decoder.TryDecode(blob));
    }

    [SampleFact(Clip, Rig)]
    public void Translations_lie_along_the_bones_animation_space_direction()
    {
        // Only rigid bones with a noticeable retarget rotation are checked.
        var (clip, _) = Decode(Clip);
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample(Rig)), out var skl, out _));
        var bones = skl!.Bones.GroupBy(b => b.JointSymbol).ToDictionary(g => g.Key, g => g.First());

        int compared = 0;
        for (int s = 0; s < clip.BoneSymbols.Count; s++)
        {
            if (!clip.HasTranslation(s) || !bones.TryGetValue(clip.BoneSymbols[s], out var bone))
                continue;
            var (correction, _) = CspkDecoder.TranslationFrame(bone);
            if (2f * MathF.Acos(MathF.Min(1f, MathF.Abs(correction.W))) < 3f * MathF.PI / 180f)
                continue;
            if (clip.Translations[s].Any(k => MathF.Abs(k.Value.Length() - 1f) > 1e-3f))
                continue;
            var restDir = Vector3.Normalize(bone.LocalPosition);
            var animDir = Vector3.Transform(restDir, Quaternion.Conjugate(correction));
            var value = Vector3.Normalize(clip.SampleTranslation(s, 0f));
            compared++;
            Assert.True(Vector3.Dot(value, animDir) > MathF.Cos(0.5f * MathF.PI / 180f), $"bone {bone.JointSymbol:X16} is off its animation-space direction");
            Assert.True(Vector3.Dot(value, animDir) > Vector3.Dot(value, restDir), $"bone {bone.JointSymbol:X16} lies along its rest offset");
        }
        Assert.True(compared >= 3, $"only {compared} rigid bones with a retarget rotation");
    }
}
