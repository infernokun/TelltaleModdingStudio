using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// A serialized PropertySet (a <c>.prop</c> file or an agent's set inside a <c>.scene</c>)
/// with values looked up by key symbol.
/// </summary>
/// <remarks>
/// Layout (MSV5/MSV6): <c>u32 version (1|2), u32 flags</c>, then for version 2 one block
/// <c>[u32 size, u32 parentCount, Symbol parents…, u32 typeCount, { Symbol type, u32 count,
/// { Symbol key, value }… }…]</c>; version 1 splits the parents and the typed values into two
/// blocks. Values are serialized by type, so instead of walking every type the reader finds a
/// key's symbol and decodes the value that follows it as the caller's expected type.
/// </remarks>
public sealed class ScenePropertySet
{
    private readonly byte[] _data;
    private readonly int _valuesStart, _end;

    private ScenePropertySet(byte[] data, int valuesStart, int end, IReadOnlyList<ulong> parents)
    {
        _data = data;
        _valuesStart = valuesStart;
        _end = end;
        Parents = parents;
    }

    /// <summary>Symbols of the parent property sets (<c>.prop</c> file names), in stored order.</summary>
    public IReadOnlyList<ulong> Parents { get; }

    /// <summary>Size of the serialized values, in bytes.</summary>
    public int Length => _end - _valuesStart;

    /// <summary>An empty set (no parents, no values).</summary>
    public static ScenePropertySet Empty { get; } = new(Array.Empty<byte>(), 0, 0, Array.Empty<ulong>());

    /// <summary>Parses a <c>.prop</c> file's bytes (with or without a meta stream header). Null if it isn't a PropertySet.</summary>
    public static ScenePropertySet? TryParse(byte[] data)
    {
        int start = 0, end = data.Length;
        if (MetaStreamHeader.TryParse(data, out var header, out _, isCompleteFile: false))
        {
            start = header!.DefaultSectionOffset;
            end = (int)Math.Min(data.Length, (long)start + header.DefaultSectionSize);
        }
        return TryParse(data, start, end);
    }

    /// <summary>Parses the PropertySet serialized at <c>data[start..end)</c>. Null when the header is implausible.</summary>
    public static ScenePropertySet? TryParse(byte[] data, int start, int end)
    {
        if (start < 0 || end > data.Length || end - start < 16)
            return null;
        uint version = U32(data, start);
        if (version is not (1 or 2))
            return null;
        int blockSize = (int)U32(data, start + 8);
        if (blockSize < 8 || start + 8L + blockSize > end)
            return null;
        int parentCount = (int)U32(data, start + 12);
        if (parentCount is < 0 or > 256 || start + 16L + parentCount * 8L > end)
            return null;
        var parents = new ulong[parentCount];
        for (int i = 0; i < parentCount; i++)
            parents[i] = U64(data, start + 16 + i * 8);
        return new ScenePropertySet(data, start + 16 + parentCount * 8, end, parents);
    }

    /// <summary>True when the key occurs in this set (not counting parents).</summary>
    public bool Contains(ulong key) => Find(key) >= 0;

    /// <summary>A <c>bool</c> value (serialized as ASCII '0'/'1').</summary>
    public bool? GetBool(ulong key)
    {
        int at = Find(key, 1);
        if (at < 0)
            return null;
        return _data[at] switch
        {
            (byte)'1' or 1 => true,
            (byte)'0' or 0 => false,
            _ => null,
        };
    }

    /// <summary>A <c>float</c> value.</summary>
    public float? GetFloat(ulong key)
    {
        int at = Find(key, 4);
        return at < 0 ? null : Finite(BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(at)));
    }

    /// <summary>An <c>int32</c> value, or an enum stored as one.</summary>
    public int? GetInt32(ulong key)
    {
        int at = Find(key, 4);
        return at < 0 ? null : BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(at));
    }

    /// <summary>A <c>Color</c> value (RGBA floats).</summary>
    public Vector4? GetColor(ulong key)
    {
        int at = Find(key, 16);
        if (at < 0)
            return null;
        var c = new Vector4(F32(_data, at), F32(_data, at + 4), F32(_data, at + 8), F32(_data, at + 12));
        return float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z) && float.IsFinite(c.W) ? c : null;
    }

    /// <summary>A <c>Vector3</c> value.</summary>
    public Vector3? GetVector3(ulong key)
    {
        int at = Find(key, 12);
        if (at < 0)
            return null;
        var v = ReadVector3(_data, at);
        return float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) ? v : null;
    }

    /// <summary>A <c>Quaternion</c> value, normalized (null when degenerate).</summary>
    public Quaternion? GetQuaternion(ulong key)
    {
        int at = Find(key, 16);
        return at < 0 ? null : NormalizedOrNull(ReadQuaternion(_data, at));
    }

    /// <summary>A <c>Symbol</c> or <c>Handle&lt;T&gt;</c> value (the file-name symbol); null when absent or empty.</summary>
    public ulong? GetSymbol(ulong key)
    {
        int at = Find(key, 8);
        if (at < 0)
            return null;
        ulong value = U64(_data, at);
        return value == 0 ? null : value;
    }

    /// <summary>
    /// A <c>DCArray</c> of symbols or handles: <c>u32 count</c> followed by the elements, with or
    /// without a leading block size (which also gives the element stride; older games write
    /// each handle as name and type symbols).
    /// </summary>
    public IReadOnlyList<ulong>? GetSymbolList(ulong key)
    {
        int p = Find(key, 4);
        if (p < 0)
            return null;
        uint first = U32(_data, p);
        if (p + 8 <= _end)
        {
            uint count = U32(_data, p + 4);
            if (count is > 0 and <= 4096 && first >= 8 && (first - 8) % count == 0 && p + (long)first <= _end)
            {
                int stride = (int)((first - 8) / count);
                if (stride is 8 or 16)
                    return ReadSymbols(p + 8, (int)count, stride);
            }
            if (count == 0 && first == 8)
                return Array.Empty<ulong>();
        }
        if (first <= 4096 && p + 4 + 8L * first <= _end)
            return ReadSymbols(p + 4, (int)first, 8);
        return null;
    }

    /// <summary>A <c>String</c> value (with or without a leading block size).</summary>
    public string? GetString(ulong key)
    {
        int at = Find(key, 4);
        return at < 0 ? null : TryReadString(_data, at, _end, out var text, out _) ? text : null;
    }

    /// <summary>
    /// A <c>LocationInfo</c> value: the agent and node it is attached to and its initial local
    /// transform (<c>String, Symbol, Transform { Quaternion, Vector3 }</c>, members blocked).
    /// </summary>
    public SceneLocation? GetLocationInfo(ulong key)
    {
        int p = Find(key, 4);
        if (p < 0 || !TryReadString(_data, p, _end, out var agent, out int used))
            return null;
        p += used;
        if (p + 8 > _end)
            return null;
        ulong node = U64(_data, p);
        p += 8;
        if (p + 4 <= _end && U32(_data, p) == 32)
            p += 4; // Transform's block size
        if (p + 28 > _end)
            return null;
        var rotation = NormalizedOrNull(ReadQuaternion(_data, p)) ?? Quaternion.Identity;
        var position = ReadVector3(_data, p + 16);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            position = Vector3.Zero;
        return new SceneLocation(agent!, node, position, rotation);
    }

    /// <summary>
    /// Finds where a key's value starts: the first occurrence of the key's symbol followed
    /// by at least <paramref name="valueSize"/> bytes. -1 when absent.
    /// </summary>
    private int Find(ulong key, int valueSize = 0)
    {
        if (key == 0)
            return -1;
        var span = _data.AsSpan(_valuesStart, _end - _valuesStart);
        Span<byte> pattern = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(pattern, key);
        int at = span.IndexOf(pattern);
        if (at < 0)
            return -1;
        int value = _valuesStart + at + 8;
        return value + valueSize <= _end ? value : -1;
    }

    private IReadOnlyList<ulong> ReadSymbols(int at, int count, int stride)
    {
        var list = new List<ulong>(count);
        for (int i = 0; i < count; i++)
        {
            ulong s = U64(_data, at + i * stride);
            if (s != 0)
                list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// Reads a <c>String</c>: <c>u32 length + bytes</c>, optionally wrapped in a block
    /// (<c>u32 size = 8 + length</c>). <paramref name="used"/> is the bytes consumed.
    /// </summary>
    internal static bool TryReadString(byte[] data, int at, int end, out string? text, out int used)
    {
        text = null;
        used = 0;
        if (at + 4 > end)
            return false;
        uint first = U32(data, at);
        if (at + 8 <= end)
        {
            uint second = U32(data, at + 4);
            if (first == 8 + second && second <= 4096 && at + 8L + second <= end)
            {
                text = System.Text.Encoding.UTF8.GetString(data, at + 8, (int)second);
                used = (int)first;
                return true;
            }
        }
        if (first <= 4096 && at + 4L + first <= end)
        {
            text = System.Text.Encoding.UTF8.GetString(data, at + 4, (int)first);
            used = 4 + (int)first;
            return true;
        }
        return false;
    }

    private static float? Finite(float value) => float.IsFinite(value) ? value : null;

    private static Quaternion? NormalizedOrNull(Quaternion q)
    {
        float length = q.Length();
        return float.IsFinite(length) && length > 1e-4f ? Quaternion.Normalize(q) : null;
    }

    private static Vector3 ReadVector3(byte[] d, int at) => new(F32(d, at), F32(d, at + 4), F32(d, at + 8));

    private static Quaternion ReadQuaternion(byte[] d, int at) => new(F32(d, at), F32(d, at + 4), F32(d, at + 8), F32(d, at + 12));

    private static float F32(byte[] d, int at) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at));

    internal static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));

    internal static ulong U64(byte[] d, int at) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at));
}

/// <summary>Where an agent starts: the agent (and node) it is attached to and its transform relative to that.</summary>
/// <param name="AttachedAgent">Parent agent name; empty when not attached.</param>
/// <param name="AttachedNode">Symbol of the parent's skeleton node it rides on; 0 for the agent itself.</param>
public readonly record struct SceneLocation(string AttachedAgent, ulong AttachedNode, Vector3 Position, Quaternion Rotation);
