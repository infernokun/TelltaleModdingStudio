using System.Collections.Concurrent;
using System.Diagnostics;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// Everything a game install offers: every file it uses after archive overrides, its
/// characters with their parts and default outfits, and their animations. Cached on disk
/// by <see cref="CatalogCache"/>.
/// </summary>
/// <remarks>
/// Which copy of a file wins is decided by <see cref="ArchivePriorities"/>. Archives are opened
/// through the caller's opener on first use and disposed with the catalog.
/// </remarks>
public sealed class GameCatalog : IDisposable
{
    private readonly Func<string, IArchiveSource> _openArchive;
    private readonly ConcurrentDictionary<string, Lazy<IArchiveSource>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, ArchiveEntryRef>> _sourceEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AnimationFamily> _animationFamilies = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ArchiveEntryRef> _byName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ArchiveEntryRef> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<ulong, ArchiveEntryRef> _bySymbol = new();
    private Dictionary<string, List<ArchiveEntryRef>> _animationsByFamily = new(StringComparer.OrdinalIgnoreCase);
    private List<ArchiveEntryRef> _unprefixedAnimations = new();
    private readonly Lazy<Dictionary<string, ArchiveEntryRef[]>> _animationCopies;
    private volatile bool _disposed;

    /// <summary>The install this catalog describes.</summary>
    public GameInstall Install { get; }

    /// <summary>Characters, sorted by name then variant.</summary>
    public IReadOnlyList<CharacterEntry> Characters { get; private set; } = Array.Empty<CharacterEntry>();

    /// <summary>Every file the game uses, one entry per file name, sorted by name.</summary>
    public IReadOnlyList<ArchiveEntryRef> Entries { get; private set; } = Array.Empty<ArchiveEntryRef>();

    /// <summary>Every archive of the install with its resolved priority, disabled ones included.</summary>
    public IReadOnlyList<CatalogArchive> Archives { get; private set; } = Array.Empty<CatalogArchive>();

    /// <summary>Archives whose table of contents couldn't be read during this build. Empty when loaded from cache.</summary>
    public IReadOnlyList<string> UnreadableArchives { get; private set; } = Array.Empty<string>();

    /// <summary>True when this catalog was loaded from the on-disk cache rather than built.</summary>
    public bool LoadedFromCache { get; private set; }

    /// <summary>How long <see cref="OpenAsync(GameInstall, Func{string, IArchiveSource}, IProgress{CatalogProgress}?, CancellationToken)"/> took.</summary>
    public TimeSpan OpenDuration { get; private set; }

    private GameCatalog(GameInstall install, Func<string, IArchiveSource> openArchive)
    {
        Install = install;
        _openArchive = openArchive;
        _animationCopies = new(BuildAnimationCopies, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Loads the catalog for <paramref name="install"/> from cache, or builds it.</summary>
    /// <param name="install">The game install.</param>
    /// <param name="openArchive">Opens an archive by full path.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    public static Task<GameCatalog> OpenAsync(GameInstall install, Func<string, IArchiveSource> openArchive,
        IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default) =>
        OpenAsync(install, openArchive, new CatalogOptions(), progress, cancellationToken);

    /// <summary>Loads or builds the catalog with the given <paramref name="options"/>.</summary>
    public static async Task<GameCatalog> OpenAsync(GameInstall install, Func<string, IArchiveSource> openArchive,
        CatalogOptions options, IProgress<CatalogProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(openArchive);
        ArgumentNullException.ThrowIfNull(options);

        var clock = Stopwatch.StartNew();
        var catalog = new GameCatalog(install, openArchive);
        try
        {
            var cacheFolder = options.CacheFolder ?? CatalogOptions.DefaultCacheFolder;
            var cachePath = CatalogCache.PathFor(install, cacheFolder);
            var fingerprint = await Task.Run(() => CatalogCache.ComputeFingerprint(install, options.Language), cancellationToken)
                .ConfigureAwait(false);

            CatalogCache? data = null;
            if (options.UseCache)
            {
                progress?.Report(new CatalogProgress("Loading cached index", 0, 1));
                data = await Task.Run(() => CatalogCache.TryLoad(cachePath, fingerprint), cancellationToken).ConfigureAwait(false);
                catalog.LoadedFromCache = data is not null;
            }

            if (data is null)
            {
                data = await catalog.BuildAsync(options, progress, cancellationToken).ConfigureAwait(false);
                // Don't cache an index with missing archives, so the next open tries again.
                if (options.UseCache && catalog.UnreadableArchives.Count == 0)
                {
                    progress?.Report(new CatalogProgress("Saving index", 0, 1));
                    try { data.Save(cachePath, fingerprint); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* cache is best effort */ }
                }
            }

            progress?.Report(new CatalogProgress("Indexing", 0, 1));
            await Task.Run(() => catalog.Apply(data), cancellationToken).ConfigureAwait(false);
            catalog.OpenDuration = clock.Elapsed;
            progress?.Report(new CatalogProgress(
                $"{catalog.Entries.Count:N0} files, {catalog.Characters.Count:N0} characters", 1, 1));
            return catalog;
        }
        catch
        {
            catalog.Dispose();
            throw;
        }
    }

    /// <summary>Looks up the copy of a file the game uses. A name with folders also matches by its file name.</summary>
    public ArchiveEntryRef? Find(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        if (_byName.TryGetValue(fileName, out var entry)) return entry;
        var bare = Path.GetFileName(fileName.Replace('\\', '/'));
        return _byFileName.GetValueOrDefault(bare);
    }

    /// <summary>
    /// Looks up a file by the symbol of its name, with or without extension, since newer games
    /// hash texture names with the extension and older ones without.
    /// </summary>
    public ArchiveEntryRef? FindBySymbol(ulong symbol) => _bySymbol.GetValueOrDefault(symbol);

    /// <summary>
    /// Animation clips for a character: clips named for the character first, then the rest of
    /// the skeleton family. Matching is by name only.
    /// </summary>
    public IReadOnlyList<AnimationEntry> AnimationsFor(CharacterEntry character)
    {
        ArgumentNullException.ThrowIfNull(character);
        var skeletonStem = character.Skeleton is null ? null : Path.GetFileNameWithoutExtension(character.Skeleton.Name);
        var family = CharacterNaming.FamilyPrefix(skeletonStem ?? character.Id) ?? CharacterNaming.FamilyPrefix(character.Id);
        // The agent and skeleton names rank above the person's name, which also matches other variants' clips.
        var exact = new[] { CharacterNaming.StripFamily(character.Id), skeletonStem is null ? null : CharacterNaming.StripFamily(skeletonStem) }
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var person = character.DisplayName.Replace(" ", "");
        var personTokens = exact.Any(t => t.Equals(person, StringComparison.OrdinalIgnoreCase)) || person.Length < 3
            ? Array.Empty<string>()
            : new[] { person };

        var strong = new List<AnimationEntry>();
        var weak = new List<AnimationEntry>();
        var mentions = new List<AnimationEntry>();
        var others = new List<AnimationEntry>();
        if (family is not null)
        {
            var group = _animationFamilies.GetOrAdd(family, f => new AnimationFamily(_animationsByFamily.GetValueOrDefault(f) ?? new List<ArchiveEntryRef>(), f.Length));
            foreach (var (entry, display) in group.Sorted)
            {
                // Clip names put the performer first, so a name further in is someone else's clip.
                if (exact.Any(t => PerformsClip(display, t)))
                    strong.Add(new AnimationEntry(entry, display, true));
                else if (personTokens.Any(t => PerformsClip(display, t)))
                    weak.Add(new AnimationEntry(entry, display, true));
                else if (exact.Concat(personTokens).Any(t => ContainsNameToken(display, t)))
                    mentions.Add(new AnimationEntry(entry, display, false));
                else
                    others.Add(new AnimationEntry(entry, display, false));
            }
        }

        // Unprefixed clips that start with the character's name, like lip-sync phonemes.
        foreach (var entry in _unprefixedAnimations)
        {
            var stem = Path.GetFileNameWithoutExtension(entry.Name);
            if (exact.Any(t => StartsWithWord(stem, t)))
                strong.Add(new AnimationEntry(entry, stem, true));
            else if (personTokens.Any(t => StartsWithWord(stem, t)))
                weak.Add(new AnimationEntry(entry, stem, true));
        }

        Comparison<AnimationEntry> byName = (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName);
        strong.Sort(byName);
        weak.Sort(byName);
        strong.AddRange(weak);
        strong.AddRange(mentions);
        strong.AddRange(others);
        return strong;

        static bool StartsWithWord(string stem, string token) =>
            stem.Length > token.Length && stem.StartsWith(token, StringComparison.OrdinalIgnoreCase) && stem[token.Length] == '_';
    }

    /// <summary>True when <paramref name="token"/> starts one of the clip name's '_' segments as a whole word.</summary>
    public static bool PerformsClip(string clipName, string token)
    {
        foreach (var segment in clipName.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length >= token.Length &&
                segment.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
                ContainsNameToken(segment[..Math.Min(segment.Length, token.Length + 1)], token))
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="token"/> occurs in <paramref name="text"/> as a whole word.</summary>
    public static bool ContainsNameToken(string text, string token)
    {
        if (token.Length == 0) return false;
        int at = 0;
        while ((at = text.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]) ||
                           (char.IsUpper(text[at]) && !char.IsUpper(text[at - 1])) ||
                           (char.IsDigit(text[at - 1]) && !char.IsDigit(text[at]));
            int end = at + token.Length;
            bool endOk = end == text.Length || !char.IsLower(text[end]) && !(char.IsDigit(text[end]) && char.IsDigit(token[^1]));
            if (startOk && endOk) return true;
            at++;
        }
        return false;
    }

    /// <summary>Reads a file's bytes from its archive, opening the archive on first use. Thread-safe.</summary>
    public byte[] Read(ArchiveEntryRef entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = GetSource(entry.ArchivePath);
        var own = _sourceEntries.GetOrAdd(source.ArchivePath, _ =>
        {
            var map = new Dictionary<string, ArchiveEntryRef>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in source.Entries) map.TryAdd(e.Name, e);
            return map;
        });
        return source.Read(own.GetValueOrDefault(entry.Name) ?? entry);
    }

    /// <summary>
    /// An animation's bytes for playback. If the winning copy holds a single pose but a copy it
    /// overrides moves, returns the highest-priority copy that moves. The Wolf Among Us needs
    /// this because later archives override some episode 1 clips with versions that lost their motion.
    /// </summary>
    public byte[] ReadAnimation(ArchiveEntryRef entry)
    {
        var data = Read(entry);
        if (!IsHeldPose(data) || !_animationCopies.Value.TryGetValue(entry.Name, out var copies))
            return data;
        foreach (var copy in copies)
        {
            if (string.Equals(copy.ArchivePath, entry.ArchivePath, StringComparison.OrdinalIgnoreCase))
                continue;
            byte[] other;
            try { other = Read(copy); }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
            if (AnimationFile.TryLoad(other, out var clip, out _) && clip!.IsSkeletal && clip.HasMotion)
                return other;
        }
        return data;

        static bool IsHeldPose(byte[] bytes) =>
            AnimationFile.TryLoad(bytes, out var clip, out _) && clip!.IsSkeletal && !clip.HasMotion;
    }

    /// <summary>Every .anm name found in more than one enabled archive: its copies, highest precedence first.</summary>
    private Dictionary<string, ArchiveEntryRef[]> BuildAnimationCopies()
    {
        var copies = new Dictionary<string, List<ArchiveEntryRef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in ArchivePriorities.InPrecedenceOrder(Archives).Reverse())
        {
            IReadOnlyList<ArchiveEntryRef> entries;
            try { entries = GetSource(archive.Path).Entries; }
            catch (Exception ex) when (ex is not ObjectDisposedException) { continue; }
            foreach (var e in entries)
            {
                if (!e.Name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!copies.TryGetValue(e.Name, out var list))
                    copies[e.Name] = list = new List<ArchiveEntryRef>(2);
                list.Add(e);
            }
        }
        return copies.Where(c => c.Value.Count > 1)
            .ToDictionary(c => c.Key, c => c.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Disposes every archive source this catalog opened.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var lazy in _sources.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try { lazy.Value.Dispose(); }
            catch (Exception) { /* a failed source has nothing to release */ }
        }
        _sources.Clear();
    }

    private IArchiveSource GetSource(string archivePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var lazy = _sources.GetOrAdd(archivePath,
            p => new Lazy<IArchiveSource>(() => _openArchive(p), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _sources.TryRemove(new KeyValuePair<string, Lazy<IArchiveSource>>(archivePath, lazy));
            throw;
        }
    }

    private async Task<CatalogCache> BuildAsync(CatalogOptions options, IProgress<CatalogProgress>? progress, CancellationToken ct)
    {
        // 1. Archive priorities from the resource descriptions.
        progress?.Report(new CatalogProgress("Reading resource descriptions", 0, 1));
        var sets = await ResourceDescriptionLoader.LoadAsync(
            Install.ArchivesFolder, Install.Game.GameNum, options.EnginePath, options.MaxParallelism, ct).ConfigureAwait(false);
        var archives = ArchivePriorities.Resolve(Install.ArchivePaths, sets, options.Language);

        // 2. Every enabled archive's table of contents.
        var enabled = archives.Where(a => a.Enabled).ToList();
        var tables = new ConcurrentDictionary<string, IReadOnlyList<ArchiveEntryRef>>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentDictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        progress?.Report(new CatalogProgress("Reading archive tables", 0, enabled.Count));
        await Parallel.ForEachAsync(enabled,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism), CancellationToken = ct },
            (archive, token) =>
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<ArchiveEntryRef> entries;
                try { entries = GetSource(archive.Path).Entries; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entries = Array.Empty<ArchiveEntryRef>(); // unreadable archive: skip it, keep the rest
                    failures[archive.Path] = ex;
                }
                tables[archive.Path] = entries;
                int n = Interlocked.Increment(ref done);
                progress?.Report(new CatalogProgress($"Reading archive tables: {Path.GetFileName(archive.Path)}", n, enabled.Count));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        UnreadableArchives = failures.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (enabled.Count > 0 && failures.Count == enabled.Count)
            throw new IOException(
                $"None of the {enabled.Count} archives in '{Install.ArchivesFolder}' could be opened: {failures.Values.First().Message}",
                failures.Values.First());

        // 3. Resolve overrides. Later copies replace earlier ones.
        var winners = new Dictionary<string, ArchiveEntryRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in ArchivePriorities.InPrecedenceOrder(archives))
            if (tables.TryGetValue(archive.Path, out var entries))
                foreach (var e in entries)
                    winners[e.Name] = e;
        var sorted = winners.Values.ToArray();
        Array.Sort(sorted, (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        // 4. Character props.
        var candidates = CharacterGrouping.SelectCharacterPropCandidates(sorted).ToList();
        var props = await ReadPropsAsync(candidates, options, progress, ct).ConfigureAwait(false);

        // 5. Characters.
        progress?.Report(new CatalogProgress("Grouping characters", 0, 1));
        var characters = CharacterGrouping.Build(sorted, props);

        return new CatalogCache { Archives = archives, Entries = sorted, Characters = characters };
    }

    private async Task<IReadOnlyDictionary<string, CharacterProp>> ReadPropsAsync(
        IReadOnlyList<ArchiveEntryRef> candidates, CatalogOptions options, IProgress<CatalogProgress>? progress, CancellationToken ct)
    {
        var props = new ConcurrentDictionary<string, CharacterProp>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        progress?.Report(new CatalogProgress("Reading character properties", 0, candidates.Count));

        var byArchive = candidates.GroupBy(c => c.ArchivePath, StringComparer.OrdinalIgnoreCase).ToList();
        await Parallel.ForEachAsync(byArchive,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism), CancellationToken = ct },
            (group, token) =>
            {
                var list = group.ToList();
                try
                {
                    if (GetSource(group.Key) is IArchivePrefetch prefetch)
                        prefetch.Prefetch(list, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* fall back to single reads */ }

                foreach (var entry in list)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (CharacterProp.TryParse(Read(entry)) is { } prop)
                            props[entry.Name] = prop;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* unreadable prop: skip */ }
                    int n = Interlocked.Increment(ref done);
                    if (n % 25 == 0 || n == candidates.Count)
                        progress?.Report(new CatalogProgress("Reading character properties", n, candidates.Count));
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        return props;
    }

    /// <summary>Installs the index and builds the in-memory lookup tables.</summary>
    private void Apply(CatalogCache data)
    {
        Archives = data.Archives;
        Entries = data.Entries;
        // Done on load instead of at build time so cached indexes get it too.
        Characters = CharacterGrouping.InheritBaseParts(data.Characters.Select(CharacterGrouping.ThinRandomPools).Select(CharacterGrouping.PickScriptedHead).ToArray());

        var byName = new Dictionary<string, ArchiveEntryRef>(data.Entries.Count, StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, ArchiveEntryRef>(StringComparer.OrdinalIgnoreCase);
        var bySymbol = new Dictionary<ulong, ArchiveEntryRef>(data.Entries.Count * 2);
        var stemRank = new Dictionary<ulong, int>();
        var stemBest = new Dictionary<ulong, ArchiveEntryRef>();
        var anmByFamily = new Dictionary<string, List<ArchiveEntryRef>>(StringComparer.OrdinalIgnoreCase);
        var unprefixed = new List<ArchiveEntryRef>();

        foreach (var e in data.Entries)
        {
            byName[e.Name] = e;
            if (e.Name.IndexOfAny(new[] { '/', '\\' }) >= 0)
                byFileName.TryAdd(Path.GetFileName(e.Name.Replace('\\', '/')), e);

            bySymbol[TelltaleCrc64.HashSymbol(e.Name)] = e;

            var ext = Path.GetExtension(e.Name);
            ulong stemSymbol = TelltaleCrc64.HashSymbol(e.Name[..^ext.Length]);
            int rank = ext.ToLowerInvariant() switch { ".d3dtx" => 3, ".d3dmesh" => 2, _ => 1 };
            if (!stemRank.TryGetValue(stemSymbol, out var existing) || rank > existing)
            {
                stemRank[stemSymbol] = rank;
                stemBest[stemSymbol] = e;
            }

            if (ext.Equals(".anm", StringComparison.OrdinalIgnoreCase))
            {
                if (CharacterNaming.FamilyPrefix(e.Name) is { } family)
                {
                    if (!anmByFamily.TryGetValue(family, out var list))
                        anmByFamily[family] = list = new List<ArchiveEntryRef>();
                    list.Add(e);
                }
                else
                {
                    unprefixed.Add(e);
                }
            }
        }

        // Extension-less symbols never displace a full-name symbol.
        foreach (var (symbol, entry) in stemBest)
            bySymbol.TryAdd(symbol, entry);

        _byName = byName;
        _byFileName = byFileName;
        _bySymbol = bySymbol;
        _animationsByFamily = anmByFamily;
        _unprefixedAnimations = unprefixed;
        _animationFamilies.Clear();
    }

    /// <summary>A skeleton family's clips with display names, sorted once on first use.</summary>
    private sealed class AnimationFamily
    {
        public IReadOnlyList<(ArchiveEntryRef Entry, string Display)> Sorted { get; }

        public AnimationFamily(List<ArchiveEntryRef> entries, int prefixLength)
        {
            var list = entries
                .Select(e => (e, Path.GetFileNameWithoutExtension(e.Name)[prefixLength..]))
                .ToList();
            list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Item2, b.Item2));
            Sorted = list;
        }
    }
}
