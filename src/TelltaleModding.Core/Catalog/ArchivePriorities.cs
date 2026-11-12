using System.Text.RegularExpressions;

namespace TelltaleModding.Core.Catalog;

/// <summary>One archive of an install with the priority the catalog resolves it at.</summary>
/// <param name="Path">Full path of the archive.</param>
/// <param name="Priority">Resource-set priority. Higher overrides files of the same name.</param>
/// <param name="SetName">Resource set that loads the archive, or null when no description mentions it.</param>
/// <param name="Enabled">False for archives the game never mounts, like other languages' sets.</param>
/// <param name="FromDescription">True when the priority came from a <c>_resdesc_</c> file, false when guessed from the name.</param>
public sealed record CatalogArchive(string Path, int Priority, string? SetName, bool Enabled, bool FromDescription);

/// <summary>
/// Works out which archive wins when the same file name exists in several, following the engine:
/// <list type="number">
/// <item>Each archive gets the priority of the resource set that lists it in
/// <c>gameDataArchives</c> in the <c>_resdesc_50_*.lua</c> files.</item>
/// <item>Localization sets for other languages, and sets mounted into them, are dropped.</item>
/// <item>Archives no description mentions get a priority from <see cref="GuessPriority"/>.</item>
/// <item>Highest priority wins. Ties go to the file name that sorts last, which is the later episode.</item>
/// </list>
/// </summary>
public static class ArchivePriorities
{
    private static readonly Regex LanguageArchive = new(
        @"(^|_)(WD\d_)?(French|German|Italian|Spanish|Portuguese|Russian|Chinese|Traditional_Chinese|Japanese|Korean|Polish|Brazilian)|^WD\d_(De|Es|Fr|Pt|It|Ru)_",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EpisodeNumber = new(@"(\d{3})(?=_|$)", RegexOptions.Compiled);

    private static readonly Regex PatchMarker = new(
        @"patch|update|_pc_pc[_-]|(^|_)p\d\d(_|$)|_dt_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Assigns a priority to every archive, from <paramref name="sets"/> where they cover it and
    /// <see cref="GuessPriority"/> otherwise. Keeps the order of <paramref name="archivePaths"/>.
    /// </summary>
    public static IReadOnlyList<CatalogArchive> Resolve(
        IReadOnlyList<string> archivePaths, IReadOnlyList<ResourceSetDescription> sets, string language = "English")
    {
        var byLogicalName = new Dictionary<string, ResourceSetDescription>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sets)
            byLogicalName.TryAdd(s.LogicalName, s);

        var activeCache = new Dictionary<ResourceSetDescription, bool>(ReferenceEqualityComparer.Instance);
        bool IsActive(ResourceSetDescription set, int depth)
        {
            if (activeCache.TryGetValue(set, out var known)) return known;
            bool active = !(set.IsLocalization && !set.Name.StartsWith(language, StringComparison.OrdinalIgnoreCase));
            if (active && depth < 16 && set.LogicalDestination is { Length: > 2 } dest &&
                byLogicalName.TryGetValue(dest, out var parent) && !ReferenceEquals(parent, set))
                active = IsActive(parent, depth + 1);
            activeCache[set] = active;
            return active;
        }

        // Archive file name to the best set listing it: active first, then highest priority.
        var owner = new Dictionary<string, ResourceSetDescription>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets)
        {
            foreach (var archive in set.Archives)
            {
                if (!owner.TryGetValue(archive, out var current) ||
                    (IsActive(set, 0), set.Priority).CompareTo((IsActive(current, 0), current.Priority)) > 0)
                    owner[archive] = set;
            }
        }

        var result = new List<CatalogArchive>(archivePaths.Count);
        foreach (var path in archivePaths)
        {
            var file = Path.GetFileName(path);
            if (owner.TryGetValue(file, out var set))
                result.Add(new CatalogArchive(path, set.Priority, set.Name, IsActive(set, 0), FromDescription: true));
            else
                result.Add(new CatalogArchive(path, GuessPriority(file), null, !IsForeignLanguageArchive(file, language), FromDescription: false));
        }
        return result;
    }

    /// <summary>
    /// Guesses a priority from the file name when no resource description covers it.
    /// The values follow the real tables: engine lowest, project -8888, episodes 1xx, patches 400+.
    /// </summary>
    public static int GuessPriority(string archiveFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveFileName);
        var lower = stem.ToLowerInvariant();
        if (lower.Contains("shader")) return -9979;
        if (lower.Contains("engine_lua") || lower.Contains("engine lua")) return -9989;

        int episode = 0;
        var m = EpisodeNumber.Match(stem);
        if (m.Success) episode = int.Parse(m.Groups[1].Value) % 100;

        int basePriority =
            m.Success ? 100 + episode :
            lower.Contains("season") ? 50 :
            lower.Contains("_ui") ? 30 :
            lower.Contains("_menu") ? 20 :
            lower.Contains("_boot") ? 10 :
            lower.Contains("_proj") ? -8888 : 0;

        return PatchMarker.IsMatch(stem) ? 400 + episode : basePriority;
    }

    /// <summary>True for archives that only hold another language's data.</summary>
    public static bool IsForeignLanguageArchive(string archiveFileName, string language = "English")
    {
        var stem = Path.GetFileNameWithoutExtension(archiveFileName);
        return LanguageArchive.IsMatch(stem) && !stem.Contains(language, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Orders enabled archives from lowest to highest precedence, so the last one added wins.</summary>
    public static IEnumerable<CatalogArchive> InPrecedenceOrder(IEnumerable<CatalogArchive> archives) =>
        archives
            .Where(a => a.Enabled)
            .OrderBy(a => a.Priority)
            .ThenBy(a => Path.GetFileName(a.Path), StringComparer.OrdinalIgnoreCase);
}
