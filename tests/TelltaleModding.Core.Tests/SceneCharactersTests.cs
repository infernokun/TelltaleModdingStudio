using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>The character a scene places under an agent name, found through the agent's props.</summary>
public class SceneCharactersTests
{
    private const string DefinitiveSeries = @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";

    private static async Task<GameCatalog> Open(string folder)
    {
        var install = GameInstall.Detect(folder, out var error) ?? throw new InvalidOperationException(error);
        var fallback = TtarchextArchiveSource.CreateOpener(install.Game.GameNum);
        return await GameCatalog.OpenAsync(install, path => Ttarch2Archive.LooksLikeTtarch2(path)
            ? Ttarch2Archive.Open(path, install.Game.GameNum, install.RootFolder)
            : fallback(path));
    }

    [GameFact(DefinitiveSeries)]
    public async Task The_scene_names_the_exact_model_where_the_name_fits_many()
    {
        using var catalog = await Open(DefinitiveSeries);
        // The game has a dozen Clementines; the Season 4 barn stages Clementine 402 with AJ.
        Assert.True(catalog.Characters.Count(c => c.DisplayName == "Clementine") > 5);
        var scenes = new SceneCharacters(catalog);
        Assert.Equal("sk62_clementine402", scenes.Find("adv_forestBarn.scene", "Clementine")?.Id);
        Assert.Equal("sk63_aj", scenes.Find("adv_forestBarn.scene", "AJ")?.Id);
        // Subtitle names are matched without case.
        Assert.Equal("sk62_clementine402", scenes.Find("adv_forestBarn.scene", "CLEMENTINE")?.Id);
        // Cameras and lights named after her are not characters.
        Assert.Null(scenes.Find("adv_forestBarn.scene", "cam_clementine_CU"));
        Assert.Null(scenes.Find("adv_forestBarn.scene", "nobody"));
        Assert.Null(scenes.Find("no_such.scene", "Clementine"));
    }

    [SkippableFact]
    public void A_scene_index_lists_the_scene_files_its_chores_are_staged_in()
    {
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample("env_cave_act1_cs_hardWorld_1.chore")), out var chore, out var error), error);
        var index = new SceneChoreLines();
        index.Add(chore!);
        Assert.Equal(new[] { "adv_cave.scene" }, index.SceneFiles);
    }
}
