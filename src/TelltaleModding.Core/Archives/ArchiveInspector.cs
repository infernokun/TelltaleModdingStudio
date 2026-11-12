using System.Text;
using TelltaleModding.Core.Games;

namespace TelltaleModding.Core.Archives;

/// <summary>Container-format tag found at the start of an archive.</summary>
public enum ArchiveMagic
{
    /// <summary>Not a known archive header.</summary>
    Unknown,

    /// <summary>Classic .ttarch, whose header starts with a small version number.</summary>
    Classic,

    /// <summary>"ZCTT": zlib-compressed .ttarch2 container.</summary>
    Zctt,

    /// <summary>"ECTT": encrypted+compressed .ttarch2 container.</summary>
    Ectt,

    /// <summary>"NCTT": uncompressed .ttarch2 container.</summary>
    Nctt,

    /// <summary>"3ATT": version-3 ATT container.</summary>
    Att3,

    /// <summary>"4ATT": version-4 ATT container.</summary>
    Att4,
}

/// <summary>What an archive's header says about it.</summary>
/// <param name="Magic">The detected container tag.</param>
/// <param name="Kind">Archive family the tag belongs to.</param>
/// <param name="Version">Version to pass ttarchext with <c>-V</c>: the header value for classic archives, 7 for .ttarch2.</param>
/// <param name="OodleCompressed">True for the lowercase tag variant, which means Oodle compression and needs the game's oo2core DLL.</param>
/// <param name="Description">Short summary for the UI.</param>
public sealed record ArchiveInfo(
    ArchiveMagic Magic,
    ArchiveKind Kind,
    int Version,
    bool OodleCompressed,
    string Description);

/// <summary>
/// Detects an archive's container format from its first bytes, following the header logic
/// in ttarchext's <c>ttarch_extract</c>.
/// </summary>
public static class ArchiveInspector
{
    /// <summary>Reads and classifies the header of the archive at <paramref name="path"/>.</summary>
    public static ArchiveInfo Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        return Inspect(stream);
    }

    /// <summary>Reads and classifies the header from the current position of <paramref name="stream"/>.</summary>
    public static ArchiveInfo Inspect(Stream stream)
    {
        Span<byte> head = stackalloc byte[4];
        int read = stream.Read(head);
        if (read < 4)
            return new ArchiveInfo(ArchiveMagic.Unknown, ArchiveKind.Ttarch, 0, false,
                "File too short to identify.");

        // .ttarch2 starts with an ASCII tag. A lowercase first letter means Oodle compression,
        // so uppercase it before matching, like ttarchext does.
        bool oodle = head[0] >= 'a' && head[0] <= 'z';
        var tag = new string(new[]
        {
            (char)char.ToUpperInvariant((char)head[0]),
            (char)head[1],
            (char)head[2],
            (char)head[3],
        });

        switch (tag)
        {
            case "ZCTT":
                return Ttarch2(ArchiveMagic.Zctt, oodle, "zlib-compressed .ttarch2 (ZCTT)");
            case "ECTT":
                return Ttarch2(ArchiveMagic.Ectt, oodle, "encrypted+compressed .ttarch2 (ECTT)");
            case "NCTT":
                return Ttarch2(ArchiveMagic.Nctt, oodle, "uncompressed .ttarch2 (NCTT)");
            case "3ATT":
                return Ttarch2(ArchiveMagic.Att3, oodle, "ATT container v3 (3ATT)");
            case "4ATT":
                return Ttarch2(ArchiveMagic.Att4, oodle, "ATT container v4 (4ATT)");
        }

        // Classic .ttarch starts with a little-endian version number.
        int version = head[0] | head[1] << 8 | head[2] << 16 | head[3] << 24;
        if (version is >= 1 and <= 9)
            return new ArchiveInfo(ArchiveMagic.Classic, ArchiveKind.Ttarch, version, false,
                $"Classic .ttarch, format version {version}");

        return new ArchiveInfo(ArchiveMagic.Unknown, ArchiveKind.Ttarch, 0, false,
            $"Unrecognised header (tag '{Printable(tag)}', first u32 0x{version:X8}).");
    }

    private static ArchiveInfo Ttarch2(ArchiveMagic magic, bool oodle, string desc) =>
        new(magic, ArchiveKind.Ttarch2, 7, oodle,
            oodle ? desc + ", Oodle-compressed" : desc);

    private static string Printable(string tag)
    {
        var sb = new StringBuilder(tag.Length);
        foreach (var c in tag)
            sb.Append(char.IsControl(c) ? '.' : c);
        return sb.ToString();
    }
}
