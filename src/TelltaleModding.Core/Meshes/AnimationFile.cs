using System.Buffers.Binary;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

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

        // The first body of a CompressedSkeletonPoseKeys type entry is u32 size + blob.
        byte[]? poseBlob = null;
        if (typeTable.Length > 0 && typeTable[0].Hash == KnownAnimationTypes.CompressedSkeletonPoseKeys)
        {
            int size = (int)U32();
            if (size >= 0 && off + size <= trailerStart)
                poseBlob = file[off..(off + size)];
        }

        return new AnimationFile
        {
            Version = version,
            NameSymbol = name,
            Length = length,
            Channels = channels,
            PoseKeysPayload = poseBlob,
        };
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
