using System.Numerics;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// Ambient light estimated from a Definitive Series scene's light-probe snapshot
/// (<c>&lt;scene&gt;_module_lightprobe.d3dtx</c>): a small, upright render of the location.
/// The mean of its top rows stands in for light from above, the bottom rows for light
/// bounced off the ground.
/// </summary>
public static class SceneProbeAmbient
{
    /// <summary>Fraction of the image's rows averaged for each of sky and ground.</summary>
    private const float Band = 0.4f;

    /// <summary>Finds and reads the snapshot for <paramref name="layout"/>'s scene; null when there is none.</summary>
    public static (Vector3 Sky, Vector3 Ground)? Find(SceneLayout layout, ISceneAssetResolver assets)
    {
        var name = TextureName(layout.Scene.Name);
        try
        {
            return assets.TryRead(name) is { } bytes ? FromTexture(bytes) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The snapshot's file name for a scene (<c>adv_x.scene</c> → <c>adv_x_module_lightprobe.d3dtx</c>).</summary>
    public static string TextureName(string sceneName) =>
        Path.GetFileNameWithoutExtension(sceneName) + "_module_lightprobe.d3dtx";

    /// <summary>Sky and ground ambient (linear RGB) from a snapshot texture's bytes; null when it cannot be decoded.</summary>
    public static (Vector3 Sky, Vector3 Ground)? FromTexture(byte[] d3dtx)
    {
        if (!D3dtxTexture.TryLoad(d3dtx, out var texture, out _) ||
            !texture!.TryToDds(out var dds, out _) ||
            !DdsImage.TryDecode(dds!, out var image, out _))
            return null;
        return FromImage(image!.Rgba, image.Width, image.Height);
    }

    /// <summary>Sky and ground ambient (linear RGB) from sRGB RGBA pixels.</summary>
    public static (Vector3 Sky, Vector3 Ground)? FromImage(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            return null;
        int band = Math.Max(1, (int)(height * Band));
        return (Mean(rgba, width, 0, band), Mean(rgba, width, height - band, height));
    }

    private static Vector3 Mean(byte[] rgba, int width, int rowStart, int rowEnd)
    {
        var sum = Vector3.Zero;
        for (int y = rowStart; y < rowEnd; y++)
        for (int x = 0; x < width; x++)
        {
            int at = (y * width + x) * 4;
            sum += SceneLighting.SrgbToLinear(new Vector3(rgba[at], rgba[at + 1], rgba[at + 2]) / 255f);
        }
        return sum / Math.Max(1, (rowEnd - rowStart) * width);
    }
}
