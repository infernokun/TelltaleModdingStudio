using System.Buffers.Binary;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

public class DdsImageTests
{
    [SkippableFact]
    public void RejectsNonDds()
    {
        var ok = DdsImage.TryDecode(new byte[200], out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [SkippableFact]
    public void DecodesUncompressed32BitBgra()
    {
        // One opaque red A8R8G8B8 pixel.
        var header = BuildHeader(1, 1, fourCc: null,
            rMask: 0x00FF0000, gMask: 0x0000FF00, bMask: 0x000000FF, aMask: 0xFF000000);
        var pixel = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pixel, 0xFFFF0000); // A=FF, R=FF
        var dds = Concat(header, pixel);

        var img = DdsImage.Decode(dds);

        Assert.Equal(1, img.Width);
        Assert.Equal(1, img.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, img.Rgba);
    }

    [SkippableFact]
    public void DecodesDxt1SolidRedBlock()
    {
        var header = BuildHeader(4, 4, fourCc: "DXT1");
        // Both colors are pure red, so the whole block is red.
        var block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0), 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), 0xF800);
        var dds = Concat(header, block);

        var img = DdsImage.Decode(dds);

        Assert.Equal(4, img.Width);
        Assert.Equal(4, img.Height);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(255, img.Rgba[i * 4 + 0]); // R
            Assert.Equal(0, img.Rgba[i * 4 + 1]);   // G
            Assert.Equal(0, img.Rgba[i * 4 + 2]);   // B
            Assert.Equal(255, img.Rgba[i * 4 + 3]); // A
        }
    }

    [SkippableTheory]
    [InlineData("DXT1")]
    [InlineData("DXT5")]
    public void Interpolated_colors_weight_toward_their_nearer_endpoint(string fourCc)
    {
        // c0 is white, c1 is black, and the pixels use indices 0 to 3 in turn.
        var color = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(color.AsSpan(0), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(color.AsSpan(2), 0x0000);
        BinaryPrimitives.WriteUInt32LittleEndian(color.AsSpan(4), 0b11_10_01_00);
        var block = fourCc == "DXT1" ? color : Concat(new byte[] { 255, 255, 0, 0, 0, 0, 0, 0 }, color);

        var img = DdsImage.Decode(Concat(BuildHeader(4, 4, fourCc), block));

        Assert.Equal(255, img.Rgba[0]);      // index 0 → c0
        Assert.Equal(0, img.Rgba[4]);        // index 1 → c1
        Assert.Equal(170, img.Rgba[8]);      // index 2 → (2·255 + 0)/3
        Assert.Equal(85, img.Rgba[12]);      // index 3 → (255 + 0)/3
    }

    [SkippableFact]
    public void DecodesDxt5OpaqueBlock()
    {
        var header = BuildHeader(4, 4, fourCc: "DXT5");
        var block = new byte[16];
        // Both alpha endpoints are 255, so every pixel is opaque.
        block[0] = 255;
        block[1] = 255;
        // Both colors are pure green, so the whole block is green.
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), 0x07E0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(10), 0x07E0);
        var dds = Concat(header, block);

        var img = DdsImage.Decode(dds);

        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(0, img.Rgba[i * 4 + 0]);   // R
            Assert.Equal(255, img.Rgba[i * 4 + 1]); // G
            Assert.Equal(0, img.Rgba[i * 4 + 2]);   // B
            Assert.Equal(255, img.Rgba[i * 4 + 3]); // A
        }
    }

    [SkippableFact]
    public void ReportsUnsupportedDx10Gracefully()
    {
        var header = BuildHeader(4, 4, fourCc: "DX10");
        var ok = DdsImage.TryDecode(Concat(header, new byte[64]), out var img, out var error);
        Assert.False(ok);
        Assert.Null(img);
        Assert.Contains("DX10", error);
    }

    [SkippableFact]
    public void ReportsTruncatedData()
    {
        var header = BuildHeader(64, 64, fourCc: "DXT1");
        var ok = DdsImage.TryDecode(Concat(header, new byte[8]), out _, out var error);
        Assert.False(ok);
        Assert.Contains("truncated", error!, StringComparison.OrdinalIgnoreCase);
    }

    // --- helpers ---

    private static byte[] BuildHeader(int width, int height, string? fourCc,
        uint rMask = 0, uint gMask = 0, uint bMask = 0, uint aMask = 0)
    {
        var h = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0), 0x20534444); // "DDS "
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), 124);        // dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(76), 32);        // pf dwSize

        if (fourCc is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 0x4);   // DDPF_FOURCC
            var cc = System.Text.Encoding.ASCII.GetBytes(fourCc);
            Array.Copy(cc, 0, h, 84, 4);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 0x40);  // DDPF_RGB
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), 32);    // rgbBitCount
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(92), rMask);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(96), gMask);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(100), bMask);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(104), aMask);
        }
        return h;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Array.Copy(a, r, a.Length);
        Array.Copy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
