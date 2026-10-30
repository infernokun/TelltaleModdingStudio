using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

public class MaterialDataTests
{
    private static D3dMeshFile Mesh(string name)
    {
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var mesh, out var error), error);
        return mesh!;
    }

    private static IReadOnlyList<MaterialData> Materials(D3dMeshFile mesh) =>
        mesh.MaterialHandles.Select(h =>
        {
            var payload = mesh.MaterialResources.First(r => r.NameSymbol == h).Payload;
            return MaterialData.TryParse(payload) ?? throw new Xunit.Sdk.XunitException("material did not parse");
        }).ToList();

    private static ulong Diffuse(MaterialData m) =>
        m.Textures.Single(t => t.Key == MaterialTextureLocator.DiffuseTextureKey).TextureSymbol;

    [SkippableFact]
    public void Definitive_series_head_binds_its_diffuse_property_and_draws_opaque()
    {
        var head = Materials(Mesh("sk62_clementine400_head.d3dmesh")).Single();

        Assert.True(head.IsCompiled);
        Assert.Equal(MaterialBlendMode.Normal, head.BlendMode);
        Assert.False(head.IsBlended);
        Assert.Equal(TelltaleCrc64.HashSymbol("sk62_clementine400_head.d3dtx"), Diffuse(head));
        // The compiled data gives each texture a role: the normal map is never a color map.
        var normal = head.Textures.Single(t => t.Key == MaterialTextureLocator.NormalMapKey);
        Assert.Equal(MaterialTextureTypes.NormalXYMap, normal.Types);
        Assert.True(head.Textures.Single(t => t.Key == MaterialTextureLocator.DiffuseTextureKey).Types
            .HasFlag(MaterialTextureTypes.ColorMap));
    }

    [SkippableFact]
    public void Cornea_layer_has_no_diffuse_property_and_blends()
    {
        var materials = Materials(Mesh("sk62_clementine400_eyesMouth.d3dmesh"));

        var cornea = materials.Where(m => !MaterialTextureLocator.HasDiffuseProperty(m)).ToList();
        Assert.Single(cornea);
        Assert.True(cornea[0].IsBlended);
        Assert.Contains(materials, m => MaterialTextureLocator.HasDiffuseProperty(m) && m.BlendMode == MaterialBlendMode.Alpha);
    }

    [SkippableFact]
    public void Engine_diffuse_beats_name_heuristics()
    {
        // "sk55_norma_…" contains "_norm", so judged by name every one of Norma's textures once
        // looked like a normal map and the black placeholder won. The property names the
        // diffuse, and the name check now needs the marker to end its word.
        var material = Materials(Mesh("sk62_clementine400_head.d3dmesh")).Single();
        var names = new Dictionary<ulong, string>
        {
            [Diffuse(material)] = "sk55_norma_bodyUpper.d3dtx",
        };
        foreach (var t in material.Textures.Where(t => t.Key != MaterialTextureLocator.DiffuseTextureKey))
            names.TryAdd(t.TextureSymbol, "color_000.d3dtx");
        var payload = Mesh("sk62_clementine400_head.d3dmesh").MaterialResources.Single().Payload;

        Assert.Equal("sk55_norma_bodyUpper.d3dtx",
            MaterialTextureLocator.PickDiffuseTexture(material, payload, s => names.GetValueOrDefault(s)));
        Assert.Equal("sk55_norma_bodyUpper.d3dtx",
            MaterialTextureLocator.PickDiffuseTexture(payload, s => names.GetValueOrDefault(s)));
    }

    [SkippableFact]
    public void Borderlands_era_materials_name_their_diffuse_but_not_one_blend_mode()
    {
        var materials = Materials(Mesh("skB4_banditMarauder_body.d3dmesh"));

        Assert.NotEmpty(materials);
        Assert.All(materials, m => Assert.Contains(TelltaleCrc64.HashSymbol("module_material.prop"), m.Parents));
        Assert.Contains(materials, MaterialTextureLocator.HasDiffuseProperty);
        // Older games compile a variant per blend mode, so no single mode is reported.
        Assert.All(materials, m => Assert.Null(m.BlendMode));
    }

    [SkippableFact]
    public void Borderlands_lens_material_sets_its_blend_mode_as_a_property()
    {
        var materials = Materials(Mesh("tftb_sk54_vaughn_glasses.d3dmesh"));

        // The frame draws as compiled (no single mode); the lens names Alpha outright, and the
        // compiled data after that property is still read.
        Assert.Null(materials[0].BlendModeProperty);
        Assert.Null(materials[0].BlendMode);
        var lens = materials[1];
        Assert.Equal(MaterialBlendMode.Alpha, lens.BlendModeProperty);
        Assert.Equal(MaterialBlendMode.Alpha, lens.BlendMode);
        Assert.True(lens.IsBlended);
        Assert.True(lens.IsCompiled);
        Assert.Equal(1f, lens.Floats[MaterialShading.AlphaKey]);
        Assert.Equal(MaterialTextureTypes.PrimaryColorMap,
            lens.Textures.Single(t => t.Key == MaterialTextureLocator.DiffuseTextureKey).Types);
    }
}
