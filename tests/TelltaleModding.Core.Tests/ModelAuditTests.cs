using System.Numerics;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>A fact that runs only when the given game is installed (the developer machine).</summary>
public sealed class GameFactAttribute : FactAttribute
{
    public GameFactAttribute(string folder)
    {
        if (!Directory.Exists(folder))
            Skip = $"Game install not found: {folder}";
    }
}

/// <summary>
/// Assembles real characters from installed games and checks the textures and layers
/// chosen from their materials' engine data.
/// </summary>
public class ModelAuditTests
{
    private const string DefinitiveSeries = @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";
    private const string Borderlands = @"F:\Steam\steamapps\common\Tales from the Borderlands";

    private sealed class CatalogResolver : IAssetResolver
    {
        private readonly GameCatalog _catalog;
        public CatalogResolver(GameCatalog catalog) => _catalog = catalog;
        public byte[]? TryRead(string fileName) => _catalog.Find(fileName) is { } e ? _catalog.Read(e) : null;
        public string? TextureNameForSymbol(ulong symbol) =>
            _catalog.FindBySymbol(symbol) is { } e && e.Name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? e.Name : null;
        public IEnumerable<string> SkeletonNames =>
            _catalog.Characters.Where(c => c.Skeleton is not null).Select(c => c.Skeleton!.Name).Distinct();
        public IEnumerable<string> MeshNamesStartingWith(string prefix) =>
            _catalog.Entries.Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                                        e.Name.EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase)).Select(e => e.Name);
    }

    private static async Task<GameCatalog> Open(string folder)
    {
        var install = GameInstall.Detect(folder, out var error) ?? throw new InvalidOperationException(error);
        var fallback = TtarchextArchiveSource.CreateOpener(install.Game.GameNum);
        return await GameCatalog.OpenAsync(install, path => Ttarch2Archive.LooksLikeTtarch2(path)
            ? Ttarch2Archive.Open(path, install.Game.GameNum, install.RootFolder)
            : fallback(path));
    }

    private static AssembledModel Build(GameCatalog catalog, string id, Func<CharacterPart, bool>? parts = null)
    {
        var character = catalog.Characters.Single(c => c.Id == id);
        var meshes = character.Parts.Where(parts ?? (p => p.DefaultOn)).Select(p => p.Mesh.Name).ToList();
        return new ModelAssembler(new CatalogResolver(catalog)).Build(meshes, character.Skeleton?.Name);
    }

    [GameFact(DefinitiveSeries)]
    public async Task Brody_injured_head_draws_its_bleeding_effect_not_a_flow_map()
    {
        using var catalog = await Open(DefinitiveSeries);
        var brody = catalog.Characters.Single(c => c.Id == "sk62_brody");
        // The game shows her uninjured head by default.
        Assert.False(brody.Parts.Single(p => p.PartName == "headInjured").DefaultOn);

        var model = Build(catalog, "sk62_brody", _ => true);
        Assert.DoesNotContain(model.Details, d => d.Contains("· fx_heartBeatBloodPulse_flow.d3dtx"));
        Assert.Contains(model.Details, d => d.Contains("effect layer: sk62_brody_headInjuredFlat_mask.d3dtx"));
        Assert.Contains(model.Details, d => d.Contains("· sk62_brody_hairInjured.d3dtx"));
        Assert.Equal(model.Geometry.Batches.Count, model.BatchTextures.Count);
        Assert.Equal(model.Geometry.Batches.Count, model.BatchBlendModes.Count);
    }

    [GameFact(DefinitiveSeries)]
    public async Task Norma_uses_her_diffuse_textures_not_black_placeholders()
    {
        // Her name contains "_norm", which name heuristics read as a normal-map marker.
        using var catalog = await Open(DefinitiveSeries);
        var model = Build(catalog, "sk55_norma");
        Assert.Contains("sk55_norma_bodyUpper.d3dtx", model.TexturesUsed);
        Assert.Contains("sk55_norma_head.d3dtx", model.TexturesUsed);
        Assert.DoesNotContain("color_000.d3dtx", model.TexturesUsed);
    }

    [GameFact(DefinitiveSeries)]
    public async Task Abels_default_outfit_has_both_arms_and_human_eyes()
    {
        using var catalog = await Open(DefinitiveSeries);
        var abel = catalog.Characters.Single(c => c.Id == "sk61_abel403");
        var worn = abel.DefaultParts.Select(p => p.PartName).ToList();
        Assert.Contains("bodyUpper", worn);
        Assert.DoesNotContain("bodyUpperArmMissing", worn);
        Assert.DoesNotContain("handMissing", worn);

        var model = Build(catalog, "sk61_abel403");
        // The clouded "zombie" cornea shell of his human eyes is an overlay without a diffuse.
        Assert.DoesNotContain(model.Details, d => d.Contains("· sk_wd4SharedParts_corneaZombie.d3dtx"));
        Assert.True(model.CanAnimate);
    }

    [GameFact(DefinitiveSeries)]
    public async Task Season_four_zombies_are_colored_through_their_gradient()
    {
        // Their "diffuse" holds red shading and green masks; the material's gradient strip colors it.
        using var catalog = await Open(DefinitiveSeries);
        var model = Build(catalog, "sk61_zombie400");
        Assert.Contains(model.Details, d => d.Contains("sk61_zombie400_bodySkinny.d3dtx (colored through sk_wd4SharedParts_gradientZombieSkins.d3dtx)"));
    }

    [GameFact(DefinitiveSeries)]
    public async Task Generic_zombie_wears_one_part_from_each_random_pool()
    {
        using var catalog = await Open(DefinitiveSeries);
        var zombie = catalog.Characters.Single(c => c.Id == "sk54_zombieAverage");
        Assert.Single(zombie.DefaultParts, p => p.Slot == CharacterSlots.Head);
        Assert.Single(zombie.DefaultParts, p => p.Slot == CharacterSlots.Hair);
        Assert.Single(zombie.DefaultParts, p => p.Slot == CharacterSlots.LowerBody);
    }

    [GameFact(Borderlands)]
    public async Task Borderlands_faces_are_no_longer_hidden_as_overlays()
    {
        using var catalog = await Open(Borderlands);
        // A generic NPC whose face material names a diffuse that isn't shipped: the face is
        // drawn (with the texture its normal map is named after), not hidden.
        var horace = Build(catalog, "sk54_blGenericMaleHorace");
        Assert.DoesNotContain(horace.Details, d => d.Contains("hidden"));
        Assert.DoesNotContain(horace.Details, d => d.Contains("· sk54_rudiger_face_nm.d3dtx"));

        // Rhys's robot eye parts are rigid (blend indices only) and still animate with him.
        var rhys = Build(catalog, "sk54_rhys");
        Assert.True(rhys.CanAnimate);
    }

    [GameFact(Borderlands)]
    public async Task Hologram_Jack_is_tinted_blue_glows_and_is_see_through()
    {
        using var catalog = await Open(Borderlands);
        // His textures are grey; his agent .prop makes them a blue, glowing, see-through hologram.
        var ai = Build(catalog, "skB1_handsomeJackAI");
        Assert.Equal("skB1_handsomeJackAI.prop", ai.AgentProp);
        Assert.Equal(66 / 255f, ai.Agent.DiffuseColor.X, 0.002f);
        Assert.All(ai.BatchMaterialParams, p =>
        {
            Assert.True(p!.DiffuseColor.Z > 4 * p.DiffuseColor.X, "tinted blue");
            Assert.Equal(Vector3.One, p.EmissiveColor);
            // His white emission lights his own color, as bright as the game shows him.
            Assert.Equal(Vector3.One, p.SelfIllumination);
            Assert.True(p.DrawAsOneLayer);
        });
        Assert.Equal(ai.Geometry.Batches.Count, ai.BatchEmissiveMaps.Count);
        Assert.All(ai.BatchEmissiveMaps, Assert.NotNull);
        Assert.Contains(ai.Details, d => d.Contains("(glows through skB1_handsomeJackAI_bodyCube2_glow.d3dtx)"));

        var ai102 = Build(catalog, "skB1_handsomeJackAI102");
        Assert.All(ai102.BatchMaterialParams, p => Assert.Equal(0.85f, p!.Opacity, 0.002f));
        // His textures have no glow maps: nothing is listed as glowing.
        Assert.Empty(ai102.BatchEmissiveMaps);

        // The plain (non-hologram) mesh names face and body textures the game does not ship;
        // it borrows the plain art of AI103, built on the same UV layout, untinted.
        var plain = Build(catalog, "skB1_handsomeJack");
        Assert.True(plain.Agent.IsDefault);
        Assert.All(plain.BatchTextures, Assert.NotNull);
        Assert.Contains(plain.Details, d => d.Contains("the game ships none of its textures; borrowed from skB1_handsomeJackAI103"));
        Assert.Contains(plain.Details, d => d.Contains("skB1_handsomeJackAI102_body.d3dtx"));
        Assert.DoesNotContain(plain.Details, d => d.Contains("Cube"));
        Assert.All(plain.BatchMaterialParams, p => Assert.Equal(Vector3.Zero, p!.SelfIllumination));
    }

    [GameFact(Borderlands)]
    public async Task Hologram_Jack_placed_in_a_scene_is_drawn_as_he_is_on_his_own()
    {
        using var catalog = await Open(Borderlands);
        var assembler = new SceneAssembler(new SceneAssetResolver(catalog));
        var layout = assembler.Describe(SceneFile.Load(catalog.Read(catalog.Find("adv_arenaGortysCoreSilo.scene")!)));
        // He inherits skB1_handsomeJackAI.prop, which makes him a blue, glowing hologram.
        var jack = layout.Objects.Single(o => o.Name == "Jack");
        Assert.True(jack.Render.IsEffect);
        Assert.Equal(66 / 255f, jack.Render.DiffuseColor.X, 0.002f);
        var silo = layout.Objects.First(o => o.ShownByDefault && o.Kind == SceneAgentKind.Environment);

        var scene = assembler.Build(layout, o => o == jack || o == silo);
        var built = scene.Model;
        var jackBatches = Enumerable.Range(0, built.Geometry.Batches.Count)
            .Where(b => scene.BatchObjects[b] == jack.Agent.Index).ToList();
        Assert.NotEmpty(jackBatches);
        Assert.All(jackBatches, b =>
        {
            var p = built.BatchMaterialParams[b]!;
            Assert.True(p.DiffuseColor.Z > 4 * p.DiffuseColor.X, "tinted blue");
            Assert.Equal(Vector3.One, p.SelfIllumination);
            Assert.Equal(Vector3.One, p.EmissiveColor);
            Assert.True(p.DrawAsOneLayer);
            Assert.NotNull(built.BatchEmissiveMaps[b]);
        });
        Assert.Contains(built.Details, d => d.Contains("Jack: drawn as an effect · tinted #427EFF, glowing #FFFFFF"));
        // The room around him is drawn as before.
        Assert.All(Enumerable.Range(0, built.Geometry.Batches.Count).Except(jackBatches), b =>
        {
            var p = built.BatchMaterialParams[b];
            Assert.True(p is null || (p.DiffuseColor == Vector3.One && p.SelfIllumination == Vector3.Zero && !p.DrawAsOneLayer));
            Assert.Null(built.BatchEmissiveMaps[b]);
        });
    }

    [GameFact(Borderlands)]
    public async Task Hologram_Jack_keeps_his_cyan_outline_in_a_scene_and_on_his_own()
    {
        using var catalog = await Open(Borderlands);
        var cyan = new Vector4(0, 1, 1, 1);
        var ai102 = Build(catalog, "skB1_handsomeJackAI102");
        Assert.All(ai102.BatchMaterialParams, p => Assert.Equal(cyan, p!.OutlineColor));

        var assembler = new SceneAssembler(new SceneAssetResolver(catalog));
        var layout = assembler.Describe(SceneFile.Load(catalog.Read(catalog.Find("jackAI.scene")!)));
        var built = assembler.Build(layout, o => o.Name == "Jack").Model;
        Assert.NotEmpty(built.BatchMaterialParams);
        Assert.All(built.BatchMaterialParams, p =>
        {
            Assert.Equal(cyan, p!.OutlineColor);
            Assert.Equal(0.85f, p.Opacity, 0.002f);
        });
        // His 85% opacity is in the textures' alpha, as on the Characters page.
        Assert.All(built.BatchTextures, t => Assert.True(t!.Rgba.Where((_, i) => i % 4 == 3).Max() <= 218));
    }

    [GameFact(Borderlands)]
    public async Task Borderlands_eye_shells_are_left_out_and_lenses_are_see_through()
    {
        using var catalog = await Open(Borderlands);
        // Vasquez's grey cornea shell (vertex alpha 0) no longer covers his irises.
        var vasquez = Build(catalog, "sk54_vasquez");
        Assert.Contains(vasquez.Details, d => d.Contains("hidden (fully transparent, color_7F7F7F.d3dtx)"));

        foreach (var (id, opacity) in new[] { ("sk55_yvette", 68 / 255f), ("sk54_vaughn", 80 / 255f) })
        {
            var model = Build(catalog, id);
            int lens = model.BatchMaterialParams.ToList().FindIndex(p => p is { Opacity: < 1f });
            Assert.True(lens >= 0, id);
            Assert.Equal(opacity, model.BatchMaterialParams[lens]!.Opacity, 0.002f);
            Assert.Equal(Meshes.MaterialBlendMode.Alpha, model.BatchBlendModes[lens]);
        }
    }
}
