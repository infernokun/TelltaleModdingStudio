using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace TelltaleModding.Core.Textures;

/// <summary>Encodes 8-bit RGBA pixels as a PNG file.</summary>
public static class PngWriter
{
    /// <summary>Encodes <paramref name="rgba"/> (rows top to bottom, 4 bytes per pixel).</summary>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Pixel data does not match the image size.", nameof(rgba));

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 });

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // RGBA
        WriteChunk(png, "IHDR", header);

        // Each row is prefixed with filter type 0 (none); zlib does the work.
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgba.Slice(y * stride, stride));
            }
        }
        WriteChunk(png, "IDAT", raw.ToArray());
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    /// <summary>Encodes a decoded texture.</summary>
    public static byte[] Encode(DdsImage image) => Encode(image.Width, image.Height, image.Rgba);

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        png.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        png.Write(buffer);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
