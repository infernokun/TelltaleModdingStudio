using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Dialogue;

/// <summary>One localized line from a <c>.landb</c> language database.</summary>
/// <param name="Id">The line's resource id; its voice file is <c>&lt;Id&gt;.wav</c>.</param>
/// <param name="Speaker">The speaker's name as the game prints it (may be empty).</param>
/// <param name="RawText">The text with the game's inline direction tags.</param>
/// <param name="LinkedSymbols">
/// Name symbols of the voice and lip-sync files a line borrows from another line;
/// empty when it uses its own <c>&lt;Id&gt;.wav</c>.
/// </param>
public sealed record DialogueLine(uint Id, string Speaker, string RawText, IReadOnlyList<ulong> LinkedSymbols)
{
    /// <summary>The voice file name the game plays for this line.</summary>
    public string AudioFileName => $"{Id}.wav";

    /// <summary>The lip-sync animation the game plays with this line.</summary>
    public string LipSyncFileName => $"{Id}.anm";

    /// <summary>The spoken text without direction tags (<c>[v-o]</c>, <c>{ThinkB}</c>, …).</summary>
    public string Text => LandbFile.StripTags(RawText);

    /// <summary>The direction tags (<c>v-o</c>, <c>confused</c>, …) in the order written.</summary>
    public IReadOnlyList<string> Directions => LandbFile.Directions(RawText);
}

/// <summary>
/// Reads the lines of a <c>.landb</c> language database (MSV5/MSV6). Each record holds
/// the line id twice around a name symbol, a few empty blocks, then a block with the
/// speaker and text strings; the fields after that differ between games, so records
/// are located by that id pattern rather than by a fixed size.
/// </summary>
public static partial class LandbFile
{
    /// <summary>Parses the lines of a <c>.landb</c> file; returns false with a reason for anything else.</summary>
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
            result.Add(new DialogueLine(id, speaker, text, LinkedSymbols(span, pos + 16)));
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

    // After the ids: a u32, then two 12-byte handle blocks (size + symbol) that are
    // zero unless the line plays another line's files.
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

    // From just after a record's ids, finds the speaker block (size, length, chars)
    // followed directly by the text block; both sizes include their 8-byte prefix.
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

    // Most databases are UTF-8; older ones store Windows-1252 (©, curly quotes).
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

    /// <summary>Removes <c>[…]</c> and <c>{…}</c> direction tags and tidies the spacing.</summary>
    public static string StripTags(string raw) =>
        Whitespace().Replace(Tags().Replace(raw, " "), " ").Trim();

    /// <summary>The contents of the <c>[…]</c> and <c>{…}</c> tags in <paramref name="raw"/>.</summary>
    public static IReadOnlyList<string> Directions(string raw) =>
        Tags().Matches(raw).Select(m => m.Value[1..^1].Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>
    /// Splits a <c>.landb</c> file name into its scene and language
    /// (<c>env_heliosoffice_english.landb</c> → <c>env_heliosoffice</c>, <c>english</c>).
    /// Files without a known language suffix are treated as English.
    /// </summary>
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
