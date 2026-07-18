namespace TelltaleModding.Core.Meta;

/// <summary>
/// The CRC64 variant Telltale's engine uses for symbol and type-name hashing:
/// polynomial 0x42F0E1EBA9EA3693 (ECMA-182), MSB-first, initial value 0, no final xor.
/// Type names are hashed lowercased and without any "class "/"struct " prefix
/// (e.g. "D3DMesh", "T3Texture", "DCArray&lt;T3MeshLOD&gt;").
/// Verified against real MSV6 headers from game files.
/// </summary>
public static class TelltaleCrc64
{
    private const ulong Polynomial = 0x42F0E1EBA9EA3693;

    private static readonly ulong[] Table = BuildTable();

    private static ulong[] BuildTable()
    {
        var table = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            ulong crc = (ulong)i << 56;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000000000000000) != 0 ? (crc << 1) ^ Polynomial : crc << 1;
            table[i] = crc;
        }
        return table;
    }

    /// <summary>Raw CRC64 over the given bytes.</summary>
    public static ulong Hash(ReadOnlySpan<byte> data)
    {
        ulong crc = 0;
        foreach (byte b in data)
            crc = Table[(int)((crc >> 56) ^ b) & 0xFF] ^ (crc << 8);
        return crc;
    }

    /// <summary>
    /// Hashes a symbol or type name the way the engine does: ASCII-lowercased first.
    /// </summary>
    public static ulong HashSymbol(string name)
    {
        Span<byte> bytes = name.Length <= 256 ? stackalloc byte[name.Length] : new byte[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bytes[i] = (byte)(c is >= 'A' and <= 'Z' ? c + 32 : c);
        }
        return Hash(bytes);
    }
}
