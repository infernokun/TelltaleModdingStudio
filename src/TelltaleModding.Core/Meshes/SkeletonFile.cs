using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>One bone of a skeleton: identity, hierarchy, and local rest transform.</summary>
/// <param name="JointSymbol">CRC64 of the bone's name.</param>
/// <param name="ParentIndex">-1 for the root.</param>
public readonly record struct SkeletonBone(
    ulong JointSymbol, ulong ParentNameSymbol, int ParentIndex,
    Vector3 LocalPosition, Quaternion LocalRotation);

/// <summary>
/// A parsed Telltale .skl skeleton (GoT / Tales from the Borderlands era).
/// Layout from TelltaleToolLib's serializer registration, verified byte-exact
/// against a full game's skeletons.
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

        var bones = new SkeletonBone[count];
        for (int i = 0; i < count; i++)
        {
            ulong joint = U64();
            ulong parentName = U64();
            int parentIndex = I32();
            U64(); I32();                             // mirror bone name + index
            var pos = new Vector3(F32(), F32(), F32());
            var rot = new Quaternion(F32(), F32(), F32(), F32());
            off = Block();                            // mRestXForm (blocked Transform)
            off += 36;                                // global/local/anim translation scales
            off = Block();                            // resource group membership map
            off = Block();                            // bone constraints
            U32();                                    // flags
            bones[i] = new SkeletonBone(joint, parentName, parentIndex, pos, rot);
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
