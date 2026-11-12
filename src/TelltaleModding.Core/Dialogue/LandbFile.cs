using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Dialogue;

/// <summary>One localized line from a <c>.landb</c> language database.</summary>
/// <param name="Id">The line's id. Its voice file is <c>&lt;Id&gt;.wav</c>.</param>
/// <param name="Speaker">The speaker's name, possibly empty.</param>
/// <param name="RawText">The text with its direction tags.</param>
/// <param name="LinkedSymbols">Symbols of voice and lip-sync files borrowed from another line, if any.</param>
public sealed record DialogueLine(uint Id, string Speaker, string RawText, IReadOnlyList<ulong> LinkedSymbols)
{
    /// <summary>The voice file name the game plays for this line.</summary>
    public string AudioFileName => $"{Id}.wav";

    /// <summary>The lip-sync animation the game plays with this line.</summary>
    public string LipSyncFileName => $"{Id}.anm";

    /// <summary>The spoken text without direction tags.</summary>
    public string Text => LandbFile.StripTags(RawText);

    /// <summary>The direction tags in the order written.</summary>
    public IReadOnlyList<string> Directions => LandbFile.Directions(RawText);

    /// <summary>The line's name symbol, which chores use to place it on their timeline.</summary>
    public ulong NameSymbol { get; init; }
}

/// <summary>
/// Reads the lines of a <c>.landb</c> language database. Record layouts differ between
/// games, so records are found by their repeated id instead of a fixed size.
/// </summary>
public static partial class LandbFile
{
    /// <summary>Parses the lines of a <c>.landb</c> file. Returns false with a reason if it can't.</summary>
    public static bool TryRead(byte[] data, out IReadOnlyList<DialogueLine> lines, out string? error)
    {
        lines = Array.Empty<DialogueLine>();
        if (!MetaStreamHeader.TryParse(data, out var header, out error))
            return false;

        var span = data.AsSpan();
        int start = header!.DefaultSectionOffset;
        int end = (int)Math.Min(data.Length, (long)start + header.DefaultSectionSize);
        var result = new List<DialogueLine>();

        // Records start with id, an 8-byte symbol, then the id again.
        int pos = start;
        while (pos + 16 <= end)
        {
            uint id = U32(span, pos);
            if (id == 0 || U32(span, pos + 12) != id || !TryReadStrings(span, pos + 16, end, out var speaker, out var text, out int next))
            {
                pos++;
                continue;
            }
            result.Add(new DialogueLine(id, speaker, text, LinkedSymbols(span, pos + 16))
            {
                NameSymbol = BinaryPrimitives.ReadUInt64LittleEndian(span[(pos + 4)..]),
            });
            pos = next;
        }

        if (result.Count == 0)
        {
            error = "No dialogue lines found.";
            return false;
        }
        lines = result;
        return true;
    }

    // After the ids: a u32, then two 12-byte handle blocks that are zero unless the
    // line plays another line's files.
    private static IReadOnlyList<ulong> LinkedSymbols(ReadOnlySpan<byte> span, int at)
    {
        List<ulong>? symbols = null;
        for (int block = at + 4; block <= at + 16 && block + 12 <= span.Length; block += 12)
        {
            if (U32(span, block) != 12)
                break;
            ulong symbol = BinaryPrimitives.ReadUInt64LittleEndian(span[(block + 4)..]);
            if (symbol != 0)
                (symbols ??= new List<ulong>()).Add(symbol);
        }
        return symbols ?? (IReadOnlyList<ulong>)Array.Empty<ulong>();
    }

    // Finds the speaker string block followed right away by the text block.
    // Block sizes include their 8-byte prefix.
    private static bool TryReadStrings(ReadOnlySpan<byte> span, int from, int end,
        out string speaker, out string text, out int next)
    {
        speaker = text = "";
        next = from;
        int limit = Math.Min(end - 16, from + 96);
        for (int q = from; q <= limit; q += 4)
        {
            if (!TryString(span, q, end, out speaker, out int afterSpeaker))
                continue;
            if (!TryString(span, afterSpeaker, end, out text, out int afterText))
                continue;
            next = afterText;
            return true;
        }
        return false;
    }

    private static bool TryString(ReadOnlySpan<byte> span, int at, int end, out string value, out int after)
    {
        value = "";
        after = at;
        if (at + 8 > end)
            return false;
        uint block = U32(span, at);
        uint length = U32(span, at + 4);
        if (block != length + 8 || at + block > end || length > 64 * 1024)
            return false;
        var bytes = span.Slice(at + 8, (int)length);
        foreach (var b in bytes)
            if (b < 0x20 && b != '\n' && b != '\r' && b != '\t')
                return false;
        value = Decode(bytes);
        after = at + (int)block;
        return true;
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding> Windows1252 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    // Most databases are UTF-8, older ones use Windows-1252.
    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Windows1252.Value.GetString(bytes);
        }
    }

    private static uint U32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);

    /// <summary>Removes <c>[...]</c> and <c>{...}</c> direction tags and cleans up spacing.</summary>
    public static string StripTags(string raw) =>
        Whitespace().Replace(Tags().Replace(raw, " "), " ").Trim();

    /// <summary>The contents of the direction tags in <paramref name="raw"/>.</summary>
    public static IReadOnlyList<string> Directions(string raw) =>
        Tags().Matches(raw).Select(m => m.Value[1..^1].Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>Splits a <c>.landb</c> file name into scene and language. No known suffix means English.</summary>
    public static (string Scene, string Language) SplitName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var language in KnownLanguages)
        {
            if (stem.EndsWith("_" + language, StringComparison.OrdinalIgnoreCase))
                return (stem[..^(language.Length + 1)], language);
        }
        return (stem, "english");
    }

    // Longest first so "traditional_chinese" wins over "chinese".
    private static readonly string[] KnownLanguages =
    {
        "traditional_chinese", "brazilian_portuguese", "latin_spanish", "simplified_chinese",
        "english", "french", "german", "italian", "spanish", "portuguese", "russian", "arabic",
        "chinese", "japanese", "korean", "polish", "dutch", "turkish", "czech", "brazilian",
    };

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
