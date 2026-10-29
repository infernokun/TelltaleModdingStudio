using System.Buffers.Binary;
using System.Numerics;

namespace TelltaleModding.Core.Meshes;

/// <summary>One key of a <see cref="PoseKeys2Clip"/> channel.</summary>
public readonly record struct PoseKey<T>(float Time, T Value);

/// <summary>
/// A decoded CompressedSkeletonPoseKeys2 clip (The Walking Dead: The Telltale Definitive Series and
/// later games). Every slot has its own rotation and translation keys at their own times; values
/// between keys blend linearly.
/// </summary>
public sealed class PoseKeys2Clip
{
    /// <summary>Bone symbol per slot (CRC64 of the joint name).</summary>
    public required IReadOnlyList<ulong> BoneSymbols { get; init; }

    /// <summary>[slot] rotation keys, time-ordered; empty when the slot has none.</summary>
    public required PoseKey<Quaternion>[][] Rotations { get; init; }

    /// <summary>[slot] translation keys (local offset from the parent), time-ordered.</summary>
    public required PoseKey<Vector3>[][] Translations { get; init; }

    public bool HasRotation(int slot) => Rotations[slot].Length > 0;
    public bool HasTranslation(int slot) => Translations[slot].Length > 0;

    /// <summary>Rotation at a time: normalized blend of the neighbouring keys, the short way round.</summary>
    public Quaternion SampleRotation(int slot, float time)
    {
        var keys = Rotations[slot];
        if (keys.Length == 0)
            return Quaternion.Identity;
        var (lo, hi, t) = Span(keys, time);
        var a = keys[lo].Value;
        var b = keys[hi].Value;
        if (Quaternion.Dot(a, b) < 0f)
            b = Quaternion.Negate(b);
        return Quaternion.Normalize(Quaternion.Lerp(a, b, t));
    }

    /// <summary>Translation at a time: linear blend of the neighbouring keys.</summary>
    public Vector3 SampleTranslation(int slot, float time)
    {
        var keys = Translations[slot];
        if (keys.Length == 0)
            return Vector3.Zero;
        var (lo, hi, t) = Span(keys, time);
        return Vector3.Lerp(keys[lo].Value, keys[hi].Value, t);
    }

    private static (int Lo, int Hi, float T) Span<T>(PoseKey<T>[] keys, float time)
    {
        if (keys.Length == 1 || time <= keys[0].Time)
            return (0, 0, 0f);
        if (time >= keys[^1].Time)
            return (keys.Length - 1, keys.Length - 1, 0f);
        int lo = 0, hi = keys.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].Time <= time) lo = mid; else hi = mid;
        }
        float span = keys[hi].Time - keys[lo].Time;
        return (lo, hi, span <= 1e-6f ? 0f : (time - keys[lo].Time) / span);
    }
}

/// <summary>
/// Decoder for CompressedSkeletonPoseKeys2, following what the game engine's own decoder does.
///
/// Blob layout:
///   0..72    float windows: translation delta min (0-2) / range (3-5), rotation delta min (6-8) /
///            range (9-11), absolute translation min (12-14) / range (15-17); float 18 scales key times
///   76       u16 slot count
///   80       u64 offset of the slot symbol table, counted from 0x60
///   0x60..   packed values (read on demand, see below)
///   then     slot symbols (u64 each), then control words (u32) to the end of the blob
///
/// Each control word is one key: bits 0-15 its time (× float 18 / 65535), bits 16-27 the slot,
/// bit 30 rotation (else translation), bit 31 delta (else absolute), and for absolute rotations
/// bits 28-29 the component order. Values come from the shared packed stream, four at a time per
/// kind: a translation or rotation delta takes one word per four values (10/11/11 bits), an
/// absolute translation or rotation two words per value as four lo words then four hi words
/// (20/22/22 bits). Delta translations add to the slot's previous key, delta rotations multiply
/// onto it (previous · delta). Absolute rotations store three components in a ±1/√2 window and
/// rebuild the fourth; with order k they land in x, y, z, w as components k, k^1, k^2, k^3.
/// A slot starts at zero translation and identity rotation.
/// </summary>
public static class Cspk2Decoder
{
    private const int ValueBase = 0x60;

    public static PoseKeys2Clip? TryDecode(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < ValueBase)
            return null;
        Span<float> f = stackalloc float[19];
        for (int i = 0; i < 19; i++)
            f[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(i * 4)..]);
        int slots = BinaryPrimitives.ReadUInt16LittleEndian(blob[76..]);
        ulong symbolOffset = BinaryPrimitives.ReadUInt64LittleEndian(blob[80..]);
        if (slots == 0 || symbolOffset > (ulong)blob.Length)
            return null;
        int symbolBase = ValueBase + (int)symbolOffset;
        int controlBase = symbolBase + slots * 8;
        if (controlBase > blob.Length || (blob.Length - controlBase) % 4 != 0)
            return null;

        var symbols = new ulong[slots];
        for (int s = 0; s < slots; s++)
            symbols[s] = BinaryPrimitives.ReadUInt64LittleEndian(blob[(symbolBase + s * 8)..]);

        var stream = new ValueStream(blob[..symbolBase].ToArray(), f);
        var rotations = new List<PoseKey<Quaternion>>[slots];
        var translations = new List<PoseKey<Vector3>>[slots];
        for (int s = 0; s < slots; s++)
        {
            rotations[s] = new List<PoseKey<Quaternion>>();
            translations[s] = new List<PoseKey<Vector3>>();
        }

        float timeScale = f[18] / 65535f;
        for (int p = controlBase; p < blob.Length; p += 4)
        {
            uint w = BinaryPrimitives.ReadUInt32LittleEndian(blob[p..]);
            float time = (w & 0xFFFF) * timeScale;
            int slot = (int)((w >> 16) & 0xFFF);
            if (slot >= slots)
                return null;
            bool delta = (w & 0x8000_0000) != 0;
            if ((w & 0x4000_0000) == 0)
            {
                var keys = translations[slot];
                var value = delta
                    ? (keys.Count > 0 ? keys[^1].Value : Vector3.Zero) + stream.TranslationDelta()
                    : stream.Translation();
                keys.Add(new PoseKey<Vector3>(time, value));
            }
            else
            {
                var keys = rotations[slot];
                Quaternion value;
                if (delta)
                {
                    var previous = keys.Count > 0 ? keys[^1].Value : Quaternion.Identity;
                    value = Quaternion.Normalize(Product(previous, stream.RotationDelta()));
                }
                else
                {
                    var c = stream.Rotation();
                    int k = (int)((w >> 28) & 3);
                    value = new Quaternion(c[k], c[k ^ 1], c[k ^ 2], c[k ^ 3]);
                }
                keys.Add(new PoseKey<Quaternion>(time, value));
            }
            if (stream.Overrun)
                return null;
        }

        return new PoseKeys2Clip
        {
            BoneSymbols = symbols,
            Rotations = rotations.Select(k => k.ToArray()).ToArray(),
            Translations = translations.Select(k => k.ToArray()).ToArray(),
        };
    }

    /// <summary>Hamilton product a · b.</summary>
    private static Quaternion Product(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>
    /// The packed value stream: four buffers (translation/rotation × delta/absolute), each refilled
    /// with four values from the stream when used up.
    /// </summary>
    private sealed class ValueStream
    {
        private readonly byte[] _data;
        private int _pos = ValueBase;
        private readonly Vector3 _tdMin, _tdScale, _rdMin, _rdScale, _taMin, _taScale;
        private readonly Vector3[] _td = new Vector3[4], _ta = new Vector3[4];
        private readonly Quaternion[] _rd = new Quaternion[4];
        private readonly float[][] _ra = { new float[4], new float[4], new float[4], new float[4] };
        private int _tdUsed = 4, _taUsed = 4, _rdUsed = 4, _raUsed = 4;

        public bool Overrun { get; private set; }

        public ValueStream(byte[] data, ReadOnlySpan<float> f)
        {
            _data = data;
            _tdMin = new Vector3(f[0], f[1], f[2]);
            _tdScale = new Vector3(f[3] / 1023f, f[4] / 2047f, f[5] / 2047f);
            _rdMin = new Vector3(f[6], f[7], f[8]);
            _rdScale = new Vector3(f[9] / 1023f, f[10] / 2047f, f[11] / 2047f);
            _taMin = new Vector3(f[12], f[13], f[14]);
            _taScale = new Vector3(f[15] / ((1 << 20) - 1), f[16] / ((1 << 22) - 1), f[17] / ((1 << 22) - 1));
        }

        private uint Word(int index)
        {
            int at = _pos + index * 4;
            if (at + 4 > _data.Length)
            {
                Overrun = true;
                return 0;
            }
            return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at));
        }

        private static Vector3 Small(uint w, Vector3 min, Vector3 scale) => new(
            min.X + (w & 0x3FF) * scale.X,
            min.Y + ((w >> 10) & 0x7FF) * scale.Y,
            min.Z + (w >> 21) * scale.Z);

        private static (uint X, uint Y, uint Z) Wide(uint lo, uint hi) =>
            (((hi & 0x3FF) << 10) | (lo & 0x3FF),
             (((hi >> 10) & 0x7FF) << 11) | ((lo >> 10) & 0x7FF),
             ((hi >> 21) << 11) | (lo >> 21));

        private static float W(float x, float y, float z) => MathF.Sqrt(MathF.Max(1f - x * x - y * y - z * z, 0f));

        public Vector3 TranslationDelta()
        {
            if (_tdUsed > 3)
            {
                for (int i = 0; i < 4; i++)
                    _td[i] = Small(Word(i), _tdMin, _tdScale);
                _pos += 16;
                _tdUsed = 0;
            }
            return _td[_tdUsed++];
        }

        public Quaternion RotationDelta()
        {
            if (_rdUsed > 3)
            {
                for (int i = 0; i < 4; i++)
                {
                    var v = Small(Word(i), _rdMin, _rdScale);
                    _rd[i] = new Quaternion(v, W(v.X, v.Y, v.Z));
                }
                _pos += 16;
                _rdUsed = 0;
            }
            return _rd[_rdUsed++];
        }

        public Vector3 Translation()
        {
            if (_taUsed > 3)
            {
                for (int i = 0; i < 4; i++)
                {
                    var (x, y, z) = Wide(Word(i), Word(4 + i));
                    _ta[i] = _taMin + new Vector3(x, y, z) * _taScale;
                }
                _pos += 32;
                _taUsed = 0;
            }
            return _ta[_taUsed++];
        }

        /// <summary>The four rotation components before ordering: three stored, the fourth rebuilt.</summary>
        public float[] Rotation()
        {
            if (_raUsed > 3)
            {
                const float Sqrt2 = 1.41421356f, InvSqrt2 = 0.70710678f;
                for (int i = 0; i < 4; i++)
                {
                    var (x, y, z) = Wide(Word(i), Word(4 + i));
                    float a = x * Sqrt2 / ((1 << 20) - 1) - InvSqrt2;
                    float b = y * Sqrt2 / ((1 << 22) - 1) - InvSqrt2;
                    float c = z * Sqrt2 / ((1 << 22) - 1) - InvSqrt2;
                    _ra[i] = new[] { a, b, c, W(a, b, c) };
                }
                _pos += 32;
                _raUsed = 0;
            }
            return _ra[_raUsed++];
        }
    }
}
