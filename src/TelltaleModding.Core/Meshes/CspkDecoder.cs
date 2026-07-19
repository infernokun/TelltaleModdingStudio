using System.Buffers.Binary;
using System.Numerics;

namespace TelltaleModding.Core.Meshes;

/// <summary>
/// A decoded CompressedSkeletonPoseKeys clip: per-bone rotation (and experimental
/// translation) samples at uniform times across the animation.
/// </summary>
public sealed class CspkClip
{
    public required IReadOnlyList<ulong> BoneSymbols { get; init; }
    public required int SampleCount { get; init; }
    public required float[] SampleTimes { get; init; }

    /// <summary>[bone][sample] absolute local rotations.</summary>
    public required Quaternion[][] Rotations { get; init; }

    /// <summary>
    /// [bone][sample] decoded translation stream. Semantics are not fully
    /// understood yet (values look like normalized bone-offset directions);
    /// playback should prefer skeleton rest translations.
    /// </summary>
    public required Vector3[][] Translations { get; init; }

    /// <summary>
    /// True for delta-only overlay clips (V=0, "_add" animations): rotations
    /// are relative and should compose onto the rest pose, not replace it.
    /// </summary>
    public required bool IsAdditive { get; init; }

    /// <summary>Sampled rotation for a bone index with slerp between samples.</summary>
    public Quaternion SampleRotation(int bone, float time)
    {
        var samples = Rotations[bone];
        if (samples.Length == 1 || time <= SampleTimes[0])
            return samples[0];
        if (time >= SampleTimes[^1])
            return samples[^1];
        int hi = 1;
        while (SampleTimes[hi] < time)
            hi++;
        float span = SampleTimes[hi] - SampleTimes[hi - 1];
        float t = span <= 1e-6f ? 0f : (time - SampleTimes[hi - 1]) / span;
        return Quaternion.Slerp(samples[hi - 1], samples[hi], t);
    }
}

/// <summary>
/// Decoder for the fixed-rate variant of Telltale's CompressedSkeletonPoseKeys
/// bit-stream (TFTB era, "CSPK1"). Reverse-engineered against the game corpus —
/// there is no public decoder for this version.
///
/// Blob layout (S = sampleCount, V = valueCount, B = blockCount; the payload
/// after the symbol table is exactly 4 + bones·2·(S+V) + S + B u32s, verified
/// against all 10,213 skeletal animations of a full game):
///   0..96    six Vector4 quantization windows (minDeltaV, rangeDeltaV,
///            minDeltaQ, rangeDeltaQ, minVector, rangeVector)
///   96..112  i32 boneCount, sampleCount, valueCount, blockCount
///   112..    bone symbol table (boneCount × u64 CRC64s; zero = unnamed bone)
///   then     [4 head u32s][body][f32 sampleTime × S][u32 blockDeltaCount × B]
///
/// V counts absolute (re-sync) keys per bone stream; the other S−V samples are
/// deltas. For V ≤ 1 the body is two contiguous planar streams (translations
/// first) of boneCount-wide groups: [absolute lo][absolute hi][delta × (S−1)]
/// (V=0 additive clips drop the absolutes). Absolutes combine the two words
/// into 20/22/22-bit components; rotation components dequantize with a
/// hardcoded √2 window (smallest-three, w reconstructed, plain xyz order).
/// Deltas pack 10/11/11 bits dequantized by the header delta windows and
/// multiply onto the previous sample. The interleaving for V > 1 (multiple
/// re-sync keys) is not solved yet — those return null.
/// </summary>
public static class CspkDecoder
{
    private const float Sqrt2 = 1.41421356f;
    private const float InvSqrt2 = 0.70710678f;

    /// <summary>
    /// Decodes a single-resync (V ≤ 1) pose-keys payload, or returns null when
    /// the blob uses multiple re-sync keys (V > 1, not solved yet) or is malformed.
    /// </summary>
    public static CspkClip? TryDecodeFixedRate(ReadOnlySpan<byte> blob, float animLength)
    {
        if (blob.Length < 128)
            return null;

        Span<float> window = stackalloc float[24];
        for (int i = 0; i < 24; i++)
            window[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * 4)..]);
        int bones = BinaryPrimitives.ReadInt32LittleEndian(blob[96..]);
        int samples = BinaryPrimitives.ReadInt32LittleEndian(blob[100..]);
        int values = BinaryPrimitives.ReadInt32LittleEndian(blob[104..]);
        int blocks = BinaryPrimitives.ReadInt32LittleEndian(blob[108..]);
        if (bones <= 0 || bones > 4096 || samples <= 0 || blocks <= 0)
            return null;

        int tableEnd = 112 + bones * 8;
        if (blob.Length < tableEnd)
            return null;
        int payloadWords = (blob.Length - tableEnd) / 4;
        if (payloadWords != 4 + bones * 2 * (samples + values) + samples + blocks)
            return null; // not a CSPK1 payload we understand
        if (values > 1)
            return null; // multi-resync interleaving not solved yet
        bool additive = values == 0;

        var symbols = new ulong[bones];
        for (int b = 0; b < bones; b++)
            symbols[b] = BinaryPrimitives.ReadUInt64LittleEndian(blob[(112 + b * 8)..]);

        int body = tableEnd + 4 * 4;
        int groupsPerStream = samples + values;
        var words = new uint[bones * 2 * groupsPerStream];
        for (int i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(blob[(body + i * 4)..]);
        uint Word(int group, int bone) => words[group * bones + bone];

        var minDeltaQ = new Vector3(window[8], window[9], window[10]);
        var rangeDeltaQ = new Vector3(window[12], window[13], window[14]);
        var minVector = new Vector3(window[16], window[17], window[18]);
        var rangeVector = new Vector3(window[20], window[21], window[22]);

        int rotBase = groupsPerStream; // translations occupy the first stream
        int deltaBase = additive ? 0 : 2; // V=1: [abs lo][abs hi][deltas…]
        var rotations = new Quaternion[bones][];
        var translations = new Vector3[bones][];
        for (int b = 0; b < bones; b++)
        {
            var trans = new Vector3[samples];
            var rots = new Quaternion[samples];
            if (additive)
            {
                trans[0] = Vector3.Zero;
                rots[0] = Quaternion.Identity;
            }
            else
            {
                var (tx, ty, tz) = Combine(Word(0, b), Word(1, b));
                trans[0] = new Vector3(
                    tx * rangeVector.X / (1 << 20) + minVector.X,
                    ty * rangeVector.Y / (1 << 22) + minVector.Y,
                    tz * rangeVector.Z / (1 << 22) + minVector.Z);
                var (qx, qy, qz) = Combine(Word(rotBase, b), Word(rotBase + 1, b));
                rots[0] = AbsoluteQuat(qx, qy, qz);
            }

            for (int s = 1; s < samples; s++)
            {
                trans[s] = trans[s - 1]; // translation deltas not yet decoded reliably
                uint w = Word(rotBase + deltaBase + s - 1, b);
                float dx = (w & 0x3FF) / 1023f * rangeDeltaQ.X + minDeltaQ.X;
                float dy = ((w >> 10) & 0x7FF) / 2047f * rangeDeltaQ.Y + minDeltaQ.Y;
                float dz = (w >> 21) / 2047f * rangeDeltaQ.Z + minDeltaQ.Z;
                float dw = MathF.Sqrt(MathF.Max(1f - dx * dx - dy * dy - dz * dz, 0f));
                rots[s] = Quaternion.Normalize(
                    Quaternion.Multiply(rots[s - 1], new Quaternion(dx, dy, dz, dw)));
            }
            rotations[b] = rots;
            translations[b] = trans;
        }

        // Per-sample key times follow the body; sanitize against a garbled table.
        int timeBase = body + words.Length * 4;
        var times = new float[samples];
        bool timesOk = true;
        for (int s = 0; s < samples; s++)
        {
            times[s] = BinaryPrimitives.ReadSingleLittleEndian(blob[(timeBase + s * 4)..]);
            if (!float.IsFinite(times[s]) || times[s] < 0f || times[s] > animLength + 0.5f
                || (s > 0 && times[s] < times[s - 1]))
                timesOk = false;
        }
        if (!timesOk)
            for (int s = 0; s < samples; s++)
                times[s] = samples == 1 ? 0f : animLength * s / (samples - 1);

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

    /// <summary>Combines the absolute lo/hi words into 20/22/22-bit components.</summary>
    private static (uint X, uint Y, uint Z) Combine(uint lo, uint hi) =>
        (((hi & 0x3FF) << 10) | (lo & 0x3FF),
         (((hi >> 10) & 0x7FF) << 11) | ((lo >> 10) & 0x7FF),
         ((hi >> 21) << 11) | (lo >> 21));

    private static Quaternion AbsoluteQuat(uint qx, uint qy, uint qz)
    {
        float x = qx * Sqrt2 / (1 << 20) - InvSqrt2;
        float y = qy * Sqrt2 / (1 << 22) - InvSqrt2;
        float z = qz * Sqrt2 / (1 << 22) - InvSqrt2;
        float w = MathF.Sqrt(MathF.Max(1f - x * x - y * y - z * z, 0f));
        return new Quaternion(x, y, z, w);
    }
}
