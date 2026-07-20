using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>One keyframe of a transform track.</summary>
public readonly record struct TransformKey(float Time, Quaternion Rotation, Vector3 Translation);

/// <summary>An uncompressed per-bone transform track (KeyframedValue&lt;Transform&gt;).</summary>
public sealed record TransformTrack(ulong BoneSymbol, uint Flags, IReadOnlyList<TransformKey> Keys)
{
    /// <summary>Samples the track at a time with linear/slerp interpolation.</summary>
    public (Quaternion Rotation, Vector3 Translation) Sample(float time)
    {
        if (Keys.Count == 0)
            return (Quaternion.Identity, Vector3.Zero);
        if (time <= Keys[0].Time || Keys.Count == 1)
            return (Keys[0].Rotation, Keys[0].Translation);
        if (time >= Keys[^1].Time)
            return (Keys[^1].Rotation, Keys[^1].Translation);
        int hi = 1;
        while (Keys[hi].Time < time)
            hi++;
        var a = Keys[hi - 1];
        var b = Keys[hi];
        float span = b.Time - a.Time;
        float t = span <= 1e-6f ? 0f : (time - a.Time) / span;
        return (Quaternion.Slerp(a.Rotation, b.Rotation, t),
                Vector3.Lerp(a.Translation, b.Translation, t));
    }
}

/// <summary>One animated channel: what kind of value it drives and which target/bone.</summary>
public readonly record struct AnimationChannel(ulong TypeHash, ulong TargetSymbol, uint Flags)
{
    public string? TypeName => KnownAnimationTypes.Resolve(TypeHash);
}

/// <summary>Known animation value-interface types of the GoT/TFTB era.</summary>
public static class KnownAnimationTypes
{
    public static readonly ulong CompressedSkeletonPoseKeys = TelltaleCrc64.HashSymbol("CompressedSkeletonPoseKeys");
    public static readonly ulong KeyframedTransform = TelltaleCrc64.HashSymbol("KeyframedValue<Transform>");

    /// <summary>
    /// Wire size of a KeyframedValue&lt;T&gt;'s min/max values: positive = raw bytes
    /// (intrinsic types), -1 = block-prefixed (Transform, String).
    /// </summary>
    private static readonly Dictionary<ulong, int> KfvValueSizes = new (string Name, int Size)[]
    {
        ("KeyframedValue<Transform>", -1), ("KeyframedValue<String>", -1),
        ("KeyframedValue<float>", 4), ("KeyframedValue<bool>", 1),
        ("KeyframedValue<Vector3>", 12), ("KeyframedValue<Quaternion>", 16),
        ("KeyframedValue<Symbol>", 8), ("KeyframedValue<Color>", 16),
        ("KeyframedValue<Vector2>", 8),
    }.ToDictionary(t => TelltaleCrc64.HashSymbol(t.Name), t => t.Size);

    /// <summary>True for any KeyframedValue&lt;T&gt; type we know how to walk.</summary>
    public static bool IsKeyframedValue(ulong hash) => KfvValueSizes.ContainsKey(hash);

    /// <summary>Raw min/max value size for a KfV type, or -1 when block-prefixed.</summary>
    public static int KeyframedValueSize(ulong hash) => KfvValueSizes[hash];

    private static readonly Dictionary<ulong, string> Names = new[]
    {
        "CompressedSkeletonPoseKeys", "CompressedSkeletonPoseKeys2",
        "KeyframedValue<Transform>", "KeyframedValue<Vector3>",
        "KeyframedValue<float>", "KeyframedValue<bool>", "KeyframedValue<Quaternion>",
        "CompressedTransformKeys", "CompressedPhonemeKeys", "SkeletonPoseValue",
    }.ToDictionary(TelltaleCrc64.HashSymbol, n => n);

    public static string? Resolve(ulong hash) => Names.TryGetValue(hash, out var n) ? n : null;
}

/// <summary>
/// A parsed Telltale .anm animation container (version 4/5, GoT / TFTB era).
/// Parses the channel table and per-channel target symbols; channel payloads are
/// kept raw (the compressed pose bit-stream is not decoded here).
///
/// Layout from TelltaleToolLib's Animation serializer, validated against a full
/// game's animations. Skeletal animations carry one CompressedSkeletonPoseKeys
/// channel whose payload embeds the rig's bone symbols — <see cref="FindBoneSymbols"/>
/// recovers them for skeleton matching.
/// </summary>
public sealed class AnimationFile
{
    public int Version { get; private init; }
    public ulong NameSymbol { get; private init; }
    public float Length { get; private init; }
    public IReadOnlyList<AnimationChannel> Channels { get; private init; } = Array.Empty<AnimationChannel>();

    /// <summary>Raw payload of the CompressedSkeletonPoseKeys channel, if present.</summary>
    public byte[]? PoseKeysPayload { get; private init; }

    /// <summary>Decoded uncompressed transform tracks (KeyframedValue&lt;Transform&gt; channels).</summary>
    public IReadOnlyList<TransformTrack> TransformTracks { get; private init; } = Array.Empty<TransformTrack>();

    /// <summary>False when an unknown channel type stopped body parsing early.</summary>
    public bool BodiesFullyParsed { get; private init; }

    /// <summary>True if this animation drives a skeleton (has a pose-keys channel).</summary>
    public bool IsSkeletal => PoseKeysPayload is not null;

    private AnimationFile() { }

    public string Describe() =>
        $"{Length:0.##}s · {Channels.Count} channel{(Channels.Count == 1 ? "" : "s")}" +
        (IsSkeletal ? " · skeletal" : "");

    public static bool TryLoad(byte[] data, out AnimationFile? animation, out string? error)
    {
        animation = null;
        if (!MetaStreamHeader.TryParse(data, out var meta, out error))
            return false;
        if (meta!.Classes.Count == 0 || meta.Classes[0].TypeName is not ("Animation" or null))
        {
            // Class 0 of .anm files hashes "Animation"; unknown name = fail soft below.
        }

        try
        {
            animation = Parse(data, meta);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            error = $"Malformed animation: {ex.Message}";
            return false;
        }
    }

    private static AnimationFile Parse(byte[] file, MetaStreamHeader meta)
    {
        int off = meta.DefaultSectionOffset;
        int sectionEnd = meta.DefaultSectionOffset + meta.DefaultSectionSize;

        uint U32()
        {
            if (off + 4 > sectionEnd) throw new InvalidDataException("section truncated");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off)); off += 4; return v;
        }
        ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(off)); off += 2; return v; }
        ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(off)); off += 8; return v; }
        float F32() { var v = BitConverter.ToSingle(file, off); off += 4; return v; }

        int version = (int)U32();
        if (version is not (4 or 5))
            throw new InvalidDataException($"Animation version {version} unsupported (expected 4/5).");
        U32();                                    // animation flags
        ulong name = U64();
        float length = F32();
        F32();                                    // additive mask
        if (file[off] is (byte)'0' or (byte)'1')  // tool props bool
            off++;

        int blockStart = off;
        int blockEnd = blockStart + (int)U32();
        if (blockEnd != sectionEnd)
            throw new InvalidDataException("value block does not end at section end");

        int channelCount = (int)U32();
        U32();                                    // runtime data buffer size
        int typeCount = (int)U32();
        var typeTable = new (ulong Hash, int Count)[typeCount];
        int totalFromTypes = 0;
        for (int i = 0; i < typeCount; i++)
        {
            ulong hash = U64();
            int count = U16();
            U16();                                // per-type version
            typeTable[i] = (hash, count);
            totalFromTypes += count;
        }
        if (totalFromTypes != channelCount)
            throw new InvalidDataException("type table does not sum to channel count");

        // Bodies sit here; the trailer is at the block's end: [u32 flags xN][u16 sel][u64 sym xN].
        int bodiesStart = off;
        int trailerStart = blockEnd - (4 * channelCount + 2 + 8 * channelCount);
        if (trailerStart < bodiesStart)
            throw new InvalidDataException("trailer overlaps channel bodies");
        int selector = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(trailerStart + 4 * channelCount));
        if (selector != 0)
            throw new InvalidDataException($"unsupported symbol-table selector {selector}");

        var channels = new AnimationChannel[channelCount];
        int flat = 0;
        foreach (var (hash, count) in typeTable)
        {
            for (int i = 0; i < count; i++, flat++)
            {
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(trailerStart + 4 * flat));
                ulong target = BinaryPrimitives.ReadUInt64LittleEndian(
                    file.AsSpan(trailerStart + 4 * channelCount + 2 + 8 * flat));
                channels[flat] = new AnimationChannel(hash, target, flags);
            }
        }

        // Channel bodies, sequential in type-table order. KeyframedValue<T> bodies are
        // self-describing (block-prefixed header/min/max/sample-array), so unknown T
        // skips cleanly; genuinely unknown types stop parsing (container info survives).
        byte[]? poseBlob = null;
        var tracks = new List<TransformTrack>();
        bool fullyParsed = true;
        ulong keyframedTransform = KnownAnimationTypes.KeyframedTransform;
        foreach (var (hash, count) in typeTable)
        {
            if (!fullyParsed)
                break;
            for (int i = 0; i < count && fullyParsed; i++)
            {
                if (hash == KnownAnimationTypes.CompressedSkeletonPoseKeys)
                {
                    int size = (int)U32();
                    if (size < 0 || off + size > trailerStart)
                        throw new InvalidDataException("pose keys payload overruns trailer");
                    poseBlob = file[off..(off + size)];
                    off += size;
                }
                else if (KnownAnimationTypes.IsKeyframedValue(hash))
                {
                    int headerEnd = off + (int)U32();
                    if (headerEnd < off || headerEnd > trailerStart)
                        throw new InvalidDataException("keyframed header block out of range");
                    ulong boneSymbol = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(off + 4));
                    uint chFlags = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off + 12));
                    off = headerEnd;
                    // min/max values: intrinsic types are raw; Transform/String are blocked.
                    int valueSize = KnownAnimationTypes.KeyframedValueSize(hash);
                    for (int m = 0; m < 2; m++)
                    {
                        off += valueSize >= 0
                            ? valueSize
                            : (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off));
                    }
                    if (off < 0 || off + 8 > trailerStart)
                        throw new InvalidDataException("keyframed min/max values out of range");
                    int arrayEnd = off + (int)U32();
                    int sampleCount = (int)U32();
                    if (arrayEnd > trailerStart || sampleCount < 0 || sampleCount > (arrayEnd - off) / 9)
                        throw new InvalidDataException("keyframed sample array out of range");

                    if (hash == keyframedTransform)
                    {
                        var keys = new TransformKey[sampleCount];
                        for (int s = 0; s < sampleCount; s++)
                        {
                            float time = F32();
                            if (file[off] is not ((byte)'0' or (byte)'1'))
                                throw new InvalidDataException("bad sample bool");
                            off++;
                            U32();                     // tangent mode
                            int valueEnd = off + (int)U32();
                            var rot = new Quaternion(F32(), F32(), F32(), F32());
                            var pos = new Vector3(F32(), F32(), F32());
                            off = valueEnd;
                            keys[s] = new TransformKey(time, rot, pos);
                        }
                        if (off != arrayEnd)
                            throw new InvalidDataException("transform samples did not consume their array");
                        tracks.Add(new TransformTrack(boneSymbol, chFlags, keys));
                    }
                    else
                    {
                        off = arrayEnd;               // other KfV<T>: skip whole sample array
                    }
                }
                else
                {
                    fullyParsed = false;              // unknown body layout; stop safely
                }
            }
        }
        if (fullyParsed && off != trailerStart)
            throw new InvalidDataException($"channel bodies ended at {off}, trailer starts at {trailerStart}");

        return new AnimationFile
        {
            Version = version,
            NameSymbol = name,
            Length = length,
            Channels = channels,
            PoseKeysPayload = poseBlob,
            TransformTracks = tracks,
            BodiesFullyParsed = fullyParsed,
        };
    }

    /// <summary>
    /// Cheaply decides whether an .anm on disk drives any of the given joints,
    /// without reading or parsing the whole file: the container header, type
    /// table, and (when present) the leading pose-keys bone table all sit near
    /// the start, and the channel-target symbol trailer is at a computable
    /// offset. Two small reads replace a full parse. Returns the match and the
    /// animation length, or null when the file needs a full <see cref="TryLoad"/>.
    /// </summary>
    public static (bool Matches, float Length)? ProbeSkeletonMatch(string path, IReadOnlySet<ulong> joints)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[Math.Min(stream.Length, 64 * 1024)];
            stream.ReadExactly(head, 0, head.Length);
            if (!MetaStreamHeader.TryParse(head, out var meta, out _))
                return null;

            int off = meta!.DefaultSectionOffset;
            int sectionEnd = meta.DefaultSectionOffset + meta.DefaultSectionSize;
            if (off + 32 > head.Length)
                return null;
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(off));
            if (version is not (4 or 5))
                return null;
            off += 16; // version, flags, name symbol
            float length = BitConverter.ToSingle(head, off);
            off += 8;  // length, additive mask
            if (off < head.Length && head[off] is (byte)'0' or (byte)'1')
                off++;
            int blockEnd = off + BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(off));
            off += 4;
            int channelCount = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(off));
            off += 8;
            int typeCount = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(off));
            off += 4;
            if (channelCount <= 0 || channelCount > 4096 || typeCount <= 0 || typeCount > 256
                || blockEnd != sectionEnd || off + typeCount * 12 > head.Length)
                return null;
            ulong firstType = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(off));
            off += typeCount * 12;

            // Pose-keys channels lead the body; their bone table is right here.
            if (firstType == KnownAnimationTypes.CompressedSkeletonPoseKeys
                && off + 4 <= head.Length)
            {
                int tableOff = off + 4 + 112;
                if (tableOff + 4 <= head.Length)
                {
                    int bones = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(off + 4 + 96));
                    if (bones > 0 && bones <= 4096 && tableOff + bones * 8 <= head.Length)
                    {
                        for (int b = 0; b < bones; b++)
                        {
                            ulong sym = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(tableOff + b * 8));
                            if (sym != 0 && joints.Contains(sym))
                                return (true, length);
                        }
                        // fall through: keyframed channels may still target the rig
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }

            // Check the channel-target trailer.
            int trailerStart = blockEnd - (4 * channelCount + 2 + 8 * channelCount);
            int symbolsStart = trailerStart + 4 * channelCount + 2;
            if (trailerStart <= 0 || symbolsStart + 8 * channelCount > stream.Length)
                return null;
            var trailer = new byte[8 * channelCount];
            stream.Position = symbolsStart;
            stream.ReadExactly(trailer, 0, trailer.Length);
            for (int i = 0; i < channelCount; i++)
            {
                if (joints.Contains(BinaryPrimitives.ReadUInt64LittleEndian(trailer.AsSpan(i * 8))))
                    return (true, length);
            }
            return (false, length);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recovers bone symbols from the pose-keys payload by scanning for 8-byte values
    /// present in <paramref name="candidates"/> (e.g. a skeleton's joint set).
    /// </summary>
    public IReadOnlySet<ulong> FindBoneSymbols(IReadOnlySet<ulong> candidates)
    {
        var found = new HashSet<ulong>();
        if (PoseKeysPayload is not { } blob)
            return found;
        for (int i = 0; i + 8 <= blob.Length; i++)
        {
            ulong v = BitConverter.ToUInt64(blob, i);
            if (candidates.Contains(v))
                found.Add(v);
        }
        return found;
    }
}
