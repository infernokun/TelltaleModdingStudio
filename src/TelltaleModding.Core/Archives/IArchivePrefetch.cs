namespace TelltaleModding.Core.Archives;

/// <summary>
/// Optional add-on for an <see cref="IArchiveSource"/> with slow per-file reads, so callers
/// can hand over many files at once and have them fetched in one batch.
/// </summary>
public interface IArchivePrefetch
{
    /// <summary>Fetches the given entries ahead of the <see cref="IArchiveSource.Read"/> calls that will follow.</summary>
    void Prefetch(IReadOnlyCollection<ArchiveEntryRef> entries, CancellationToken cancellationToken = default);
}
