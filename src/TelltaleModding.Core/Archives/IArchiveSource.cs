namespace TelltaleModding.Core.Archives;

/// <summary>One file stored inside a game archive.</summary>
/// <param name="ArchivePath">Full path of the archive that holds the file.</param>
/// <param name="Name">The stored file name, e.g. <c>sk62_clementine400_head.d3dmesh</c>.</param>
/// <param name="Size">Uncompressed size in bytes.</param>
public sealed record ArchiveEntryRef(string ArchivePath, string Name, long Size);

/// <summary>Random access to the files inside one archive.</summary>
public interface IArchiveSource : IDisposable
{
    /// <summary>Full path of the archive.</summary>
    string ArchivePath { get; }

    /// <summary>Every file in the archive's table of contents.</summary>
    IReadOnlyList<ArchiveEntryRef> Entries { get; }

    /// <summary>Reads one file's bytes. Safe to call from several threads at once.</summary>
    byte[] Read(ArchiveEntryRef entry);
}
