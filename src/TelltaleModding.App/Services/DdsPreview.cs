using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.App.Services;

/// <summary>Turns a decoded <see cref="DdsImage"/> into an Avalonia bitmap for display.</summary>
public static class DdsPreview
{
    /// <summary>
    /// Attempts to load a DDS file into a bitmap. Returns null (with a reason) for
    /// formats the decoder does not support.
    /// </summary>
    public static (Bitmap? Bitmap, string? Error) TryLoad(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return (null, $"Could not read file: {ex.Message}");
        }

        if (!DdsImage.TryDecode(bytes, out var image, out var error))
            return (null, error);

        return (ToBitmap(image!), null);
    }

    /// <summary>Copies the RGBA buffer into a WriteableBitmap.</summary>
    public static Bitmap ToBitmap(DdsImage image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);

        using var fb = bitmap.Lock();
        unsafe
        {
            fixed (byte* src = image.Rgba)
            {
                int srcStride = image.Width * 4;
                for (int y = 0; y < image.Height; y++)
                {
                    Buffer.MemoryCopy(
                        src + y * srcStride,
                        (byte*)fb.Address + y * fb.RowBytes,
                        fb.RowBytes,
                        srcStride);
                }
            }
        }
        return bitmap;
    }
}
