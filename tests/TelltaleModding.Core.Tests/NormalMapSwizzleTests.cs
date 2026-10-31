using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

/// <summary>Normal maps put where the viewer reads them, from the swizzle their texture carries.</summary>
public class NormalMapSwizzleTests
{
    private static (D3dtxTexture Texture, DdsImage Image) Load(string sample)
    {
        Assert.True(D3dtxTexture.TryLoad(File.ReadAllBytes(SampleData.Sample(sample)), out var texture, out var error), error);
        Assert.True(texture!.TryToDds(out var dds, out error), error);
        Assert.True(DdsImage.TryDecode(dds!, out var image, out error), error);
        return (texture, image!);
    }

    [SkippableFact]
    public void Newer_bc3_normal_maps_store_x_in_alpha_and_y_in_blue()
    {
        // Tales from the Borderlands, like the Definitive Series and Michonne: X from alpha,
        // Y from blue, Z from green, as the game's shaders read them.
        var (texture, image) = Load("BanditPsychoMask_Nrm.d3dtx");
        Assert.Equal(new byte[] { 3, 2, 1, 0 }, texture.Swizzle);
        Assert.Equal(DdsPixelFormat.Bc3, image.Format);

        var fixedUp = NormalMapSwizzle.Apply(image, texture.Swizzle);
        Assert.NotSame(image, fixedUp);
        for (int i = 0; i < image.Rgba.Length; i += 4 * 97)
        {
            Assert.Equal(image.Rgba[i + 3], fixedUp.Rgba[i + 2]); // X: alpha → where the viewer reads X (blue)
            Assert.Equal(image.Rgba[i + 2], fixedUp.Rgba[i + 3]); // Y: blue → alpha
            Assert.Equal(image.Rgba[i + 1], fixedUp.Rgba[i + 1]); // Z stays in green
        }
    }

    [SkippableFact]
    public void Maps_without_a_swizzle_or_already_in_place_are_left_alone()
    {
        var (_, image) = Load("BanditPsychoMask_Nrm.d3dtx");
        // The older games' textures carry no swizzle; they already store X in blue and Y in alpha.
        Assert.Same(image, NormalMapSwizzle.Apply(image, null));
        Assert.Same(image, NormalMapSwizzle.Apply(image, new byte[] { 2, 3, 1, 0 }));
    }
}
