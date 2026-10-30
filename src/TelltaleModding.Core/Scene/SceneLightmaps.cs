using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// Baked lightmaps of an assembled scene: a second UV set laid out over per-mesh lightmap
/// atlases (Tales from the Borderlands: <c>&lt;mesh&gt;_000.d3dtx</c> sampled with texcoord set 5).
/// Lighting at a texel is <c>sample.rgb × <see cref="Scale"/> × <see cref="Intensity"/></c>;
/// a lit color is the diffuse color times that (plus any dynamic lights).
/// </summary>
public sealed class SceneLightmaps
{
    /// <summary>Stored lighting is divided by this so up to twice full brightness fits in 8 bits.</summary>
    public const float Scale = 2f;

    /// <summary>Lightmap UVs, one per vertex of <see cref="AssembledScene.Model"/>'s geometry (zero where a batch has none).</summary>
    public required Vector2[] Uv2 { get; init; }

    /// <summary>For each batch of the geometry, its lightmap (lighting ÷ <see cref="Scale"/>, alpha 255), or null.</summary>
    public required IReadOnlyList<DdsImage?> BatchLightmaps { get; init; }

    /// <summary>The scene's lightmap intensity ("HDR lightmaps Intensity", 1 when unset).</summary>
    public float Intensity { get; init; } = 1;

    /// <summary>Batches that have a lightmap.</summary>
    public int LightmappedBatches => BatchLightmaps.Count(b => b is not null);
}

/// <summary>Reads lightmap UVs from meshes and decodes lightmap atlases.</summary>
public static class SceneLightmapReader
{
    /// <summary>Texcoord set the games lay lightmaps out on.</summary>
    public const int LightmapTexCoordSet = 5;

    /// <summary>
    /// HDR atlases (BC3) store lighting as RGB × alpha × this ("RGBM"); a texel's alpha is
    /// about 1/6 where the lighting is at or below full brightness.
    /// </summary>
    public const float RgbmRange = 6f;

    /// <summary>
    /// The lightmap UVs of a mesh whose vertex buffers live in the file's async section (Tales
    /// from the Borderlands), one per vertex; null when the mesh has no lightmap texcoord set.
    /// </summary>
    public static Vector2[]? ReadUvs(byte[] meshFile, D3dMeshFile mesh)
    {
        if (mesh.LegacyComponents.Count > 0 || mesh.PlatformAttributes.Count > 0)
            return null;
        var attr = mesh.Attributes.FirstOrDefault(a => a.Attribute == GfxVertexAttribute.TexCoord && a.AttributeIndex == LightmapTexCoordSet);
        if (attr.Attribute != GfxVertexAttribute.TexCoord || attr.AttributeIndex != LightmapTexCoordSet)
            return null;
        if (!MetaStreamHeader.TryParse(meshFile, out var header, out _, isCompleteFile: false) ||
            attr.BufferIndex < 0 || attr.BufferIndex >= mesh.VertexBuffers.Count || mesh.VertexBuffers.Count == 0)
            return null;
        var buffer = mesh.VertexBuffers[attr.BufferIndex];
        int count = mesh.VertexBuffers[0].Count;
        int size = attr.Format switch { GfxFormat.SN16x2 => 4, GfxFormat.F32x2 => 8, _ => 0 };
        long start = (long)header!.AsyncSectionOffset + buffer.DataOffset;
        if (size == 0 || buffer.Count != count || attr.BufferOffset + size > buffer.Stride ||
            start + (long)buffer.Stride * count > Math.Min(meshFile.Length, (long)header.AsyncSectionOffset + header.AsyncSectionSize))
            return null;
        var uvs = new Vector2[count];
        var span = meshFile.AsSpan();
        for (int i = 0; i < count; i++)
        {
            int at = (int)start + i * buffer.Stride + attr.BufferOffset;
            uvs[i] = attr.Format == GfxFormat.F32x2
                ? new Vector2(BinaryPrimitives.ReadSingleLittleEndian(span[at..]), BinaryPrimitives.ReadSingleLittleEndian(span[(at + 4)..]))
                : new Vector2(Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(span[at..]) / 32767f),
                              Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(span[(at + 2)..]) / 32767f));
        }
        return uvs;
    }

    /// <summary>
    /// Decodes a lightmap atlas to RGBA with lighting ÷ <see cref="SceneLightmaps.Scale"/>
    /// (HDR atlases expanded from RGBM), box-filtered down to at most <paramref name="maxSize"/>
    /// on a side (0 keeps full size). Null when it cannot be decoded.
    /// </summary>
    public static DdsImage? Decode(byte[] d3dtx, int maxSize = 0)
    {
        if (!D3dtxTexture.TryLoad(d3dtx, out var texture, out _) ||
            !texture!.TryToDds(out var dds, out _) ||
            !DdsImage.TryDecode(dds!, out var image, out _))
            return null;
        bool rgbm = texture.SurfaceFormat is T3SurfaceFormat.BC3 or T3SurfaceFormat.ARGB8;
        var rgba = (byte[])image!.Rgba.Clone();
        for (int i = 0; i < rgba.Length; i += 4)
        {
            float m = rgbm ? rgba[i + 3] / 255f * RgbmRange : 1f;
            for (int c = 0; c < 3; c++)
                rgba[i + c] = (byte)Math.Clamp(MathF.Round(rgba[i + c] * m / SceneLightmaps.Scale), 0, 255);
            rgba[i + 3] = 255;
        }
        int width = image.Width, height = image.Height;
        while (maxSize > 0 && (width > maxSize || height > maxSize))
            (rgba, width, height) = Halve(rgba, width, height);
        return DdsImage.TryDecode(WrapRgba(rgba, width, height), out var result, out _) ? result : null;
    }

    private static (byte[] Rgba, int Width, int Height) Halve(byte[] rgba, int width, int height)
    {
        int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
        var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int x0 = Math.Min(width - 1, x * 2), x1 = Math.Min(width - 1, x * 2 + 1);
            int y0 = Math.Min(height - 1, y * 2), y1 = Math.Min(height - 1, y * 2 + 1);
            for (int c = 0; c < 4; c++)
                result[(y * w + x) * 4 + c] = (byte)((rgba[(y0 * width + x0) * 4 + c] + rgba[(y0 * width + x1) * 4 + c] +
                                                       rgba[(y1 * width + x0) * 4 + c] + rgba[(y1 * width + x1) * 4 + c] + 2) / 4);
        }
        return (result, w, h);
    }

    /// <summary>Wraps RGBA pixels in an uncompressed 32-bit DDS.</summary>
    private static byte[] WrapRgba(byte[] rgba, int width, int height)
    {
        var dds = new byte[128 + rgba.Length];
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(at), value);
        Put(0, 0x20534444);
        Put(4, 124);
        Put(8, 0x100F);
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(20, (uint)(width * 4));
        Put(76, 32);
        Put(80, 0x41);
        Put(88, 32);
        Put(92, 0x000000FF);
        Put(96, 0x0000FF00);
        Put(100, 0x00FF0000);
        Put(104, 0xFF000000);
        Put(108, 0x1000);
        rgba.CopyTo(dds, 128);
        return dds;
    }
}
