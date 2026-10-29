using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using TelltaleModding.Core.Archives;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// Tests for the in-process .ttarch2 reader. The two checked-in samples hold the
/// same 1706 files in different containers (Game of Thrones ECTT with Blowfish +
/// zlib chunks, and test.ttarch2 NCTT, stored with a 3ATT table), so each serves as
/// ground truth for the other. Synthetic archives cover the zCTT/Oodle variants
/// and tiny chunks; tests against installed games skip when the game is absent.
/// </summary>
public class Ttarch2ArchiveTests
{
    private const int GameOfThronesNum = 57;
    private const int TalesFromTheBorderlandsNum = 56;
    private const int DefinitiveSeriesNum = 67;

    internal const string DefinitiveSeriesRoot =
        @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";
    internal const string TalesFromTheBorderlandsArchives =
        @"F:\Steam\steamapps\common\Tales from the Borderlands\archives";

    private static string DefinitiveSeriesArchives => Path.Combine(DefinitiveSeriesRoot, "Archives");

    // --- checked-in samples ---

    [SkippableFact]
    public void ListsEncryptedSample()
    {
        using var archive = Ttarch2Archive.Open(SampleData.GameOfThrones, GameOfThronesNum);

        Assert.Equal(1706, archive.Entries.Count);
        Assert.Equal(Path.GetFullPath(SampleData.GameOfThrones), archive.ArchivePath);
        var skl = Assert.Single(archive.Entries, e => e.Name == "sk62_essosFemale.skl");
        Assert.Equal(36213, skl.Size);
        Assert.All(archive.Entries, e => Assert.Equal(archive.ArchivePath, e.ArchivePath));
    }

    [SkippableFact]
    public void ListsUnencryptedSample()
    {
        using var archive = Ttarch2Archive.Open(SampleData.TestArchive, GameOfThronesNum);

        Assert.Equal(1706, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.Name == "obj_chairLYunkaiTavern.prop");
    }

    [SkippableFact]
    public void EncryptedAndStoredSamplesYieldIdenticalFiles()
    {
        using var ectt = Ttarch2Archive.Open(SampleData.GameOfThrones, GameOfThronesNum);
        using var nctt = Ttarch2Archive.Open(SampleData.TestArchive, GameOfThronesNum);
        var stored = nctt.Entries.ToDictionary(e => e.Name);

        long total = 0;
        foreach (var entry in ectt.Entries)
        {
            var a = ectt.Read(entry);
            var b = nctt.Read(stored[entry.Name]);
            Assert.True(a.AsSpan().SequenceEqual(b), entry.Name);
            total += a.Length;
        }
        // ~27 MB through ~424 chunks of 64 KB: plenty of files straddle chunk edges.
        Assert.True(total > 20_000_000);
    }

    [SkippableFact]
    public void ReadsMetaStreamHeader()
    {
        using var archive = Ttarch2Archive.Open(SampleData.GameOfThrones, GameOfThronesNum);
        var data = archive.Read(archive.Entries.Single(e => e.Name == "sk62_essosFemale.skl"));

        Assert.Equal(36213, data.Length);
        Assert.Equal("5VSM", Encoding.ASCII.GetString(data, 0, 4));
    }

    [SkippableFact]
    public void DecryptsLuaScriptsLikeTtarchext()
    {
        using var archive = Ttarch2Archive.Open(SampleData.TestArchive, GameOfThronesNum);
        var entry = archive.Entries.Single(e => e.Name == "Episode.lua");
        var data = archive.Read(entry);

        // Stored as "\x1bLEn" + Blowfish; ttarchext writes it out as plain Lua bytecode.
        Assert.Equal(entry.Size, data.Length);
        Assert.Equal("\x1bLuaR", Encoding.ASCII.GetString(data, 0, 5));
    }

    [SkippableFact]
    public void ConcurrentReadsMatchSequentialReads()
    {
        using var archive = Ttarch2Archive.Open(SampleData.GameOfThrones, GameOfThronesNum);
        var entries = archive.Entries.Take(400).ToArray();
        var sequential = entries.Select(archive.Read).ToArray();

        using var fresh = Ttarch2Archive.Open(SampleData.GameOfThrones, GameOfThronesNum);
        var parallel = new byte[entries.Length][];
        Parallel.For(0, entries.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => parallel[i] = fresh.Read(fresh.Entries[i]));

        for (int i = 0; i < entries.Length; i++)
            Assert.True(sequential[i].AsSpan().SequenceEqual(parallel[i]), entries[i].Name);
    }

    [SkippableFact]
    public void EncryptedArchiveNeedsAGameWithAKey()
    {
        // Game 0 is a classic .ttarch game: no .ttarch2 key.
        Assert.Throws<ArgumentOutOfRangeException>(() => Ttarch2Archive.Open(SampleData.GameOfThrones, 0));
    }

    [SkippableFact]
    public void EncryptedArchiveWithTheWrongKeyFailsClearly()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => Ttarch2Archive.Open(SampleData.GameOfThrones, DefinitiveSeriesNum));
        Assert.Contains("right game", ex.Message);
    }

    [SkippableFact]
    public void RejectsEntriesFromAnotherArchive()
    {
        using var archive = Ttarch2Archive.Open(SampleData.TestArchive, GameOfThronesNum);
        var foreign = new ArchiveEntryRef(@"C:\elsewhere.ttarch2", archive.Entries[0].Name, archive.Entries[0].Size);

        Assert.Throws<ArgumentException>(() => archive.Read(foreign));
    }

    [SkippableFact]
    public void AcceptsAnEqualEntryRecord()
    {
        using var archive = Ttarch2Archive.Open(SampleData.TestArchive, GameOfThronesNum);
        var e = archive.Entries[5];

        Assert.Equal(archive.Read(e), archive.Read(new ArchiveEntryRef(e.ArchivePath, e.Name, e.Size)));
    }

    [SkippableFact]
    public void SniffsMagic()
    {
        Assert.True(Ttarch2Archive.LooksLikeTtarch2(SampleData.GameOfThrones));
        Assert.True(Ttarch2Archive.LooksLikeTtarch2(SampleData.TestArchive));
        Assert.False(Ttarch2Archive.LooksLikeTtarch2(SampleData.MeshSample));
        Assert.False(Ttarch2Archive.LooksLikeTtarch2(Path.Combine(SampleData.SamplesDir, "missing.ttarch2")));

        using var lower = new TempFile(Encoding.ASCII.GetBytes("zCTT\x01\0\0\0"));
        Assert.True(Ttarch2Archive.LooksLikeTtarch2(lower.Path));
    }

    [SkippableFact]
    public void RejectsNonArchives()
    {
        Assert.Throws<InvalidDataException>(() => Ttarch2Archive.Open(SampleData.MeshSample, GameOfThronesNum));
    }

    // --- synthetic containers ---

    [SkippableTheory]
    [InlineData("ZCTT", false)]
    [InlineData("zCTT", false)]
    [InlineData("ZCTT", true)]
    [InlineData("NCTT", false)]
    [InlineData("4ATT", false)]
    public void ReadsSyntheticZlibArchives(string container, bool rawDeflate)
    {
        var files = SyntheticFiles();
        var bytes = SyntheticArchive.Build(files, container, compressionType: 0, chunkSize: 256,
            data => rawDeflate ? Deflate(data) : ZlibCompress(data));
        using var file = new TempFile(bytes);
        using var archive = Ttarch2Archive.Open(file.Path, gameNum: 0);

        AssertMatches(files, archive);
    }

    [InstalledGameFact(DefinitiveSeriesRoot)]
    public void ReadsSyntheticOodleArchive()
    {
        // The game's Oodle DLL both builds the fixture and decodes it.
        var dll = Path.Combine(DefinitiveSeriesRoot, "oo2core_5_win64.dll");
        using var oodle = new OodleCompressor(dll);
        var files = SyntheticFiles();
        var bytes = SyntheticArchive.Build(files, "zCTT", compressionType: 1, chunkSize: 256, oodle.Compress);
        using var file = new TempFile(bytes);
        using var archive = Ttarch2Archive.Open(file.Path, gameNum: 0, oodleSearchDir: DefinitiveSeriesRoot);

        AssertMatches(files, archive);
    }

    [SkippableFact]
    public void OodleArchiveWithoutTheDllFailsClearly()
    {
        var bytes = SyntheticArchive.Build(SyntheticFiles(), "zCTT", compressionType: 1, chunkSize: 256, d => d);
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "oodle.ttarch2");
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<FileNotFoundException>(() => Ttarch2Archive.Open(path, 0, oodleSearchDir: dir.Path));
        Assert.Contains("oo2core", ex.Message);
    }

    // --- installed games (skipped when absent) ---

    [InstalledGameFact(DefinitiveSeriesRoot)]
    public void ReadsDefinitiveSeriesAnimationArchive()
    {
        using var archive = Ttarch2Archive.Open(
            Path.Combine(DefinitiveSeriesArchives, "WDC_pc_WalkingDead401_anichore.ttarch2"), DefinitiveSeriesNum, DefinitiveSeriesRoot);

        Assert.Equal(13041, archive.Entries.Count);
        var anm = archive.Entries.First(e => e.Name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(anm.Size, archive.Read(anm).Length);
    }

    [InstalledGameFact(DefinitiveSeriesRoot)]
    public void DefinitiveSeriesMeshMatchesCheckedInSample()
    {
        // samples/sk62_clementine400_head.d3dmesh was extracted from this archive by ttarchext.
        if (!File.Exists(Path.Combine(SampleData.SamplesDir, "sk62_clementine400_head.d3dmesh")))
            return;
        using var archive = Ttarch2Archive.Open(
            Path.Combine(DefinitiveSeriesArchives, "WDC_pc_WalkingDead401_txmesh.ttarch2"), DefinitiveSeriesNum, DefinitiveSeriesRoot);
        var entry = archive.Entries.Single(e => e.Name == "sk62_clementine400_head.d3dmesh");

        Assert.Equal(File.ReadAllBytes(SampleData.Sample("sk62_clementine400_head.d3dmesh")), archive.Read(entry));
    }

    [InstalledGameFact(TalesFromTheBorderlandsArchives)]
    public void ReadsTalesFromTheBorderlandsArchive()
    {
        using var archive = Ttarch2Archive.Open(
            Path.Combine(TalesFromTheBorderlandsArchives, "Borderlands_pc_Boot_txmesh.ttarch2"), TalesFromTheBorderlandsNum);

        Assert.Equal(161, archive.Entries.Count);
        long total = archive.Entries.Sum(e => (long)archive.Read(e).Length);
        Assert.Equal(archive.Entries.Sum(e => e.Size), total);
    }

    // --- helpers ---

    private static void AssertMatches(IReadOnlyList<(string Name, byte[] Data)> files, Ttarch2Archive archive)
    {
        Assert.Equal(files.Select(f => f.Name), archive.Entries.Select(e => e.Name));
        for (int i = 0; i < files.Count; i++)
        {
            Assert.Equal(files[i].Data.Length, archive.Entries[i].Size);
            Assert.Equal(files[i].Data, archive.Read(archive.Entries[i]));
        }
    }

    /// <summary>Files sized to start, end and straddle 256-byte chunks in every way.</summary>
    private static List<(string Name, byte[] Data)> SyntheticFiles()
    {
        var rng = new Random(1234);
        var files = new List<(string, byte[])>();
        foreach (var size in new[] { 10, 0, 300, 1, 2000, 255, 256, 257, 700 })
        {
            var data = new byte[size];
            // Mostly compressible text, with one incompressible file so some full
            // chunks are stored raw.
            if (size == 2000) rng.NextBytes(data);
            else for (int i = 0; i < size; i++) data[i] = (byte)"telltale "[(i + size) % 9];
            files.Add(($"file_{files.Count}_{size}.bin", data));
        }
        return files;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>Writes small .ttarch2 archives the way Telltale's tools lay them out.</summary>
    private static class SyntheticArchive
    {
        public static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files, string container,
            int compressionType, int chunkSize, Func<byte[], byte[]> compress)
        {
            // Inner stream: 4ATT, names size, count, 28-byte records, names, data.
            var names = new MemoryStream();
            var data = new MemoryStream();
            var records = new MemoryStream();
            foreach (var (name, bytes) in files)
            {
                Span<byte> rec = new byte[28];
                BinaryPrimitives.WriteUInt64LittleEndian(rec, (ulong)name.GetHashCode());
                BinaryPrimitives.WriteUInt64LittleEndian(rec[8..], (ulong)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(rec[16..], (uint)bytes.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(rec[24..], 0);
                BinaryPrimitives.WriteUInt16LittleEndian(rec[26..], (ushort)names.Length);
                records.Write(rec);
                names.Write(Encoding.ASCII.GetBytes(name + "\0"));
                data.Write(bytes);
            }
            var inner = new MemoryStream();
            var w = new BinaryWriter(inner);
            w.Write(Encoding.ASCII.GetBytes("4ATT"));
            w.Write((uint)names.Length);
            w.Write((uint)files.Count);
            w.Write(records.ToArray());
            w.Write(names.ToArray());
            w.Write(data.ToArray());
            var stream = inner.ToArray();

            if (container == "4ATT")
                return stream;

            var outer = new MemoryStream();
            var o = new BinaryWriter(outer);
            o.Write(Encoding.ASCII.GetBytes(container));
            if (char.IsLower(container[0]))
                o.Write((uint)compressionType);
            if (container == "NCTT")
            {
                o.Write((ulong)stream.Length);
                o.Write(stream);
                return outer.ToArray();
            }

            // Chunked: compress each chunk; a full chunk that does not shrink is
            // stored raw (a chunk exactly chunkSize long is taken as uncompressed).
            var chunks = new List<byte[]>();
            for (int at = 0; at < stream.Length; at += chunkSize)
            {
                var plain = stream.AsSpan(at, Math.Min(chunkSize, stream.Length - at)).ToArray();
                var packed = compress(plain);
                chunks.Add(packed.Length >= chunkSize && plain.Length == chunkSize ? plain : packed);
            }
            o.Write((uint)chunkSize);
            o.Write((uint)chunks.Count);
            long offset = outer.Length + 8L * (chunks.Count + 1);
            o.Write((ulong)offset);
            foreach (var c in chunks)
                o.Write((ulong)(offset += c.Length));
            foreach (var c in chunks)
                o.Write(c);
            return outer.ToArray();
        }
    }

    /// <summary>Minimal binding to OodleLZ_Compress, only for building fixtures.</summary>
    private sealed class OodleCompressor : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate nint CompressFn(int compressor, [In] byte[] raw, nint rawLen, [Out] byte[] comp, int level,
            nint options, nint dictionaryBase, nint lrm, nint scratch, nint scratchSize);

        private const int Kraken = 8;
        private const int LevelNormal = 4;

        private readonly IntPtr _lib;
        private readonly CompressFn _compress;

        public OodleCompressor(string dll)
        {
            _lib = NativeLibrary.Load(dll);
            _compress = Marshal.GetDelegateForFunctionPointer<CompressFn>(NativeLibrary.GetExport(_lib, "OodleLZ_Compress"));
        }

        public byte[] Compress(byte[] raw)
        {
            var comp = new byte[raw.Length + 1024];
            int n = (int)_compress(Kraken, raw, raw.Length, comp, LevelNormal, 0, 0, 0, 0, 0);
            Assert.True(n > 0, "OodleLZ_Compress failed");
            return comp[..n];
        }

        // The decompressor shares the module, so it is deliberately left loaded.
        public void Dispose() { }
    }
}

/// <summary>A fact that runs only when an installed game's folder exists on this machine.</summary>
public sealed class InstalledGameFactAttribute : FactAttribute
{
    public InstalledGameFactAttribute(string requiredPath)
    {
        if (!Directory.Exists(requiredPath))
            Skip = $"Game not installed at '{requiredPath}'.";
    }
}
