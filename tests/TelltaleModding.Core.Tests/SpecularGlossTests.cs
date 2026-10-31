using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// The Definitive Series' earlier-season specular maps keep their gloss in alpha. Read as color
/// intensity alone, Clementine 203's dull clothes (gloss 0.09) got a sharp highlight that looked
/// like plastic.
/// </summary>
public class SpecularGlossTests
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

    private static DdsImage Decode(GameCatalog catalog, string name)
    {
        Assert.True(D3dtxTexture.TryLoad(catalog.Read(catalog.Find(name)!), out var texture, out var error), error);
        Assert.True(texture!.TryToDds(out var dds, out error), error);
        Assert.True(DdsImage.TryDecode(dds!, out var image, out error), error);
        return image!;
    }

    [GameFact(DefinitiveSeries)]
    public async Task Earlier_season_maps_keep_gloss_in_alpha_and_season_4_maps_stay_packed()
    {
        using var catalog = await Open(DefinitiveSeries);
        var clothes = Decode(catalog, "sk56_clementine200_body_spec.d3dtx");
        Assert.Equal(SpecularMapLayout.ColorGloss, ModelAssembler.ClassifySpecularMap(clothes));
        // A flat, dull map: intensity about 0.21 and gloss about 0.09.
        double gloss = 0;
        int n = 0;
        for (int i = 3; i < clothes.Rgba.Length; i += 4 * 17, n++)
            gloss += clothes.Rgba[i];
        Assert.InRange(gloss / n / 255, 0.05, 0.15);

        Assert.Equal(SpecularMapLayout.Packed, ModelAssembler.ClassifySpecularMap(Decode(catalog, "sk62_clementine400_hands_spec.d3dtx")));
    }

    [GameFact(DefinitiveSeries)]
    public async Task Clementine_203_reads_her_gloss_from_her_maps()
    {
        using var catalog = await Open(DefinitiveSeries);
        var character = catalog.Characters.Single(c => c.Id == "sk56_clementine203");
        var meshes = character.Parts.Where(p => p.DefaultOn).Select(p => p.Mesh.Name).ToList();
        var model = new ModelAssembler(new GameCatalogResolver(catalog)).Build(meshes, character.Skeleton?.Name);
        var withMaps = model.BatchSpecularMaps.Select((m, i) => (m, i)).Where(x => x.m is not null).ToList();
        Assert.NotEmpty(withMaps);
        Assert.All(withMaps, x => Assert.Equal(SpecularMapLayout.ColorGloss, model.BatchMaterialParams[x.i]!.SpecularLayout));
    }

    private sealed class GameCatalogResolver : IAssetResolver
    {
        private readonly GameCatalog _catalog;
        public GameCatalogResolver(GameCatalog catalog) => _catalog = catalog;
        public byte[]? TryRead(string fileName) => _catalog.Find(fileName) is { } e ? _catalog.Read(e) : null;
        public string? TextureNameForSymbol(ulong symbol) =>
            _catalog.FindBySymbol(symbol) is { } e && e.Name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? e.Name : null;
        public IEnumerable<string> SkeletonNames =>
            _catalog.Characters.Where(c => c.Skeleton is not null).Select(c => c.Skeleton!.Name).Distinct();
    }
}
