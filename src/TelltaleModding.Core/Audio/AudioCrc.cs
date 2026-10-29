namespace TelltaleModding.Core.Audio;

/// <summary>The two CRC32 variants audio containers use.</summary>
internal static class AudioCrc
{
    private static readonly uint[] ZipTable = BuildReflected(0xEDB88320);
    private static readonly uint[] OggTable = BuildDirect(0x04C11DB7);

    private static uint[] BuildReflected(uint poly)
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint[] BuildDirect(uint poly)
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ poly : c << 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>The common (zip/PNG) CRC32.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = ZipTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    /// <summary>The Ogg page checksum (non-reflected, zero initial value).</summary>
    public static uint Ogg(ReadOnlySpan<byte> data) => OggUpdate(0, data);

    /// <summary>Continues an Ogg page checksum over more bytes.</summary>
    public static uint OggUpdate(uint c, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) c = (c << 8) ^ OggTable[((c >> 24) ^ b) & 0xFF];
        return c;
    }
}
