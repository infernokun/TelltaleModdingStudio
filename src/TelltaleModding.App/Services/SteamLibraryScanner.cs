using System.Text.RegularExpressions;

namespace TelltaleModding.App.Services;

/// <summary>
/// Finds installed games that look like Telltale titles (they ship .ttarch/.ttarch2
/// archives) in the user's Steam libraries, so they can be added with one click.
/// </summary>
public static class SteamLibraryScanner
{
    private static readonly Regex LibraryPath = new("\"path\"\\s+\"(?<p>[^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Steam library roots (folders containing <c>steamapps</c>).</summary>
    public static IEnumerable<string> LibraryRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamInstalls = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
        };
        foreach (var steam in steamInstalls)
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;
            roots.Add(steam);
            try
            {
                foreach (Match m in LibraryPath.Matches(File.ReadAllText(vdf)))
                    roots.Add(m.Groups["p"].Value.Replace(@"\\", @"\"));
            }
            catch (IOException) { }
        }

        // Libraries that Steam no longer lists (or another Steam install) are commonly
        // at <drive>:\SteamLibrary or <drive>:\Steam.
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
                    continue;
            }
            catch (IOException) { continue; }
            foreach (var name in new[] { "SteamLibrary", "Steam", @"Program Files (x86)\Steam" })
            {
                var candidate = Path.Combine(drive.RootDirectory.FullName, name);
                if (Directory.Exists(Path.Combine(candidate, "steamapps")))
                    roots.Add(candidate);
            }
        }
        return roots.Where(r => Directory.Exists(Path.Combine(r, "steamapps", "common")));
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
        return found.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName).ToList();
    }

    /// <summary>True when the folder (or one level below it) holds .ttarch/.ttarch2 archives.</summary>
    public static bool HasTelltaleArchives(string folder)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder).Prepend(folder))
            {
                if (Directory.EnumerateFiles(dir, "*.ttarch2").Any() || Directory.EnumerateFiles(dir, "*.ttarch").Any())
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
}
