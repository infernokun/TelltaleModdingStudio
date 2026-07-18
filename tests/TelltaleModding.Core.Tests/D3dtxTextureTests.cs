using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

public class D3dtxTextureTests
{
    private static D3dtxTexture Load(string path)
    {
        Assert.True(D3dtxTexture.TryLoad(File.ReadAllBytes(path), out var tex, out var error), error);
        return tex!;
    }

    [Fact]
    public void Parses_bc1_solid_color_texture()
    {
        var tex = Load(SampleData.Sample("color_EEE.d3dtx"));
        Assert.Equal(4, tex.Width);
        Assert.Equal(4, tex.Height);
        Assert.Equal(1, tex.MipCount);
        Assert.Equal(T3SurfaceFormat.BC1, tex.SurfaceFormat);
    }

    [Fact]
    public void Converts_bc1_to_dds_that_decodes_to_its_solid_color()
    {
        var tex = Load(SampleData.Sample("color_EEE.d3dtx"));
        var dds = tex.ToDds();

        Assert.True(DdsImage.TryDecode(dds, out var image, out var error), error);
        Assert.Equal(4, image!.Width);

        // The single BC1 block encodes one uniform color (dark gray, ~RGB 12,13,12);
        // every decoded pixel must match the first, and the payload must be untouched.
        for (int i = 4; i < image.Rgba.Length; i += 4)
        {
            Assert.Equal(image.Rgba[0], image.Rgba[i]);
            Assert.Equal(image.Rgba[1], image.Rgba[i + 1]);
            Assert.Equal(image.Rgba[2], image.Rgba[i + 2]);
        }
        Assert.InRange(image.Rgba[0], 8, 16);
        var original = File.ReadAllBytes(SampleData.Sample("color_EEE.d3dtx"));
        Assert.Equal(original[^8..], dds[128..]);
    }

    [Fact]
    public void Converts_mipmapped_bc3_normal_map()
    {
        var tex = Load(SampleData.Sample("BanditPsychoMask_Nrm.d3dtx"));
        Assert.Equal(T3SurfaceFormat.BC3, tex.SurfaceFormat);
        Assert.Equal(256, tex.Width);
        Assert.Equal(7, tex.MipCount);

        var dds = tex.ToDds();
        Assert.Equal(128 + tex.Regions.Sum(r => r.DataSize), dds.Length);
        Assert.True(DdsImage.TryDecode(dds, out var image, out var error), error);
        Assert.Equal(256, image!.Width);
    }

    [Fact]
    public void Dds_mip_chain_is_largest_first()
    {
        var tex = Load(SampleData.Sample("BanditPsychoMask_Nrm.d3dtx"));
        // d3dtx stores smallest mip first; conversion must re-order.
        Assert.Equal(tex.MipCount - 1, tex.Regions[0].MipIndex);

        var dds = tex.ToDds();
        // Mip 0 of a 256×256 BC3 texture is 64×64 blocks × 16 bytes = 65536 bytes.
        var mip0 = tex.Regions.Single(r => r.MipIndex == 0);
        Assert.Equal(65536, mip0.DataSize);
        var expected = File.ReadAllBytes(SampleData.Sample("BanditPsychoMask_Nrm.d3dtx"))[^mip0.DataSize..];
        Assert.Equal(expected, dds[128..(128 + mip0.DataSize)]);
    }

    [Fact]
    public void Converts_a8_texture_and_preview_decodes_it()
    {
        var tex = Load(SampleData.Sample("fx_flyWD.d3dtx"));
        Assert.Equal(T3SurfaceFormat.A8, tex.SurfaceFormat);
        Assert.Equal(128, tex.Width);

        var dds = tex.ToDds();
        Assert.True(DdsImage.TryDecode(dds, out var image, out var error), error);
        Assert.Equal(128, image!.Width);
    }

    [Fact]
    public void Parses_headerless_v7_payload()
    {
        var tex = Load(SampleData.Sample("sk55_sasha_bodyLower_detail.d3dtx"));
        Assert.True(tex.Width > 0);
        Assert.True(tex.Regions.Count > 0);
        Assert.True(tex.TryToDds(out var dds, out var error), error);
        Assert.True(DdsImage.TryDecode(dds!, out _, out var decodeError), decodeError);
    }

    [Fact]
    public void Rejects_garbage()
    {
        Assert.False(D3dtxTexture.TryLoad("certainly not a texture"u8.ToArray(), out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Rejects_a_mesh_file()
    {
        Assert.False(D3dtxTexture.TryLoad(File.ReadAllBytes(SampleData.MeshSample), out _, out _));
    }
}
