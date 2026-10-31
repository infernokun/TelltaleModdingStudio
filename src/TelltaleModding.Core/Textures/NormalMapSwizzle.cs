namespace TelltaleModding.Core.Textures;

/// <summary>
/// Puts a normal map's channels where the viewer reads them. Newer textures (Michonne, Tales
/// from the Borderlands, the Definitive Series) carry a swizzle naming the channel each of X,
/// Y and Z comes from: their BC3 normal maps store X in alpha, Y in blue and Z in green
/// (swizzle 3, 2, 1, 0), as the games' shaders read them. Older textures (The Wolf Among Us,
/// Game of Thrones, the original Season Two) have no swizzle and keep X in blue and Y in
/// alpha, which is how the viewer reads BC3 and BC2 maps; BC5 maps it reads as X in red and
/// Y in green, anything else as X, Y, Z in red, green, blue.
/// </summary>
public static class NormalMapSwizzle
{
    private const byte Zero = 4, One = 5;

    /// <summary>
    /// <paramref name="image"/> rearranged by <paramref name="swizzle"/> into the viewer's layout
    /// for its format, or the image itself when it has no swizzle or already matches.
    /// </summary>
    public static DdsImage Apply(DdsImage image, IReadOnlyList<byte>? swizzle)
    {
        if (swizzle is not { Count: >= 3 })
            return image;
        bool alphaLayout = image.Format is DdsPixelFormat.Bc3 or DdsPixelFormat.Bc2;
        // Where X, Y and Z go: blue, alpha, green for BC3/BC2; red, green, blue otherwise.
        (int X, int Y, int Z, int Spare) target = alphaLayout ? (2, 3, 1, 0) : (0, 1, 2, 3);
        if (swizzle[0] == target.X && swizzle[1] == target.Y && swizzle[2] == target.Z)
            return image;

        var src = image.Rgba;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i + target.X] = Pick(src, i, swizzle[0]);
            dst[i + target.Y] = Pick(src, i, swizzle[1]);
            dst[i + target.Z] = Pick(src, i, swizzle[2]);
            dst[i + target.Spare] = 255;
        }
        return image.WithPixels(dst);
    }

    private static byte Pick(byte[] rgba, int pixel, byte channel) => channel switch
    {
        <= 3 => rgba[pixel + channel],
        Zero => 0,
        One => 255,
        // Unknown selectors read as a flat normal component.
        _ => 128,
    };
}
