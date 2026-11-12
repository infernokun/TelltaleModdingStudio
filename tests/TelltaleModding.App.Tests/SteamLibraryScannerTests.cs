using TelltaleModding.App.Services;
using Xunit;

namespace TelltaleModding.App.Tests;

public class SteamLibraryScannerTests
{
    [Fact]
    public void Libraries_listed_by_a_Steam_install_are_found()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tms-steam-{Guid.NewGuid():N}");
        try
        {
            var steam = Path.Combine(root, "Steam");
            var library = Path.Combine(root, "Games", "SteamLibrary");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            Directory.CreateDirectory(Path.Combine(library, "steamapps", "common"));
            // libraryfolders.vdf escapes backslashes, as Steam writes it on Windows.
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam.Replace(@"\", @"\\")}\"\n\t}}\n" +
                $"\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{library.Replace(@"\", @"\\")}\"\n\t}}\n}}\n");

            var roots = SteamLibraryScanner.LibraryRoots(new[] { steam, Path.Combine(root, "missing") });

            Assert.Equal(2, roots.Count);
            Assert.Contains(Path.GetFullPath(steam), roots);
            Assert.Contains(Path.GetFullPath(library), roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_Steam_install_reached_through_a_link_is_listed_once()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tms-steam-{Guid.NewGuid():N}");
        try
        {
            var steam = Path.Combine(root, "share", "Steam");
            Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
            File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam.Replace(@"\", @"\\")}\"\n\t}}\n}}\n");
            var link = Path.Combine(root, "steam-link");
            try
            {
                Directory.CreateSymbolicLink(link, steam);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // Windows without the right to make links.
            }

            var roots = SteamLibraryScanner.LibraryRoots(new[] { link, steam });

            Assert.Equal(new[] { Path.GetFullPath(steam) }, roots);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Installed_Telltale_games_are_found_from_Steam_manifests_alone()
    {
        var library = Path.Combine(Path.GetTempPath(), $"tms-steam-{Guid.NewGuid():N}");
        try
        {
            var steamapps = Path.Combine(library, "steamapps");
            void Install(int appId, string folder, bool withFolder = true)
            {
                Directory.CreateDirectory(steamapps);
                File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{appId}.acf"),
                    $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"installdir\"\t\t\"{folder}\"\n}}\n");
                if (withFolder)
                    Directory.CreateDirectory(Path.Combine(steamapps, "common", folder));
            }
            Install(261030, "The Walking Dead Season Two");
            Install(1449690, "The Walking Dead The Telltale Definitive Series");
            Install(330830, "Tales from the Borderlands", withFolder: false); // listed, folder gone
            Install(1708010, "TheExpanseATelltaleSeries");                    // not a Telltale engine game
            // A Telltale folder Steam does not list is left to the full scan.
            Directory.CreateDirectory(Path.Combine(steamapps, "common", "The Walking Dead Michonne", "Archives"));
            File.WriteAllBytes(Path.Combine(steamapps, "common", "The Walking Dead Michonne", "Archives", "WDM_pc_data.ttarch2"), new byte[4]);

            var found = SteamLibraryScanner.FindInstalled(new[] { library, Path.Combine(library, "missing") });

            Assert.Equal(new[]
            {
                Path.Combine(steamapps, "common", "The Walking Dead Season Two"),
                Path.Combine(steamapps, "common", "The Walking Dead The Telltale Definitive Series"),
            }, found);
        }
        finally
        {
            Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public void Archives_are_found_whatever_the_case_of_their_extension()
    {
        var game = Path.Combine(Path.GetTempPath(), $"tms-game-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "Archives"));
            File.WriteAllBytes(Path.Combine(game, "Archives", "Game_pc_data.TTARCH2"), new byte[4]);

            Assert.True(SteamLibraryScanner.HasTelltaleArchives(game));
        }
        finally
        {
            Directory.Delete(game, recursive: true);
        }
    }
}
