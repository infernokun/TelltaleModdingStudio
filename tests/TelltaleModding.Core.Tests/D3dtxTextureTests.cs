using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

public class D3dtxTextureTests
{
    private static D3dtxTexture Load(string path)
    {
        Assert.True(D3dtxTexture.TryLoad(File.ReadAllBytes(path), out var tex, out var error), error);
        return tex!;
    }

    [SkippableFact]
    public void Parses_bc1_solid_color_texture()
    {
        var tex = Load(SampleData.Sample("color_EEE.d3dtx"));
        Assert.Equal(4, tex.Width);
        Assert.Equal(4, tex.Height);
        Assert.Equal(1, tex.MipCount);
        Assert.Equal(T3SurfaceFormat.BC1, tex.SurfaceFormat);
    }

    [SkippableFact]
    public void Parses_and_decodes_definitive_series_v9_texture()
    {
        var tex = Load(SampleData.Sample("sk62_clementine400_head.d3dtx"));
        Assert.Equal("sk62_clementine400_head.d3dtx", tex.Name);
        Assert.Equal(1024, tex.Width);
        Assert.Equal(1024, tex.Height);
        Assert.Equal(9, tex.MipCount);
        Assert.Equal(T3SurfaceFormat.BC1, tex.SurfaceFormat);

        Assert.True(DdsImage.TryDecode(tex.ToDds(), out var image, out var error), error);
        Assert.Equal(1024, image!.Width);
    }

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
    public void Converts_a8_texture_and_preview_decodes_it()
    {
        var tex = Load(SampleData.Sample("fx_flyWD.d3dtx"));
        Assert.Equal(T3SurfaceFormat.A8, tex.SurfaceFormat);
        Assert.Equal(128, tex.Width);

        var dds = tex.ToDds();
        Assert.True(DdsImage.TryDecode(dds, out var image, out var error), error);
        Assert.Equal(128, image!.Width);
    }

    [SkippableFact]
    public void Parses_headerless_v7_payload()
    {
        var tex = Load(SampleData.Sample("sk55_sasha_bodyLower_detail.d3dtx"));
        Assert.True(tex.Width > 0);
        Assert.True(tex.Regions.Count > 0);
        Assert.True(tex.TryToDds(out var dds, out var error), error);
        Assert.True(DdsImage.TryDecode(dds!, out _, out var decodeError), decodeError);
    }

    [SkippableTheory]
    // The Wolf Among Us: version 4, region headers without a face index.
    [InlineData("sk_sharedParts_eyeBrown.d3dtx", 256, 7, T3SurfaceFormat.BC1)]
    [InlineData("sk54_bigby_head.d3dtx", 512, 8, T3SurfaceFormat.BC1)]
    // Game of Thrones: version 5.
    [InlineData("sk_gotSharedParts_eyesPupil.d3dtx", 128, 8, T3SurfaceFormat.A8)]
    public void Parses_and_decodes_msv5_era_texture(string sample, int size, int mips, T3SurfaceFormat format)
    {
        var tex = Load(SampleData.Sample(sample));
        Assert.Equal(sample, tex.Name);
        Assert.Equal(size, tex.Width);
        Assert.Equal(size, tex.Height);
        Assert.Equal(mips, tex.MipCount);
        Assert.Equal(format, tex.SurfaceFormat);
        Assert.Equal(mips, tex.Regions.Count);
        Assert.Equal(tex.MipCount - 1, tex.Regions[0].MipIndex);

        Assert.True(DdsImage.TryDecode(tex.ToDds(), out var image, out var error), error);
        Assert.Equal(size, image!.Width);
    }

    [SkippableFact]
    public void Msv5_era_face_texture_decodes_to_skin_tones()
    {
        // A misplaced mip or misread header would decode to noise; Bigby's face texture
        // averages to a warm skin color (red > green > blue).
        var tex = Load(SampleData.Sample("sk54_bigby_head.d3dtx"));
        Assert.True(DdsImage.TryDecode(tex.ToDds(), out var image, out var error), error);
        double r = 0, g = 0, b = 0;
        for (int i = 0; i < image!.Rgba.Length; i += 4)
        {
            r += image.Rgba[i];
            g += image.Rgba[i + 1];
            b += image.Rgba[i + 2];
        }
        Assert.True(r > g && g > b, $"average color {r} {g} {b}");
    }

    [SkippableFact]
    public void Rejects_garbage()
    {
        Assert.False(D3dtxTexture.TryLoad("certainly not a texture"u8.ToArray(), out _, out var error));
        Assert.NotNull(error);
    }

    [SkippableFact]
    public void Rejects_a_mesh_file()
    {
        Assert.False(D3dtxTexture.TryLoad(File.ReadAllBytes(SampleData.MeshSample), out _, out _));
    }
}
