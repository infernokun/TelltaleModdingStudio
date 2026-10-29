using System.Diagnostics;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// A fact that runs only when a real game install and a runnable ttarchext are present
/// (the developer machine's Steam libraries); skipped everywhere else.
/// </summary>
public sealed class InstallFactAttribute : FactAttribute
{
    public InstallFactAttribute(string folder)
    {
        if (!Directory.Exists(folder))
            Skip = $"Game install not found: {folder}";
        else if (EngineFactAttribute.FindRunnableEngine() is null)
            Skip = "No runnable ttarchext engine on this host.";
    }
}

/// <summary>
/// End-to-end catalog builds over real installs through the ttarchext fallback reader.
/// The index is cached under %TEMP% so repeated runs are quick; the first run per
/// install takes seconds (Definitive Series: ~300 archives).
/// </summary>
public class GameCatalogIntegrationTests
{
    private const string DefinitiveSeries = @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";
    private const string Borderlands = @"F:\Steam\steamapps\common\Tales from the Borderlands";
    private const string SeasonTwo = @"D:\SteamLibrary\steamapps\common\The Walking Dead Season Two";
    private const string WolfAmongUs = @"A:\SteamLibrary\steamapps\common\The Wolf Among Us";
    private const string Thrones = @"D:\SteamLibrary\steamapps\common\Game of Thrones";
    private const string Michonne = @"F:\Steam\steamapps\common\The Walking Dead Michonne";

    private static readonly string CacheFolder = Path.Combine(Path.GetTempPath(), "TelltaleModdingStudioTests", "catalogs");

    private static async Task<GameCatalog> Open(string folder)
    {
        var install = GameInstall.Detect(folder, out var error) ?? throw new InvalidOperationException(error);
        var engine = EngineFactAttribute.FindRunnableEngine();
        return await GameCatalog.OpenAsync(install, TtarchextArchiveSource.CreateOpener(install.Game.GameNum, engine),
            new CatalogOptions { CacheFolder = CacheFolder, EnginePath = engine });
    }

    [InstallFact(DefinitiveSeries)]
    public async Task DefinitiveSeries_Clementine400_HasTheGamesDefaultOutfit()
    {
        using var catalog = await Open(DefinitiveSeries);

        Assert.Equal(67, catalog.Install.Game.GameNum);
        Assert.True(catalog.Characters.Count > 300, $"only {catalog.Characters.Count} characters");

        var clem = catalog.Characters.Single(c => c.Id == "sk62_clementine400");
        Assert.Equal(("Clementine", "400"), (clem.DisplayName, clem.Variant));
        Assert.Equal("sk62_clementine400.skl", clem.Skeleton!.Name);
        Assert.True(clem.DefaultsFromGameData);
        Assert.Equal(
            new[] { "bodyLowerB", "bodyUpperB", "bodyUpperBBackpack", "eyesMouth", "hairBandage", "hairCap",
                    "hairSidePony", "hands", "handsNoFinger", "head" },
            clem.Parts.Select(p => p.PartName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "bodyLowerB", "bodyUpperB", "eyesMouth", "hairCap", "hands", "head" },
            clem.DefaultParts.Select(p => p.PartName).OrderBy(n => n, StringComparer.Ordinal));

        var anims = catalog.AnimationsFor(clem);
        Assert.True(anims.Count(a => a.NamedForCharacter) > 1000);
        Assert.Contains(anims, a => a.File.Name == "sk62_action_clementine400AjCloseDoor.anm" && a.NamedForCharacter);
        Assert.Contains(anims, a => a.File.Name == "sk62_clementine400_jog.anm" && a.NamedForCharacter);
        // Character-named clips come before the rest of the family.
        int lastNamed = anims.ToList().FindLastIndex(a => a.NamedForCharacter);
        int firstOther = anims.ToList().FindIndex(a => !a.NamedForCharacter);
        Assert.True(firstOther < 0 || lastNamed < firstOther);
    }

    [InstallFact(DefinitiveSeries)]
    public async Task DefinitiveSeries_PatchArchivesOverrideEpisodes_AndTexturesResolveBySymbol()
    {
        using var catalog = await Open(DefinitiveSeries);

        var patch = catalog.Archives.Single(a => Path.GetFileName(a.Path) == "WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2");
        var episode = catalog.Archives.Single(a => Path.GetFileName(a.Path) == "WDC_pc_WalkingDead401_data.ttarch2");
        Assert.True(patch.FromDescription && episode.FromDescription);
        Assert.Equal(400, patch.Priority);
        Assert.Equal(101, episode.Priority);
        Assert.Contains(catalog.Archives, a => !a.Enabled && Path.GetFileName(a.Path).StartsWith("WD4_De_", StringComparison.Ordinal));

        // Every file the patch carries is served from the patch or something ranked above it
        // (later patches, mods), never from the episode archives it patches.
        var priorityOf = catalog.Archives.ToDictionary(a => a.Path, a => a.Priority, StringComparer.OrdinalIgnoreCase);
        using (var patchSource = TtarchextArchiveSource.Open(patch.Path, 67, EngineFactAttribute.FindRunnableEngine()))
        {
            Assert.NotEmpty(patchSource.Entries);
            foreach (var e in patchSource.Entries)
                Assert.True(priorityOf[catalog.Find(e.Name)!.ArchivePath] >= patch.Priority, e.Name);
        }

        var head = catalog.Find("sk62_clementine400_head.d3dtx");
        Assert.NotNull(head);
        Assert.Same(head, catalog.FindBySymbol(TelltaleCrc64.HashSymbol("sk62_clementine400_head.d3dtx")));
        Assert.Same(head, catalog.FindBySymbol(TelltaleCrc64.HashSymbol("sk62_clementine400_head")));
        Assert.Equal("6VSM", System.Text.Encoding.ASCII.GetString(catalog.Read(catalog.Find("sk62_clementine400.skl")!), 0, 4));
    }

    [InstallFact(DefinitiveSeries)]
    public async Task DefinitiveSeries_ReopensFromCacheQuickly()
    {
        using (await Open(DefinitiveSeries)) { }
        var clock = Stopwatch.StartNew();
        using var catalog = await Open(DefinitiveSeries);
        Assert.True(catalog.LoadedFromCache);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"cached open took {clock.Elapsed}");
        Assert.True(catalog.Entries.Count > 300_000);
    }

    [InstallFact(Borderlands)]
    public async Task Borderlands_FindsTheMainCast()
    {
        using var catalog = await Open(Borderlands);

        Assert.Equal(56, catalog.Install.Game.GameNum);
        Assert.Equal("Tales from the Borderlands", catalog.Install.DisplayName);
        Assert.True(catalog.Characters.Count >= 80, $"only {catalog.Characters.Count} characters");

        var rhys = catalog.Characters.Single(c => c.Id == "sk54_rhys");
        Assert.Equal(("Rhys", null), (rhys.DisplayName, rhys.Variant));
        Assert.Contains(rhys.DefaultParts, p => p.PartName == "body");
        Assert.DoesNotContain(rhys.DefaultParts, p => p.Mesh.Name.StartsWith("sk54_rhysFuture", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(catalog.Characters, c => c.Id == "sk54_rhysFuture" && c.DisplayName == "Rhys" && c.Variant == "Future");

        var sasha = catalog.Characters.Single(c => c.Id == "sk55_sasha");
        Assert.Equal("sk55_sasha.d3dmesh", Assert.Single(sasha.DefaultParts).Mesh.Name);
        Assert.Contains(catalog.Characters, c => c.Id == "sk55_fiona");
        Assert.Contains(catalog.Characters, c => c.Id == "skB0_claptrap");
        Assert.Contains(catalog.AnimationsFor(sasha), a => a.File.Name == "sk55_idle_sashaHandsHips.anm" && a.NamedForCharacter);
        Assert.Contains(catalog.AnimationsFor(rhys), a => a.File.Name == "sk54_action_rhysWakeUpDragged.anm" && a.NamedForCharacter);

        // Episode 105 outranks 101 for files every episode carries.
        Assert.EndsWith("Borderlands_pc_Borderlands105_data.ttarch2", catalog.Find("sk54_rhys.prop")!.ArchivePath);
    }

    [InstallFact(SeasonTwo)]
    public async Task SeasonTwo_Builds() => await AssertBuilds(SeasonTwo, 55, "sk56_clementine200");

    [InstallFact(WolfAmongUs)]
    public async Task WolfAmongUs_Builds() => await AssertBuilds(WolfAmongUs, 54, "sk54_bigby");

    [InstallFact(Thrones)]
    public async Task GameOfThrones_Builds() => await AssertBuilds(Thrones, 57, "sk61_asher");

    [InstallFact(Michonne)]
    public async Task Michonne_Builds() => await AssertBuilds(Michonne, 59, "sk55_michonne");

    private static async Task AssertBuilds(string folder, int gameNum, string expectedCharacter)
    {
        using var catalog = await Open(folder);
        Assert.Equal(gameNum, catalog.Install.Game.GameNum);
        var character = catalog.Characters.Single(c => c.Id.Equals(expectedCharacter, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(character.DefaultParts);
        Assert.Contains(catalog.Archives, a => a.FromDescription);
    }
}
