using System.Buffers.Binary;

namespace TelltaleModding.Core.Meta;

/// <summary>Which container generation a Telltale asset file uses.</summary>
public enum MetaStreamFormat
{
    /// <summary>"5VSM": Meta Stream Version 5.</summary>
    Msv5,

    /// <summary>"6VSM": Meta Stream Version 6 (GoT / Michonne / TTDS era).</summary>
    Msv6,
}

/// <summary>One class-version record from a meta stream header.</summary>
/// <param name="TypeHash">CRC64 of the lowercased type name (<see cref="TelltaleCrc64.HashSymbol"/>).</param>
/// <param name="VersionCrc">CRC of the class's serialized layout; changes when the game changes the struct.</param>
public readonly record struct MetaClassRecord(ulong TypeHash, uint VersionCrc)
{
    /// <summary>Readable type name, or null when the hash isn't in <see cref="KnownTypeNames"/>.</summary>
    public string? TypeName => KnownTypeNames.Resolve(TypeHash);
}

/// <summary>
/// Parsed header of an MSV5/MSV6 meta stream, the container wrapping every Telltale
/// asset (.d3dmesh, .d3dtx, .skl, ...). The header is followed by three sections:
/// "default" (the serialized object tree), "debug" (usually empty), and "async"
/// (bulk payload: vertex/index buffers for meshes, pixel data for textures).
/// </summary>
public sealed class MetaStreamHeader
{
    public MetaStreamFormat Format { get; }
    public IReadOnlyList<MetaClassRecord> Classes { get; }

    /// <summary>Size of the header itself; the default section starts here.</summary>
    public int HeaderSize { get; }

    public int DefaultSectionSize { get; }
    public int DebugSectionSize { get; }
    public int AsyncSectionSize { get; }

    public int DefaultSectionOffset => HeaderSize;
    public int DebugSectionOffset => HeaderSize + DefaultSectionSize;
    public int AsyncSectionOffset => HeaderSize + DefaultSectionSize + DebugSectionSize;

    /// <summary>Header plus all three sections; it must equal the file size.</summary>
    public long TotalSize => (long)HeaderSize + DefaultSectionSize + DebugSectionSize + AsyncSectionSize;

    private MetaStreamHeader(
        MetaStreamFormat format, IReadOnlyList<MetaClassRecord> classes,
        int headerSize, int defaultSize, int debugSize, int asyncSize)
    {
        Format = format;
        Classes = classes;
        HeaderSize = headerSize;
        DefaultSectionSize = defaultSize;
        DebugSectionSize = debugSize;
        AsyncSectionSize = asyncSize;
    }

    private const uint MagicMsv6 = 0x3656534D; // ASCII "6VSM" read big-endian
    private const uint MagicMsv5 = 0x3556534D; // ASCII "5VSM"

    /// <summary>Magics of older Telltale container generations we recognise but do not parse yet.</summary>
    private static readonly Dictionary<string, string> LegacyMagics = new()
    {
        ["ERTM"] = "MTRE (pre-MSV meta stream, ~2006-2014 era)",
        ["MBIN"] = "MBIN (raw meta binary, no section header)",
        ["MBES"] = "MBES (encrypted meta binary)",
        ["NIBM"] = "MBIN (byte-swapped)",
    };

    /// <summary>Human-readable one-line summary, e.g. for a file-info status line.</summary>
    public string Describe()
    {
        var root = Classes.Count > 0
            ? Classes[0].TypeName ?? $"0x{Classes[0].TypeHash:X16}"
            : "empty";
        return $"{Format.ToString().ToUpperInvariant()} · root {root} · {Classes.Count} class versions" +
               $" · data {FormatSize(DefaultSectionSize)} · bulk {FormatSize(AsyncSectionSize)}";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>True if a class with the given name appears in the header's version records.</summary>
    public bool ContainsClass(string typeName)
    {
        ulong hash = TelltaleCrc64.HashSymbol(typeName);
        foreach (var record in Classes)
            if (record.TypeHash == hash)
                return true;
        return false;
    }

    /// <summary>Parses a meta stream header, throwing on malformed or unsupported input.</summary>
    public static MetaStreamHeader Parse(ReadOnlySpan<byte> data)
    {
        if (!TryParse(data, out var header, out var error))
            throw new InvalidDataException(error);
        return header!;
    }

    /// <summary>
    /// Attempts to parse a meta stream header from the start of a file's bytes.
    /// <paramref name="data"/> may be the whole file or just its first few KB, but
    /// section sizes are validated against <paramref name="data"/>'s length when it
    /// covers the whole file (pass <paramref name="isCompleteFile"/> = false otherwise).
    /// </summary>
    public static bool TryParse(
        ReadOnlySpan<byte> data, out MetaStreamHeader? header, out string? error,
        bool isCompleteFile = true)
    {
        header = null;
        error = null;

        if (data.Length < 20)
        {
            error = "File too small to hold a meta stream header.";
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        MetaStreamFormat format;
        switch (magic)
        {
            case MagicMsv6:
                format = MetaStreamFormat.Msv6;
                break;
            case MagicMsv5:
                format = MetaStreamFormat.Msv5;
                break;
            default:
                var magicText = System.Text.Encoding.ASCII.GetString(data[..4]);
                error = LegacyMagics.TryGetValue(magicText, out var known)
                    ? $"Unsupported container: {known}. Only MSV5/MSV6 streams are supported."
                    : $"Not a Telltale meta stream (magic '{magicText}').";
                return false;
        }

        int defaultSize = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int debugSize = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        int asyncSize = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        int classCount = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);

        if (defaultSize < 0 || debugSize < 0 || asyncSize < 0)
        {
            error = "Malformed meta stream header: negative section size.";
            return false;
        }

        // Real files have a handful of records; an absurd count means we misread the file.
        if (classCount is < 0 or > 4096)
        {
            error = $"Malformed meta stream header: implausible class count {classCount}.";
            return false;
        }

        int headerSize = 20 + classCount * 12;
        if (data.Length < headerSize)
        {
            error = $"Truncated meta stream header: need {headerSize} bytes for {classCount} class records.";
            return false;
        }

        var classes = new MetaClassRecord[classCount];
        for (int i = 0; i < classCount; i++)
        {
            int offset = 20 + i * 12;
            classes[i] = new MetaClassRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]),
                BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]));
        }

        var result = new MetaStreamHeader(format, classes, headerSize, defaultSize, debugSize, asyncSize);

        if (isCompleteFile && result.TotalSize != data.Length)
        {
            error = $"Section sizes disagree with file size: header says {result.TotalSize} bytes, file is {data.Length}.";
            return false;
        }

        header = result;
        return true;
    }
}
