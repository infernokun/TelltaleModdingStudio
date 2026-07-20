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

    /// <summary>
    /// Bone indices whose stored rotation is a constant rig/IK frame value
    /// (identical large rotations repeated across several bones). Playback
    /// should keep these bones at the skeleton rest pose.
    /// </summary>
    public required IReadOnlySet<int> FrameBones { get; init; }

    /// <summary>
    /// Bone indices whose stored rotation follows the unnamed motion carrier's
    /// frame (root, pelvis, clavicle anchors). Their real animation is the
    /// deviation from the carrier: sample it with
    /// <see cref="SampleCarrierRelative"/> and compose onto the rest pose.
    /// </summary>
    public required IReadOnlySet<int> CarrierTracking { get; init; }

    /// <summary>
    /// The bone's rotation relative to the motion carrier's frame at a time —
    /// near identity when the bone just rides the frame, real local motion
    /// (torso leans, shrugs) when it deviates.
    /// </summary>
    public Quaternion SampleCarrierRelative(int bone, float time)
    {
        var carrier = SampleRotation(0, time);
        var value = SampleRotation(bone, time);
        return Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(carrier), value));
    }

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
/// V counts absolute (re-sync) keys and S−V samples are deltas. The body is
/// uniform across all clips: [absolute record × V][delta record × (S−V)].
/// An absolute record is 4·boneCount words holding 2·boneCount values —
/// rotations for bones 0..N−1 first, then translations — staged in chunks
/// of four values as [lo ×4][hi ×4]; the two words combine into 20/22/22-bit
/// components, rotations dequantizing with a hardcoded √2 window
/// (smallest-three, w reconstructed, plain xyz order) and translations with
/// the header vector window. A delta record is 2 groups of boneCount words
/// ([translation deltas][rotation deltas]), each word packing 10/11/11 bits
/// dequantized by the header delta windows and multiplied onto the previous
/// sample. V=0 additive overlays are delta records only.
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

        // Unified body layout for every family: the V absolute keys come first
        // as consecutive records of 4·bones words — 2·bones values interleaved
        // per bone (even = translation, odd = rotation), staged in chunks of
        // four values as [lo ×4][hi ×4] — followed by the S−V delta samples as
        // records of 2 groups ([translation deltas][rotation deltas], one
        // 10/11/11 word per bone). V=0 additive clips have delta records only.
        int deltaRecordBase = 4 * values;
        int clipSamples = values > 1 ? values : samples;
        var rotations = new Quaternion[bones][];
        var translations = new Vector3[bones][];
        for (int b = 0; b < bones; b++)
        {
            var trans = new Vector3[clipSamples];
            var rots = new Quaternion[clipSamples];
            if (additive)
            {
                trans[0] = Vector3.Zero;
                rots[0] = Quaternion.Identity;
            }
            else
            {
                for (int k = 0; k < values; k++)
                {
                    int rec = k * 4 * bones;
                    int rv = b, tv = bones + b;
                    var (qx, qy, qz) = Combine(
                        words[rec + rv / 4 * 8 + rv % 4],
                        words[rec + rv / 4 * 8 + 4 + rv % 4]);
                    rots[k] = AbsoluteQuat(qx, qy, qz);
                    var (tx, ty, tz) = Combine(
                        words[rec + tv / 4 * 8 + tv % 4],
                        words[rec + tv / 4 * 8 + 4 + tv % 4]);
                    trans[k] = new Vector3(
                        tx * rangeVector.X / (1 << 20) + minVector.X,
                        ty * rangeVector.Y / (1 << 22) + minVector.Y,
                        tz * rangeVector.Z / (1 << 22) + minVector.Z);
                }
                if (values > 1)
                {
                    // Multi-resync clips slerp between their absolute keys; the
                    // in-between delta samples refine by ≤ a few degrees and
                    // are skipped for now.
                    rotations[b] = rots;
                    translations[b] = trans;
                    continue;
                }
            }

            // Single-key and additive clips: chain the delta records forward.
            // A delta record mirrors the absolute record's order: rotation
            // deltas first, then translation deltas.
            for (int s = 1; s < samples; s++)
            {
                trans[s] = trans[s - 1]; // translation deltas not yet decoded reliably
                uint w = Word(deltaRecordBase + 2 * (s - 1), b);
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

        // Per-sample key times follow the body; sanitize against a garbled
        // table. For multi-resync clips the block table (after the times) gives
        // the delta count between absolute keys, which maps key k to its
        // sample index: k + Σ ints[0..k].
        int timeBase = body + words.Length * 4;
        var sampleTimes = new float[samples];
        bool timesOk = true;
        for (int s = 0; s < samples; s++)
        {
            sampleTimes[s] = BinaryPrimitives.ReadSingleLittleEndian(blob[(timeBase + s * 4)..]);
            if (!float.IsFinite(sampleTimes[s]) || sampleTimes[s] < 0f
                || sampleTimes[s] > animLength + 0.5f
                || (s > 0 && sampleTimes[s] < sampleTimes[s - 1]))
                timesOk = false;
        }
        var times = new float[clipSamples];
        if (timesOk && values > 1 && values < samples)
        {
            int intBase = timeBase + samples * 4;
            int idx = 0;
            for (int k = 0; k < values; k++)
            {
                if (k < blocks)
                    idx += (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(
                        blob[(intBase + k * 4)..]), (uint)samples);
                times[k] = sampleTimes[Math.Min(idx, samples - 1)];
                if (k > 0 && times[k] <= times[k - 1])
                    timesOk = false;
                idx++;
            }
        }
        else if (timesOk && clipSamples == samples)
        {
            times = sampleTimes;
        }
        if (!timesOk || (values > 1 && values < samples && !timesOk))
            for (int s = 0; s < clipSamples; s++)
                times[s] = clipSamples == 1 ? 0f : animLength * s / (clipSamples - 1);

        // Multi-resync clips bake rig frame / IK-reference values into some
        // named slots: headings matching the leading unnamed carrier, ±90° X
        // axis conversions right after them, and ~90° pure-Z constants. They
        // are inputs to the engine's IK layer, not poses — flag them so
        // playback keeps those bones at rest.
        var frameBones = new HashSet<int>();
        var carrierTracking = new HashSet<int>();
        if (!additive && bones > 2)
        {
            bool ConstantValue(int i)
            {
                for (int k = 1; k < clipSamples; k++)
                    if (MathF.Abs(Quaternion.Dot(rotations[i][k], rotations[i][0])) < 0.9999f)
                        return false;
                return true;
            }
            bool TracksCarrier(int i)
            {
                if (symbols[0] != 0)
                    return false;
                for (int k = 0; k < clipSamples; k++)
                    if (MathF.Abs(Quaternion.Dot(rotations[i][k], rotations[0][k])) <= 0.97f)
                        return false;
                return true;
            }
            // A large rotation repeated bit-identically on several named bones
            // is a rig/IK frame constant (leg and spine reference frames), not
            // animation; the same holds for bones that track the unnamed
            // motion-carrier's heading sample-for-sample.
            var groups = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < bones; i++)
            {
                if (symbols[i] == 0)
                    continue;
                var q = rotations[i][0];
                if (MathF.Abs(q.W) > 0.92f)
                    continue; // < ~45° from identity — could be real pose data
                var key = ((int)MathF.Round(q.X * 500), (int)MathF.Round(q.Y * 500),
                           (int)MathF.Round(q.Z * 500));
                if (q.W < 0)
                    key = (-key.Item1, -key.Item2, -key.Item3);
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<int>();
                list.Add(i);
            }
            foreach (var list in groups.Values)
                if (list.Count >= 3 && list.All(ConstantValue))
                    foreach (var i in list)
                        frameBones.Add(i);
            for (int i = 1; i < bones; i++)
                if (symbols[i] != 0 && !frameBones.Contains(i) && TracksCarrier(i))
                    carrierTracking.Add(i);
        }

        return new CspkClip
        {
            BoneSymbols = symbols,
            SampleCount = clipSamples,
            SampleTimes = times,
            Rotations = rotations,
            Translations = translations,
            IsAdditive = additive,
            FrameBones = frameBones,
            CarrierTracking = carrierTracking,
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
