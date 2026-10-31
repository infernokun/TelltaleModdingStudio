using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>Files a character's .prop names: its idle, resting face and phoneme table.</summary>
public class PropertyReferencesTests
{
    private const string DefinitiveSeries = @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";
    private const string Borderlands = @"F:\Steam\steamapps\common\Tales from the Borderlands";

    private static async Task<GameCatalog> Open(string folder)
    {
        var install = GameInstall.Detect(folder, out var error) ?? throw new InvalidOperationException(error);
        var fallback = TtarchextArchiveSource.CreateOpener(install.Game.GameNum);
        return await GameCatalog.OpenAsync(install, path => Ttarch2Archive.LooksLikeTtarch2(path)
            ? Ttarch2Archive.Open(path, install.Game.GameNum, install.RootFolder)
            : fallback(path));
    }

    private static string? Find(GameCatalog catalog, string prop, string key, params string[] extensions) =>
        PropertyReferences.Find(catalog.Read(catalog.Find(prop)!), key, s => catalog.FindBySymbol(s)?.Name,
            n => catalog.Find(n) is { } e ? catalog.Read(e) : null, extensions);

    [GameFact(DefinitiveSeries)]
    public async Task A_character_prop_names_its_idle_and_resting_face()
    {
        using var catalog = await Open(DefinitiveSeries);
        Assert.Equal("sk63_idle_ajStandA.anm", Find(catalog, "sk63_aj.prop", PropertyReferences.IdleAnimation, ".anm", ".chore"));
        Assert.Equal("aj_face_default.chore", Find(catalog, "sk63_aj.prop", PropertyReferences.IdleFace, ".chore", ".anm"));
        // An idle can be a chore: Clementine 402 stands, breathes and glances in adv_everywhere_clementine.
        Assert.Equal("adv_everywhere_clementine.chore", Find(catalog, "sk62_clementine402.prop", PropertyReferences.IdleAnimation, ".anm", ".chore"));
    }

    [GameFact(Borderlands)]
    public async Task Unset_keys_find_nothing_and_phoneme_tables_list_their_poses()
    {
        using var catalog = await Open(Borderlands);
        // Hologram Jack's idle is a chore held in an animation-or-chore value; the plain Jack sets none.
        Assert.Equal("skB1_idle_handsomeJackStandA.chore", Find(catalog, "skB1_handsomeJackAI102.prop", PropertyReferences.IdleAnimation, ".anm", ".chore"));
        Assert.Null(Find(catalog, "skB1_handsomeJack.prop", PropertyReferences.IdleAnimation, ".anm", ".chore"));

        var table = Find(catalog, "skB1_handsomeJack.prop", PropertyReferences.PhonemeTable, ".ptable");
        Assert.Equal("skB1_handsomeJack.ptable", table);
        var clips = PropertyReferences.PhonemeClips(catalog.Read(catalog.Find(table!)!), PhonemePoses.Phonemes, s => catalog.FindBySymbol(s)?.Name);
        Assert.Equal(12, clips.Count);
        Assert.Equal("handsomeJack_phoneme_AA.anm", clips[TelltaleCrc64.HashSymbol("AA")]);
    }
}
