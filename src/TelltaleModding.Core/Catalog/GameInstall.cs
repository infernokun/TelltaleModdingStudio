using TelltaleModding.Core.Games;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// An installed Telltale game: where its archives are and which ttarchext game key
/// opens them. Use <see cref="Detect"/> on whatever folder the user picked.
/// </summary>
public sealed class GameInstall
{
    /// <summary>Executable name → ttarchext game index, for installs that ship one.</summary>
    private static readonly (string Exe, int GameNum)[] KnownExecutables =
    {
        ("WDC.exe", 67),
        ("Borderlands.exe", 56),
        ("TheWalkingDead2.exe", 55),
        ("TheWolfAmongUs.exe", 54),
        ("Thrones.exe", 57),
        ("WalkingDeadMichonne.exe", 59),
        // Not verified against an install on the development machine:
        ("MinecraftStoryMode.exe", 58),
        ("WalkingDead3.exe", 61),
    };

    /// <summary>Archive-name prefix → ttarchext game index (checked when no known exe is found).</summary>
    private static readonly (string Prefix, int GameNum)[] KnownArchivePrefixes =
    {
        ("WDC_pc_", 67),
        ("Borderlands_pc_", 56),
        ("BL_pc_", 56),
        ("WD2_pc_", 55),
        ("WalkingDead2_pc_", 55),
        ("WalkingDead_pc_", 55),   // Season Two's own archives (Season One uses classic .ttarch)
        ("Fables_pc_", 54),
        ("GameOfThrones_pc_", 57),
        ("WDM_pc_", 59),
        // Not verified against an install on the development machine:
        ("MCSM_pc_", 58),
        ("MC2_pc_", 63),
        ("WD3_pc_", 61),
        ("Batman_pc_", 60),
        ("Batman2_pc_", 64),
    };

    private static readonly string[] ArchiveFolderNames = { "Archives", "archives", "Pack", "pack" };

    /// <summary>The game's install folder (holds the exe, or the archives folder's parent).</summary>
    public string RootFolder { get; }

    /// <summary>The folder the user picked (install root, its archives folder, or a parent of the install).</summary>
    public string SelectedFolder { get; }

    /// <summary>Folder holding the <c>.ttarch2</c>/<c>.ttarch</c> files and <c>_resdesc_</c> descriptions.</summary>
    public string ArchivesFolder { get; }

    /// <summary>The ttarchext game entry (its <see cref="GameEntry.GameNum"/> selects the key).</summary>
    public GameEntry Game { get; }

    /// <summary>Readable game title, e.g. "The Walking Dead: The Telltale Definitive Series".</summary>
    public string DisplayName { get; }

    /// <summary>Every archive in <see cref="ArchivesFolder"/> (top level only), sorted by name.</summary>
    public IReadOnlyList<string> ArchivePaths { get; }

    private GameInstall(string root, string selected, string archivesFolder, GameEntry game, IReadOnlyList<string> archives)
    {
        RootFolder = root;
        SelectedFolder = selected;
        ArchivesFolder = archivesFolder;
        Game = game;
        DisplayName = TidyName(game.Name);
        ArchivePaths = archives;
    }

    /// <summary>
    /// Creates an install for a known game without detection, for games the tables don't
    /// recognise. Throws if <paramref name="archivesFolder"/> holds no archives of the game's kind.
    /// </summary>
    public static GameInstall Create(string archivesFolder, GameEntry game)
    {
        var folder = Path.GetFullPath(archivesFolder);
        var archives = ListArchives(folder, game.Kind);
        if (archives.Count == 0)
            throw new DirectoryNotFoundException($"No {(game.Kind == ArchiveKind.Ttarch2 ? ".ttarch2" : ".ttarch")} archives in '{folder}'.");
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? folder;
        return new GameInstall(root, folder, folder, game, archives);
    }

    /// <summary>
    /// Finds a Telltale game in <paramref name="folder"/>: the install root, its
    /// <c>Archives</c>/<c>Pack</c> folder, or a folder one level above the install
    /// (e.g. Steam's <c>common</c>, when it holds exactly one recognised game).
    /// Returns null with a user-facing <paramref name="error"/> when nothing is found.
    /// </summary>
    public static GameInstall? Detect(string folder, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "No folder given.";
            return null;
        }

        string selected;
        try
        {
            selected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (File.Exists(selected))
                selected = Path.GetDirectoryName(selected)!;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{folder}' is not a valid folder path.";
            return null;
        }

        if (!Directory.Exists(selected))
        {
            error = $"Folder '{selected}' does not exist.";
            return null;
        }

        // 1. The install the folder belongs to (user picked Archives/Pack), or the folder itself.
        var parent = Path.GetDirectoryName(selected);
        if (parent is not null && ArchiveFolderNames.Contains(Path.GetFileName(selected), StringComparer.OrdinalIgnoreCase) &&
            TryDetectAt(parent, selected) is { } fromParent)
            return fromParent;
        if (TryDetectAt(selected, selected) is { } direct)
            return direct;

        // 2. A parent of the install: look one level down.
        var found = new List<GameInstall>();
        try
        {
            foreach (var child in Directory.EnumerateDirectories(selected))
                if (TryDetectAt(child, selected) is { } install)
                    found.Add(install);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (found.Count == 1)
            return found[0];
        if (found.Count > 1)
        {
            error = $"'{selected}' contains several Telltale games ({string.Join(", ", found.Select(f => f.DisplayName))}). " +
                    "Pick one game's folder.";
            return null;
        }

        error = $"No supported Telltale game found in '{selected}'. Pick the game's install folder " +
                "(the one with the game's .exe and an Archives or Pack folder).";
        return null;
    }

    /// <summary>Detects a game whose install root is exactly <paramref name="root"/>.</summary>
    private static GameInstall? TryDetectAt(string root, string selected)
    {
        foreach (var archivesFolder in CandidateArchiveFolders(root))
        {
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(archivesFolder)
                    .Where(f => f.EndsWith(".ttarch2", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".ttarch", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (files.Count == 0) continue;

            var game = IdentifyByExecutable(root) ?? IdentifyByArchives(files);
            if (game is null) continue;

            var archives = ListArchives(archivesFolder, game.Kind);
            if (archives.Count == 0) continue;
            return new GameInstall(root, selected, archivesFolder, game, archives);
        }
        return null;
    }

    private static IEnumerable<string> CandidateArchiveFolders(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ArchiveFolderNames)
        {
            var path = Path.Combine(root, name);
            if (Directory.Exists(path) && seen.Add(Path.GetFullPath(path)))
                yield return path;
        }
        if (seen.Add(root))
            yield return root;
    }

    private static GameEntry? IdentifyByExecutable(string root)
    {
        foreach (var (exe, num) in KnownExecutables)
            if (File.Exists(Path.Combine(root, exe)))
                return GameDatabase.ByNumber(num);
        return null;
    }

    /// <summary>Identifies a game from its archive names (majority vote over known prefixes).</summary>
    public static GameEntry? IdentifyByArchives(IEnumerable<string> archivePaths)
    {
        var votes = new Dictionary<int, int>();
        foreach (var path in archivePaths)
        {
            var name = Path.GetFileName(path);
            foreach (var (prefix, num) in KnownArchivePrefixes)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                // Season Two's 'WalkingDead_pc_' names are only meaningful for .ttarch2 files.
                if (num == 55 && !name.EndsWith(".ttarch2", StringComparison.OrdinalIgnoreCase)) continue;
                votes[num] = votes.GetValueOrDefault(num) + 1;
                break;
            }
        }
        if (votes.Count == 0) return null;
        return GameDatabase.ByNumber(votes.MaxBy(kv => kv.Value).Key);
    }

    private static IReadOnlyList<string> ListArchives(string folder, ArchiveKind kind)
    {
        var ext = kind == ArchiveKind.Ttarch2 ? ".ttarch2" : ".ttarch";
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string TidyName(string name)
    {
        foreach (var suffix in new[] { " (all episodes)" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        return name;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{DisplayName} ({ArchivesFolder})";
}
