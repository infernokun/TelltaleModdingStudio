using System.Buffers.Binary;

namespace TelltaleModding.Core.Textures;

/// <summary>
/// A decoded DDS texture as a top-down, tightly-packed RGBA8888 pixel buffer, suitable
/// for handing to any UI toolkit's bitmap. Supports the block formats Telltale textures
/// most commonly use (BC1/DXT1, BC2/DXT3, BC3/DXT5) plus uncompressed 32-bit.
/// </summary>
public sealed class DdsImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Pixels in RGBA order, row-major, top-down. Length is Width*Height*4.</summary>
    public byte[] Rgba { get; }

    private DdsImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    private const uint MagicDds = 0x20534444; // "DDS "

    /// <summary>Decodes a DDS image, throwing on unsupported input.</summary>
    public static DdsImage Decode(byte[] data)
    {
        if (!TryDecode(data, out var image, out var error))
            throw new NotSupportedException(error);
        return image!;
    }

    /// <summary>
    /// Attempts to decode a DDS image. Returns false with a human-readable
    /// <paramref name="error"/> for unsupported formats rather than throwing.
    /// </summary>
    public static bool TryDecode(byte[] data, out DdsImage? image, out string? error)
    {
        image = null;
        error = null;

        if (data.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(data) != MagicDds)
        {
            error = "Not a DDS file (missing 'DDS ' magic).";
            return false;
        }

        // DDS_HEADER starts at offset 4. Fields we need (little-endian):
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));

        // DDS_PIXELFORMAT is at offset 4 + 72 = 76; dwFlags at +4, dwFourCC at +8.
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(80));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(84));
        uint rgbBitCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(88));
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(92));
        uint gMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96));
        uint bMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(100));
        uint aMask = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(104));

        if (width <= 0 || height <= 0)
        {
            error = $"Invalid dimensions {width}x{height}.";
            return false;
        }

        const uint DDPF_FOURCC = 0x4;
        const uint DDPF_RGB = 0x40;

        int dataOffset = 128;
        var body = data.AsSpan(dataOffset);
        var rgba = new byte[width * height * 4];

        if ((pfFlags & DDPF_FOURCC) != 0)
        {
            switch (FourCcString(fourCc))
            {
                case "DXT1":
                    return DecodeBlocks(body, width, height, 8, DecodeDxt1Block, rgba, out image, out error);
                case "DXT3":
                    return DecodeBlocks(body, width, height, 16, DecodeDxt3Block, rgba, out image, out error);
                case "DXT5":
                    return DecodeBlocks(body, width, height, 16, DecodeDxt5Block, rgba, out image, out error);
                case "DX10":
                    error = "DX10-header DDS (e.g. BC7) is not supported yet — extract and view externally.";
                    return false;
                default:
                    error = $"Unsupported DDS FourCC '{FourCcString(fourCc)}'.";
                    return false;
            }
        }

        if ((pfFlags & DDPF_RGB) != 0 && rgbBitCount == 32)
        {
            if (!DecodeUncompressed32(body, width, height, rMask, gMask, bMask, aMask, rgba))
            {
                error = "Uncompressed DDS data is truncated.";
                return false;
            }
            image = new DdsImage(width, height, rgba);
            return true;
        }

        error = "Unsupported DDS pixel format (only DXT1/3/5 and 32-bit RGB/RGBA are decoded).";
        return false;
    }

    private delegate void BlockDecoder(ReadOnlySpan<byte> block, Span<uint> outPixels);

    private static bool DecodeBlocks(
        ReadOnlySpan<byte> body, int width, int height, int blockBytes,
        BlockDecoder decoder, byte[] rgba, out DdsImage? image, out string? error)
    {
        image = null;
        error = null;

        int blocksWide = (width + 3) / 4;
        int blocksHigh = (height + 3) / 4;
        if (body.Length < blocksWide * blocksHigh * blockBytes)
        {
            error = "DDS pixel data is truncated for its declared dimensions.";
            return false;
        }

        Span<uint> pixels = stackalloc uint[16];
        int idx = 0;
        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++, idx += blockBytes)
            {
                decoder(body.Slice(idx, blockBytes), pixels);
                for (int py = 0; py < 4; py++)
                {
                    int y = by * 4 + py;
                    if (y >= height) break;
                    for (int px = 0; px < 4; px++)
                    {
                        int x = bx * 4 + px;
                        if (x >= width) continue;
                        uint c = pixels[py * 4 + px];
                        int o = (y * width + x) * 4;
                        rgba[o + 0] = (byte)(c & 0xFF);
                        rgba[o + 1] = (byte)((c >> 8) & 0xFF);
                        rgba[o + 2] = (byte)((c >> 16) & 0xFF);
                        rgba[o + 3] = (byte)((c >> 24) & 0xFF);
                    }
                }
            }
        }

        image = new DdsImage(width, height, rgba);
        return true;
    }

    // Packs to 0xAABBGGRR so the copy loop above can splat it into RGBA bytes.
    private static uint Pack(byte r, byte g, byte b, byte a) =>
        (uint)(r | (g << 8) | (b << 16) | (a << 24));

    private static void UnpackColors(ReadOnlySpan<byte> block, out ushort c0, out ushort c1,
        Span<(byte r, byte g, byte b)> palette)
    {
        c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Rgb565(c0, out var r0, out var g0, out var b0);
        Rgb565(c1, out var r1, out var g1, out var b1);
        palette[0] = (r0, g0, b0);
        palette[1] = (r1, g1, b1);
    }

    private static void DecodeDxt1Block(ReadOnlySpan<byte> block, Span<uint> outPixels)
    {
        Span<(byte r, byte g, byte b)> pal = stackalloc (byte, byte, byte)[4];
        Span<byte> alpha = stackalloc byte[4];
        UnpackColors(block, out ushort c0, out ushort c1, pal);
        alpha[0] = alpha[1] = alpha[2] = alpha[3] = 255;

        if (c0 > c1)
        {
            pal[2] = Lerp(pal[0], pal[1], 1, 2);
            pal[3] = Lerp(pal[0], pal[1], 2, 1);
        }
        else
        {
            pal[2] = Mid(pal[0], pal[1]);
            pal[3] = (0, 0, 0);
            alpha[3] = 0; // 1-bit transparency
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int i = 0; i < 16; i++)
        {
            int sel = (int)((indices >> (i * 2)) & 0x3);
            var (r, g, b) = pal[sel];
            outPixels[i] = Pack(r, g, b, alpha[sel]);
        }
    }

    private static void DecodeDxt3Block(ReadOnlySpan<byte> block, Span<uint> outPixels)
    {
        // First 8 bytes: 4-bit explicit alpha per pixel. Next 8 bytes: DXT1-style color.
        Span<byte> alpha = stackalloc byte[16];
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(block);
        for (int i = 0; i < 16; i++)
        {
            int nib = (int)((a >> (i * 4)) & 0xF);
            alpha[i] = (byte)(nib * 17); // 0..15 -> 0..255
        }
        DecodeColorInto(block[8..], alpha, outPixels);
    }

    private static void DecodeDxt5Block(ReadOnlySpan<byte> block, Span<uint> outPixels)
    {
        // First 8 bytes: interpolated alpha. Next 8 bytes: DXT1-style color.
        Span<byte> aPal = stackalloc byte[8];
        byte a0 = block[0], a1 = block[1];
        aPal[0] = a0;
        aPal[1] = a1;
        if (a0 > a1)
            for (int i = 1; i <= 6; i++) aPal[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
        {
            for (int i = 1; i <= 4; i++) aPal[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            aPal[6] = 0;
            aPal[7] = 255;
        }

        // 16 * 3-bit indices packed into 6 bytes.
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (i * 8);

        Span<byte> alpha = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            int sel = (int)((bits >> (i * 3)) & 0x7);
            alpha[i] = aPal[sel];
        }
        DecodeColorInto(block[8..], alpha, outPixels);
    }

    private static void DecodeColorInto(ReadOnlySpan<byte> colorBlock, ReadOnlySpan<byte> alpha, Span<uint> outPixels)
    {
        Span<(byte r, byte g, byte b)> pal = stackalloc (byte, byte, byte)[4];
        UnpackColors(colorBlock, out _, out _, pal);
        // In DXT3/5 the color block is always treated as 4-color mode.
        pal[2] = Lerp(pal[0], pal[1], 1, 2);
        pal[3] = Lerp(pal[0], pal[1], 2, 1);

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(colorBlock[4..]);
        for (int i = 0; i < 16; i++)
        {
            int sel = (int)((indices >> (i * 2)) & 0x3);
            var (r, g, b) = pal[sel];
            outPixels[i] = Pack(r, g, b, alpha[i]);
        }
    }

    private static bool DecodeUncompressed32(
        ReadOnlySpan<byte> body, int width, int height,
        uint rMask, uint gMask, uint bMask, uint aMask, byte[] rgba)
    {
        if (body.Length < width * height * 4) return false;

        int rShift = MaskShift(rMask), gShift = MaskShift(gMask),
            bShift = MaskShift(bMask), aShift = MaskShift(aMask);

        for (int i = 0; i < width * height; i++)
        {
            uint px = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(i * 4, 4));
            int o = i * 4;
            rgba[o + 0] = rMask != 0 ? (byte)((px & rMask) >> rShift) : (byte)0;
            rgba[o + 1] = gMask != 0 ? (byte)((px & gMask) >> gShift) : (byte)0;
            rgba[o + 2] = bMask != 0 ? (byte)((px & bMask) >> bShift) : (byte)0;
            rgba[o + 3] = aMask != 0 ? (byte)((px & aMask) >> aShift) : (byte)255;
        }
        return true;
    }

    private static int MaskShift(uint mask)
    {
        if (mask == 0) return 0;
        int shift = 0;
        while ((mask & 1) == 0) { mask >>= 1; shift++; }
        return shift;
    }

    private static void Rgb565(ushort c, out byte r, out byte g, out byte b)
    {
        int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g6 << 2) | (g6 >> 4));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    private static (byte, byte, byte) Lerp((byte r, byte g, byte b) a, (byte r, byte g, byte b) c, int wa, int wc)
    {
        int total = wa + wc;
        return ((byte)((a.r * wa + c.r * wc) / total),
                (byte)((a.g * wa + c.g * wc) / total),
                (byte)((a.b * wa + c.b * wc) / total));
    }

    private static (byte, byte, byte) Mid((byte r, byte g, byte b) a, (byte r, byte g, byte b) c) =>
        ((byte)((a.r + c.r) / 2), (byte)((a.g + c.g) / 2), (byte)((a.b + c.b) / 2));

    private static string FourCcString(uint fourCc) => new(new[]
    {
        (char)(fourCc & 0xFF),
        (char)((fourCc >> 8) & 0xFF),
        (char)((fourCc >> 16) & 0xFF),
        (char)((fourCc >> 24) & 0xFF),
    });
}
