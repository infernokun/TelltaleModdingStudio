namespace TelltaleModding.Core.Scripts;

/// <summary>Raised inside the decompiler when a construct can't be reconstructed.</summary>
internal sealed class DecompileException : Exception
{
    public DecompileException(string message) : base(message) { }
}

/// <summary>
/// Turns a Lua 5.1/5.2 chunk back into source. Each function is decompiled on its own:
/// value-producing and/or/comparison expressions are found first from their jump pattern
/// (all exits converge on one register) and collapsed; statements are then rebuilt by a
/// walk over the instruction ranges, recognising loops from their back jumps, for-loop
/// instructions, and if/elseif/else from condition chains whose short-circuit graph is
/// reduced to a single and/or/not expression. Registers hold pending expressions until an
/// instruction consumes them or a local's debug scope begins. Anything that doesn't fit is
/// emitted as a commented instruction listing for that range only.
/// </summary>
internal sealed class LuaDecompiler
{
    private LuaDecompiler(LuaChunk chunk, IReadOnlyDictionary<LuaFunction, string> names)
    {
        Chunk = chunk;
        Names = names;
    }

    public LuaChunk Chunk { get; }
    public IReadOnlyDictionary<LuaFunction, string> Names { get; }
    public int Fallbacks { get; set; }

    public static string Decompile(LuaChunk chunk, IReadOnlyDictionary<LuaFunction, string> names, out int fallbacks)
    {
        var d = new LuaDecompiler(chunk, names);
        var main = d.DecompileFunction(chunk.Main);
        var w = new LuaWriter(chunk.SinglePrecision);
        w.WriteLine($"-- Decompiled from {(chunk.IsLua51 ? "Lua 5.1" : "Lua 5.2")} bytecode");
        if (chunk.SourceName is { } src)
            w.WriteLine("-- Source: " + src);
        w.WriteBlankLine();
        w.WriteBlock(main.Body, topLevel: true);
        fallbacks = d.Fallbacks;
        return w.ToString();
    }

    /// <param name="upvalueNames">Names the enclosing function uses for the captured values, for stripped code.</param>
    public FunctionExpr DecompileFunction(LuaFunction f, IReadOnlyList<string>? upvalueNames = null) =>
        new FunctionDecompiler(this, f, upvalueNames).Run();
}

/// <summary>Decompiles one function prototype.</summary>
internal sealed partial class FunctionDecompiler
{
    private const int ExitTrue = -1, ExitFalse = -2;
    /// <summary>Real compilers stop at about 200 levels; deeper trees only come from damaged data.</summary>
    private const int MaxDepth = 250;
    private int _blockDepth;

    private readonly LuaDecompiler _d;
    private readonly IReadOnlyList<string>? _parentUpvalueNames;
    private readonly LuaFunction _f;
    private readonly LuaInstruction[] _code;
    private readonly int _n;
    private readonly bool _v51;
    private readonly LuaLocals _locals;
    private readonly bool _synthetic;
    private readonly bool[] _pseudo;
    private readonly List<int>?[] _backJumps;
    private readonly List<int>?[] _jumpSources;
    private readonly Region?[] _regionAt;
    private readonly bool[] _reachable;

    // Evaluator state.
    private readonly LuaExpr?[] _pending;
    private readonly int[] _pendingPc;
    private int _captureReg = -1;
    private int _multiTop = -1;
    private readonly List<(LuaExpr Target, LuaExpr Value)> _assignBuffer = new();
    private readonly HashSet<int> _suppressed = new();
    private readonly HashSet<int> _declared = new();
    private readonly HashSet<int> _loopVarRegs = new();
    private int _loopDepth;
    private List<LuaStmt> _out = new();
    private List<(int Local, int Stmt)> _levelDecls = new();

    private sealed record Ctx(int Continue, int LoopExit, int SharedHeader)
    {
        public static readonly Ctx Top = new(-1, -1, -1);
    }

    private sealed class Region
    {
        public int Start;
        public int Fin;
        public int Reg;
        public int Pt = -1, Pf = -1, Fj = -1;
        public List<int> Tests = new();
        public int ValueEnd => Pf >= 0 ? (Fj >= 0 ? Fj : Pf) : Fin;
        /// <summary>The last operand is a value (not a comparison feeding the LOADBOOL pair).</summary>
        public bool HasFinal => Pf < 0 || Fj >= 0;
    }

    public FunctionDecompiler(LuaDecompiler d, LuaFunction f, IReadOnlyList<string>? upvalueNames = null)
    {
        _d = d;
        _f = f;
        _parentUpvalueNames = upvalueNames;
        _code = f.Code;
        _n = _code.Length;
        _v51 = d.Chunk.IsLua51;
        int regs = Math.Max(f.MaxStackSize, 1) + 2;

        // Stripped functions: treat every register as a local live for the whole function.
        _synthetic = f.Locals.Length == 0 && f.LineInfo.Length == 0 && (f.NumParams > 0 || _n > 1);
        if (_synthetic)
        {
            // Names carry the nesting depth so inner functions never shadow captured outer ones.
            int depth = f.Path.Count(c => c == '/');
            string suffix = depth == 0 ? "" : "_" + depth;
            var fake = new LuaLocalVariable[Math.Max(f.MaxStackSize, f.NumParams)];
            for (int r = 0; r < fake.Length; r++)
                fake[r] = new LuaLocalVariable((r < f.NumParams ? "a" + (r + 1) : "r" + r) + suffix, 0, _n + 1);
            _locals = new LuaLocals(f, fake);
        }
        else
            _locals = new LuaLocals(f);
        regs = Math.Max(regs, _locals.MaxRegister + 1);

        _pending = new LuaExpr?[regs];
        _pendingPc = new int[regs];
        _pseudo = new bool[_n + 1];
        _backJumps = new List<int>?[_n + 1];
        _jumpSources = new List<int>?[_n + 2];
        _regionAt = new Region?[_n + 1];

        for (int pc = 0; pc < _n; pc++)
        {
            var ins = _code[pc];
            if (_pseudo[pc]) continue;
            switch (ins.Op)
            {
                case LuaOpCode.Closure when _v51 && ins.Bx < f.Protos.Length:
                    for (int k = 1; k <= f.Protos[ins.Bx].NumUpvalues && pc + k < _n; k++)
                        _pseudo[pc + k] = true;
                    break;
                case LuaOpCode.SetList when ins.C == 0 && pc + 1 < _n:
                case LuaOpCode.LoadKx when pc + 1 < _n:
                    _pseudo[pc + 1] = true;
                    break;
            }
        }
        for (int pc = 0; pc < _n; pc++)
        {
            if (_pseudo[pc]) continue;
            var ins = _code[pc];
            int t;
            switch (ins.Op)
            {
                case LuaOpCode.Jmp:
                case LuaOpCode.ForLoop:
                case LuaOpCode.ForPrep:
                case LuaOpCode.TForLoop when !_v51:
                    t = pc + 1 + ins.SBx;
                    break;
                default:
                    continue;
            }
            if (t < 0 || t > _n) continue;
            (_jumpSources[t] ??= new List<int>()).Add(pc);
            if (ins.Op == LuaOpCode.Jmp && t <= pc)
                (_backJumps[t] ??= new List<int>()).Add(pc);
        }
        _reachable = ComputeReachable();
        FindRegions();
    }

    private bool[] ComputeReachable()
    {
        var seen = new bool[_n + 1];
        var stack = new Stack<int>();
        stack.Push(0);
        void Go(int t) { if (t >= 0 && t < _n && !seen[t]) stack.Push(t); }
        while (stack.Count > 0)
        {
            int pc = stack.Pop();
            if (pc < 0 || pc >= _n || seen[pc]) continue;
            seen[pc] = true;
            var i = _code[pc];
            switch (i.Op)
            {
                case LuaOpCode.Jmp: Go(Target(pc)); break;
                case LuaOpCode.Return: break;
                case LuaOpCode.TailCall: Go(pc + 1); break;
                case LuaOpCode.Eq or LuaOpCode.Lt or LuaOpCode.Le or LuaOpCode.Test or LuaOpCode.TestSet:
                    Go(pc + 1); Go(pc + 2); break;
                case LuaOpCode.LoadBool when i.C != 0: Go(pc + 2); break;
                case LuaOpCode.ForPrep: Go(Target(pc)); break;
                case LuaOpCode.ForLoop: Go(Target(pc)); Go(pc + 1); break;
                case LuaOpCode.TForLoop when _v51: Go(pc + 1); Go(pc + 2); break;
                case LuaOpCode.TForLoop: Go(Target(pc)); Go(pc + 1); break;
                case LuaOpCode.Closure when _v51 && i.Bx < _f.Protos.Length:
                    for (int k = 1; k <= _f.Protos[i.Bx].NumUpvalues && pc + k < _n; k++) seen[pc + k] = true;
                    Go(pc + 1 + _f.Protos[i.Bx].NumUpvalues); break;
                case LuaOpCode.SetList when i.C == 0: if (pc + 1 < _n) seen[pc + 1] = true; Go(pc + 2); break;
                case LuaOpCode.LoadKx: if (pc + 1 < _n) seen[pc + 1] = true; Go(pc + 2); break;
                default: Go(pc + 1); break;
            }
        }
        return seen;
    }

    public FunctionExpr Run()
    {
        var parameters = new List<string>();
        for (int i = 0; i < _f.NumParams; i++)
            parameters.Add(i < _locals.Count && _locals[i].StartPc == 0 ? _locals[i].Name : "a" + (i + 1));
        List<LuaStmt> body;
        try
        {
            body = Block(0, _n, Ctx.Top);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _d.Fallbacks++;
            body = new List<LuaStmt> { Listing(0, _n, ex.Message) };
        }
        return new FunctionExpr(_f, parameters, _f.IsVararg, body);
    }

    // ------------------------------------------------------------------ helpers

    private int Target(int pc) => pc + 1 + _code[pc].SBx;

    private static bool IsTestOp(LuaOpCode op) =>
        op is LuaOpCode.Eq or LuaOpCode.Lt or LuaOpCode.Le or LuaOpCode.Test or LuaOpCode.TestSet;

    /// <summary>A conditional instruction followed by its jump.</summary>
    private bool IsTest(int pc) =>
        pc >= 0 && pc + 1 < _n && !_pseudo[pc] && IsTestOp(_code[pc].Op) && _code[pc + 1].Op == LuaOpCode.Jmp;

    private bool IsTestPartner(int pc) => pc > 0 && IsTest(pc - 1);

    /// <summary>Where control really goes from <paramref name="pc"/>, following unconditional jumps.</summary>
    private int Resolve(int pc)
    {
        for (int guard = 0; guard < 100 && pc >= 0 && pc < _n; guard++)
        {
            var op = _code[pc].Op;
            if (op == LuaOpCode.Jmp && !_pseudo[pc])
            {
                int t = Target(pc);
                if (t == pc) break;
                pc = t;
            }
            else if (op == LuaOpCode.Close)
                pc++;
            else
                break;
        }
        return pc;
    }

    private bool IsTemp(int reg, int pc) => _locals.IndexAt(reg, pc) < 0;

    /// <summary>True when the instruction only builds an expression (writes temporaries).</summary>
    private bool ExprOnly(int pc, int allowReg = -1)
    {
        if (_pseudo[pc]) return true;
        var i = _code[pc];
        bool T(int reg) => reg == allowReg || IsTemp(reg, pc);
        switch (i.Op)
        {
            case LuaOpCode.Move: case LuaOpCode.LoadK: case LuaOpCode.LoadKx: case LuaOpCode.GetUpval:
            case LuaOpCode.GetGlobal: case LuaOpCode.GetTabUp: case LuaOpCode.GetTable: case LuaOpCode.NewTable:
            case LuaOpCode.Closure: case LuaOpCode.Add: case LuaOpCode.Sub: case LuaOpCode.Mul: case LuaOpCode.Div:
            case LuaOpCode.Mod: case LuaOpCode.Pow: case LuaOpCode.Unm: case LuaOpCode.Not: case LuaOpCode.Len:
            case LuaOpCode.Concat: case LuaOpCode.Self: case LuaOpCode.VarArg: case LuaOpCode.LoadNil:
                return T(i.A);
            case LuaOpCode.LoadBool:
                return i.C == 0 && T(i.A);
            case LuaOpCode.Call:
                return i.C != 1 && T(i.A);
            case LuaOpCode.SetTable:
            case LuaOpCode.SetList:
                return IsTemp(i.A, pc) && BuildsTable(i.A, pc);
            case LuaOpCode.ExtraArg:
                return true;
        }
        return false;
    }

    /// <summary>True when <paramref name="reg"/> was last written by NEWTABLE (a constructor in progress).</summary>
    private bool BuildsTable(int reg, int pc)
    {
        if (_buildsTable.TryGetValue(pc, out bool known)) return known;
        bool result = false;
        for (int p = pc - 1; p >= 0; p--)
        {
            if (_pseudo[p]) continue;
            var i = _code[p];
            if (i.Op == LuaOpCode.NewTable && i.A == reg) { result = true; break; }
            if (i.Op is LuaOpCode.SetTable or LuaOpCode.SetList && i.A == reg && _buildsTable.TryGetValue(p, out bool earlier))
            {
                result = earlier;
                break;
            }
            if (WritesReg(i, reg) || i.Op is LuaOpCode.Jmp or LuaOpCode.Return) break;
        }
        _buildsTable[pc] = result;
        return result;
    }

    private readonly Dictionary<int, bool> _buildsTable = new();

    private bool HasDeclarationAt(int pc)
    {
        foreach (int li in _locals.StartingAt(pc))
            if (Declarable(li, pc)) return true;
        return false;
    }

    private bool Declarable(int li, int pc)
    {
        var l = _locals[li];
        if (l.Name.StartsWith('(') || _suppressed.Contains(li) || _declared.Contains(li)) return false;
        if (l.StartPc == 0 && li < _f.NumParams) return false;
        if (_v51 && l.StartPc == 0 && l.Name == "arg" && li == _f.NumParams && (_f.VarargFlags & 1) != 0) return false; // 5.1 compat `arg`
        if (_synthetic && li < _f.NumParams) return false;
        return true;
    }

    private bool ReadsReg(LuaInstruction i, int reg)
    {
        int a = i.A, b = i.B, c = i.C;
        bool RK(int x) => x == reg;
        switch (i.Op)
        {
            case LuaOpCode.Move: case LuaOpCode.Unm: case LuaOpCode.Not: case LuaOpCode.Len: return b == reg;
            case LuaOpCode.GetTable: return b == reg || RK(c);
            case LuaOpCode.GetTabUp: return RK(c);
            case LuaOpCode.SetTable: return a == reg || RK(b) || RK(c);
            case LuaOpCode.SetTabUp: return RK(b) || RK(c);
            case LuaOpCode.Self: return b == reg || RK(c);
            case LuaOpCode.Add: case LuaOpCode.Sub: case LuaOpCode.Mul: case LuaOpCode.Div:
            case LuaOpCode.Mod: case LuaOpCode.Pow: case LuaOpCode.Eq: case LuaOpCode.Lt: case LuaOpCode.Le:
                return RK(b) || RK(c);
            case LuaOpCode.Concat: return reg >= b && reg <= c;
            case LuaOpCode.Test: return a == reg;
            case LuaOpCode.TestSet: return b == reg;
            case LuaOpCode.Call: case LuaOpCode.TailCall: return reg >= a && (b == 0 || reg <= a + b - 1);
            case LuaOpCode.Return: return reg >= a && (b == 0 || reg <= a + b - 2);
            case LuaOpCode.SetGlobal: case LuaOpCode.SetUpval: return a == reg;
            case LuaOpCode.SetList: return reg >= a && (b == 0 || reg <= a + b);
            case LuaOpCode.ForLoop: case LuaOpCode.ForPrep: return reg >= a && reg <= a + 2;
            case LuaOpCode.TForCall: case LuaOpCode.TForLoop: return reg >= a && reg <= a + 2;
        }
        return false;
    }

    private bool WritesReg(LuaInstruction i, int reg)
    {
        int a = i.A;
        switch (i.Op)
        {
            case LuaOpCode.Move: case LuaOpCode.LoadK: case LuaOpCode.LoadKx: case LuaOpCode.LoadBool:
            case LuaOpCode.GetUpval: case LuaOpCode.GetGlobal: case LuaOpCode.GetTabUp: case LuaOpCode.GetTable:
            case LuaOpCode.NewTable: case LuaOpCode.Add: case LuaOpCode.Sub: case LuaOpCode.Mul: case LuaOpCode.Div:
            case LuaOpCode.Mod: case LuaOpCode.Pow: case LuaOpCode.Unm: case LuaOpCode.Not: case LuaOpCode.Len:
            case LuaOpCode.Concat: case LuaOpCode.Closure:
                return a == reg;
            case LuaOpCode.LoadNil: return reg >= a && reg <= (_v51 ? i.B : a + i.B);
            case LuaOpCode.Self: return reg == a || reg == a + 1;
            case LuaOpCode.Call: return reg >= a && (i.C == 0 || reg <= a + i.C - 2);
            case LuaOpCode.VarArg: return reg >= a && (i.B == 0 || reg <= a + i.B - 2);
        }
        return false;
    }

    /// <summary>True when <paramref name="reg"/>'s value at <paramref name="from"/> is used before being overwritten on some path.</summary>
    private bool LiveAt(int reg, int from)
    {
        foreach (int li in _locals.StartingAt(from))
            if (_locals.RegisterOf(li) == reg) return true; // the value initialises a local
        var seen = new HashSet<int>();
        var work = new Stack<int>();
        work.Push(from);
        while (work.Count > 0 && seen.Count < 2048)
        {
            int pc = work.Pop();
            if (pc < 0 || pc >= _n || !seen.Add(pc)) continue;
            if (_pseudo[pc]) { work.Push(pc + 1); continue; }
            var i = _code[pc];
            if (ReadsReg(i, reg)) return true;
            if (WritesReg(i, reg) || (i.Op == LuaOpCode.TestSet && i.A == reg)) continue;
            switch (i.Op)
            {
                case LuaOpCode.Return: case LuaOpCode.TailCall:
                    continue;
                case LuaOpCode.ForPrep:
                    work.Push(Target(pc));
                    continue;
                case LuaOpCode.ForLoop:
                case LuaOpCode.TForLoop when !_v51:
                    work.Push(Target(pc));
                    work.Push(pc + 1);
                    continue;
                case LuaOpCode.TForLoop:
                    work.Push(pc + 1);
                    work.Push(pc + 2);
                    continue;
                case LuaOpCode.Jmp:
                    if (Target(pc) > pc) work.Push(Target(pc));
                    continue;
                case LuaOpCode.Eq or LuaOpCode.Lt or LuaOpCode.Le or LuaOpCode.Test or LuaOpCode.TestSet:
                    work.Push(pc + 2);
                    work.Push(pc + 1);
                    continue;
                case LuaOpCode.LoadBool when i.C != 0:
                    work.Push(pc + 2);
                    continue;
                default:
                    work.Push(pc + 1);
                    continue;
            }
        }
        return false;
    }

    private bool HasForeignJumpInto(int from, int to, int ownerStart, int ownerEnd)
    {
        for (int t = Math.Max(from, 0); t <= to && t <= _n; t++)
            if (_jumpSources[t] is { } src)
                foreach (int s in src)
                    if (s < ownerStart || s >= ownerEnd)
                        return true;
        return false;
    }

    private CommentStmt Listing(int from, int to, string reason)
    {
        var lines = new List<string>
        {
            $"[decompile fallback: {reason}; instructions {from + 1}-{Math.Max(from + 1, to)} of function {_f.Path}]",
        };
        lines.AddRange(LuaDisassembler.ListRange(_d.Chunk, _f, _locals, from, to, _d.Names));
        return new CommentStmt(lines);
    }
}
