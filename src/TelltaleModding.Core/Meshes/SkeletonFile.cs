using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>One bone of a skeleton: identity, hierarchy, and local rest transform.</summary>
/// <param name="JointSymbol">CRC64 of the bone's name.</param>
/// <param name="ParentIndex">-1 for the root.</param>
/// <param name="RestRotation">Rotation of the entry's mRestXForm (usually equals LocalRotation).</param>
public readonly record struct SkeletonBone(
    ulong JointSymbol, ulong ParentNameSymbol, int ParentIndex,
    Vector3 LocalPosition, Quaternion LocalRotation,
    Vector3 RestPosition, Quaternion RestRotation)
{
    /// <summary>
    /// The entry's mAnimTranslationScale (global × local translation scale): how this bone's
    /// proportions differ from the rig its animations were authored on. The engine uses it to
    /// turn an animation's translation direction onto this skeleton's rest direction (see
    /// <see cref="CspkDecoder.TranslationFrame"/>).
    /// </summary>
    public Vector3 AnimTranslationScale { get; init; }
}

/// <summary>
/// A parsed Telltale .skl skeleton (Wolf Among Us / Game of Thrones through Tales from
/// the Borderlands era). Layout from TelltaleToolLib's serializer registration, verified
/// byte-exact against full games' skeletons. The older MSV5 games (The Wolf Among Us,
/// Game of Thrones) write the same entries without the mirror bone name and index.
/// </summary>
public sealed class SkeletonFile
{
    public IReadOnlyList<SkeletonBone> Bones { get; private init; } = Array.Empty<SkeletonBone>();

    /// <summary>Joint symbols as a set, for fast mesh/animation compatibility checks.</summary>
    public IReadOnlySet<ulong> JointSymbols { get; private init; } = new HashSet<ulong>();

    private SkeletonFile() { }

    /// <summary>Attempts to parse a .skl file's bytes.</summary>
    public static bool TryLoad(byte[] data, out SkeletonFile? skeleton, out string? error)
    {
        skeleton = null;
        if (!MetaStreamHeader.TryParse(data, out var meta, out error))
            return false;
        if (meta!.Classes.Count == 0 || meta.Classes[0].TypeName != "Skeleton")
        {
            error = "Not a skeleton (header does not declare Skeleton).";
            return false;
        }

        try
        {
            skeleton = Parse(data, meta);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            error = $"Malformed skeleton: {ex.Message}";
            return false;
        }
    }

    private static SkeletonFile Parse(byte[] file, MetaStreamHeader meta)
    {
        int off = meta.DefaultSectionOffset;
        int sectionEnd = meta.DefaultSectionOffset + meta.DefaultSectionSize;

        uint U32()
        {
            if (off + 4 > sectionEnd) throw new InvalidDataException("section truncated");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off)); off += 4; return v;
        }
        int I32() => (int)U32();
        float F32() { var v = BitConverter.ToSingle(file, off); off += 4; return v; }
        ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(off)); off += 8; return v; }
        int Block() { int start = off; int size = I32(); return start + size; }

        int entriesEnd = Block();
        int count = I32();
        if (count is < 0 or > 4096)
            throw new InvalidDataException($"implausible bone count {count}");

        // Entries of the MSV5 games lack the mirror bone fields. Tell the layouts apart by
        // where the first entry's rest transform block (32 bytes, counting its own size)
        // starts: after joint/parent/index (20), [mirror name + index (12),] local pos + quat (28).
        bool hasMirror = true;
        if (count > 0)
        {
            int BlockSizeAt(int at) =>
                at + 4 <= sectionEnd ? BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at)) : -1;
            const int restTransformBlockSize = 32;
            hasMirror = BlockSizeAt(off + 60) == restTransformBlockSize
                        || BlockSizeAt(off + 48) != restTransformBlockSize;
        }

        var bones = new SkeletonBone[count];
        for (int i = 0; i < count; i++)
        {
            ulong joint = U64();
            ulong parentName = U64();
            int parentIndex = I32();
            if (hasMirror)
            {
                U64(); I32();                         // mirror bone name + index
            }
            var pos = new Vector3(F32(), F32(), F32());
            var rot = new Quaternion(F32(), F32(), F32(), F32());
            int restEnd = Block();                    // mRestXForm (blocked Transform)
            var restRot = new Quaternion(F32(), F32(), F32(), F32());
            var restPos = new Vector3(F32(), F32(), F32());
            off = restEnd;
            off += 24;                                // global/local translation scales
            var animScale = new Vector3(F32(), F32(), F32());
            off = Block();                            // resource group membership map
            off = Block();                            // bone constraints
            U32();                                    // flags
            bones[i] = new SkeletonBone(joint, parentName, parentIndex, pos, rot, restPos, restRot)
            {
                AnimTranslationScale = animScale,
            };
        }
        if (off != entriesEnd || off != sectionEnd)
            throw new InvalidDataException($"skeleton did not consume its section (at {off}, expected {sectionEnd})");

        return new SkeletonFile
        {
            Bones = bones,
            JointSymbols = bones.Select(b => b.JointSymbol).ToHashSet(),
        };
    }

    /// <summary>
    /// Index of each joint symbol in <see cref="Bones"/>. A few skeletons list a joint twice
    /// (sk55_zombiePole); the first bone with the symbol wins.
    /// </summary>
    public IReadOnlyDictionary<ulong, int> BoneIndexBySymbol =>
        _boneIndex ??= Bones.Select((b, i) => (b.JointSymbol, i))
            .GroupBy(t => t.JointSymbol)
            .ToDictionary(g => g.Key, g => g.First().i);

    private Dictionary<ulong, int>? _boneIndex;

    /// <summary>
    /// World (model-space) transform of every bone from the given local pose,
    /// composed down the parent chain. Row-vector convention (v' = v · M).
    /// </summary>
    public Matrix4x4[] ComposeWorld(Func<SkeletonBone, (Vector3 Pos, Quaternion Rot)> localPose)
    {
        var world = new Matrix4x4[Bones.Count];
        for (int i = 0; i < Bones.Count; i++)
        {
            var (pos, rot) = localPose(Bones[i]);
            var local = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
            world[i] = Bones[i].ParentIndex >= 0 ? local * world[Bones[i].ParentIndex] : local;
        }
        return world;
    }

    /// <summary>World transforms of the rest (bind) pose.</summary>
    public Matrix4x4[] BindPoseWorld() => ComposeWorld(b => (b.LocalPosition, b.LocalRotation));

    /// <summary>
    /// Fraction of the given bone symbols this skeleton contains (1.0 = every bone
    /// of the mesh/animation exists in this skeleton).
    /// </summary>
    public double Coverage(IEnumerable<ulong> boneSymbols)
    {
        int total = 0, hit = 0;
        foreach (var symbol in boneSymbols)
        {
            total++;
            if (JointSymbols.Contains(symbol))
                hit++;
        }
        return total == 0 ? 0 : (double)hit / total;
    }
}
