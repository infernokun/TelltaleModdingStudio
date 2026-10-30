using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class SceneChoreLinesTests
{
    private const string WolfAmongUs = @"A:\SteamLibrary\steamapps\common\The Wolf Among Us";
    private const string Borderlands = @"F:\Steam\steamapps\common\Tales from the Borderlands";

    private static DialogueEntry Entry(uint id, string? audio = null) =>
        new(new DialogueLine(id, "AJ", "", Array.Empty<ulong>()) { NameSymbol = SceneChoreLines.LineSymbol(id) },
            "env_cave_act1", "WalkingDead401", new ArchiveEntryRef("a.ttarch2", "env_cave_act1_english.landb", 0),
            new ArchiveEntryRef("a.ttarch2", audio ?? $"{id}.wav", 0));

    private static ChoreFile HardWorld()
    {
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample("env_cave_act1_cs_hardWorld_1.chore")), out var chore, out var error), error);
        return chore!;
    }

    [SkippableFact]
    public void Lines_are_named_by_the_symbol_of_their_lang_resource()
    {
        Assert.True(LandbFile.TryRead(File.ReadAllBytes(SampleData.Sample("landb_wau_env_openarmslobby_english.landb")), out var lines, out var error), error);
        Assert.All(lines, l => Assert.Equal(TelltaleCrc64.HashSymbol($"{l.Id}.lang"), l.NameSymbol));
    }

    [Fact]
    public void A_line_that_plays_another_lines_recording_is_also_known_by_that_lines_symbol()
    {
        Assert.Equal(new[] { SceneChoreLines.LineSymbol(357138734) }, SceneChoreLines.VoiceSymbols(Entry(357138734)));
        Assert.Equal(new[] { SceneChoreLines.LineSymbol(357138734), SceneChoreLines.LineSymbol(295896370) },
            SceneChoreLines.VoiceSymbols(Entry(357138734, "295896370.wav")));
    }

    [SkippableFact]
    public void Finds_a_line_by_its_own_symbol_or_by_the_recording_it_plays()
    {
        var lines = new SceneChoreLines();
        lines.Add(HardWorld());

        var own = lines.Find(Entry(396764597));
        Assert.NotNull(own);
        Assert.Equal("AJ", own!.Agent);
        Assert.Equal(7.99f, own.Start, 0.01f);
        Assert.Equal(11.11f, own.End, 0.01f);

        // Another line voiced with AJ's recording is placed where the chore plays that recording.
        var borrowed = lines.Find(Entry(400000001, "396764597.wav"));
        Assert.Equal(own.Resource, borrowed!.Resource);

        Assert.Null(lines.Find(Entry(400000001)));
    }

    [SkippableFact]
    public void Prefers_the_chore_the_dialog_plays_the_line_with()
    {
        var first = HardWorld();
        var exchange = HardWorld();
        var lines = new SceneChoreLines();
        lines.Add(first);
        lines.Add(exchange);
        Assert.Same(first, lines.Find(Entry(396764012))!.Chore);

        lines.AddExchange(new[] { 396764012u }, exchange);
        Assert.Same(exchange, lines.Find(Entry(396764012))!.Chore);
        // Lines the exchange's chore does not voice still come from any chore.
        lines.AddExchange(new[] { 396764596u }, ChoreWithoutLines());
        Assert.Equal("AJ", lines.Find(Entry(396764596))!.Agent);
    }

    private static ChoreFile ChoreWithoutLines()
    {
        Assert.True(ChoreFile.TryRead(File.ReadAllBytes(SampleData.Sample("chore_wau_adv_103puddingNPie_brokenDispenser.chore")), out var chore, out var error), error);
        return chore!;
    }

    private static async Task<(GameCatalog Catalog, DialogueIndex Index, IReadOnlyList<ArchiveEntryRef> Chores)> Open(string folder)
    {
        var install = GameInstall.Detect(folder, out var error) ?? throw new InvalidOperationException(error);
        var fallback = TtarchextArchiveSource.CreateOpener(install.Game.GameNum);
        var catalog = await GameCatalog.OpenAsync(install, path => Ttarch2Archive.LooksLikeTtarch2(path)
            ? Ttarch2Archive.Open(path, install.Game.GameNum, install.RootFolder)
            : fallback(path));
        var chores = catalog.Entries.Where(e => e.Name.EndsWith(".chore", StringComparison.OrdinalIgnoreCase)).ToList();
        return (catalog, DialogueIndex.Build(catalog), chores);
    }

    private static async Task<ChoreLine?> FindAsync(string game, uint id)
    {
        var (catalog, index, chores) = await Open(game);
        using (catalog)
        {
            var line = index.Lines.Single(l => l.Line.Id == id);
            return SceneChoreLines.Build(catalog, chores, line.Scene).Find(line);
        }
    }

    [InstalledGameFact(WolfAmongUs)]
    public async Task Finds_a_line_voiced_with_an_earlier_lines_recording()
    {
        // "Is it funny?" plays the recording of an earlier take, which the chore places.
        var found = await FindAsync(WolfAmongUs, 357138734);
        Assert.NotNull(found);
        Assert.Equal("fabletownAndersen_cop_cs_TestingYou_10.chore", found!.Chore.Name);
        Assert.Equal("Wachowski", found.Agent);
        Assert.Equal(7.33f, found.Start, 0.01f);
    }

    [InstalledGameFact(Ttarch2ArchiveTests.DefinitiveSeriesRoot)]
    public async Task Finds_a_line_in_a_chore_embedded_in_the_exchanges_chore()
    {
        var found = await FindAsync(Ttarch2ArchiveTests.DefinitiveSeriesRoot, 396763363);
        Assert.NotNull(found);
        Assert.Equal("(Ratings) env_cave_act1_cs_heroes_13.chore", found!.Chore.Name);
        Assert.Equal("Clementine", found.Agent);
        Assert.Equal(1.55f, found.Start, 0.01f);
    }

    [InstalledGameFact(Borderlands)]
    public async Task Finds_a_line_in_a_chore_named_unlike_its_scene_through_the_dialog()
    {
        // Marcus opens the episode in ui_openingIntro.chore, named after nothing in the scene.
        var found = await FindAsync(Borderlands, 361570435);
        Assert.NotNull(found);
        Assert.Equal("ui_openingIntro.chore", found!.Chore.Name);
        Assert.Equal("Marcus", found.Agent);
        Assert.Equal(0.86f, found.Start, 0.01f);
    }
}
