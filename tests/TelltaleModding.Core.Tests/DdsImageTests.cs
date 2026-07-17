using System.Buffers.Binary;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

public class DdsImageTests
{
    [Fact]
    public void RejectsNonDds()
    {
        var ok = DdsImage.TryDecode(new byte[200], out _, out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void DecodesUncompressed32BitBgra()
    {
        // A8R8G8B8 masks; one red pixel (R=255,G=0,B=0,A=255).
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

    [Fact]
    public void DecodesDxt1SolidRedBlock()
    {
        var header = BuildHeader(4, 4, fourCc: "DXT1");
        // c0 = c1 = 0xF800 (pure red in RGB565), indices all 0 -> whole block is color 0.
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

    [Fact]
    public void DecodesDxt5OpaqueBlock()
    {
        var header = BuildHeader(4, 4, fourCc: "DXT5");
        var block = new byte[16];
        // Alpha: a0=255, a1=255 -> every pixel fully opaque regardless of indices.
        block[0] = 255;
        block[1] = 255;
        // Color: c0=c1=0x07E0 (pure green in RGB565), indices 0 -> whole block green.
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

    [Fact]
    public void ReportsUnsupportedDx10Gracefully()
    {
        var header = BuildHeader(4, 4, fourCc: "DX10");
        var ok = DdsImage.TryDecode(Concat(header, new byte[64]), out var img, out var error);
        Assert.False(ok);
        Assert.Null(img);
        Assert.Contains("DX10", error);
    }

    [Fact]
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
