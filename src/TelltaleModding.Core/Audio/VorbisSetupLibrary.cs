using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace TelltaleModding.Core.Audio;

/// <summary>
/// The Vorbis setup headers a game's audio runtime was built with, keyed by the CRC32
/// that FMOD sound banks store in place of the header itself. They are harvested from
/// the audio DLLs that ship in the game folder: the files are only read, never loaded,
/// so 32-bit and 64-bit builds both work.
/// </summary>
public sealed class VorbisSetupLibrary
{
    private static readonly ConcurrentDictionary<string, VorbisSetupLibrary> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly byte[] Signature = { 5, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' };

    private readonly Dictionary<uint, byte[]> _setups;

    private VorbisSetupLibrary(Dictionary<uint, byte[]> setups, string? source)
    {
        _setups = setups;
        Source = source;
    }

    public static VorbisSetupLibrary Empty { get; } = new(new Dictionary<uint, byte[]>(), null);

    /// <summary>Number of distinct setup headers available.</summary>
    public int Count => _setups.Count;

    /// <summary>The folder the headers were harvested from, if any.</summary>
    public string? Source { get; }

    /// <summary>True when a setup header with this CRC32 is known.</summary>
    public bool Contains(uint crc) => _setups.ContainsKey(crc);

    internal bool TryGet(uint crc, out byte[] setup) => _setups.TryGetValue(crc, out setup!);

    /// <summary>
    /// Scans every *.dll directly inside <paramref name="gameRootFolder"/> for embedded
    /// setup headers. Results are cached per folder; a missing folder gives an empty library.
    /// </summary>
    public static VorbisSetupLibrary FromFolder(string gameRootFolder)
    {
        if (string.IsNullOrWhiteSpace(gameRootFolder) || !Directory.Exists(gameRootFolder))
            return Empty;
        var key = Path.GetFullPath(gameRootFolder).TrimEnd('\\', '/');
        return Cache.GetOrAdd(key, static folder =>
        {
            var setups = new Dictionary<uint, byte[]>();
            foreach (var dll in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
            {
                byte[] image;
                try { image = File.ReadAllBytes(dll); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                Harvest(image, setups);
            }
            return new VorbisSetupLibrary(setups, folder);
        });
    }

    /// <summary>Builds a library from the bytes of one or more DLL images.</summary>
    internal static VorbisSetupLibrary FromImages(params byte[][] images)
    {
        var setups = new Dictionary<uint, byte[]>();
        foreach (var image in images) Harvest(image, setups);
        return new VorbisSetupLibrary(setups, null);
    }

    /// <summary>Builds a library from bare setup packets, keyed by their CRC32.</summary>
    internal static VorbisSetupLibrary FromPackets(IEnumerable<byte[]> packets)
    {
        var setups = new Dictionary<uint, byte[]>();
        foreach (var p in packets) setups[AudioCrc.Crc32(p)] = p;
        return new VorbisSetupLibrary(setups, null);
    }

    private static List<int> FindSignatures(byte[] image)
    {
        var hits = new List<int>();
        var span = image.AsSpan();
        int pos = 0;
        while (true)
        {
            int i = span[pos..].IndexOf(Signature);
            if (i < 0) break;
            hits.Add(pos + i);
            pos += i + 1;
        }
        return hits;
    }

    /// <summary>
    /// Adds every setup header found in a DLL image. The runtime keeps a table of
    /// records <c>{ptr data, u32 size, u32 crc, ptr base, u32 patchOffset, u32 patchLength}</c>:
    /// plain records point straight at a header; patched records copy <c>size</c> bytes
    /// of the base header and overwrite <c>patchLength</c> bytes at <c>patchOffset</c>
    /// with the bytes at <c>data</c>. Every record carries its own CRC, which is checked.
    /// </summary>
    internal static void Harvest(byte[] image, Dictionary<uint, byte[]> setups)
    {
        var hits = FindSignatures(image);
        if (hits.Count == 0) return;

        var pe = PeImage.TryOpen(image);
        int before = setups.Count;
        if (pe is not null) HarvestTable(image, pe, hits, setups);

        if (setups.Count == before)
        {
            // No table found: take each embedded header at face value, using the
            // header's own structure to find where it ends.
            foreach (int hit in hits)
            {
                int end = Math.Min(image.Length, hit + 0x20000);
                foreach (int ch in new[] { 2, 1 })
                {
                    if (VorbisSetupParser.TryParse(image, hit, end, ch, out int len, out _))
                    {
                        var packet = image.AsSpan(hit, len).ToArray();
                        setups.TryAdd(AudioCrc.Crc32(packet), packet);
                        break;
                    }
                }
            }
        }
    }

    private static void HarvestTable(byte[] image, PeImage pe, List<int> hits, Dictionary<uint, byte[]> setups)
    {
        int ptrSize = pe.Is64 ? 8 : 4;
        int recSize = ptrSize * 2 + 16;

        // Locate the table through references to the first embedded header.
        if (pe.FileToVa(hits[0]) is not ulong firstVa) return;

        int tableRef = -1;
        Span<byte> needle = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(needle, firstVa);
        var span = image.AsSpan();
        int from = 0;
        while (true)
        {
            int i = span[from..].IndexOf(needle[..ptrSize]);
            if (i < 0) break;
            int at = from + i;
            if (TryReadRecord(image, pe, at, ptrSize, out _)) { tableRef = at; break; }
            from = at + 1;
        }
        if (tableRef < 0) return;

        int start = tableRef;
        while (start - recSize >= 0 && TryReadRecord(image, pe, start - recSize, ptrSize, out _))
            start -= recSize;

        for (int pos = start; TryReadRecord(image, pe, pos, ptrSize, out var rec); pos += recSize)
        {
            byte[] packet;
            if (rec.BaseVa == 0)
            {
                packet = image.AsSpan(rec.DataOffset, rec.Size).ToArray();
            }
            else
            {
                packet = image.AsSpan(rec.BaseOffset, rec.Size).ToArray();
                image.AsSpan(rec.DataOffset, rec.PatchLength).CopyTo(packet.AsSpan(rec.PatchOffset));
            }
            if (AudioCrc.Crc32(packet) == rec.Crc && packet[0] == 5)
                setups.TryAdd(rec.Crc, packet);
        }
    }

    private readonly record struct Record(int DataOffset, int Size, uint Crc, ulong BaseVa, int BaseOffset, int PatchOffset, int PatchLength);

    private static bool TryReadRecord(byte[] image, PeImage pe, int pos, int ptrSize, out Record rec)
    {
        rec = default;
        int recSize = ptrSize * 2 + 16;
        if (pos < 0 || pos + recSize > image.Length) return false;
        var s = image.AsSpan(pos);
        ulong dataVa = ptrSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
        int size = BinaryPrimitives.ReadInt32LittleEndian(s[ptrSize..]);
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(s[(ptrSize + 4)..]);
        ulong baseVa = ptrSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(s[(ptrSize + 8)..]) : BinaryPrimitives.ReadUInt32LittleEndian(s[(ptrSize + 8)..]);
        int patchOffset = BinaryPrimitives.ReadInt32LittleEndian(s[(ptrSize * 2 + 8)..]);
        int patchLength = BinaryPrimitives.ReadInt32LittleEndian(s[(ptrSize * 2 + 12)..]);

        if (size <= 7 || size > 0x40000) return false;
        if (pe.VaToFile(dataVa) is not int dataOffset) return false;
        if (baseVa == 0)
        {
            if (dataOffset + size > image.Length || image[dataOffset] != 5) return false;
            rec = new Record(dataOffset, size, crc, 0, 0, 0, 0);
            return true;
        }
        if (pe.VaToFile(baseVa) is not int baseOffset) return false;
        if (baseOffset + size > image.Length || image[baseOffset] != 5) return false;
        if (patchOffset < 0 || patchLength < 0 || patchOffset + patchLength > size) return false;
        if (dataOffset + patchLength > image.Length) return false;
        rec = new Record(dataOffset, size, crc, baseVa, baseOffset, patchOffset, patchLength);
        return true;
    }

    /// <summary>Just enough of the PE format to map virtual addresses to file offsets.</summary>
    private sealed class PeImage
    {
        private readonly (uint Va, uint Size, uint Raw, uint RawSize)[] _sections;
        private readonly ulong _imageBase;

        public bool Is64 { get; }

        private PeImage(bool is64, ulong imageBase, (uint, uint, uint, uint)[] sections)
        {
            Is64 = is64;
            _imageBase = imageBase;
            _sections = sections;
        }

        public static PeImage? TryOpen(byte[] d)
        {
            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return null;
            int pe = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x3C));
            if (pe <= 0 || pe + 24 + 0x60 > d.Length || BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pe)) != 0x00004550)
                return null;
            int nsec = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pe + 6));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pe + 20));
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pe + 24));
            bool is64 = magic == 0x20B;
            if (!is64 && magic != 0x10B) return null;
            ulong imageBase = is64
                ? BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(pe + 24 + 24))
                : BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pe + 24 + 28));
            var sections = new (uint, uint, uint, uint)[nsec];
            for (int i = 0; i < nsec; i++)
            {
                int o = pe + 24 + optSize + 40 * i;
                if (o + 40 > d.Length) return null;
                var s = d.AsSpan(o);
                sections[i] = (BinaryPrimitives.ReadUInt32LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[20..]), BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
            }
            return new PeImage(is64, imageBase, sections);
        }

        public ulong? FileToVa(int offset)
        {
            foreach (var (va, _, raw, rawSize) in _sections)
                if (offset >= raw && offset < raw + rawSize)
                    return _imageBase + va + (ulong)(offset - raw);
            return null;
        }

        public int? VaToFile(ulong address)
        {
            if (address < _imageBase) return null;
            ulong rva = address - _imageBase;
            foreach (var (va, _, raw, rawSize) in _sections)
                if (rva >= va && rva < (ulong)va + rawSize)
                    return (int)(raw + (rva - va));
            return null;
        }
    }
}
