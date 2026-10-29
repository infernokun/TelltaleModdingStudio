using System.Collections.Concurrent;
using System.Diagnostics;
using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Archives;

/// <summary>
/// <see cref="IArchiveSource"/> backed by the ttarchext executable: the table of contents
/// comes from one <c>-l</c> listing, and each <see cref="Read"/> extracts the file with an
/// exact-name <c>-f</c> filter into a private temp folder, reads the bytes and deletes it.
/// Slow (one process per read) but works for every archive generation ttarchext knows,
/// which makes it the fallback for classic <c>.ttarch</c> games. Implements
/// <see cref="IArchivePrefetch"/> so bulk readers can pull many files with one process.
/// </summary>
/// <remarks>
/// Note that ttarchext always decrypts <c>.lua</c>/<c>.lenc</c> scripts on extraction,
/// so <see cref="Read"/> returns plain Lua for those rather than the stored bytes.
/// </remarks>
public sealed class TtarchextArchiveSource : IArchiveSource, IArchivePrefetch
{
    private static readonly Lazy<string> _processTempRoot = new(CreateProcessTempRoot);
    private static int _instanceCounter;

    private readonly TtarchRunner _runner;
    private readonly int _gameNum;
    private readonly string _workDir;
    private readonly ConcurrentDictionary<string, string> _prefetched = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    /// <inheritdoc/>
    public string ArchivePath { get; }

    /// <inheritdoc/>
    public IReadOnlyList<ArchiveEntryRef> Entries { get; }

    private TtarchextArchiveSource(TtarchRunner runner, int gameNum, string archivePath, IReadOnlyList<ArchiveEntryRef> entries)
    {
        _runner = runner;
        _gameNum = gameNum;
        ArchivePath = archivePath;
        Entries = entries;
        var safeName = string.Concat(Path.GetFileNameWithoutExtension(archivePath)
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
        _workDir = Path.Combine(_processTempRoot.Value, $"{safeName}-{Interlocked.Increment(ref _instanceCounter)}");
    }

    /// <summary>
    /// Lists <paramref name="archivePath"/> with ttarchext and returns a source over it.
    /// Throws <see cref="TtarchException"/> when the engine can't be found or fails.
    /// </summary>
    /// <param name="archivePath">Full path of the archive.</param>
    /// <param name="gameNum">ttarchext game index (selects the decryption key).</param>
    /// <param name="enginePath">ttarchext executable; located automatically when null.</param>
    public static TtarchextArchiveSource Open(string archivePath, int gameNum, string? enginePath = null)
    {
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Archive not found.", fullPath);

        var runner = new TtarchRunner(enginePath);
        var args = TtarchArguments.Extract(gameNum, fullPath, ".", new ExtractOptions { ListOnly = true });
        var result = runner.RunAsync(args).GetAwaiter().GetResult();
        var listing = ArchiveListing.Parse(result.Output);
        if (!result.Success && listing.Count == 0)
            throw new TtarchException(
                $"ttarchext could not list '{Path.GetFileName(fullPath)}' (exit code {result.ExitCode}): {Tail(result.Output)}");

        var entries = listing.Files
            .Select(f => new ArchiveEntryRef(fullPath, f.Name, f.Size))
            .ToArray();
        return new TtarchextArchiveSource(runner, gameNum, fullPath, entries);
    }

    /// <summary>An archive opener for <c>GameCatalog.OpenAsync</c> that uses this class.</summary>
    public static Func<string, IArchiveSource> CreateOpener(int gameNum, string? enginePath = null) =>
        path => Open(path, gameNum, enginePath);

    /// <inheritdoc/>
    public byte[] Read(ArchiveEntryRef entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);

        if (_prefetched.TryRemove(entry.Name, out var cachedPath) && File.Exists(cachedPath))
        {
            try
            {
                return File.ReadAllBytes(cachedPath);
            }
            finally
            {
                TryDeleteFile(cachedPath);
            }
        }

        // A private folder per read keeps concurrent reads (and concurrent copies of the
        // same file) from ever touching each other's output.
        var dir = Path.Combine(_workDir, Guid.NewGuid().ToString("N"));
        try
        {
            var result = _runner.ExtractAsync(_gameNum, ArchivePath, dir,
                new ExtractOptions { Filter = entry.Name, Overwrite = true }).GetAwaiter().GetResult();
            var path = Path.Combine(dir, entry.Name);
            if (!File.Exists(path))
                throw new IOException(
                    $"ttarchext did not extract '{entry.Name}' from '{Path.GetFileName(ArchivePath)}' " +
                    $"(exit code {result.ExitCode}): {Tail(result.Output)}");
            return File.ReadAllBytes(path);
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Extracts all of <paramref name="entries"/> with a single ttarchext run (using a
    /// wildcard built from the names' common prefix and suffix) and keeps them on disk
    /// until each is read once. Entries from other archives are ignored.
    /// </summary>
    public void Prefetch(IReadOnlyCollection<ArchiveEntryRef> entries, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wanted = entries
            .Where(e => string.Equals(e.ArchivePath, ArchivePath, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Name)
            .Where(n => !_prefetched.ContainsKey(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
            return;

        var dir = Path.Combine(_workDir, "prefetch-" + Guid.NewGuid().ToString("N"));
        var filter = wanted.Count == 1 ? wanted[0] : BuildWildcard(wanted);
        _runner.ExtractAsync(_gameNum, ArchivePath, dir,
            new ExtractOptions { Filter = filter, Overwrite = true },
            cancellationToken: cancellationToken).GetAwaiter().GetResult();

        var wantedSet = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        foreach (var name in wanted)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path))
                _prefetched[name] = path;
        }

        // The wildcard can match more than was asked for; drop the extras straight away.
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (!wantedSet.Contains(rel) && !wantedSet.Contains(rel.Replace('/', '\\')))
                    TryDeleteFile(file);
            }
        }
    }

    /// <summary>
    /// Builds a ttarchext wildcard (<c>*</c>/<c>?</c>, case-insensitive) matching every
    /// name: longest common prefix + <c>*</c> + longest common suffix.
    /// </summary>
    public static string BuildWildcard(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return "*";
        var first = names[0];
        int prefix = first.Length;
        int suffix = first.Length;
        foreach (var n in names)
        {
            prefix = Math.Min(prefix, CommonPrefix(first, n));
            suffix = Math.Min(suffix, CommonSuffix(first, n));
        }
        int shortest = names.Min(n => n.Length);
        if (prefix + suffix > shortest)
            suffix = Math.Max(0, shortest - prefix);
        return first[..prefix] + "*" + first[(first.Length - suffix)..];

        static int CommonPrefix(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        static int CommonSuffix(string a, string b)
        {
            int i = 0;
            while (i < a.Length && i < b.Length &&
                   char.ToLowerInvariant(a[a.Length - 1 - i]) == char.ToLowerInvariant(b[b.Length - 1 - i])) i++;
            return i;
        }
    }

    /// <summary>Deletes this source's temp files.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _prefetched.Clear();
        TryDeleteDirectory(_workDir);
    }

    private static string CreateProcessTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "TelltaleModdingStudio", "ttarchext");
        Directory.CreateDirectory(root);
        RemoveStaleProcessFolders(root);
        var mine = Path.Combine(root, "p" + Environment.ProcessId);
        Directory.CreateDirectory(mine);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDirectory(mine);
        return mine;
    }

    /// <summary>Removes folders left behind by earlier processes that no longer run.</summary>
    private static void RemoveStaleProcessFolders(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "p*"))
            {
                if (!int.TryParse(Path.GetFileName(dir).AsSpan(1), out int pid) || pid == Environment.ProcessId)
                    continue;
                if (!IsProcessAlive(pid))
                    TryDeleteDirectory(dir);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        static bool IsProcessAlive(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static string Tail(IReadOnlyList<string> output) =>
        string.Join(" | ", output.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3)).Trim();

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
