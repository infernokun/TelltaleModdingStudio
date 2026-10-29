namespace TelltaleModding.Core.Archives;

/// <summary>
/// Optional companion to <see cref="IArchiveSource"/> for sources whose per-file reads
/// are expensive (e.g. one external process per file). Callers that are about to read
/// many files from the same archive hand them over first so the source can fetch them
/// in one batch; later <see cref="IArchiveSource.Read"/> calls are then served locally.
/// Sources with cheap random access simply don't implement it.
/// </summary>
public interface IArchivePrefetch
{
    /// <summary>Fetches the given entries ahead of the <see cref="IArchiveSource.Read"/> calls that will follow.</summary>
    void Prefetch(IReadOnlyCollection<ArchiveEntryRef> entries, CancellationToken cancellationToken = default);
}
