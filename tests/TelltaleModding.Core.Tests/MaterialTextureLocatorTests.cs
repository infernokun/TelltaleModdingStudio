using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

public class MaterialTextureLocatorTests
{
    // A material payload is scanned for 8-byte texture symbols; padding keeps them apart.
    private static byte[] Payload(params string[] textures) =>
        textures.SelectMany(t => BitConverter.GetBytes(TelltaleCrc64.HashSymbol(t)).Concat(new byte[8])).ToArray();

    private static string? Lookup(ulong symbol, string[] known) =>
        known.FirstOrDefault(t => TelltaleCrc64.HashSymbol(t) == symbol);

    [SkippableFact]
    public void Real_diffuse_wins_over_engine_placeholders_listed_first()
    {
        // Alvin (Definitive Series): color_000 fills unused slots ahead of the real diffuse.
        string[] textures =
        {
            "sk54_alvin_body_nm.d3dtx", "color_000.d3dtx", "color_000.d3dtx", "sk54_alvin_body_detail.d3dtx",
            "sk54_alvin_body.d3dtx", "sk54_alvin_body_spec.d3dtx",
        };
        Assert.Equal("sk54_alvin_body.d3dtx",
            MaterialTextureLocator.PickDiffuseTexture(Payload(textures), s => Lookup(s, textures)));
    }

    [SkippableFact]
    public void Colour_texture_is_the_stem_the_other_maps_extend()
    {
        // Clementine 305: a damage mask and a damage overlay are listed around the real head.
        string[] textures =
        {
            "sk62_clementine_headWrinkle_nm.d3dtx", "sk62_clementine_head_nm.d3dtx",
            "sk62_clementine_head303_damageMask.d3dtx", "color_000.d3dtx", "sk62_clementine_head_detail.d3dtx",
            "sk62_clementine_head.d3dtx", "sk_wd3SharedParts_head_visibility.d3dtx",
            "sk62_clementine_headDamage303.d3dtx", "sk62_clementine_head_spec.d3dtx",
        };
        Assert.Equal("sk62_clementine_head.d3dtx",
            MaterialTextureLocator.PickDiffuseTexture(Payload(textures), s => Lookup(s, textures)));
    }

    [SkippableFact]
    public void Normal_map_matches_the_colour_texture_and_skips_wrinkle_maps()
    {
        string[] textures =
        {
            "sk62_clementine_headWrinkle_nm.d3dtx", "sk62_clementine_headWrinkleB_mask.d3dtx",
            "sk62_clementine_head_nm.d3dtx", "sk62_clementine_head.d3dtx", "sk62_clementine_head_spec.d3dtx",
        };
        Assert.Equal("sk62_clementine_head_nm.d3dtx",
            MaterialTextureLocator.PickNormalMap(Payload(textures), s => Lookup(s, textures)));

        // Only a wrinkle map: better no normal map than a permanently wrinkled face.
        string[] wrinkleOnly = { "face_wrinkle_nm.d3dtx", "face.d3dtx" };
        Assert.Null(MaterialTextureLocator.PickNormalMap(Payload(wrinkleOnly), s => Lookup(s, wrinkleOnly)));
    }

    [SkippableFact]
    public void Placeholder_is_used_over_maps_when_no_real_diffuse_exists()
    {
        string[] textures = { "prop_nm.d3dtx", "color_7F7F7F.d3dtx" };
        Assert.Equal("color_7F7F7F.d3dtx",
            MaterialTextureLocator.PickDiffuseTexture(Payload(textures), s => Lookup(s, textures)));
    }

    [SkippableTheory]
    [InlineData("color_000.d3dtx", true)]
    [InlineData("color_EEE", true)]
    [InlineData("color_BBC344.d3dtx", true)]
    [InlineData("color_array_000.d3dtx", true)]
    [InlineData("normalxy_000.d3dtx", true)]
    [InlineData("color_shirtRed.d3dtx", false)]
    [InlineData("sk54_alvin_body.d3dtx", false)]
    public void Recognises_engine_placeholders(string name, bool expected) =>
        Assert.Equal(expected, MaterialTextureLocator.IsEnginePlaceholder(name));
}
