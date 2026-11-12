namespace TelltaleModding.Core.Textures;

/// <summary>
/// Puts a normal map's channels where the viewer reads them. Newer textures carry a swizzle
/// saying which channel X, Y and Z come from. The viewer reads BC3 and BC2 maps as X in blue
/// and Y in alpha, and everything else as X, Y, Z in red, green, blue.
/// </summary>
public static class NormalMapSwizzle
{
    private const byte Zero = 4, One = 5;

    /// <summary>
    /// Rearranges <paramref name="image"/> by <paramref name="swizzle"/> into the viewer's layout,
    /// or returns it as is when there's no swizzle or it already matches.
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
