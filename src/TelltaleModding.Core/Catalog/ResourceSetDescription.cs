using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// One Telltale resource set, as declared by a loose <c>_resdesc_50_*.lua</c> (or
/// <c>_resourcedescriptions_500_*.lenc</c>) file next to the archives. The engine mounts
/// every enabled set and resolves a file name to the copy in the highest-priority set;
/// patch sets (priority 400+) and mods (700) therefore override episode sets (~100),
/// which override season/project sets (~50 / -8888).
/// </summary>
/// <param name="Name">Set name, e.g. <c>WalkingDead401</c> or <c>PC_P01_WalkingDead401</c>.</param>
/// <param name="Priority">Higher wins when two sets contain the same file name.</param>
/// <param name="EnableMode"><c>bootable</c>, <c>constant</c> or <c>localization</c>.</param>
/// <param name="LogicalName">The set's logical location, e.g. <c>&lt;WalkingDead401&gt;</c>.</param>
/// <param name="LogicalDestination">Where the set mounts: <c>&lt;&gt;</c> (root) or another set's logical name.</param>
/// <param name="Archives">Archive file names the set loads (no folder).</param>
/// <param name="SourceFile">File name of the description this was read from.</param>
public sealed record ResourceSetDescription(
    string Name, int Priority, string EnableMode, string LogicalName, string LogicalDestination,
    IReadOnlyList<string> Archives, string SourceFile)
{
    private static readonly Regex TextAssignment = new(
        @"^\s*set\.(\w+)\s*=\s*(?:""([^""]*)""|(-?\d+(?:\.\d+)?))",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TextArchive = new(
        @"""([^""]+\.ttarch2?)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True for language-specific sets that the engine only enables for one language.</summary>
    public bool IsLocalization => string.Equals(EnableMode, "localization", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a decrypted description: plain Lua source (TTDS, TFTB, Michonne) or compiled
    /// Lua 5.1/5.2 bytecode (Season Two, Wolf Among Us, Game of Thrones). Returns null for
    /// files that don't register a set (version stamps, helper scripts).
    /// </summary>
    public static ResourceSetDescription? Parse(byte[] data, string sourceFile)
    {
        Dictionary<string, object?> fields;
        List<string> archives;
        if (data.Length >= 5 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a')
        {
            if (!LuaBytecode.TryReadAssignments(data, out fields, out archives))
                return null;
        }
        else
        {
            var text = Encoding.Latin1.GetString(data);
            fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (Match m in TextAssignment.Matches(text))
            {
                if (fields.ContainsKey(m.Groups[1].Value)) continue;
                fields[m.Groups[1].Value] = m.Groups[2].Success
                    ? m.Groups[2].Value
                    : double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            }
            archives = TextArchive.Matches(text).Select(m => m.Groups[1].Value).ToList();
        }

        if (fields.GetValueOrDefault("name") is not string name || fields.GetValueOrDefault("priority") is not double priority)
            return null;

        return new ResourceSetDescription(
            name,
            (int)Math.Round(priority),
            fields.GetValueOrDefault("enableMode") as string ?? "",
            fields.GetValueOrDefault("logicalName") as string ?? $"<{name}>",
            fields.GetValueOrDefault("logicalDestination") as string ?? "<>",
            archives.Select(a => Path.GetFileName(a.Replace('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            sourceFile);
    }

    /// <summary>
    /// True when <paramref name="fileName"/> looks like a resource description
    /// (<c>_resdesc_*</c>, <c>_rescdesc_*</c>, <c>_resourcedescriptions_*</c>; .lua or .lenc).
    /// </summary>
    public static bool IsDescriptionFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var ext = Path.GetExtension(name);
        return name.StartsWith("_res", StringComparison.OrdinalIgnoreCase) &&
               (ext.Equals(".lua", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lenc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Minimal reader for the main chunk of compiled Lua 5.1 ("LuaQ") and 5.2 ("LuaR")
    /// bytecode: walks the constant table and the <c>LOADK</c>/<c>SETTABLE</c> instructions
    /// to recover <c>set.key = constant</c> assignments. Telltale builds use 4-byte floats.
    /// </summary>
    private static class LuaBytecode
    {
        public static bool TryReadAssignments(byte[] d, out Dictionary<string, object?> fields, out List<string> archives)
        {
            fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            archives = new List<string>();
            try
            {
                byte version = d[4];
                if (version is not (0x51 or 0x52)) return false;
                bool little = d[6] == 1;
                int sizeInt = d[7], sizeSizeT = d[8], sizeInstr = d[9], sizeNumber = d[10];
                bool integral = d[11] != 0;
                if (sizeInstr != 4 || sizeInt is not (4 or 8) || sizeSizeT is not (4 or 8) || sizeNumber is not (4 or 8))
                    return false;

                int pos = version == 0x52 ? 18 : 12;

                long ReadInt(int size)
                {
                    long v = size == 4
                        ? (little ? BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos)) : BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos)))
                        : (little ? BinaryPrimitives.ReadInt64LittleEndian(d.AsSpan(pos)) : BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(pos)));
                    pos += size;
                    return v;
                }

                string? ReadString()
                {
                    long len = ReadInt(sizeSizeT);
                    if (len <= 0) return null;
                    var s = Encoding.Latin1.GetString(d, pos, (int)len - 1); // stored with a trailing NUL
                    pos += (int)len;
                    return s;
                }

                if (version == 0x51)
                {
                    ReadString();       // source name
                    ReadInt(sizeInt);   // linedefined
                    ReadInt(sizeInt);   // lastlinedefined
                    pos += 4;           // nups, numparams, is_vararg, maxstacksize
                }
                else
                {
                    ReadInt(sizeInt);
                    ReadInt(sizeInt);
                    pos += 3;           // numparams, is_vararg, maxstacksize
                }

                int codeCount = (int)ReadInt(sizeInt);
                if (codeCount < 0 || pos + codeCount * 4L > d.Length) return false;
                var code = new uint[codeCount];
                for (int i = 0; i < codeCount; i++, pos += 4)
                    code[i] = little ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos)) : BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));

                int constCount = (int)ReadInt(sizeInt);
                if (constCount < 0 || constCount > 1 << 20) return false;
                var constants = new object?[constCount];
                for (int i = 0; i < constCount; i++)
                {
                    byte type = d[pos++];
                    switch (type)
                    {
                        case 0: constants[i] = null; break;
                        case 1: constants[i] = d[pos++] != 0; break;
                        case 3:
                            constants[i] = integral
                                ? ReadInt(sizeNumber)
                                : sizeNumber == 4
                                    ? (double)(little ? BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(pos)) : BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(pos)))
                                    : (little ? BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(pos)) : BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(pos)));
                            if (!integral) pos += sizeNumber;
                            if (constants[i] is long l) constants[i] = (double)l;
                            break;
                        case 4: constants[i] = ReadString(); break;
                        default: return false;
                    }
                }

                object? K(int i) => i >= 0 && i < constants.Length ? constants[i] : null;

                // Telltale's Lua 5.1 build renumbers the opcodes (SETTABLE is 11, not 9), so find
                // SETTABLE as the instruction that stores into the "name" key, i.e. `set.name = …`.
                int standardSetTable = version == 0x51 ? 9 : 10;
                int opSetTable = -1;
                foreach (uint ins in code)
                {
                    int b = (int)(ins >> 23);
                    if (b >= 256 && K(b - 256) is "name")
                    {
                        opSetTable = (int)(ins & 0x3F);
                        break;
                    }
                }
                if (opSetTable < 0) return false;
                // LOADK's number is only known for the stock numbering.
                int opLoadK = opSetTable == standardSetTable ? 1 : -1;

                var registers = new Dictionary<int, object?>();
                object? Rk(int x) => x >= 256 ? K(x - 256) : registers.GetValueOrDefault(x);

                foreach (uint ins in code)
                {
                    int op = (int)(ins & 0x3F);
                    int a = (int)((ins >> 6) & 0xFF);
                    if (op == opLoadK)
                    {
                        int bx = (int)(ins >> 14);
                        registers[a] = bx < constants.Length ? constants[bx] : null;
                    }
                    else if (op == opSetTable)
                    {
                        int c = (int)((ins >> 14) & 0x1FF);
                        int b = (int)(ins >> 23);
                        if (Rk(b) is string key && !fields.ContainsKey(key))
                            fields[key] = Rk(c);
                    }
                    else
                    {
                        // Any other instruction may overwrite register A; forget it.
                        registers.Remove(a);
                    }
                }

                foreach (var k in constants)
                    if (k is string s && (s.EndsWith(".ttarch2", StringComparison.OrdinalIgnoreCase) ||
                                          s.EndsWith(".ttarch", StringComparison.OrdinalIgnoreCase)))
                        archives.Add(s);
                return true;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException)
            {
                return false;
            }
        }
    }
}
