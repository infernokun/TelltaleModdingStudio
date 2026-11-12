using System.Text.RegularExpressions;

namespace TelltaleModding.App.Services;

/// <summary>
/// Finds installed games that look like Telltale titles (they ship .ttarch/.ttarch2
/// archives) in the user's Steam libraries, so they can be added with one click.
/// </summary>
public static class SteamLibraryScanner
{
    private static readonly Regex LibraryPath = new("\"path\"\\s+\"(?<p>[^\"]+)\"", RegexOptions.Compiled);

    // Windows paths ignore case; Linux and macOS paths don't.
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

    /// <summary>Where Steam itself is usually installed on this OS.</summary>
    public static IEnumerable<string> SteamInstallFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam");
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

    /// <summary>Game folders under the Steam libraries that contain Telltale archives.</summary>
    public static List<string> FindTelltaleInstalls()
    {
        var found = new List<string>();
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
