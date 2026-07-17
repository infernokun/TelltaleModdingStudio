using System.Globalization;
using System.Text.RegularExpressions;

namespace TelltaleModding.Core.Archives;

/// <summary>One file recorded inside an archive's table of contents.</summary>
/// <param name="Name">The stored file name (may include a relative path).</param>
/// <param name="Offset">Byte offset reported by ttarchext.</param>
/// <param name="Size">Uncompressed size in bytes.</param>
public sealed record ArchiveFileEntry(string Name, long Offset, long Size)
{
    /// <summary>File extension including the dot, lowercased (empty if none).</summary>
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>Coarse category derived from the extension.</summary>
    public AssetCategory Category => AssetClassifier.Classify(Name);
}

/// <summary>
/// The parsed result of a ttarchext listing (<c>-l</c>). Each file line ttarchext
/// prints has the shape <c>"  {offset:x8} {size,-10} {name}"</c>; everything else
/// (status lines beginning with <c>- </c>, verbose <c>offset/size/name</c> dumps) is
/// ignored.
/// </summary>
public sealed class ArchiveListing
{
    private static readonly Regex FileLine = new(
        @"^\s{2}([0-9a-fA-F]{8})\s+(\d+)\s+(.+?)\s*$",
        RegexOptions.Compiled);

    public IReadOnlyList<ArchiveFileEntry> Files { get; }

    public ArchiveListing(IReadOnlyList<ArchiveFileEntry> files) => Files = files;

    /// <summary>Total number of files listed.</summary>
    public int Count => Files.Count;

    /// <summary>Returns the entries in a given category.</summary>
    public IEnumerable<ArchiveFileEntry> InCategory(AssetCategory category) =>
        Files.Where(f => f.Category == category);

    /// <summary>Parses ttarchext output lines into a listing.</summary>
    public static ArchiveListing Parse(IEnumerable<string> outputLines)
    {
        var files = new List<ArchiveFileEntry>();
        foreach (var line in outputLines)
        {
            if (string.IsNullOrEmpty(line))
                continue;

            var m = FileLine.Match(line);
            if (!m.Success)
                continue;

            long offset = long.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            long size = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var name = m.Groups[3].Value;
            files.Add(new ArchiveFileEntry(name, offset, size));
        }
        return new ArchiveListing(files);
    }
}
