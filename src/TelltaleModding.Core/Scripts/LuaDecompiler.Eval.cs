namespace TelltaleModding.Core.Scripts;

// Symbolic execution: registers hold pending expressions until consumed or declared.
internal sealed partial class FunctionDecompiler
{
    private void Emit(LuaStmt s)
    {
        FlushAssignments();
        _out.Add(s);
    }

    private void FlushAssignments()
    {
        if (_assignBuffer.Count == 0) return;
        var targets = new List<LuaExpr>();
        var values = new List<LuaExpr?>();
        for (int i = _assignBuffer.Count - 1; i >= 0; i--)
        {
            targets.Add(_assignBuffer[i].Target);
            values.Add(_assignBuffer[i].Value);
        }
        _assignBuffer.Clear();
        var list = CollapseMulti(values);
        // Trailing nils loaded into registers are the adjustment of a shorter value list.
        while (list.Count > 1 && ReferenceEquals(list[^1], ConstExpr.LoadedNil)) list.RemoveAt(list.Count - 1);
        _out.Add(new AssignStmt(targets, list));
    }

    private bool HasPendingTemps()
    {
        for (int r = 0; r < _pending.Length; r++)
            if (_pending[r] is not null && r != _captureReg) return true;
        return false;
    }

    private void Store(LuaExpr target, LuaExpr value)
    {
        if (_assignBuffer.Count > 0 || HasPendingTemps())
        {
            _assignBuffer.Add((target, value));
            if (!HasPendingTemps()) FlushAssignments();
            return;
        }
        _out.Add(new AssignStmt(new List<LuaExpr> { target }, new List<LuaExpr> { value }));
    }

    /// <summary>Joins consecutive parts of one multi-result call/vararg back into the call.</summary>
    private static List<LuaExpr> CollapseMulti(List<LuaExpr?> values)
    {
        var list = new List<LuaExpr>();
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (v is MultiPartExpr mp)
            {
                int k = 1;
                while (i + k < values.Count && values[i + k] is MultiPartExpr next && next.Source == mp.Source && next.Index == mp.Index + k)
                    k++;
                list.Add(mp.Source);
                i += k - 1;
                continue;
            }
            list.Add(v ?? ConstExpr.Nil);
        }
        return list;
    }

    private void DeclareLocals(int pc, bool onlyEmpty = false)
    {
        var starting = _locals.StartingAt(pc);
        if (starting.Count == 0) return;
        var names = new List<string>();
        var values = new List<LuaExpr?>();
        var declared = new List<int>();
        foreach (int li in starting.OrderBy(i => _locals.RegisterOf(i)))
        {
            if (!Declarable(li, pc)) continue;
            if (onlyEmpty && _locals[li].EndPc > pc) continue;
            _declared.Add(li);
            declared.Add(li);
            names.Add(_locals[li].Name);
            values.Add(TakePending(_locals.RegisterOf(li)));
        }
        if (names.Count == 0) return;
        FlushAssignments();
        while (values.Count > 0 && (values[^1] is null || values[^1] is ConstExpr { IsNil: true }))
            values.RemoveAt(values.Count - 1);
        var list = CollapseMulti(values);
        bool localFunction = names.Count == 1 && list.Count == 1 && list[0] is FunctionExpr;
        _out.Add(new LocalStmt(names, list) { IsLocalFunction = localFunction });
        foreach (int li in declared)
            _levelDecls.Add((li, _out.Count - 1));
    }

    // ------------------------------------------------------------------ registers

    private LuaExpr? TakePending(int reg)
    {
        if (reg < 0 || reg >= _pending.Length) return null;
        var e = _pending[reg];
        _pending[reg] = null;
        return e;
    }

    private LuaExpr Read(int reg, int pc)
    {
        if (reg >= 0 && reg < _pending.Length && _pending[reg] is { } e)
        {
            _pending[reg] = null;
            return e;
        }
        var l = _locals.At(reg, pc);
        if (l is not null) return new LocalExpr(l.Name);
        // Registers are nil on entry; the compiler drops a leading LOADNIL.
        if (!WrittenBefore(reg, pc)) return ConstExpr.Nil;
        throw new DecompileException($"register r{reg} is read before it is set");
    }

    /// <summary>True when some instruction before <paramref name="pc"/> writes <paramref name="reg"/>.</summary>
    private bool WrittenBefore(int reg, int pc)
    {
        for (int i = 0; i < pc && i < _n; i++)
            if (!_pseudo[i] && (WritesReg(_code[i], reg) || _code[i].Op is LuaOpCode.TestSet && _code[i].A == reg))
                return true;
        return false;
    }

    private LuaExpr RK(int x, int pc) =>
        LuaInstruction.IsConstant(x) ? K(x - LuaInstruction.RkConstantBit) : Read(x, pc);

    private LuaExpr K(int k)
    {
        if (k < 0 || k >= _f.Constants.Length) throw new DecompileException($"constant {k} out of range");
        var c = _f.Constants[k];
        return c.Kind switch
        {
            LuaConstantKind.Nil => ConstExpr.Nil,
            LuaConstantKind.Boolean => c.Boolean ? ConstExpr.True : ConstExpr.False,
            _ => new ConstExpr(c),
        };
    }

    private void Write(int reg, LuaExpr e, int pc)
    {
        if (reg < 0 || reg >= _pending.Length) throw new DecompileException($"register r{reg} out of range");
        if (e.Depth > MaxDepth) throw new DecompileException("expression nested too deeply");
        if (reg != _captureReg && _locals.At(reg, pc) is { } l)
        {
            Store(new LocalExpr(l.Name), e);
            return;
        }
        _pending[reg] = e;
        _pendingPc[reg] = pc;
    }

    private List<LuaExpr> ReadRange(int first, int count, int pc)
    {
        int last = count < 0 ? _multiTop : first + count - 1;
        if (count < 0 && _multiTop < first) throw new DecompileException("open value list without a multi-value source");
        var list = new List<LuaExpr>();
        for (int r = first; r <= last; r++)
            list.Add(Read(r, pc));
        return list;
    }

    private string UpvalueName(int index)
    {
        var name = _f.UpvalueName(index);
        if (name is not null) return name;
        if (!_v51 && _f.Parent is null && index == 0) return "_ENV";
        if (_parentUpvalueNames is not null && index < _parentUpvalueNames.Count) return _parentUpvalueNames[index];
        return "upvalue" + index;
    }

    private LuaExpr Upvalue(int index) => new UpvalueExpr(UpvalueName(index));

    private LuaExpr TabUp(int up, LuaExpr key)
    {
        if (UpvalueName(up) == "_ENV" && key is ConstExpr { Value.IsString: true } k)
            return new GlobalExpr(k.Value.Text!, true);
        return new IndexExpr(Upvalue(up), key);
    }

    private LuaExpr Global(int k)
    {
        var c = K(k);
        if (c is not ConstExpr { Value.IsString: true } s) throw new DecompileException("global name is not a string");
        return new GlobalExpr(s.Value.Text!, false);
    }

    private CallExpr BuildCall(int pc)
    {
        var i = _code[pc];
        var args = ReadRange(i.A + 1, i.B == 0 ? -1 : i.B - 1, pc);
        var fn = Read(i.A, pc);
        return new CallExpr(fn, args);
    }

    /// <summary>Stores <paramref name="count"/> results of one call/vararg into consecutive registers.</summary>
    private void WriteMulti(int first, int count, LuaExpr source, int pc)
    {
        var targets = new List<LuaExpr>();
        for (int k = 0; k < count; k++)
            if (first + k != _captureReg && _locals.At(first + k, pc) is { } l) targets.Add(new LocalExpr(l.Name));
        if (targets.Count == count)
        {
            Emit(new AssignStmt(targets, new List<LuaExpr> { source })); // all results go to variables
            return;
        }
        for (int k = 0; k < count; k++) Write(first + k, new MultiPartExpr(source, k, count), pc);
    }

    /// <summary>
    /// Without debug info every register is printed as one function-wide local, so a closure
    /// made inside a loop that captures anything but a parameter or loop variable would share
    /// a variable that was fresh per iteration in the original.
    /// </summary>
    private bool CapturesLoopLocal(LuaFunction proto, int pc)
    {
        for (int k = 0; k < proto.NumUpvalues; k++)
        {
            bool inStack;
            int index;
            if (_v51)
            {
                if (pc + 1 + k >= _n) break;
                inStack = _code[pc + 1 + k].Op == LuaOpCode.Move;
                index = _code[pc + 1 + k].B;
            }
            else
            {
                if (k >= proto.Upvalues.Length) break;
                inStack = proto.Upvalues[k].InStack;
                index = proto.Upvalues[k].Index;
            }
            if (inStack && index >= _f.NumParams && !_loopVarRegs.Contains(index)) return true;
        }
        return false;
    }

    /// <summary>What the closure built at <paramref name="pc"/> calls each of its upvalues here.</summary>
    private List<string> CapturedNames(LuaFunction proto, int pc)
    {
        var names = new List<string>();
        for (int k = 0; k < proto.NumUpvalues; k++)
        {
            bool inStack;
            int index;
            if (_v51)
            {
                if (pc + 1 + k >= _n) break;
                var pseudo = _code[pc + 1 + k];
                inStack = pseudo.Op == LuaOpCode.Move;
                index = pseudo.B;
            }
            else
            {
                if (k >= proto.Upvalues.Length) break;
                inStack = proto.Upvalues[k].InStack;
                index = proto.Upvalues[k].Index;
            }
            names.Add(inStack ? _locals.At(index, pc)?.Name ?? "r" + index : UpvalueName(index));
        }
        return names;
    }

    /// <summary>SETLIST into a table that is a variable (not a constructor): one assignment per item.</summary>
    private int SetListAsAssignments(int pc)
    {
        var i = _code[pc];
        int block = i.C, next = pc + 1;
        if (i.C == 0)
        {
            if (pc + 1 >= _n) throw new DecompileException("SETLIST without block number");
            block = _v51 ? (int)_code[pc + 1].Raw : _code[pc + 1].Ax;
            next = pc + 2;
        }
        int count = i.B == 0 ? _multiTop - i.A : i.B;
        if (count < 0) throw new DecompileException("bad list size");
        int first = (block - 1) * 50 + 1;
        var values = new List<LuaExpr>();
        for (int k = 1; k <= count; k++) values.Add(Read(i.A + k, pc));
        var table = Read(i.A, pc);
        for (int k = 0; k < values.Count; k++)
            Emit(new AssignStmt(new List<LuaExpr> { new IndexExpr(table, new ConstExpr(LuaConstant.FromNumber(first + k))) },
                new List<LuaExpr> { values[k] }));
        return next;
    }

    // ------------------------------------------------------------------ instructions

    /// <summary>Executes one straight-line instruction and returns the next pc.</summary>
    private int Exec(int pc)
    {
        var i = _code[pc];
        int a = i.A, b = i.B, c = i.C;
        switch (i.Op)
        {
            case LuaOpCode.Move:
                Write(a, Read(b, pc), pc);
                break;
            case LuaOpCode.LoadK:
                Write(a, K(i.Bx), pc);
                break;
            case LuaOpCode.LoadKx:
                if (pc + 1 >= _n) throw new DecompileException("LOADKX without argument");
                Write(a, K(_code[pc + 1].Ax), pc);
                return pc + 2;
            case LuaOpCode.LoadBool:
                if (c != 0) throw new DecompileException("boolean load outside an expression");
                Write(a, b != 0 ? ConstExpr.True : ConstExpr.False, pc);
                break;
            case LuaOpCode.LoadNil:
            {
                int last = _v51 ? b : a + b;
                var targets = new List<LuaExpr>();
                for (int r = a; r <= last; r++)
                {
                    if (r != _captureReg && _locals.At(r, pc) is { } l) targets.Add(new LocalExpr(l.Name));
                    else Write(r, ConstExpr.LoadedNil, pc);
                }
                foreach (var t in targets)
                    Emit(new AssignStmt(new List<LuaExpr> { t }, new List<LuaExpr> { ConstExpr.Nil }));
                break;
            }
            case LuaOpCode.GetUpval:
                Write(a, Upvalue(b), pc);
                break;
            case LuaOpCode.GetGlobal:
                Write(a, Global(i.Bx), pc);
                break;
            case LuaOpCode.GetTabUp:
                Write(a, TabUp(b, RK(c, pc)), pc);
                break;
            case LuaOpCode.GetTable:
            {
                var key = RK(c, pc);
                var obj = Read(b, pc);
                Write(a, new IndexExpr(obj, key), pc);
                break;
            }
            case LuaOpCode.SetGlobal:
                Store(Global(i.Bx), Read(a, pc));
                break;
            case LuaOpCode.SetTabUp:
            {
                var value = RK(c, pc);
                var key = RK(b, pc);
                Store(TabUp(a, key), value);
                break;
            }
            case LuaOpCode.SetUpval:
                Store(Upvalue(b), Read(a, pc));
                break;
            case LuaOpCode.SetTable:
            {
                var value = RK(c, pc);
                var key = RK(b, pc);
                if (a < _pending.Length && _pending[a] is TableExpr t)
                {
                    t.Add(new TableItem(key, value, pc));
                    if (t.Depth > MaxDepth) throw new DecompileException("table nested too deeply");
                }
                else
                    Store(new IndexExpr(Read(a, pc), key), value);
                break;
            }
            case LuaOpCode.NewTable:
                Write(a, new TableExpr(), pc);
                break;
            case LuaOpCode.Self:
            {
                var key = RK(c, pc);
                var obj = Read(b, pc);
                Write(a + 1, new SelfArgExpr(obj), pc);
                Write(a, new MethodRefExpr(obj, key), pc);
                break;
            }
            case LuaOpCode.Add: Arith("+"); break;
            case LuaOpCode.Sub: Arith("-"); break;
            case LuaOpCode.Mul: Arith("*"); break;
            case LuaOpCode.Div: Arith("/"); break;
            case LuaOpCode.Mod: Arith("%"); break;
            case LuaOpCode.Pow: Arith("^"); break;
            case LuaOpCode.Unm: Write(a, new UnaryExpr("-", Read(b, pc)), pc); break;
            case LuaOpCode.Not: Write(a, new UnaryExpr("not", Read(b, pc)), pc); break;
            case LuaOpCode.Len: Write(a, new UnaryExpr("#", Read(b, pc)), pc); break;
            case LuaOpCode.Concat:
            {
                if (c < b) throw new DecompileException("bad concatenation range");
                var parts = new List<LuaExpr>();
                for (int r = b; r <= c; r++) parts.Add(Read(r, pc));
                LuaExpr e = parts[^1];
                for (int k = parts.Count - 2; k >= 0; k--) e = new BinaryExpr("..", parts[k], e);
                Write(a, e, pc);
                break;
            }
            case LuaOpCode.Call:
            {
                var call = BuildCall(pc);
                if (c == 1)
                {
                    call.Results = 0;
                    Emit(new CallStmt(call));
                }
                else if (c == 0)
                {
                    call.Results = -1;
                    Write(a, call, pc);
                    _multiTop = a;
                }
                else if (c == 2)
                {
                    call.Results = 1;
                    Write(a, call, pc);
                }
                else
                {
                    call.Results = c - 1;
                    WriteMulti(a, c - 1, call, pc);
                }
                break;
            }
            case LuaOpCode.VarArg:
                if (b == 0)
                {
                    Write(a, new VarArgExpr(true), pc);
                    _multiTop = a;
                }
                else if (b == 2)
                    Write(a, new VarArgExpr(false), pc);
                else if (b > 2)
                    WriteMulti(a, b - 1, new VarArgExpr(true), pc);
                break;
            case LuaOpCode.SetList:
            {
                if (a >= _pending.Length || _pending[a] is not TableExpr)
                    return SetListAsAssignments(pc);
                int block = c;
                int next = pc + 1;
                if (c == 0)
                {
                    if (pc + 1 >= _n) throw new DecompileException("SETLIST without block number");
                    block = _v51 ? (int)_code[pc + 1].Raw : _code[pc + 1].Ax;
                    next = pc + 2;
                }
                if (a >= _pending.Length || _pending[a] is not TableExpr table)
                    throw new DecompileException("list items stored outside a table constructor");
                int count = b == 0 ? _multiTop - a : b;
                if (count < 0) throw new DecompileException("bad list size");
                int first = (block - 1) * 50 + 1;
                int positional = table.Items.Count(it => it.Key is null);
                for (int k = 1; k <= count; k++)
                {
                    int order = _pendingPc[a + k];
                    var v = Read(a + k, pc);
                    int index = first + k - 1;
                    if (index == positional + 1)
                    {
                        table.Add(new TableItem(null, v, order));
                        positional++;
                    }
                    else
                        table.Add(new TableItem(new ConstExpr(LuaConstant.FromNumber(index)), v, order));
                }
                return next;
            }
            case LuaOpCode.Close:
                break;
            case LuaOpCode.Closure:
            {
                if (i.Bx >= _f.Protos.Length) throw new DecompileException("closure index out of range");
                var proto = _f.Protos[i.Bx];
                if (_synthetic && _loopDepth > 0 && CapturesLoopLocal(proto, pc))
                    throw new DecompileException("closure captures a per-iteration local (no debug info)");
                Write(a, _d.DecompileFunction(proto, CapturedNames(proto, pc)), pc);
                return pc + 1 + (_v51 ? proto.NumUpvalues : 0);
            }
            case LuaOpCode.ExtraArg:
                break;
            default:
                throw new DecompileException($"unexpected {LuaDisassembler.OpName(i.Op, i.RawOp)}");
        }
        return pc + 1;

        void Arith(string op)
        {
            var right = RK(c, pc);
            var left = RK(b, pc);
            Write(a, new BinaryExpr(op, left, right), pc);
        }
    }
}
