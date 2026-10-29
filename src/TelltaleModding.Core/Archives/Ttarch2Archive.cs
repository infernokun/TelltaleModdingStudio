using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TelltaleModding.Core.Archives;

/// <summary>
/// In-process random access to a .ttarch2 archive, a port of the reading path of
/// ttarchext's <c>ttarch_extract</c>, <c>ttarch_fseek</c> and <c>ttarch_fread</c>.
/// </summary>
/// <remarks>
/// <para>
/// A .ttarch2 is an outer container wrapped around an inner "ATT" file table. The
/// container is one of: <c>ZCTT</c> (data split into fixed-size chunks, each
/// compressed on its own), <c>ECTT</c> (the same, with every chunk also
/// Blowfish-encrypted using the per-game key), <c>NCTT</c> (stored as-is), or no
/// container at all (the file starts straight at the table). A lowercase first
/// letter (<c>zCTT</c>/<c>eCTT</c>) adds a u32 compression type: 0 zlib, 1 Oodle.
/// </para>
/// <para>
/// Inside, the uncompressed stream starts with <c>3ATT</c> or <c>4ATT</c>, the names
/// block size and the file count, then one 28-byte record per file (name hash u64,
/// offset u64, size u32, unknown u32, name block u16, name offset u16), then the
/// names (null-terminated, addressed in 64 KB blocks), then the file data. File
/// offsets are relative to the end of the names block.
/// </para>
/// <para>
/// <see cref="Open"/> reads only the header and the chunks the table spans;
/// <see cref="Read"/> decompresses only the chunks one file spans, keeping a few
/// recently used chunks so neighbouring small files share the work.
/// </para>
/// <para>Classic .ttarch archives are not handled here.</para>
/// </remarks>
public sealed class Ttarch2Archive : IArchiveSource
{
    private const int EntryRecordSize = 8 + 8 + 4 + 4 + 2 + 2;
    private const int NameBlockSize = 0x10000;
    private const int CacheCapacity = 8;

    // Reads spanning at least this many chunks decompress them in parallel.
    private const int ParallelChunkThreshold = 4;

    private readonly SafeFileHandle _file;
    private readonly long _fileLength;

    // Chunked containers: file offset of each chunk, plus one past the last.
    // Null for NCTT and bare tables, whose stream starts at _dataStart.
    private readonly long[]? _chunkStarts;
    private readonly long _dataStart;
    private readonly int _chunkSize;
    private readonly TtarchBlowfish? _cipher;       // chunk encryption (ECTT only)
    private readonly TtarchBlowfish? _gameCipher;   // the game's key, for Lua and legacy meta files
    private readonly OodleDecompressor? _oodle;

    private readonly Dictionary<ArchiveEntryRef, long> _offsets;

    // End of the last file in the stream, known once the table is read; Oodle
    // needs it to size a short final chunk.
    private readonly long? _streamEnd;
    private readonly ChunkCache _cache = new(CacheCapacity);

    /// <inheritdoc />
    public string ArchivePath { get; }

    /// <inheritdoc />
    public IReadOnlyList<ArchiveEntryRef> Entries { get; }

    private Ttarch2Archive(string path, SafeFileHandle file, int gameNum, string? oodleSearchDir)
    {
        ArchivePath = path;
        _file = file;
        _gameCipher = Ttarch2GameKeys.TryGetCipher(gameNum);
        _fileLength = RandomAccess.GetLength(file);

        // --- outer container ---
        Span<byte> head = stackalloc byte[16];
        ReadExactly(0, head[..4]);
        bool hasCompressionType = head[0] is >= (byte)'a' and <= (byte)'z';
        string tag = Encoding.ASCII.GetString(head[..4]).ToUpperInvariant();
        long pos = 4;
        bool oodle = false;
        if (hasCompressionType)
        {
            ReadExactly(pos, head[..4]);
            oodle = BinaryPrimitives.ReadUInt32LittleEndian(head) == 1;   // anything else: zlib, as ttarchext
            pos += 4;
        }

        switch (tag)
        {
            case "ECTT":
            case "ZCTT":
                ReadExactly(pos, head[..8]);
                _chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(head);
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
                pos += 8;
                if (_chunkSize <= 0 || count > (_fileLength - pos) / 8)
                    throw Corrupt($"implausible chunk table ({count} chunks of {_chunkSize} bytes)");
                _chunkStarts = ReadChunkTable(pos, (int)count);

                if (tag == "ECTT")
                    _cipher = _gameCipher ?? throw new ArgumentOutOfRangeException(
                        nameof(gameNum), gameNum,
                        $"'{Path.GetFileName(path)}' is encrypted, but game {gameNum} has no .ttarch2 key.");
                if (oodle)
                    _oodle = OodleDecompressor.Locate(oodleSearchDir, path);
                break;

            case "NCTT":
                _dataStart = pos + 8;   // after the u64 data size
                break;

            case "3ATT":
            case "4ATT":
                _dataStart = 0;
                break;

            default:
                throw new InvalidDataException(
                    $"'{Path.GetFileName(path)}' is not a .ttarch2 archive (header '{Printable(head[..4])}').");
        }

        // --- inner file table ---
        try
        {
            (Entries, _offsets, _streamEnd) = ReadTable();
        }
        catch (InvalidDataException ex) when (_cipher is not null)
        {
            throw new InvalidDataException(
                $"{ex.Message} The archive is encrypted: check that game {gameNum} is the right game.", ex);
        }
    }

    /// <summary>
    /// Opens a .ttarch2 archive and reads its table of contents.
    /// </summary>
    /// <param name="path">The archive file.</param>
    /// <param name="gameNum">
    /// ttarchext game index (see <c>Data/games.json</c>), used to pick the Blowfish
    /// key. Encrypted (ECTT) archives cannot be opened without it; in other
    /// archives it is only used to decrypt Lua scripts.
    /// </param>
    /// <param name="oodleSearchDir">
    /// Where to look for the game's <c>oo2core_*_win64.dll</c>, normally the install
    /// root; its parent and subfolders are searched too, as is the archive's own
    /// folder and its parent. Only Oodle-compressed archives need it.
    /// </param>
    /// <exception cref="InvalidDataException">The file is not a readable .ttarch2.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The archive is encrypted and the game has no key.</exception>
    /// <exception cref="FileNotFoundException">The archive, or the Oodle DLL it needs, is missing.</exception>
    public static Ttarch2Archive Open(string path, int gameNum, string? oodleSearchDir = null)
    {
        path = Path.GetFullPath(path);
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            return new Ttarch2Archive(path, file, gameNum, oodleSearchDir);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when the file starts with one of the .ttarch2 tags (ZCTT, ECTT, NCTT,
    /// 3ATT, 4ATT, in either case of the first letter). Reads four bytes; never throws.
    /// </summary>
    public static bool LooksLikeTtarch2(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[4];
            if (RandomAccess.Read(file, head, 0) < 4)
                return false;
            head[0] = (byte)char.ToUpperInvariant((char)head[0]);
            return Encoding.ASCII.GetString(head) is "ZCTT" or "ECTT" or "NCTT" or "3ATT" or "4ATT";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one file, returning the same bytes ttarchext extracts: encrypted Lua
    /// scripts (<c>.lua</c>/<c>.lenc</c>) come back decrypted and legacy
    /// block-encrypted meta streams deciphered; all other files are returned as
    /// stored. A <c>\x1bLEo</c> Lua script is 4 bytes shorter than
    /// <see cref="ArchiveEntryRef.Size"/> because its tag is dropped. Safe to call
    /// from several threads at once.
    /// </summary>
    /// <exception cref="ArgumentException">The entry is not from this archive.</exception>
    public byte[] Read(ArchiveEntryRef entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_offsets.TryGetValue(entry, out long offset))
            throw new ArgumentException($"'{entry.Name}' is not an entry of '{ArchivePath}'.", nameof(entry));
        if (entry.Size > Array.MaxLength)
            throw new NotSupportedException($"'{entry.Name}' is too large ({entry.Size} bytes) to read into memory.");

        var data = new byte[entry.Size];
        ReadStream(offset, data);
        return Ttarch2FileDecryption.Apply(entry.Name, data, _gameCipher);
    }

    /// <summary>Closes the archive file.</summary>
    public void Dispose() => _file.Dispose();

    /// <summary>Parses the ATT table at the start of the (uncompressed) stream.</summary>
    private (IReadOnlyList<ArchiveEntryRef>, Dictionary<ArchiveEntryRef, long>, long? StreamEnd) ReadTable()
    {
        var head = new byte[16];
        ReadStream(0, head.AsSpan(0, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(head);
        long pos = 4;
        if (version == 0x54544133)   // "3ATT" carries one extra u32
        {
            pos += 4;
        }
        else if (version != 0x54544134)   // "4ATT"
        {
            throw Corrupt($"unsupported file table '{Printable(head.AsSpan(0, 4))}'");
        }

        ReadStream(pos, head.AsSpan(0, 8));
        uint namesSize = BinaryPrimitives.ReadUInt32LittleEndian(head);
        uint files = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        pos += 8;

        long tableSize = (long)files * EntryRecordSize + namesSize;
        if (tableSize > Array.MaxLength || tableSize > StreamLengthUpperBound())
            throw Corrupt($"implausible file table ({files} files, {namesSize} bytes of names)");

        var table = new byte[tableSize];
        ReadStream(pos, table);
        var names = table.AsSpan((int)(files * EntryRecordSize));
        long baseOffset = pos + tableSize;

        var entries = new ArchiveEntryRef[files];
        var offsets = new Dictionary<ArchiveEntryRef, long>((int)files);
        long? streamEnd = null;
        for (int i = 0; i < files; i++)
        {
            var rec = table.AsSpan(i * EntryRecordSize, EntryRecordSize);
            long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(rec[16..]);
            long nameAt = (long)BinaryPrimitives.ReadUInt16LittleEndian(rec[24..]) * NameBlockSize
                        + BinaryPrimitives.ReadUInt16LittleEndian(rec[26..]);
            if (offset < 0 || nameAt >= names.Length)
                throw Corrupt($"file record {i} points outside the archive");

            var rest = names[(int)nameAt..];
            int end = rest.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);

            entries[i] = new ArchiveEntryRef(ArchivePath, name, size);
            offsets.TryAdd(entries[i], baseOffset + offset);
            streamEnd = Math.Max(streamEnd ?? 0, baseOffset + offset + size);
        }
        return (entries, offsets, streamEnd);
    }

    /// <summary>
    /// Fills <paramref name="dest"/> from the uncompressed stream starting at
    /// <paramref name="offset"/>. Chunk <c>i</c> holds stream bytes
    /// <c>[i * chunkSize, (i + 1) * chunkSize)</c>, as in ttarchext's
    /// <c>ttarch_fseek</c>; a short chunk is simply followed by the next one.
    /// </summary>
    private void ReadStream(long offset, Span<byte> dest)
    {
        if (dest.IsEmpty)
            return;

        if (_chunkStarts is null)
        {
            ReadExactly(_dataStart + offset, dest);
            return;
        }

        int chunkCount = _chunkStarts.Length - 1;
        long index = offset / _chunkSize;
        int skip = (int)(offset % _chunkSize);

        // Big reads: decompress every chunk the range covers up front, in parallel.
        long last = Math.Min((offset + dest.Length - 1) / _chunkSize, chunkCount - 1);
        Chunk[]? prefetched = null;
        if (last - index + 1 >= ParallelChunkThreshold)
        {
            long first = index;
            prefetched = new Chunk[last - first + 1];
            try
            {
                Parallel.For(0, prefetched.Length, k =>
                    prefetched[k] = GetChunk((int)(first + k), cache: k == 0 || k == prefetched.Length - 1));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
            }
        }

        int written = 0;
        long prefetchBase = index;
        while (written < dest.Length)
        {
            if (index >= chunkCount)
                throw Corrupt($"a read at stream offset {offset} runs past the last chunk");

            long k = index - prefetchBase;
            var chunk = prefetched is not null && k < prefetched.Length
                ? prefetched[k]
                : GetChunk((int)index, cache: true);

            if (skip >= chunk.Length)
            {
                skip -= chunk.Length;
                index++;
                continue;
            }

            int n = Math.Min(chunk.Length - skip, dest.Length - written);
            chunk.Data.AsSpan(skip, n).CopyTo(dest[written..]);
            written += n;
            skip = 0;
            index++;
        }
    }

    /// <summary>Returns chunk <paramref name="index"/> decrypted and decompressed.</summary>
    private Chunk GetChunk(int index, bool cache)
    {
        if (_cache.TryGet(index, out var hit))
            return hit;

        long start = _chunkStarts![index];
        long size = _chunkStarts[index + 1] - start;
        if (size <= 0 || size > Array.MaxLength || start + size > _fileLength)
            throw Corrupt($"chunk {index} has an invalid extent");

        var raw = new byte[size];
        ReadExactly(start, raw);
        _cipher?.Decrypt(raw);

        // A chunk exactly one chunk long was stored uncompressed (ttarchext's rule).
        Chunk chunk;
        if (size == _chunkSize)
        {
            chunk = new Chunk(raw, raw.Length);
        }
        else
        {
            var output = new byte[_chunkSize];
            int length;
            try
            {
                length = _oodle is not null ? DecodeOodle(index, raw, output) : Inflate(raw, output);
            }
            catch (InvalidDataException)
            {
                length = 0;
            }
            if (length <= 0)
                throw Corrupt($"chunk {index} did not decompress");
            chunk = new Chunk(output, length);
        }

        if (cache)
            _cache.Add(index, chunk);
        return chunk;
    }

    /// <summary>
    /// Oodle-decompresses one chunk. Oodle must be told the exact decoded size:
    /// ttarchext always passes the chunk size, which a short final chunk rejects,
    /// so that chunk is retried with the size implied by the file table.
    /// </summary>
    private int DecodeOodle(int index, byte[] input, byte[] output)
    {
        int length = _oodle!.Decompress(input, input.Length, output, _chunkSize);
        if (length <= 0 && index == _chunkStarts!.Length - 2 && _streamEnd is long end)
        {
            long remainder = end - (long)index * _chunkSize;
            if (remainder is > 0 and < int.MaxValue && remainder < _chunkSize)
                length = _oodle.Decompress(input, input.Length, output, (int)remainder);
        }
        return length;
    }

    /// <summary>
    /// zlib-decompresses one chunk. Like ttarchext this accepts zlib-wrapped data
    /// and falls back to raw deflate when the zlib header is absent or wrong.
    /// </summary>
    private static int Inflate(byte[] input, byte[] output)
    {
        bool zlibHeader = input.Length >= 2 && (input[0] & 0x0F) == 8 && (input[0] << 8 | input[1]) % 31 == 0;
        if (zlibHeader)
        {
            try
            {
                return Drain(new ZLibStream(new MemoryStream(input), CompressionMode.Decompress), output);
            }
            catch (InvalidDataException)
            {
                // Fall through: some data merely looks like a zlib header.
            }
        }
        return Drain(new DeflateStream(new MemoryStream(input), CompressionMode.Decompress), output);

        static int Drain(Stream stream, byte[] output)
        {
            using (stream)
            {
                int total = 0, n;
                while (total < output.Length && (n = stream.Read(output, total, output.Length - total)) > 0)
                    total += n;
                return total;
            }
        }
    }

    /// <summary>Reads the chunk offset table and rebases it the way ttarchext does.</summary>
    private long[] ReadChunkTable(long pos, int count)
    {
        var table = new byte[(count + 1) * 8L];
        ReadExactly(pos, table);
        long dataStart = pos + table.Length;

        // ttarchext ignores the absolute value of the first offset and places chunk
        // 0 right after the table; later chunks follow by the offset deltas.
        var starts = new long[count + 1];
        long first = (long)BinaryPrimitives.ReadUInt64LittleEndian(table);
        for (int i = 0; i <= count; i++)
            starts[i] = dataStart + ((long)BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(i * 8)) - first);
        return starts;
    }

    // Generous ceiling on the uncompressed stream size, to reject garbage tables early.
    private long StreamLengthUpperBound() =>
        _chunkStarts is null ? _fileLength : (long)(_chunkStarts.Length - 1) * _chunkSize;

    private void ReadExactly(long offset, Span<byte> dest)
    {
        while (!dest.IsEmpty)
        {
            int n = RandomAccess.Read(_file, dest, offset);
            if (n <= 0)
                throw Corrupt($"unexpected end of file at offset {offset}");
            dest = dest[n..];
            offset += n;
        }
    }

    private InvalidDataException Corrupt(string what) =>
        new($"'{Path.GetFileName(ArchivePath)}' is damaged or unsupported: {what}.");

    private static string Printable(ReadOnlySpan<byte> tag)
    {
        var sb = new StringBuilder(tag.Length);
        foreach (var b in tag)
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return sb.ToString();
    }

    /// <summary>One decompressed chunk; only the first <see cref="Length"/> bytes of <see cref="Data"/> are valid.</summary>
    private readonly record struct Chunk(byte[] Data, int Length);

    /// <summary>A tiny thread-safe LRU of decompressed chunks, keyed by chunk index.</summary>
    private sealed class ChunkCache
    {
        private readonly int _capacity;
        private readonly LinkedList<(int Index, Chunk Chunk)> _order = new();
        private readonly Dictionary<int, LinkedListNode<(int Index, Chunk Chunk)>> _nodes = new();

        public ChunkCache(int capacity) => _capacity = capacity;

        public bool TryGet(int index, out Chunk chunk)
        {
            lock (_order)
            {
                if (_nodes.TryGetValue(index, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    chunk = node.Value.Chunk;
                    return true;
                }
            }
            chunk = default;
            return false;
        }

        public void Add(int index, Chunk chunk)
        {
            lock (_order)
            {
                if (_nodes.ContainsKey(index))
                    return;
                _nodes[index] = _order.AddFirst((index, chunk));
                if (_order.Count > _capacity)
                {
                    _nodes.Remove(_order.Last!.Value.Index);
                    _order.RemoveLast();
                }
            }
        }
    }
}
