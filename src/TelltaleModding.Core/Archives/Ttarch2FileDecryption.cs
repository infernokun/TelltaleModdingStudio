using System.Buffers.Binary;

namespace TelltaleModding.Core.Archives;

/// <summary>
/// The per-file decryption ttarchext's <c>ttarch_dumpa</c> applies to old meta streams and
/// Lua scripts, so <see cref="Ttarch2Archive.Read"/> returns the same bytes ttarchext writes.
/// </summary>
internal static class Ttarch2FileDecryption
{
    /// <summary>
    /// Decrypts the stored bytes of <paramref name="name"/> in place and returns them, or a copy
    /// without the 4-byte tag for <c>\x1bLEo</c> Lua scripts.
    /// </summary>
    public static byte[] Apply(string name, byte[] data, TtarchBlowfish? cipher)
    {
        if (cipher is null || data.Length < 4)
            return data;

        DecryptMeta(data, cipher);

        var ext = Path.GetExtension(name);
        if (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lenc", StringComparison.OrdinalIgnoreCase))
            return DecryptLua(data, cipher);
        return data;
    }

    // ttarch_meta_crypt: older meta streams are split into blocks after the 4-byte tag.
    // Every Nth block is Blowfish-encrypted, every Mth is clear, and the rest are XORed with 0xFF.
    private static void DecryptMeta(byte[] data, TtarchBlowfish cipher)
    {
        (int size, int crypt, int clean) = BinaryPrimitives.ReadUInt32LittleEndian(data) switch
        {
            0x4D424553 => (0x40, 0x40, 0x64),   // "SEBM"
            0xFB4A1764 => (0x80, 0x20, 0x50),
            0xEB794091 => (0x80, 0x20, 0x50),
            0x64AFDEFB => (0x80, 0x20, 0x50),
            0x64AFDEAA => (0x100, 0x8, 0x18),
            _ => (0, 0, 0),
        };
        if (size == 0)
            return;

        int blocks = (data.Length - 4) / size;
        for (int i = 0; i < blocks; i++)
        {
            var block = data.AsSpan(4 + i * size, size);
            if (i % crypt == 0)
            {
                cipher.Decrypt(block);
            }
            else if (i % clean == 0)
            {
                // Stored in the clear.
            }
            else
            {
                for (int k = 0; k < block.Length; k++)
                    block[k] ^= 0xFF;
            }
        }
    }

    // The Lua branch of ttarch_meta_dump. "\x1bLEn" scripts get their tag replaced with "\x1bLua",
    // "\x1bLEo" scripts lose the tag, and untagged ones are decrypted whole if that gives a Lua header.
    private static byte[] DecryptLua(byte[] data, TtarchBlowfish cipher)
    {
        if (data[0] == 0x1B && data[1] == 'L' && data[2] == 'E' && data[3] == 'n')
        {
            cipher.Decrypt(data.AsSpan(4));
            "\x1bLua"u8.CopyTo(data);
            return data;
        }
        if (data[0] == 0x1B && data[1] == 'L' && data[2] == 'E' && data[3] == 'o')
        {
            cipher.Decrypt(data.AsSpan(4));
            return data[4..];
        }

        // Untagged, so test-decrypt the first block. ttarchext also tries the older Blowfish
        // here, but no .ttarch2 game needs it.
        if (data.Length < 8)
            return data;
        Span<byte> probe = stackalloc byte[8];
        data.AsSpan(0, 8).CopyTo(probe);
        cipher.Decrypt(probe);
        if (IsLua(probe))
            cipher.Decrypt(data);
        return data;
    }

    private static bool IsLua(ReadOnlySpan<byte> d) =>
        d.StartsWith("Lua"u8) || d.StartsWith("\x1bLu"u8);
}
