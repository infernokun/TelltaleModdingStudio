using System.Text;

namespace TelltaleModding.Core.Scripts;

/// <summary>
/// Annotated instruction listings: one section per function with its constants, locals and
/// upvalues, then each instruction with its source line and a readable description where
/// registers are shown by local name and constants are resolved inline.
/// </summary>
internal static class LuaDisassembler
{
    public static string Disassemble(LuaChunk chunk, IReadOnlyDictionary<LuaFunction, string> names)
    {
        var sb = new StringBuilder();
        sb.Append("-- ").Append(chunk.IsLua51 ? "Lua 5.1" : "Lua 5.2").Append(" bytecode");
        if (chunk.TelltaleOpcodes) sb.Append(" (Telltale opcode numbering)");
        sb.Append(", ").Append(chunk.SizeOfNumber).Append("-byte ")
          .Append(chunk.IntegralNumbers ? "integer" : chunk.SinglePrecision ? "float" : "double").Append(" numbers\n");
        if (chunk.SourceName is { } src)
            sb.Append("-- source: ").Append(src).Append('\n');
        foreach (var f in chunk.Main.Descendants())
        {
            sb.Append('\n');
            AppendFunction(sb, chunk, f, names);
        }
        return sb.ToString();
    }

    private static void AppendFunction(StringBuilder sb, LuaChunk chunk, LuaFunction f, IReadOnlyDictionary<LuaFunction, string> names)
    {
        var locals = new LuaLocals(f);
        string title = f.Parent is null ? "main chunk" : names.TryGetValue(f, out var n) ? n : "anonymous function";
        sb.Append("-- ").Append(new string('=', 70)).Append('\n');
        sb.Append("-- function ").Append(f.Path).Append(": ").Append(title);
        if (f.Parent is not null && f.LineDefined > 0)
            sb.Append(" (lines ").Append(f.LineDefined).Append('-').Append(f.LastLineDefined).Append(')');
        sb.Append('\n');
        sb.Append("-- ").Append(f.NumParams).Append(" params").Append(f.IsVararg ? " + vararg" : "")
          .Append(", ").Append(f.MaxStackSize).Append(" registers, ").Append(f.NumUpvalues).Append(" upvalues, ")
          .Append(f.Code.Length).Append(" instructions, ").Append(f.Constants.Length).Append(" constants, ")
          .Append(f.Protos.Length).Append(" functions\n");

        if (f.Constants.Length > 0)
        {
            sb.Append("-- constants:\n");
            for (int i = 0; i < f.Constants.Length; i++)
                sb.Append("--   K").Append(i).Append(" = ").Append(Shorten(f.Constants[i].ToLiteral(chunk.SinglePrecision), 120)).Append('\n');
        }
        if (f.Locals.Length > 0)
        {
            sb.Append("-- locals:\n");
            for (int i = 0; i < f.Locals.Length; i++)
            {
                var l = f.Locals[i];
                sb.Append("--   r").Append(locals.RegisterOf(i)).Append(' ').Append(l.Name)
                  .Append("  [").Append(l.StartPc + 1).Append(", ").Append(l.EndPc + 1).Append(")\n");
            }
        }
        if (f.Upvalues.Length > 0)
        {
            sb.Append("-- upvalues:\n");
            for (int i = 0; i < f.Upvalues.Length; i++)
            {
                var u = f.Upvalues[i];
                sb.Append("--   U").Append(i).Append(' ').Append(u.Name ?? "?");
                if (!chunk.IsLua51 && f.Parent is not null)
                    sb.Append(u.InStack ? "  (parent register " : "  (parent upvalue ").Append(u.Index).Append(')');
                sb.Append('\n');
            }
        }
        foreach (var line in ListRange(chunk, f, locals, 0, f.Code.Length, names))
            sb.Append(line).Append('\n');
    }

    /// <summary>Listing lines for instructions <c>[from, to)</c>.</summary>
    public static List<string> ListRange(LuaChunk chunk, LuaFunction f, LuaLocals locals, int from, int to,
        IReadOnlyDictionary<LuaFunction, string>? names = null)
    {
        var lines = new List<string>();
        for (int pc = from; pc < to && pc < f.Code.Length; pc++)
            lines.Add(FormatLine(chunk, f, locals, pc, names));
        return lines;
    }

    public static string FormatLine(LuaChunk chunk, LuaFunction f, LuaLocals locals, int pc,
        IReadOnlyDictionary<LuaFunction, string>? names = null)
    {
        var ins = f.Code[pc];
        int line = f.LineAt(pc);
        string head = $"{pc + 1,5} {(line > 0 ? "[" + line + "]" : ""),-7} {OpName(ins.Op, ins.RawOp),-10} {Operands(chunk, ins),-14}";
        string desc;
        try { desc = Describe(chunk, f, locals, pc, names); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { desc = "(bad operands)"; }
        return desc.Length == 0 ? head.TrimEnd() : head + " ; " + desc;
    }

    public static string OpName(LuaOpCode op, int raw) => op == LuaOpCode.Invalid ? $"OP_{raw}" : op.ToString().ToUpperInvariant();

    private static string Operands(LuaChunk chunk, LuaInstruction i) => i.Op switch
    {
        LuaOpCode.LoadK or LuaOpCode.GetGlobal or LuaOpCode.SetGlobal or LuaOpCode.Closure => $"{i.A} {i.Bx}",
        LuaOpCode.Jmp => chunk.IsLua51 ? $"{i.SBx}" : $"{i.A} {i.SBx}",
        LuaOpCode.ForLoop or LuaOpCode.ForPrep or LuaOpCode.TForLoop when !(chunk.IsLua51 && i.Op == LuaOpCode.TForLoop) => $"{i.A} {i.SBx}",
        LuaOpCode.ExtraArg => $"{i.Ax}",
        LuaOpCode.LoadKx => $"{i.A}",
        _ => $"{i.A} {i.B} {i.C}",
    };

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static string Describe(LuaChunk chunk, LuaFunction f, LuaLocals locals, int pc, IReadOnlyDictionary<LuaFunction, string>? names)
    {
        var ins = f.Code[pc];
        int a = ins.A, b = ins.B, c = ins.C;
        string R(int r, int at = -1)
        {
            var l = locals.At(r, at < 0 ? pc : at);
            return l is null ? "r" + r : l.Name;
        }
        string K(int k) => k >= 0 && k < f.Constants.Length ? Shorten(f.Constants[k].ToLiteral(chunk.SinglePrecision), 60) : $"K{k}?";
        string RK(int x) => LuaInstruction.IsConstant(x) ? K(x - LuaInstruction.RkConstantBit) : R(x);
        string Up(int u) => f.UpvalueName(u) ?? "U" + u;
        string Key(int x)
        {
            if (LuaInstruction.IsConstant(x))
            {
                int k = x - LuaInstruction.RkConstantBit;
                if (k < f.Constants.Length && f.Constants[k].IsString && LuaText.IsIdentifier(f.Constants[k].Text))
                    return "." + f.Constants[k].Text;
            }
            return "[" + RK(x) + "]";
        }
        string Range(int first, int last) =>
            last < first ? "(none)" : string.Join(", ", Enumerable.Range(first, last - first + 1).Select(r => R(r)));
        string Target(int offsetFrom, int sbx) => "[" + (offsetFrom + 1 + sbx + 1) + "]";
        string JumpOfNext() => pc + 1 < f.Code.Length && f.Code[pc + 1].Op == LuaOpCode.Jmp ? Target(pc + 1, f.Code[pc + 1].SBx) : "next+1";
        bool env(int u) => Up(u) == "_ENV";

        switch (ins.Op)
        {
            case LuaOpCode.Move:
                return $"{R(a)} := {R(b)}";
            case LuaOpCode.LoadK:
                return $"{R(a)} := {K(ins.Bx)}";
            case LuaOpCode.LoadKx:
                return pc + 1 < f.Code.Length ? $"{R(a)} := {K(f.Code[pc + 1].Ax)}" : $"{R(a)} := K(next)";
            case LuaOpCode.LoadBool:
                return $"{R(a)} := {(b != 0 ? "true" : "false")}" + (c != 0 ? "; skip next" : "");
            case LuaOpCode.LoadNil:
                return chunk.IsLua51 ? $"{Range(a, b)} := nil" : $"{Range(a, a + b)} := nil";
            case LuaOpCode.GetUpval:
                return $"{R(a)} := {Up(b)}";
            case LuaOpCode.GetGlobal:
                return $"{R(a)} := {GlobalName(f, ins.Bx)}";
            case LuaOpCode.GetTabUp:
                return env(b) && LuaInstruction.IsConstant(c) ? $"{R(a)} := {GlobalName(f, c - LuaInstruction.RkConstantBit)}" : $"{R(a)} := {Up(b)}{Key(c)}";
            case LuaOpCode.GetTable:
                return $"{R(a)} := {R(b)}{Key(c)}";
            case LuaOpCode.SetGlobal:
                return $"{GlobalName(f, ins.Bx)} := {R(a)}";
            case LuaOpCode.SetTabUp:
                return env(a) && LuaInstruction.IsConstant(b) ? $"{GlobalName(f, b - LuaInstruction.RkConstantBit)} := {RK(c)}" : $"{Up(a)}{Key(b)} := {RK(c)}";
            case LuaOpCode.SetUpval:
                return $"{Up(b)} := {R(a)}";
            case LuaOpCode.SetTable:
                return $"{R(a)}{Key(b)} := {RK(c)}";
            case LuaOpCode.NewTable:
                return $"{R(a)} := {{}}";
            case LuaOpCode.Self:
                return $"{R(a + 1)} := {R(b)}; {R(a)} := {R(b)}{Key(c)}";
            case LuaOpCode.Add: return $"{R(a)} := {RK(b)} + {RK(c)}";
            case LuaOpCode.Sub: return $"{R(a)} := {RK(b)} - {RK(c)}";
            case LuaOpCode.Mul: return $"{R(a)} := {RK(b)} * {RK(c)}";
            case LuaOpCode.Div: return $"{R(a)} := {RK(b)} / {RK(c)}";
            case LuaOpCode.Mod: return $"{R(a)} := {RK(b)} % {RK(c)}";
            case LuaOpCode.Pow: return $"{R(a)} := {RK(b)} ^ {RK(c)}";
            case LuaOpCode.Unm: return $"{R(a)} := -{R(b)}";
            case LuaOpCode.Not: return $"{R(a)} := not {R(b)}";
            case LuaOpCode.Len: return $"{R(a)} := #{R(b)}";
            case LuaOpCode.Concat:
                return $"{R(a)} := " + string.Join(" .. ", Enumerable.Range(b, Math.Max(0, c - b + 1)).Select(r => R(r)));
            case LuaOpCode.Jmp:
                return "goto " + Target(pc, ins.SBx) + (!chunk.IsLua51 && a > 0 ? $"; close upvalues >= r{a - 1}" : "");
            case LuaOpCode.Eq:
                return $"if {RK(b)} {(a != 0 ? "==" : "~=")} {RK(c)} then goto {JumpOfNext()}";
            case LuaOpCode.Lt:
                return $"if {(a != 0 ? "" : "not ")}{RK(b)} < {RK(c)} then goto {JumpOfNext()}";
            case LuaOpCode.Le:
                return $"if {(a != 0 ? "" : "not ")}{RK(b)} <= {RK(c)} then goto {JumpOfNext()}";
            case LuaOpCode.Test:
                return $"if {(c != 0 ? "" : "not ")}{R(a)} then goto {JumpOfNext()}";
            case LuaOpCode.TestSet:
                return $"if {(c != 0 ? "" : "not ")}{R(b)} then {R(a)} := {R(b)}; goto {JumpOfNext()}";
            case LuaOpCode.Call:
            {
                string args = b == 0 ? $"{R(a + 1)}..top" : Range(a + 1, a + b - 1);
                string call = $"{R(a)}({args})";
                return c switch
                {
                    0 => $"{R(a)}..top := {call}",
                    1 => call,
                    _ => $"{Range(a, a + c - 2)} := {call}",
                };
            }
            case LuaOpCode.TailCall:
                return $"return {R(a)}({(b == 0 ? $"{R(a + 1)}..top" : Range(a + 1, a + b - 1))})";
            case LuaOpCode.Return:
                return b == 0 ? $"return {R(a)}..top" : b == 1 ? "return" : $"return {Range(a, a + b - 2)}";
            case LuaOpCode.ForLoop:
                return $"{R(a)} += {R(a + 2)}; if {R(a)} within {R(a + 1)} then {R(a + 3, pc + 1 + ins.SBx)} := {R(a)}; goto {Target(pc, ins.SBx)}";
            case LuaOpCode.ForPrep:
                return $"{R(a)} -= {R(a + 2)}; goto {Target(pc, ins.SBx)}";
            case LuaOpCode.TForCall:
                return $"{Range(a + 3, a + 2 + c)} := {R(a)}({R(a + 1)}, {R(a + 2)})";
            case LuaOpCode.TForLoop:
                if (chunk.IsLua51)
                    return $"{Range(a + 3, a + 2 + c)} := {R(a)}({R(a + 1)}, {R(a + 2)}); if {R(a + 3)} ~= nil then {R(a + 2)} := {R(a + 3)} else skip next";
                return $"if {R(a + 1)} ~= nil then {R(a)} := {R(a + 1)}; goto {Target(pc, ins.SBx)}";
            case LuaOpCode.SetList:
            {
                int block = c;
                if (c == 0 && pc + 1 < f.Code.Length)
                    block = chunk.IsLua51 ? (int)f.Code[pc + 1].Raw : f.Code[pc + 1].Ax;
                int first = (block - 1) * 50 + 1;
                return $"{R(a)}[{first}..] := " + (b == 0 ? $"{R(a + 1)}..top" : Range(a + 1, a + b));
            }
            case LuaOpCode.Close:
                return $"close upvalues >= r{a}";
            case LuaOpCode.Closure:
            {
                string name = ins.Bx < f.Protos.Length
                    ? (names is not null && names.TryGetValue(f.Protos[ins.Bx], out var nm) ? nm : "function " + f.Protos[ins.Bx].Path) +
                      (f.Protos[ins.Bx].LineDefined > 0 ? $" (line {f.Protos[ins.Bx].LineDefined})" : "")
                    : $"proto {ins.Bx}?";
                return $"{R(a)} := closure {name}";
            }
            case LuaOpCode.VarArg:
                return b == 0 ? $"{R(a)}..top := ..." : $"{Range(a, a + b - 2)} := ...";
            case LuaOpCode.ExtraArg:
                return $"argument {ins.Ax}";
        }
        return "";
    }

    private static string GlobalName(LuaFunction f, int k)
    {
        if (k < 0 || k >= f.Constants.Length) return $"_G[K{k}?]";
        var c = f.Constants[k];
        return c.IsString && LuaText.IsIdentifier(c.Text) ? c.Text! : "_G[" + c.ToLiteral() + "]";
    }
}

/// <summary>
/// Best-effort names for function prototypes, from the instruction that stores each closure:
/// a global (<c>Foo</c>), a field (<c>Menu.Show</c>, <c>obj:Update</c> when the first
/// parameter is <c>self</c>), a local or an upvalue.
/// </summary>
internal static class LuaFunctionNames
{
    public static Dictionary<LuaFunction, string> Compute(LuaChunk chunk)
    {
        var names = new Dictionary<LuaFunction, string>();
        foreach (var f in chunk.Main.Descendants())
        {
            LuaLocals? locals = null;
            for (int pc = 0; pc < f.Code.Length; pc++)
            {
                var ins = f.Code[pc];
                if (ins.Op != LuaOpCode.Closure || ins.Bx >= f.Protos.Length) continue;
                var proto = f.Protos[ins.Bx];
                if (names.ContainsKey(proto)) continue;
                locals ??= new LuaLocals(f);
                int next = pc + 1 + (chunk.IsLua51 ? proto.NumUpvalues : 0);
                var name = NameFor(chunk, f, locals, pc, next, ins.A, proto);
                if (name is not null)
                    names[proto] = name;
            }
        }
        return names;
    }

    private static string? NameFor(LuaChunk chunk, LuaFunction f, LuaLocals locals, int pc, int next, int reg, LuaFunction proto)
    {
        // A local whose scope starts at the closure (local function) or right after it.
        if (locals.At(reg, pc) is { } l1) return l1.Name;
        if (next < f.Code.Length + 1 && locals.At(reg, next) is { } l2 && l2.StartPc <= next) return l2.Name;
        if (next >= f.Code.Length) return null;
        var st = f.Code[next];
        string? KStr(int k) => k >= 0 && k < f.Constants.Length && f.Constants[k].IsString ? f.Constants[k].Text : null;
        bool method = proto.NumParams > 0 && proto.Locals.Length > 0 && proto.Locals[0].Name == "self";
        switch (st.Op)
        {
            case LuaOpCode.SetGlobal when st.A == reg:
                return KStr(st.Bx);
            case LuaOpCode.SetTabUp when st.C == reg && LuaInstruction.IsConstant(st.B) && f.UpvalueName(st.A) == "_ENV":
                return KStr(st.B - LuaInstruction.RkConstantBit);
            case LuaOpCode.SetUpval when st.A == reg:
                return f.UpvalueName(st.B);
            case LuaOpCode.SetTable when st.C == reg && LuaInstruction.IsConstant(st.B):
            {
                var key = KStr(st.B - LuaInstruction.RkConstantBit);
                if (key is null) return null;
                var table = TableName(chunk, f, locals, st.A, next, 0);
                return (table ?? "?") + (method ? ":" : ".") + key;
            }
            case LuaOpCode.Move when st.B == reg && locals.At(st.A, next) is { } l3:
                return l3.Name;
        }
        return null;
    }

    // What register `reg` holds at `pc`: a local, or a global / field loaded just before.
    private static string? TableName(LuaChunk chunk, LuaFunction f, LuaLocals locals, int reg, int pc, int depth)
    {
        if (locals.At(reg, pc) is { } l) return l.Name;
        if (depth > 4) return null;
        for (int p = pc - 1; p >= 0 && p >= pc - 64; p--)
        {
            var ins = f.Code[p];
            if (ins.Op is LuaOpCode.Jmp or LuaOpCode.Return or LuaOpCode.ForLoop or LuaOpCode.TForLoop) return null;
            if (ins.A != reg) continue;
            string? KStr(int k) => k >= 0 && k < f.Constants.Length && f.Constants[k].IsString ? f.Constants[k].Text : null;
            switch (ins.Op)
            {
                case LuaOpCode.GetGlobal: return KStr(ins.Bx);
                case LuaOpCode.GetTabUp when LuaInstruction.IsConstant(ins.C) && f.UpvalueName(ins.B) == "_ENV":
                    return KStr(ins.C - LuaInstruction.RkConstantBit);
                case LuaOpCode.GetUpval: return f.UpvalueName(ins.B);
                case LuaOpCode.Move: return TableName(chunk, f, locals, ins.B, p, depth + 1);
                case LuaOpCode.GetTable when LuaInstruction.IsConstant(ins.C):
                {
                    var key = KStr(ins.C - LuaInstruction.RkConstantBit);
                    var b = TableName(chunk, f, locals, ins.B, p, depth + 1);
                    return key is null || b is null ? null : b + "." + key;
                }
                case LuaOpCode.SetTable or LuaOpCode.SetList or LuaOpCode.SetGlobal or LuaOpCode.SetTabUp or LuaOpCode.SetUpval
                    or LuaOpCode.Test or LuaOpCode.Eq or LuaOpCode.Lt or LuaOpCode.Le:
                    continue; // these don't write register A
                default: return null;
            }
        }
        return null;
    }
}
