using System.Text;
using System.Text.RegularExpressions;

namespace TelltaleModding.Core.Scripts;

/// <summary>What a script file contains.</summary>
public enum LuaScriptKind { Source, Bytecode51, Bytecode52, Unknown }

/// <summary>
/// A game script ready for viewing: plain source passes through unchanged, compiled
/// bytecode is decompiled and disassembled on first access (names and string constants
/// are available immediately, for indexing).
/// </summary>
public sealed class LuaScript
{
    private readonly Lazy<(string Text, int Fallbacks)> _decompiled;
    private readonly Lazy<string> _disassembly;

    private LuaScript(LuaScriptKind kind, string versionLabel, string? sourceName, int functionCount,
        IReadOnlyList<string> functionNames, IReadOnlyList<string> stringConstants,
        Func<(string, int)> decompile, Func<string> disassemble, LuaChunk? chunk)
    {
        Kind = kind;
        VersionLabel = versionLabel;
        SourceName = sourceName;
        FunctionCount = functionCount;
        FunctionNames = functionNames;
        StringConstants = stringConstants;
        Chunk = chunk;
        _decompiled = new Lazy<(string, int)>(decompile);
        _disassembly = new Lazy<string>(disassemble);
    }

    public LuaScriptKind Kind { get; }
    /// <summary>"Lua 5.2 bytecode", "Lua 5.1 bytecode", "Lua source", ...</summary>
    public string VersionLabel { get; }
    /// <summary>The chunk name recorded in the bytecode's debug info, if any.</summary>
    public string? SourceName { get; }
    /// <summary>Number of functions, including the main chunk.</summary>
    public int FunctionCount { get; }
    /// <summary>Best-effort names of the functions the script defines, in definition order.</summary>
    public IReadOnlyList<string> FunctionNames { get; }
    /// <summary>Distinct string constants, in order of first use.</summary>
    public IReadOnlyList<string> StringConstants { get; }
    /// <summary>The parsed bytecode (null for source and unrecognised data).</summary>
    public LuaChunk? Chunk { get; }

    /// <summary>Readable Lua: the original text for source, decompiled code for bytecode.</summary>
    public string Decompiled => _decompiled.Value.Text;
    /// <summary>Annotated instruction listing (empty for source).</summary>
    public string Disassembly => _disassembly.Value;
    /// <summary>Code regions the decompiler emitted as instruction listings instead of source.</summary>
    public int DecompileFallbacks => _decompiled.Value.Fallbacks;

    internal static LuaScript FromChunk(LuaChunk chunk)
    {
        var names = LuaFunctionNames.Compute(chunk);
        var all = chunk.Main.Descendants().ToList();
        var functionNames = all.Where(names.ContainsKey).Select(f => names[f]).ToList();
        var strings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in all)
            foreach (var k in f.Constants)
                if (k.IsString && seen.Add(k.Text!))
                    strings.Add(k.Text!);

        string Disassemble()
        {
            try { return LuaDisassembler.Disassemble(chunk, names); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return "-- disassembly failed: " + ex.Message + "\n";
            }
        }

        (string, int) Decompile()
        {
            try
            {
                var text = LuaDecompiler.Decompile(chunk, names, out int fallbacks);
                return (text, fallbacks);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var sb = new StringBuilder();
                sb.Append("-- decompile failed (").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append(")\n");
                sb.Append("-- annotated disassembly follows\n\n");
                foreach (var line in Disassemble().Split('\n'))
                    sb.Append(line.StartsWith("--") || line.Length == 0 ? line : "-- " + line).Append('\n');
                return (sb.ToString(), 1);
            }
        }

        string label = chunk.IsLua51 ? "Lua 5.1 bytecode" : "Lua 5.2 bytecode";
        return new LuaScript(chunk.IsLua51 ? LuaScriptKind.Bytecode51 : LuaScriptKind.Bytecode52, label,
            chunk.SourceName, all.Count, functionNames, strings, Decompile, Disassemble, chunk);
    }

    internal static LuaScript FromSource(string text)
    {
        var names = new List<string>();
        foreach (Match m in SourceFunctionName.Matches(text))
            names.Add(m.Groups[1].Success ? "local " + m.Groups[1].Value : m.Groups[2].Value);
        int count = 1 + FunctionKeyword.Matches(text).Count;
        var strings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in StringLiteral.Matches(text))
        {
            var s = m.Groups[2].Value;
            if (seen.Add(s)) strings.Add(s);
        }
        return new LuaScript(LuaScriptKind.Source, "Lua source", null, count, names, strings,
            () => (text, 0), () => "", null);
    }

    internal static LuaScript FromUnknown(string label, string preview) =>
        new(LuaScriptKind.Unknown, label, null, 0, Array.Empty<string>(), Array.Empty<string>(),
            () => (preview, 0), () => "", null);

    private static readonly Regex SourceFunctionName =
        new(@"\blocal\s+function\s+([A-Za-z_]\w*)|\bfunction\s+([A-Za-z_][\w.:]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex FunctionKeyword = new(@"\bfunction\b", RegexOptions.Compiled);
    private static readonly Regex StringLiteral = new(@"([""'])((?:\\.|(?!\1)[^\\\r\n])*)\1", RegexOptions.Compiled);
}

/// <summary>Detects and opens Lua scripts (source, Lua 5.1 or 5.2 bytecode).</summary>
public static class LuaScriptReader
{
    /// <summary>True for script file names: <c>.lua</c>, <c>.lenc</c>, <c>.luac</c>.</summary>
    public static bool IsScriptFile(string name)
    {
        var ext = Path.GetExtension(name);
        return ext.Equals(".lua", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".lenc", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".luac", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens a (decrypted) script. Never throws: data that isn't Lua source or supported
    /// bytecode comes back as <see cref="LuaScriptKind.Unknown"/> with a text or hex preview.
    /// </summary>
    public static LuaScript Read(byte[] data, string name)
    {
        try
        {
            data ??= Array.Empty<byte>();
            if (data.Length == 0)
                return LuaScript.FromUnknown("Empty file", "-- (empty file)\n");

            if (LuaChunk.HasSignature(data))
            {
                if (data.Length > 4 && data[4] is not (0x51 or 0x52))
                    return LuaScript.FromUnknown($"Lua {data[4] >> 4}.{data[4] & 0xF} bytecode (unsupported)",
                        $"-- Lua {data[4] >> 4}.{data[4] & 0xF} bytecode is not supported.\n\n" + HexPreview(data));
                try
                {
                    return LuaScript.FromChunk(LuaChunk.Parse(data));
                }
                catch (LuaFormatException ex)
                {
                    return LuaScript.FromUnknown("Damaged Lua bytecode", $"-- Could not read the bytecode: {ex.Message}\n\n" + HexPreview(data));
                }
            }
            if (data.Length >= 4 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'E')
                return LuaScript.FromUnknown("Encrypted Lua",
                    "-- This script is still encrypted (\\x1bLE header); it needs the game's key.\n\n" + HexPreview(data));

            if (TryDecodeText(data) is { } text)
                return LuaScript.FromSource(text);

            return LuaScript.FromUnknown("Unknown data", $"-- {name}: not Lua source or bytecode.\n\n" + HexPreview(data));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return LuaScript.FromUnknown("Unreadable", "-- Could not read this script: " + ex.Message + "\n");
        }
    }

    private static string? TryDecodeText(byte[] data)
    {
        int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        int control = 0;
        for (int i = start; i < data.Length; i++)
        {
            byte b = data[i];
            if (b == 0) return null;
            if (b < 0x20 && b is not ((byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0C or 0x1A)) control++;
        }
        if (control > Math.Max(2, (data.Length - start) / 100)) return null;
        try
        {
            return new UTF8Encoding(false, true).GetString(data, start, data.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data, start, data.Length - start);
        }
    }

    private static string HexPreview(byte[] data, int max = 512)
    {
        var sb = new StringBuilder();
        int len = Math.Min(data.Length, max);
        for (int row = 0; row < len; row += 16)
        {
            sb.Append("-- ").Append(row.ToString("X6")).Append("  ");
            for (int i = 0; i < 16; i++)
                sb.Append(row + i < len ? data[row + i].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int i = 0; i < 16 && row + i < len; i++)
            {
                byte b = data[row + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        if (data.Length > len) sb.Append("-- ... (").Append(data.Length).Append(" bytes)\n");
        return sb.ToString();
    }
}
