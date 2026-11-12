using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TelltaleModding.App.Services;

/// <summary>Finds installed Telltale games in the user's Steam libraries.</summary>
public static class SteamLibraryScanner
{
    private static readonly Regex LibraryPath = new("\"path\"\\s+\"(?<p>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex InstallDir = new("\"installdir\"\\s+\"(?<d>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex ManifestName = new(@"^appmanifest_(?<id>\d+)\.acf$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Steam app IDs of games made with Telltale's engine, checked against the Steam store
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> TelltaleAppIds = new Dictionary<int, string>
    {
        [207610] = "The Walking Dead",
        [261030] = "The Walking Dead: Season Two",
        [429570] = "The Walking Dead: Michonne",
        [536220] = "The Walking Dead: A New Frontier",
        [866800] = "The Walking Dead: The Final Season",
        [1449690] = "The Walking Dead: The Telltale Definitive Series",
        [250320] = "The Wolf Among Us",
        [3884860] = "The Wolf Among Us Remastered",
        [330830] = "Tales from the Borderlands",
        [330840] = "Game of Thrones",
        [376870] = "Minecraft: Story Mode",
        [498240] = "Batman - The Telltale Series",
        [675260] = "Batman: The Enemy Within",
        [579950] = "Marvel's Guardians of the Galaxy",
        [31170] = "Tales of Monkey Island",
        [31290] = "Back to the Future: The Game",
        [31270] = "Puzzle Agent",
        [94590] = "Puzzle Agent 2",
        [31280] = "Poker Night at the Inventory (2010)",
        [3897800] = "Poker Night at the Inventory",
        [234710] = "Poker Night 2",
        [8200] = "Sam & Max 101: Culture Shock",
        [8260] = "Sam & Max 201: Ice Station Santa",
        [31220] = "Sam & Max 301: The Penal Zone",
        [31250] = "Sam & Max 304: Beyond the Alley of the Dolls",
        [31260] = "Sam & Max 305: The City That Dares Not Sleep",
        [1440440] = "Sam & Max Save the World",
        [1747050] = "Sam & Max: Beyond Time and Space",
        [2648050] = "Sam & Max: The Devil's Playhouse",
        [8310] = "Bone: Out From Boneville",
        [8330] = "Telltale Texas Hold 'Em",
        [8340] = "Strong Bad's Cool Game for Attractive People",
        [31100] = "Wallace & Gromit's Grand Adventures",
        [94600] = "Hector: Episode 1",
        [94610] = "Hector: Episode 2",
        [94620] = "Hector: Badge of Carnage",
    };

    // Windows paths ignore case, Linux and macOS paths don't.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly EnumerationOptions AnyCase = new() { MatchCasing = MatchCasing.CaseInsensitive };

    /// <summary>Steam library roots (folders containing <c>steamapps</c>).</summary>
    public static IEnumerable<string> LibraryRoots()
    {
        var roots = LibraryRoots(SteamInstallFolders());

        // Also check the usual library folder names at each drive root.
        var names = OperatingSystem.IsWindows()
            ? new[] { "SteamLibrary", "Steam", @"Program Files (x86)\Steam" }
            : new[] { "SteamLibrary", "Steam" };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;
            }
            catch (IOException) { continue; }
            foreach (var name in names)
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, name);
                if (Directory.Exists(Path.Combine(candidate, "steamapps")))
                    roots.Add(Canonical(candidate));
            }
        }
        return roots.Where(r => Directory.Exists(Path.Combine(r, "steamapps", "common")));
    }

    /// <summary>
    /// The given Steam installs that exist, plus every library in their libraryfolders.vdf.
    /// Links are followed so a library only shows up once.
    /// </summary>
    public static HashSet<string> LibraryRoots(IEnumerable<string> steamInstalls)
    {
        var roots = new HashSet<string>(PathComparer);
        foreach (var steam in steamInstalls)
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;
            roots.Add(Canonical(steam));
            try
            {
                foreach (Match m in LibraryPath.Matches(File.ReadAllText(vdf)))
                    roots.Add(Canonical(m.Groups["p"].Value.Replace(@"\\", @"\")));
            }
            catch (IOException) { }
        }
        return roots;
    }

    /// <summary>Places Steam itself might be installed, including the registry path on Windows.</summary>
    public static IEnumerable<string> SteamInstallFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
            // Registry last, so a duplicate keeps the normal casing instead of lower case.
            foreach (var folder in RegistrySteamFolders())
                yield return folder;
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            yield break;
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Steam");
            yield break;
        }
        // Native package, then Flatpak and Snap.
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".steam", "root");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
        yield return Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam");
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistrySteamFolders()
    {
        var keys = new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        };
        foreach (var (hive, key, name) in keys)
        {
            string? folder = null;
            try
            {
                using var opened = hive.OpenSubKey(key);
                folder = opened?.GetValue(name) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            if (!string.IsNullOrWhiteSpace(folder))
                yield return Path.GetFullPath(folder);
        }
    }

    /// <summary>The Telltale games Steam lists as installed.</summary>
    public static List<string> FindInstalled() => FindInstalled(LibraryRoots(SteamInstallFolders()));

    /// <summary>
    /// The Telltale games Steam lists as installed in <paramref name="libraryRoots"/>, read
    /// from the app manifests. No game folder is opened.
    /// </summary>
    public static List<string> FindInstalled(IEnumerable<string> libraryRoots)
    {
        var found = new List<string>();
        foreach (var root in libraryRoots)
        {
            var steamapps = Path.Combine(root, "steamapps");
            try
            {
                foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    var match = ManifestName.Match(Path.GetFileName(manifest));
                    if (!match.Success || !int.TryParse(match.Groups["id"].Value, out int appId) ||
                        !TelltaleAppIds.ContainsKey(appId))
                        continue;
                    if (InstallDir.Match(File.ReadAllText(manifest)) is { Success: true } dir)
                    {
                        var game = Path.Combine(steamapps, "common", dir.Groups["d"].Value.Replace(@"\\", @"\"));
                        if (Directory.Exists(game))
                            found.Add(game);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found.Distinct(PathComparer).OrderBy(Path.GetFileName).ToList();
    }

    /// <summary>
    /// Game folders in the Steam libraries that hold Telltale archives, plus the games Steam
    /// lists. Opens every game folder, so only use it for a manual scan.
    /// </summary>
    public static List<string> FindTelltaleInstalls()
    {
        var found = FindInstalled();
        foreach (var root in LibraryRoots())
        {
            IEnumerable<string> games;
            try { games = Directory.EnumerateDirectories(Path.Combine(root, "steamapps", "common")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var game in games)
            {
                if (HasTelltaleArchives(game))
                    found.Add(game);
            }
        }
        return found.Distinct(PathComparer).OrderBy(Path.GetFileName).ToList();
    }

    /// <summary>True when the folder or one level below it holds .ttarch/.ttarch2 archives.</summary>
    public static bool HasTelltaleArchives(string folder)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder).Prepend(folder))
            {
                if (Directory.EnumerateFiles(dir, "*.ttarch2", AnyCase).Any() || Directory.EnumerateFiles(dir, "*.ttarch", AnyCase).Any())
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    // The real folder after following links, with no trailing separator.
    private static string Canonical(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var target = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true);
            return Path.TrimEndingDirectorySeparator(target?.FullName ?? full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return dir;
        }
    }
}
