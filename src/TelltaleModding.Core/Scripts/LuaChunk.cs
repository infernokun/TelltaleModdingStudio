using System.Buffers.Binary;
using System.Text;

namespace TelltaleModding.Core.Scripts;

/// <summary>Version-independent Lua 5.1/5.2 opcodes.</summary>
public enum LuaOpCode : byte
{
    Move, LoadK, LoadKx, LoadBool, LoadNil, GetUpval, GetGlobal, GetTabUp, GetTable,
    SetGlobal, SetTabUp, SetUpval, SetTable, NewTable, Self,
    Add, Sub, Mul, Div, Mod, Pow, Unm, Not, Len, Concat,
    Jmp, Eq, Lt, Le, Test, TestSet, Call, TailCall, Return,
    ForLoop, ForPrep, TForCall, TForLoop, SetList, Close, Closure, VarArg, ExtraArg,
    Invalid,
}

/// <summary>One decoded instruction. Field meaning depends on <see cref="Op"/>.</summary>
public readonly struct LuaInstruction
{
    public const int RkConstantBit = 256;
    private const int MaxArgSBx = (1 << 18) - 1 >> 1;

    public LuaInstruction(uint raw, LuaOpCode op)
    {
        Raw = raw;
        Op = op;
    }

    public uint Raw { get; }
    public LuaOpCode Op { get; }
    public int RawOp => (int)(Raw & 0x3F);
    public int A => (int)((Raw >> 6) & 0xFF);
    public int C => (int)((Raw >> 14) & 0x1FF);
    public int B => (int)(Raw >> 23);
    public int Bx => (int)(Raw >> 14);
    public int SBx => Bx - MaxArgSBx;
    public int Ax => (int)(Raw >> 6);

    public static bool IsConstant(int rk) => rk >= RkConstantBit;
}

/// <summary>Type of a constant-table entry.</summary>
public enum LuaConstantKind { Nil, Boolean, Number, String }

/// <summary>A constant-table entry (nil, boolean, number or string).</summary>
public sealed class LuaConstant
{
    public static readonly LuaConstant Nil = new(LuaConstantKind.Nil, false, 0, null);
    public static readonly LuaConstant True = new(LuaConstantKind.Boolean, true, 0, null);
    public static readonly LuaConstant False = new(LuaConstantKind.Boolean, false, 0, null);

    private LuaConstant(LuaConstantKind kind, bool boolean, double number, byte[]? bytes)
    {
        Kind = kind;
        Boolean = boolean;
        Number = number;
        Bytes = bytes;
        if (bytes is not null)
            Text = LuaText.Decode(bytes);
    }

    public static LuaConstant FromNumber(double value) => new(LuaConstantKind.Number, false, value, null);
    public static LuaConstant FromBytes(byte[] value) => new(LuaConstantKind.String, false, 0, value);
    public static LuaConstant FromString(string value) => FromBytes(Encoding.UTF8.GetBytes(value));

    public LuaConstantKind Kind { get; }
    public bool Boolean { get; }
    public double Number { get; }
    /// <summary>Raw string bytes, for <see cref="LuaConstantKind.String"/>.</summary>
    public byte[]? Bytes { get; }
    /// <summary>The string decoded as UTF-8 (or Latin-1 when it isn't valid UTF-8).</summary>
    public string? Text { get; }

    public bool IsString => Kind == LuaConstantKind.String;

    /// <summary>The constant as Lua source.</summary>
    public string ToLiteral(bool singlePrecision = false) => Kind switch
    {
        LuaConstantKind.Nil => "nil",
        LuaConstantKind.Boolean => Boolean ? "true" : "false",
        LuaConstantKind.Number => LuaText.FormatNumber(Number, singlePrecision),
        _ => LuaText.QuoteString(Bytes!),
    };

    public override string ToString() => ToLiteral();
}

/// <summary>A local variable's debug record: active for <c>StartPc &lt;= pc &lt; EndPc</c>.</summary>
public sealed record LuaLocalVariable(string Name, int StartPc, int EndPc);

/// <summary>An upvalue: where the closure captures it from (5.2) and its debug name.</summary>
public sealed record LuaUpvalue(bool InStack, int Index, string? Name);

/// <summary>One compiled function prototype.</summary>
public sealed class LuaFunction
{
    internal LuaFunction() { }

    public int LineDefined { get; internal set; }
    public int LastLineDefined { get; internal set; }
    public int NumParams { get; internal set; }
    /// <summary>The raw vararg flags (5.1: 1 = has arg, 2 = is vararg, 4 = needs arg).</summary>
    public int VarargFlags { get; internal set; }
    public bool IsVararg { get; internal set; }
    public int MaxStackSize { get; internal set; }
    public int NumUpvalues { get; internal set; }
    public LuaInstruction[] Code { get; internal set; } = Array.Empty<LuaInstruction>();
    public LuaConstant[] Constants { get; internal set; } = Array.Empty<LuaConstant>();
    public LuaFunction[] Protos { get; internal set; } = Array.Empty<LuaFunction>();
    public LuaUpvalue[] Upvalues { get; internal set; } = Array.Empty<LuaUpvalue>();
    /// <summary>The chunk name stored with this function (null when stripped or inherited).</summary>
    public string? Source { get; internal set; }
    public int[] LineInfo { get; internal set; } = Array.Empty<int>();
    public LuaLocalVariable[] Locals { get; internal set; } = Array.Empty<LuaLocalVariable>();
    public LuaFunction? Parent { get; internal set; }
    /// <summary>Position in the chunk: "main", "main/0", "main/0/2", ...</summary>
    public string Path { get; internal set; } = "main";

    /// <summary>True when the function carries line or local debug info.</summary>
    public bool HasDebugInfo => LineInfo.Length > 0 || Locals.Length > 0;

    internal uint[] RawCode { get; set; } = Array.Empty<uint>();

    /// <summary>Source line of instruction <paramref name="pc"/>, or 0 when unknown.</summary>
    public int LineAt(int pc) => pc >= 0 && pc < LineInfo.Length ? LineInfo[pc] : 0;

    /// <summary>Debug name of upvalue <paramref name="index"/>, if recorded.</summary>
    public string? UpvalueName(int index) =>
        index >= 0 && index < Upvalues.Length ? Upvalues[index].Name : null;

    /// <summary>This function and all nested functions, depth first.</summary>
    public IEnumerable<LuaFunction> Descendants()
    {
        yield return this;
        foreach (var p in Protos)
            foreach (var d in p.Descendants())
                yield return d;
    }
}

/// <summary>Thrown for malformed or unsupported bytecode.</summary>
public sealed class LuaFormatException : Exception
{
    public LuaFormatException(string message) : base(message) { }
}

/// <summary>
/// A compiled Lua 5.1 ("\x1bLuaQ") or 5.2 ("\x1bLuaR") chunk. Any integer, size_t and number
/// width in the header is accepted (Telltale builds use 32-bit sizes and 4-byte floats).
/// Telltale's Lua 5.1 build numbers its opcodes differently from stock Lua (seven unused
/// slots are interleaved); the numbering is detected by validating the code against both.
/// </summary>
public sealed class LuaChunk
{
    // Telltale 5.1: stock order with unused opcodes at 0, 8, 13, 15, 26, 35 and 41.
    private static readonly LuaOpCode[] Stock51 =
    {
        LuaOpCode.Move, LuaOpCode.LoadK, LuaOpCode.LoadBool, LuaOpCode.LoadNil, LuaOpCode.GetUpval,
        LuaOpCode.GetGlobal, LuaOpCode.GetTable, LuaOpCode.SetGlobal, LuaOpCode.SetUpval, LuaOpCode.SetTable,
        LuaOpCode.NewTable, LuaOpCode.Self, LuaOpCode.Add, LuaOpCode.Sub, LuaOpCode.Mul, LuaOpCode.Div,
        LuaOpCode.Mod, LuaOpCode.Pow, LuaOpCode.Unm, LuaOpCode.Not, LuaOpCode.Len, LuaOpCode.Concat,
        LuaOpCode.Jmp, LuaOpCode.Eq, LuaOpCode.Lt, LuaOpCode.Le, LuaOpCode.Test, LuaOpCode.TestSet,
        LuaOpCode.Call, LuaOpCode.TailCall, LuaOpCode.Return, LuaOpCode.ForLoop, LuaOpCode.ForPrep,
        LuaOpCode.TForLoop, LuaOpCode.SetList, LuaOpCode.Close, LuaOpCode.Closure, LuaOpCode.VarArg,
    };

    private static readonly LuaOpCode[] Telltale51 = BuildTelltale51();

    private static readonly LuaOpCode[] Stock52 =
    {
        LuaOpCode.Move, LuaOpCode.LoadK, LuaOpCode.LoadKx, LuaOpCode.LoadBool, LuaOpCode.LoadNil,
        LuaOpCode.GetUpval, LuaOpCode.GetTabUp, LuaOpCode.GetTable, LuaOpCode.SetTabUp, LuaOpCode.SetUpval,
        LuaOpCode.SetTable, LuaOpCode.NewTable, LuaOpCode.Self, LuaOpCode.Add, LuaOpCode.Sub, LuaOpCode.Mul,
        LuaOpCode.Div, LuaOpCode.Mod, LuaOpCode.Pow, LuaOpCode.Unm, LuaOpCode.Not, LuaOpCode.Len,
        LuaOpCode.Concat, LuaOpCode.Jmp, LuaOpCode.Eq, LuaOpCode.Lt, LuaOpCode.Le, LuaOpCode.Test,
        LuaOpCode.TestSet, LuaOpCode.Call, LuaOpCode.TailCall, LuaOpCode.Return, LuaOpCode.ForLoop,
        LuaOpCode.ForPrep, LuaOpCode.TForCall, LuaOpCode.TForLoop, LuaOpCode.SetList, LuaOpCode.Closure,
        LuaOpCode.VarArg, LuaOpCode.ExtraArg,
    };

    private static LuaOpCode[] BuildTelltale51()
    {
        var gaps = new HashSet<int> { 0, 8, 13, 15, 26, 35, 41 };
        var map = new LuaOpCode[Stock51.Length + gaps.Count];
        int s = 0;
        for (int i = 0; i < map.Length; i++)
            map[i] = gaps.Contains(i) ? LuaOpCode.Invalid : Stock51[s++];
        return map;
    }

    private LuaChunk() { }

    /// <summary>0x51 or 0x52.</summary>
    public int Version { get; private set; }
    public bool IsLua51 => Version == 0x51;
    public int SizeOfInt { get; private set; }
    public int SizeOfSizeT { get; private set; }
    public int SizeOfNumber { get; private set; }
    public bool IntegralNumbers { get; private set; }
    public bool LittleEndian { get; private set; }
    /// <summary>True when the code uses Telltale's 5.1 opcode numbering.</summary>
    public bool TelltaleOpcodes { get; private set; }
    public LuaFunction Main { get; private set; } = null!;

    /// <summary>The chunk name from debug info ("@file.lua", "=stdin", ...), if any.</summary>
    public string? SourceName => Main.Source;

    /// <summary>Numbers are 4-byte floats, so they should print with float precision.</summary>
    public bool SinglePrecision => SizeOfNumber == 4 && !IntegralNumbers;

    /// <summary>True for data starting with a Lua 5.1 or 5.2 bytecode signature.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 5 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a';

    /// <summary>Parses a chunk. Throws <see cref="LuaFormatException"/> on malformed data.</summary>
    public static LuaChunk Parse(byte[] data)
    {
        if (!HasSignature(data))
            throw new LuaFormatException("Not Lua bytecode (missing \\x1bLua signature).");
        var chunk = new LuaChunk { Version = data[4] };
        if (chunk.Version is not (0x51 or 0x52))
            throw new LuaFormatException($"Unsupported Lua bytecode version {chunk.Version >> 4}.{chunk.Version & 0xF}.");
        if (data.Length < 12)
            throw new LuaFormatException("Truncated header.");
        if (data[5] != 0)
            throw new LuaFormatException($"Unsupported bytecode format {data[5]}.");
        chunk.LittleEndian = data[6] == 1;
        chunk.SizeOfInt = data[7];
        chunk.SizeOfSizeT = data[8];
        int sizeInstr = data[9];
        chunk.SizeOfNumber = data[10];
        chunk.IntegralNumbers = data[11] != 0;
        if (chunk.SizeOfInt is not (4 or 8) || chunk.SizeOfSizeT is not (4 or 8) || sizeInstr != 4 ||
            chunk.SizeOfNumber is not (4 or 8))
            throw new LuaFormatException(
                $"Unsupported header sizes (int {chunk.SizeOfInt}, size_t {chunk.SizeOfSizeT}, instruction {sizeInstr}, number {chunk.SizeOfNumber}).");

        int pos = chunk.Version == 0x52 ? 18 : 12;
        if (data.Length < pos)
            throw new LuaFormatException("Truncated header.");
        var reader = new Reader(chunk, data, pos);
        chunk.Main = reader.ReadFunction(null, "main");

        LuaOpCode[] map;
        if (chunk.Version == 0x52)
            map = Stock52;
        else
        {
            int stockErrors = CountInvalid(chunk.Main, Stock51);
            int telltaleErrors = stockErrors == 0 ? int.MaxValue : CountInvalid(chunk.Main, Telltale51);
            chunk.TelltaleOpcodes = telltaleErrors < stockErrors;
            map = chunk.TelltaleOpcodes ? Telltale51 : Stock51;
        }
        foreach (var f in chunk.Main.Descendants())
        {
            var code = new LuaInstruction[f.RawCode.Length];
            for (int i = 0; i < code.Length; i++)
            {
                uint raw = f.RawCode[i];
                int op = (int)(raw & 0x3F);
                code[i] = new LuaInstruction(raw, op < map.Length ? map[op] : LuaOpCode.Invalid);
            }
            f.Code = code;
        }
        return chunk;
    }

    private static int CountInvalid(LuaFunction root, LuaOpCode[] map)
    {
        int bad = 0;
        foreach (var f in root.Descendants())
        {
            var raw = f.RawCode;
            int n = raw.Length, nk = f.Constants.Length, stack = Math.Max(f.MaxStackSize, 1);
            LuaOpCode OpAt(int i) => i < n && (raw[i] & 0x3F) < map.Length ? map[raw[i] & 0x3F] : LuaOpCode.Invalid;
            bool Rk(int x) => x >= LuaInstruction.RkConstantBit ? x - LuaInstruction.RkConstantBit < nk : x < stack;
            for (int i = 0; i < n; i++)
            {
                var ins = new LuaInstruction(raw[i], OpAt(i));
                bool ok = ins.Op switch
                {
                    LuaOpCode.Invalid => false,
                    LuaOpCode.Move => ins.A < stack && ins.B < stack,
                    LuaOpCode.LoadK => ins.A < stack && ins.Bx < nk,
                    LuaOpCode.LoadBool => ins.A < stack && ins.B <= 1 && ins.C <= 1,
                    LuaOpCode.GetGlobal or LuaOpCode.SetGlobal => ins.A < stack && ins.Bx < nk && f.Constants[ins.Bx].IsString,
                    LuaOpCode.GetTable or LuaOpCode.Self => ins.A < stack && ins.B < stack && Rk(ins.C),
                    LuaOpCode.SetTable => ins.A < stack && Rk(ins.B) && Rk(ins.C),
                    >= LuaOpCode.Add and <= LuaOpCode.Pow => ins.A < stack && Rk(ins.B) && Rk(ins.C),
                    LuaOpCode.Unm or LuaOpCode.Not or LuaOpCode.Len => ins.A < stack && ins.B < stack,
                    LuaOpCode.Concat => ins.A < stack && ins.B < ins.C && ins.C < stack,
                    LuaOpCode.Jmp or LuaOpCode.ForLoop or LuaOpCode.ForPrep => i + 1 + ins.SBx >= 0 && i + 1 + ins.SBx < n,
                    LuaOpCode.Eq or LuaOpCode.Lt or LuaOpCode.Le => ins.A <= 1 && Rk(ins.B) && Rk(ins.C) && OpAt(i + 1) == LuaOpCode.Jmp,
                    LuaOpCode.Test => ins.A < stack && ins.C <= 1 && OpAt(i + 1) == LuaOpCode.Jmp,
                    LuaOpCode.TestSet => ins.A < stack && ins.B < stack && ins.C <= 1 && OpAt(i + 1) == LuaOpCode.Jmp,
                    LuaOpCode.Closure => ins.A < stack && ins.Bx < f.Protos.Length,
                    LuaOpCode.Return => ins.A < stack + 1,
                    _ => ins.A < stack + 1,
                };
                if (!ok) bad++;
                if (ins.Op == LuaOpCode.Closure && ins.Bx < f.Protos.Length)
                    i += f.Protos[ins.Bx].NumUpvalues; // upvalue pseudo-instructions
            }
        }
        return bad;
    }

    private sealed class Reader
    {
        private readonly LuaChunk _chunk;
        private readonly byte[] _d;
        private int _pos;

        public Reader(LuaChunk chunk, byte[] data, int pos)
        {
            _chunk = chunk;
            _d = data;
            _pos = pos;
        }

        private void Need(long count)
        {
            if (count < 0 || _pos + count > _d.Length)
                throw new LuaFormatException($"Truncated chunk at offset {_pos}.");
        }

        private byte Byte()
        {
            Need(1);
            return _d[_pos++];
        }

        private long Integer(int size)
        {
            Need(size);
            var s = _d.AsSpan(_pos, size);
            _pos += size;
            if (size == 4)
                return _chunk.LittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(s) : BinaryPrimitives.ReadInt32BigEndian(s);
            return _chunk.LittleEndian ? BinaryPrimitives.ReadInt64LittleEndian(s) : BinaryPrimitives.ReadInt64BigEndian(s);
        }

        private int Count(int elementSize)
        {
            long n = Integer(_chunk.SizeOfInt);
            if (n < 0 || n * Math.Max(elementSize, 1) > _d.Length - _pos)
                throw new LuaFormatException($"Bad element count {n} at offset {_pos}.");
            return (int)n;
        }

        private uint UInt32()
        {
            Need(4);
            var s = _d.AsSpan(_pos, 4);
            _pos += 4;
            return _chunk.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
        }

        private double Number()
        {
            int size = _chunk.SizeOfNumber;
            if (_chunk.IntegralNumbers)
                return Integer(size);
            Need(size);
            var s = _d.AsSpan(_pos, size);
            _pos += size;
            if (size == 4)
                return _chunk.LittleEndian ? BinaryPrimitives.ReadSingleLittleEndian(s) : BinaryPrimitives.ReadSingleBigEndian(s);
            return _chunk.LittleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(s) : BinaryPrimitives.ReadDoubleBigEndian(s);
        }

        private byte[]? Bytes()
        {
            long n = Integer(_chunk.SizeOfSizeT);
            if (n == 0) return null;
            Need(n);
            var b = _d.AsSpan(_pos, (int)n - 1).ToArray(); // stored with a trailing NUL
            _pos += (int)n;
            return b;
        }

        private string? String()
        {
            var b = Bytes();
            return b is null ? null : LuaText.Decode(b);
        }

        public LuaFunction ReadFunction(LuaFunction? parent, string path)
        {
            if (path.Count(c => c == '/') > 200)
                throw new LuaFormatException("Functions nested too deeply.");
            bool v51 = _chunk.Version == 0x51;
            var f = new LuaFunction { Parent = parent, Path = path };
            if (v51)
                f.Source = String();
            f.LineDefined = (int)Integer(_chunk.SizeOfInt);
            f.LastLineDefined = (int)Integer(_chunk.SizeOfInt);
            if (v51)
                f.NumUpvalues = Byte();
            f.NumParams = Byte();
            f.VarargFlags = Byte();
            f.IsVararg = v51 ? (f.VarargFlags & 2) != 0 : f.VarargFlags != 0;
            f.MaxStackSize = Byte();

            int n = Count(4);
            var raw = new uint[n];
            for (int i = 0; i < n; i++)
                raw[i] = UInt32();
            f.RawCode = raw;

            n = Count(1);
            var k = new LuaConstant[n];
            for (int i = 0; i < n; i++)
            {
                int type = Byte();
                k[i] = type switch
                {
                    0 => LuaConstant.Nil,
                    1 => Byte() != 0 ? LuaConstant.True : LuaConstant.False,
                    3 => LuaConstant.FromNumber(Number()),
                    4 => LuaConstant.FromBytes(Bytes() ?? Array.Empty<byte>()),
                    _ => throw new LuaFormatException($"Unknown constant type {type} at offset {_pos - 1}."),
                };
            }
            f.Constants = k;

            n = Count(1);
            var protos = new LuaFunction[n];
            for (int i = 0; i < n; i++)
                protos[i] = ReadFunction(f, path + "/" + i);
            f.Protos = protos;

            (bool, int)[] upDescs = Array.Empty<(bool, int)>();
            if (!v51)
            {
                n = Count(2);
                upDescs = new (bool, int)[n];
                for (int i = 0; i < n; i++)
                    upDescs[i] = (Byte() != 0, Byte());
                f.NumUpvalues = n;
                f.Source = String();
            }

            n = Count(4);
            var lines = new int[n];
            for (int i = 0; i < n; i++)
                lines[i] = (int)Integer(_chunk.SizeOfInt);
            f.LineInfo = lines;

            n = Count(1);
            var locals = new LuaLocalVariable[n];
            for (int i = 0; i < n; i++)
            {
                var name = String() ?? "";
                int start = (int)Integer(_chunk.SizeOfInt);
                int end = (int)Integer(_chunk.SizeOfInt);
                locals[i] = new LuaLocalVariable(name, start, end);
            }
            f.Locals = locals;

            n = Count(1);
            var names = new string?[n];
            for (int i = 0; i < n; i++)
                names[i] = String();

            int upCount = v51 ? f.NumUpvalues : upDescs.Length;
            var ups = new LuaUpvalue[upCount];
            for (int i = 0; i < upCount; i++)
            {
                var (inStack, index) = v51 ? (false, 0) : upDescs[i];
                ups[i] = new LuaUpvalue(inStack, index, i < names.Length ? names[i] : null);
            }
            f.Upvalues = ups;
            return f;
        }
    }
}

/// <summary>String and number formatting shared by the disassembler and decompiler.</summary>
internal static class LuaText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in",
        "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    public static string Decode(byte[] bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static bool IsIdentifier(string? s)
    {
        if (string.IsNullOrEmpty(s) || Keywords.Contains(s)) return false;
        if (!(char.IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    public static string QuoteString(byte[] bytes)
    {
        bool utf8 = true;
        try { StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { utf8 = false; }

        var sb = new StringBuilder(bytes.Length + 2);
        sb.Append('"');
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            switch (b)
            {
                case (byte)'"': sb.Append("\\\""); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                case (byte)'\a': sb.Append("\\a"); break;
                case (byte)'\b': sb.Append("\\b"); break;
                case (byte)'\f': sb.Append("\\f"); break;
                case (byte)'\v': sb.Append("\\v"); break;
                default:
                    if (b < 0x20 || b == 0x7F || (b >= 0x80 && !utf8))
                        sb.Append('\\').Append(((int)b).ToString("000"));
                    else if (b < 0x80)
                        sb.Append((char)b);
                    else
                    {
                        // Copy the whole UTF-8 sequence.
                        int len = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : 2;
                        len = Math.Min(len, bytes.Length - i);
                        sb.Append(Encoding.UTF8.GetString(bytes, i, len));
                        i += len - 1;
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string FormatNumber(double v, bool singlePrecision)
    {
        if (double.IsNaN(v)) return "(0/0)";
        if (double.IsPositiveInfinity(v)) return "(1/0)";
        if (double.IsNegativeInfinity(v)) return "(-1/0)";
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
            return (v == 0 && double.IsNegative(v)) ? "-0" : ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string s = singlePrecision
            ? ((float)v).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return s;
    }
}
