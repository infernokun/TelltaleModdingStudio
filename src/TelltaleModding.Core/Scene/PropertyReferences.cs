using System.Buffers.Binary;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// Files a property set (<c>.prop</c>) refers to under a key: a character's idle animation
/// ("Walk Animation - Idle"), default face ("Walk Animation - Idle Face") or phoneme table
/// ("Phoneme Table"). A value follows its key's symbol as a handle (a small block holding the
/// file's symbol) or a pair of them (animation or chore), so the file is the first symbol
/// after the key that names a file of the wanted kind. Keys a property set does not set come
/// from the property sets it inherits.
/// </summary>
public static class PropertyReferences
{
    public const string IdleAnimation = "Walk Animation - Idle";
    public const string IdleFace = "Walk Animation - Idle Face";
    public const string PhonemeTable = "Phoneme Table";

    // A handle pair (animation, chore) with its block sizes fits well inside this.
    private const int Reach = 48;
    private const int MaxDepth = 4;
    private const int HandleBlock = 12;

    /// <summary>
    /// The file <paramref name="key"/> names in <paramref name="prop"/> (a <c>.prop</c> file) or
    /// the property sets it inherits, with one of <paramref name="extensions"/>; null when unset.
    /// </summary>
    /// <param name="nameOf">The file name of a symbol, or null.</param>
    /// <param name="read">Reads a file by name, or null.</param>
    public static string? Find(byte[] prop, string key, Func<ulong, string?> nameOf, Func<string, byte[]?> read,
        params string[] extensions)
    {
        ulong symbol = TelltaleCrc64.HashSymbol(key);
        var level = new List<byte[]> { prop };
        var seen = new HashSet<ulong>();
        for (int depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<byte[]>();
            foreach (var data in level)
            {
                if (FindIn(data, symbol, nameOf, extensions) is { } found)
                    return found;
                if (MaterialData.TryParse(PropertySet(data)) is not { } set)
                    continue;
                foreach (var parent in set.Parents)
                    if (seen.Add(parent) && nameOf(parent) is { } name && read(name) is { } bytes)
                        next.Add(bytes);
            }
            level = next;
        }
        return null;
    }

    /// <summary>
    /// The phoneme poses a <c>.ptable</c> lists, by phoneme symbol (<c>AA</c> to its
    /// <c>handsomeJack_phoneme_AA.anm</c>). Each entry's key is the phoneme's symbol, followed
    /// by its animation.
    /// </summary>
    public static Dictionary<ulong, string> PhonemeClips(byte[] table, IEnumerable<string> phonemes, Func<ulong, string?> nameOf)
    {
        var clips = new Dictionary<ulong, string>();
        foreach (var phoneme in phonemes)
        {
            ulong symbol = TelltaleCrc64.HashSymbol(phoneme);
            if (FindIn(table, symbol, nameOf, new[] { ".anm" }) is { } clip)
                clips[symbol] = clip;
        }
        return clips;
    }

    private static string? FindIn(byte[] data, ulong key, Func<ulong, string?> nameOf, string[] extensions)
    {
        var span = data.AsSpan();
        Span<byte> pattern = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(pattern, key);
        for (int from = 0; from < span.Length;)
        {
            int at = span[from..].IndexOf(pattern);
            if (at < 0)
                return null;
            at += from;
            // Handles are 12-byte blocks (size, symbol); an animation-or-chore value is two of them.
            // Walking the blocks keeps an empty value from reaching into the next key's.
            if (at + 20 <= span.Length && BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 8)..]) == HandleBlock)
            {
                for (int p = at + 8, n = 0; n < 2 && p + HandleBlock <= span.Length &&
                     BinaryPrimitives.ReadUInt32LittleEndian(span[p..]) == HandleBlock; p += HandleBlock, n++)
                {
                    ulong handle = BinaryPrimitives.ReadUInt64LittleEndian(span[(p + 4)..]);
                    if (handle != 0 && nameOf(handle) is { } file &&
                        extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                        return file;
                }
                from = at + 1;
                continue;
            }
            int end = Math.Min(span.Length - 8, at + 8 + Reach);
            for (int p = at + 8; p <= end; p++)
            {
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(span[p..]);
                if (value == 0 || nameOf(value) is not { } name)
                    continue;
                if (extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    return name;
                break; // the key's value names some other kind of file
            }
            from = at + 1;
        }
        return null;
    }

    // MaterialData reads a bare property set; a .prop wraps it in a meta stream.
    private static byte[] PropertySet(byte[] data)
    {
        if (!MetaStreamHeader.TryParse(data, out var header, out _) || header is null)
            return data;
        int size = Math.Min(header.DefaultSectionSize, data.Length - header.DefaultSectionOffset);
        return size > 0 ? data.AsSpan(header.DefaultSectionOffset, size).ToArray() : data;
    }
}
