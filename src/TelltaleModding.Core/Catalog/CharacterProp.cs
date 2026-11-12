using System.Buffers.Binary;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// The parts of a character's <c>.prop</c> the catalog needs: its skeleton, its mesh list,
/// and which meshes start visible.
/// </summary>
/// <remarks>
/// Layout from TelltaleToolLib's <c>PropertySet::MetaOperation_SerializeAsync</c>:
/// <c>u32 version, u32 flags, [block u32 size, u32 parentCount, Symbol parents…,
/// u32 typeCount, { Symbol type, u32 count, { Symbol key, value }… }…]</c>.
/// Values are serialized by type, so rather than walk them all this scans for the few keys
/// it needs by symbol. Bools are stored as ASCII '0'/'1'. Only the top-level
/// "Mesh X - Visible" keys are the default outfit; nested sets hold style toggles and are ignored.
/// </remarks>
public sealed class CharacterProp
{
    private static readonly ulong KeySkeletonFile = TelltaleCrc64.HashSymbol("Skeleton File");
    private static readonly ulong KeyMeshList = TelltaleCrc64.HashSymbol("D3D Mesh List");
    private static readonly ulong KeyMesh = TelltaleCrc64.HashSymbol("D3D Mesh");

    /// <summary>Type symbol of <c>bool</c> values in a PropertySet.</summary>
    public static readonly ulong BoolType = TelltaleCrc64.HashSymbol("bool");

    /// <summary>Symbol of the skeleton's file name, if set.</summary>
    public ulong? SkeletonSymbol { get; private init; }

    /// <summary>Symbols of the mesh file names in "D3D Mesh List", in stored order.</summary>
    public IReadOnlyList<ulong> MeshListSymbols { get; private init; } = Array.Empty<ulong>();

    /// <summary>Symbol of the single "D3D Mesh", if set and non-empty.</summary>
    public ulong? MeshSymbol { get; private init; }

    /// <summary>Symbols of the parent property sets.</summary>
    public IReadOnlyList<ulong> ParentSymbols { get; private init; } = Array.Empty<ulong>();

    /// <summary>Every top-level <c>bool</c> property, by key symbol.</summary>
    public IReadOnlyDictionary<ulong, bool> TopLevelBools { get; private init; } = new Dictionary<ulong, bool>();

    /// <summary>True when the prop names a skeleton and at least one mesh.</summary>
    public bool DescribesCharacter => SkeletonSymbol is not null && (MeshListSymbols.Count > 0 || MeshSymbol is not null);

    /// <summary>Default visibility of a mesh, or null when the prop has no key for it and the engine shows it.</summary>
    public bool? IsMeshVisible(string meshName)
    {
        var stem = meshName.EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase) ? meshName[..^8] : meshName;
        return TopLevelBools.TryGetValue(VisibilityKey(stem), out var visible) ? visible : null;
    }

    /// <summary>The engine's visibility key symbol for a mesh.</summary>
    public static ulong VisibilityKey(string meshStem) => TelltaleCrc64.HashSymbol($"Mesh {meshStem} - Visible");

    /// <summary>Reads the character-relevant keys from a <c>.prop</c> file's bytes. Null if it isn't a PropertySet.</summary>
    public static CharacterProp? TryParse(byte[] data)
    {
        int start = 0, end = data.Length;
        if (MetaStreamHeader.TryParse(data, out var header, out _, isCompleteFile: false))
        {
            start = header!.DefaultSectionOffset;
            end = (int)Math.Min(data.Length, (long)start + header.DefaultSectionSize);
        }
        if (end - start < 16)
            return null;

        // Header: version, flags, then a block whose size counts itself.
        uint version = U32(data, start);
        if (version is not (1 or 2))
            return null;
        int blockSize = (int)U32(data, start + 8);
        int topEnd = blockSize >= 8 && start + 8L + blockSize <= end ? start + 8 + blockSize : end;
        int parentCount = (int)U32(data, start + 12);
        var parents = new List<ulong>();
        if (parentCount is >= 0 and <= 64 && start + 16 + parentCount * 8 <= topEnd)
        {
            for (int i = 0; i < parentCount; i++)
                parents.Add(U64(data, start + 16 + i * 8));

            // Version 1 closes the parents block and opens a second one for the properties.
            int second = start + 16 + parentCount * 8;
            if (version == 1 && second == topEnd && second + 4 <= end)
            {
                int size2 = (int)U32(data, second);
                topEnd = size2 >= 8 && second + (long)size2 <= end ? second + size2 : end;
            }
        }

        ulong? skeleton = null, mesh = null;
        IReadOnlyList<ulong>? meshList = null;
        for (int p = start; p + 8 <= topEnd; p++)
        {
            ulong v = U64(data, p);
            if (skeleton is null && v == KeySkeletonFile && p + 16 <= topEnd)
            {
                ulong s = U64(data, p + 8);
                if (s != 0) skeleton = s;
            }
            else if (meshList is null && v == KeyMeshList)
            {
                meshList = ReadSymbolArray(data, p + 8, topEnd);
            }
            else if (mesh is null && v == KeyMesh && p + 16 <= topEnd)
            {
                ulong s = U64(data, p + 8);
                if (s != 0) mesh = s;
            }
        }

        return new CharacterProp
        {
            SkeletonSymbol = skeleton,
            MeshListSymbols = meshList ?? Array.Empty<ulong>(),
            MeshSymbol = mesh,
            ParentSymbols = parents,
            TopLevelBools = ReadTopLevelBools(data, start, topEnd),
        };
    }

    /// <summary>Reads <c>u32 count + count×Symbol</c>, also accepting a leading block size.</summary>
    private static IReadOnlyList<ulong>? ReadSymbolArray(byte[] data, int p, int end)
    {
        if (p + 4 > end) return null;
        uint first = U32(data, p);
        // Some files put a block size first, which counts itself, the count and the symbols.
        if (p + 8 <= end)
        {
            uint second = U32(data, p + 4);
            if (second <= 4096 && first == 8 + 8 * second && p + 8 + 8L * second <= end)
                return ReadSymbols(p + 8, (int)second);
        }
        if (first <= 4096 && p + 4 + 8L * first <= end)
            return ReadSymbols(p + 4, (int)first);
        return null;

        IReadOnlyList<ulong> ReadSymbols(int at, int count)
        {
            var list = new List<ulong>(count);
            for (int i = 0; i < count; i++)
            {
                ulong s = U64(data, at + i * 8);
                if (s != 0) list.Add(s);
            }
            return list;
        }
    }

    /// <summary>Finds every run of <c>bool</c> values and keeps those not inside a nested PropertySet.</summary>
    private static IReadOnlyDictionary<ulong, bool> ReadTopLevelBools(byte[] data, int start, int topEnd)
    {
        var runs = new List<(int Start, int End, List<(ulong Key, bool Value)> Pairs)>();
        for (int p = start; p + 12 <= topEnd; p++)
        {
            if (U64(data, p) != BoolType) continue;
            uint count = U32(data, p + 8);
            if (count == 0 || count > 4096 || p + 12 + 9L * count > topEnd) continue;
            var pairs = new List<(ulong, bool)>((int)count);
            bool ok = true;
            for (int i = 0; i < count && ok; i++)
            {
                int at = p + 12 + i * 9;
                byte b = data[at + 8];
                if (b is (byte)'0' or (byte)'1' or 0 or 1)
                    pairs.Add((U64(data, at), b is (byte)'1' or 1));
                else
                    ok = false;
            }
            if (ok)
            {
                runs.Add((p, p + 12 + 9 * (int)count, pairs));
                p += 11 + 9 * (int)count;
            }
        }

        var topLevel = runs.Where(r => !IsInsideNestedSet(data, start, topEnd, r.Start, r.End)).ToList();
        // If everything looked nested, trust the largest run. The agent's own list is usually the longest.
        if (topLevel.Count == 0 && runs.Count > 0)
            topLevel.Add(runs.MaxBy(r => r.Pairs.Count));

        var result = new Dictionary<ulong, bool>();
        foreach (var run in topLevel)
            foreach (var (key, value) in run.Pairs)
                result.TryAdd(key, value);
        return result;
    }

    /// <summary>True when a plausible nested PropertySet header before <paramref name="runStart"/> encloses the run.</summary>
    private static bool IsInsideNestedSet(byte[] data, int start, int topEnd, int runStart, int runEnd)
    {
        for (int p = start + 4; p + 20 <= runStart; p++)
        {
            uint version = U32(data, p);
            if (version is not (1 or 2)) continue;
            long size = U32(data, p + 8);
            long blockEnd = p + 8 + size;
            if (size < 16 || blockEnd < runEnd || blockEnd > topEnd) continue;
            uint parents = U32(data, p + 12);
            if (parents > 64) continue;
            long typeCountAt = p + 16 + 8L * parents;
            if (typeCountAt + 4 > runStart) continue;
            uint types = U32(data, (int)typeCountAt);
            if (types is 0 or > 256) continue;
            return true;
        }
        return false;
    }

    private static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
    private static ulong U64(byte[] d, int at) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at));
}
