using System.Buffers.Binary;
using System.Numerics;

namespace TelltaleModding.Core.Meshes;

/// <summary>
/// A decoded CompressedSkeletonPoseKeys clip: each slot's local rotation and translation at every
/// sample. Translations are in bone-length space (see <see cref="CspkDecoder.ToLocalTranslation"/>).
/// </summary>
public sealed class CspkClip
{
    /// <summary>Bone symbol per slot, 0 for a padding slot.</summary>
    public required IReadOnlyList<ulong> BoneSymbols { get; init; }

    /// <summary>Number of samples, absolute and delta together.</summary>
    public required int SampleCount { get; init; }

    /// <summary>Time of each sample in seconds, ascending.</summary>
    public required float[] SampleTimes { get; init; }

    /// <summary>[slot][sample] local rotations.</summary>
    public required Quaternion[][] Rotations { get; init; }

    /// <summary>[slot][sample] translations in bone-length space (see <see cref="CspkDecoder.ToLocalTranslation"/>).</summary>
    public required Vector3[][] Translations { get; init; }

    /// <summary>True when the clip layers onto the pose underneath instead of replacing it.</summary>
    public required bool IsAdditive { get; init; }

    /// <summary>Rotation at a time, blended the short way between the nearest samples.</summary>
    public Quaternion SampleRotation(int slot, float time)
    {
        var (lo, hi, t) = Span(time);
        var a = Rotations[slot][lo];
        var b = Rotations[slot][hi];
        if (Quaternion.Dot(a, b) < 0f)
            b = Quaternion.Negate(b);
        return Quaternion.Normalize(Quaternion.Lerp(a, b, t));
    }

    /// <summary>Translation at a time, blended between the nearest samples.</summary>
    public Vector3 SampleTranslation(int slot, float time)
    {
        var (lo, hi, t) = Span(time);
        return Vector3.Lerp(Translations[slot][lo], Translations[slot][hi], t);
    }

    /// <summary>The two samples around a time and the blend factor between them.</summary>
    private (int Lo, int Hi, float T) Span(float time)
    {
        if (SampleCount == 1 || time <= SampleTimes[0])
            return (0, 0, 0f);
        if (time >= SampleTimes[^1])
            return (SampleCount - 1, SampleCount - 1, 0f);
        int lo = 0, hi = SampleCount - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (time < SampleTimes[mid]) hi = mid; else lo = mid;
        }
        float span = SampleTimes[hi] - SampleTimes[lo];
        return (lo, hi, span <= 1e-6f ? 0f : (time - SampleTimes[lo]) / span);
    }
}

/// <summary>
/// Decoder for Telltale's CompressedSkeletonPoseKeys, matching the engine's decoder.
///
/// Blob layout (N slots, a multiple of four; S samples; V absolute keys; B blocks):
///   0..96    floats: min/range for translation delta (0-2/4-6), rotation delta (8-10/12-14)
///            and absolute translation (16-18/20-22)
///   96..112  i32 N, S, V, B
///   0x80     slot symbols (u64, 0 = padding)
///   then     V absolute keys of 4N words, S-V delta samples of 2N words,
///            S f32 sample times, B u32 block counts
///
/// Slots go in groups of four. An absolute key group is 16 words: translation lo, translation hi,
/// rotation lo, rotation hi (four each), each lo/hi pair packing 20/22/22-bit x/y/z. Rotation
/// x/y/z span -1..1 and w is rebuilt. A delta group is 8 words: four translation, four rotation,
/// each 10/11/11-bit. Deltas add to the previous translation and compose after the previous rotation.
///
/// Block count k is how many delta samples come before absolute key k; the last block counts
/// the deltas after the final key.
/// </summary>
public static class CspkDecoder
{
    /// <summary>Animation channel flag marking an additive value.</summary>
    public const uint AdditiveFlag = 0x10000;

    private const float AbsScaleX = 1f / ((1 << 20) - 1);
    private const float AbsScaleYz = 1f / ((1 << 22) - 1);

    /// <summary>
    /// Decodes a CompressedSkeletonPoseKeys payload, or returns null when it's malformed.
    /// <paramref name="additive"/> comes from the channel's <see cref="AdditiveFlag"/>.
    /// </summary>
    public static CspkClip? TryDecode(ReadOnlySpan<byte> blob, bool additive = false)
    {
        if (blob.Length < 0x80)
            return null;

        Span<float> w = stackalloc float[24];
        for (int i = 0; i < 24; i++)
            w[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * 4)..]);
        int slots = BinaryPrimitives.ReadInt32LittleEndian(blob[96..]);
        int samples = BinaryPrimitives.ReadInt32LittleEndian(blob[100..]);
        int keys = BinaryPrimitives.ReadInt32LittleEndian(blob[104..]);
        int blocks = BinaryPrimitives.ReadInt32LittleEndian(blob[108..]);
        if (slots <= 0 || slots > 4096 || slots % 4 != 0 || samples <= 0 || samples > 1_000_000
            || keys < 0 || keys > samples || blocks <= 0 || blocks > samples + 1)
            return null;

        long absStart = 0x80 + 8L * slots;
        long deltaStart = absStart + 16L * slots * keys;
        long timeStart = deltaStart + 8L * slots * (samples - keys);
        long blockStart = timeStart + 4L * samples;
        if (blockStart + 4L * blocks != blob.Length)
            return null;

        var symbols = new ulong[slots];
        for (int s = 0; s < slots; s++)
            symbols[s] = BinaryPrimitives.ReadUInt64LittleEndian(blob[(0x80 + 8 * s)..]);

        var times = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            times[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(int)(timeStart + 4 * i)..]);
            if (!float.IsFinite(times[i]) || (i > 0 && times[i] < times[i - 1]))
                return null;
        }

        var blockCounts = new int[blocks];
        long deltaTotal = 0;
        for (int b = 0; b < blocks; b++)
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(blob[(int)(blockStart + 4 * b)..]);
            if (count > samples)
                return null;
            blockCounts[b] = (int)count;
            deltaTotal += count;
        }
        if (deltaTotal != samples - keys)
            return null;

        var absMin = new Vector3(w[16], w[17], w[18]);
        var absScale = new Vector3(w[20] * AbsScaleX, w[21] * AbsScaleYz, w[22] * AbsScaleYz);
        var dtMin = new Vector3(w[0], w[1], w[2]);
        var dtScale = new Vector3(w[4] / 1023f, w[5] / 2047f, w[6] / 2047f);
        var drMin = new Vector3(w[8], w[9], w[10]);
        var drScale = new Vector3(w[12] / 1023f, w[13] / 2047f, w[14] / 2047f);

        var rotations = new Quaternion[slots][];
        var translations = new Vector3[slots][];
        for (int s = 0; s < slots; s++)
        {
            rotations[s] = new Quaternion[samples];
            translations[s] = new Vector3[samples];
        }

        var curT = new Vector3[slots];
        var curR = new Quaternion[slots];
        Array.Fill(curR, Quaternion.Identity);
        int remaining = blockCounts[0], block = 0, key = 0, delta = 0;
        for (int i = 0; i < samples; i++)
        {
            if (remaining == 0)
            {
                // Absolute key: 16 words per group of four slots.
                if (key >= keys)
                    return null;
                var rec = blob[(int)(absStart + 16L * slots * key)..];
                for (int s = 0; s < slots; s++)
                {
                    int g = s / 4 * 16, lane = s % 4;
                    var (tx, ty, tz) = Combine(Word(rec, g + lane), Word(rec, g + 4 + lane));
                    curT[s] = absMin + new Vector3(tx, ty, tz) * absScale;
                    var (rx, ry, rz) = Combine(Word(rec, g + 8 + lane), Word(rec, g + 12 + lane));
                    curR[s] = WithW(rx * (2f * AbsScaleX) - 1f, ry * (2f * AbsScaleYz) - 1f, rz * (2f * AbsScaleYz) - 1f);
                }
                key++;
                block++;
                if (block < blocks)
                    remaining = blockCounts[block];
            }
            else
            {
                // Delta sample: 8 words per group of four slots.
                var rec = blob[(int)(deltaStart + 8L * slots * delta)..];
                for (int s = 0; s < slots; s++)
                {
                    int g = s / 4 * 8, lane = s % 4;
                    uint t = Word(rec, g + lane);
                    curT[s] += dtMin + new Vector3(t & 0x3FF, (t >> 10) & 0x7FF, t >> 21) * dtScale;
                    uint r = Word(rec, g + 4 + lane);
                    var d = drMin + new Vector3(r & 0x3FF, (r >> 10) & 0x7FF, r >> 21) * drScale;
                    curR[s] = Quaternion.Multiply(curR[s], WithW(d.X, d.Y, d.Z));
                }
                delta++;
                remaining--;
            }
            for (int s = 0; s < slots; s++)
            {
                translations[s][i] = curT[s];
                rotations[s][i] = curR[s];
            }
        }

        return new CspkClip
        {
            BoneSymbols = symbols,
            SampleCount = samples,
            SampleTimes = times,
            Rotations = rotations,
            Translations = translations,
            IsAdditive = additive,
        };
    }

    /// <summary>
    /// How the engine maps a bone's stored translation to its local offset: the bone's length and
    /// a correction rotation. The correction turns the rest direction divided by
    /// <see cref="SkeletonBone.AnimTranslationScale"/> onto this skeleton's rest direction.
    /// </summary>
    public static (Quaternion Correction, float Length) TranslationFrame(SkeletonBone bone)
    {
        var offset = bone.LocalPosition;
        float length = offset.Length();
        var dir = Normalized(offset);
        var scale = bone.AnimTranslationScale;
        var animDir = scale.X != 0f && scale.Y != 0f && scale.Z != 0f ? Normalized(dir / scale) : dir;

        var correction = Quaternion.Identity;
        float dot = Vector3.Dot(animDir, dir);
        if (dot < 1f)
        {
            float half = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 0.5f;
            var axis = Normalized(Vector3.Cross(animDir, dir)) * MathF.Sin(half);
            correction = Quaternion.Normalize(new Quaternion(axis, MathF.Cos(half)));
        }
        return length > 1e-6f ? (correction, length) : (Quaternion.Identity, 1f);
    }

    /// <summary>Turns a stored translation into the bone's local offset.</summary>
    public static Vector3 ToLocalTranslation(Vector3 stored, (Quaternion Correction, float Length) frame) =>
        Vector3.Transform(stored * frame.Length, frame.Correction);

    /// <summary>The inverse of <see cref="ToLocalTranslation"/>.</summary>
    public static Vector3 FromLocalTranslation(Vector3 local, (Quaternion Correction, float Length) frame) =>
        Vector3.Transform(local, Quaternion.Conjugate(frame.Correction)) / frame.Length;

    private static uint Word(ReadOnlySpan<byte> record, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(record[(index * 4)..]);

    /// <summary>Combines a lo/hi word pair into 20/22/22-bit x/y/z, low bits in lo.</summary>
    private static (uint X, uint Y, uint Z) Combine(uint lo, uint hi) =>
        (((hi & 0x3FF) << 10) | (lo & 0x3FF),
         (((hi >> 10) & 0x7FF) << 11) | ((lo >> 10) & 0x7FF),
         ((hi >> 21) << 11) | (lo >> 21));

    private static Quaternion WithW(float x, float y, float z) =>
        new(x, y, z, MathF.Sqrt(MathF.Max(1f - x * x - y * y - z * z, 0f)));

    private static Vector3 Normalized(Vector3 v)
    {
        float lengthSquared = v.LengthSquared();
        return lengthSquared < 1e-20f ? v : v / MathF.Sqrt(lengthSquared);
    }
}
