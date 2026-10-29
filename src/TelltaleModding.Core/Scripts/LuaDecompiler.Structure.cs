namespace TelltaleModding.Core.Scripts;

// Control-flow reconstruction: value regions, blocks, if/else, loops.
internal sealed partial class FunctionDecompiler
{
    // ------------------------------------------------------------------ value regions

    /// <summary>
    /// Finds and/or/comparison expressions whose result lands in one register. Their exits
    /// are: the join point ("fin") reached by tests that carry the tested value
    /// (TESTSET, or TEST on the result register), and the LOADBOOL false/true pair that
    /// comparisons jump to when the result must be a boolean.
    /// </summary>
    private void FindRegions()
    {
        // Candidate (result register, LOADBOOL pair) per join point; stronger evidence first.
        var seeds = new SortedDictionary<int, List<(int Reg, int Pt, int Pf, int Rank)>>();
        void Seed(int fin, int reg, int pt = -1, int pf = -1, int rank = 0)
        {
            if (fin <= 0 || fin > _n) return;
            if (!seeds.TryGetValue(fin, out var list)) seeds[fin] = list = new();
            int i = list.FindIndex(x => x.Reg == reg);
            if (i < 0) list.Add((reg, pt, pf, rank));
            else if (list[i].Pf < 0 && pf >= 0) list[i] = (reg, pt, pf, Math.Min(rank, list[i].Rank));
        }

        for (int p = 0; p + 1 < _n; p++)
        {
            if (!IsTest(p)) continue;
            int tgt = Target(p + 1);
            var ins = _code[p];
            if (ins.Op == LuaOpCode.TestSet && tgt == p + 2)
                Seed(tgt, ins.A); // `x = y and x`: the value is already in place
            if (tgt <= p + 2 || tgt > _n) continue;
            if (ins.Op == LuaOpCode.TestSet)
                Seed(tgt, ins.A);
            if (tgt + 1 < _n && IsLoadBool(tgt, 0, 1, out int r1) && IsLoadBool(tgt + 1, 1, 0, out int r2) && r1 == r2)
                Seed(tgt + 2, r1, tgt + 1, tgt);
            else if (tgt > 0 && tgt < _n && IsLoadBool(tgt, 1, 0, out int r3) && IsLoadBool(tgt - 1, 0, 1, out int r4) && r3 == r4)
                Seed(tgt + 1, r3, tgt, tgt - 1);
            if (ins.Op == LuaOpCode.Test)
            {
                int reg = ins.A;
                if (IsTemp(reg, p))
                {
                    if (tgt < _n && LiveAt(reg, tgt))
                        Seed(tgt, reg, rank: 1);
                }
                else if (ins.C == 1 && !_synthetic)
                    Seed(tgt, reg, rank: 2); // x = x or default
            }
        }

        var inRegion = new Region?[_n + 1];
        foreach (var (fin, list) in seeds)
        {
            Region? r = null;
            foreach (var s in list.OrderBy(x => x.Rank))
                if ((r = BuildRegion(fin, s.Reg, s.Pt, s.Pf, inRegion)) is not null) break;
            if (r is null) continue;
            for (int pc = r.Start; pc < r.Fin; pc++)
                if (inRegion[pc] is null || inRegion[pc]!.Start >= r.Start)
                    inRegion[pc] = r;
            _regionAt[r.Start] = r;
        }
    }

    private bool IsLoadBool(int pc, int b, int c, out int reg)
    {
        reg = -1;
        if (pc < 0 || pc >= _n || _pseudo[pc]) return false;
        var i = _code[pc];
        if (i.Op != LuaOpCode.LoadBool || i.B != b || (c == 0 ? i.C != 0 : i.C == 0)) return false;
        reg = i.A;
        return true;
    }

    private Region? BuildRegion(int fin, int reg, int pt, int pf, Region?[] inRegion)
    {
        var r = new Region { Fin = fin, Reg = reg, Pt = pt, Pf = pf };
        if (pf >= 0 && pf >= 1 && _code[pf - 1].Op == LuaOpCode.Jmp && !IsTestPartner(pf - 1) &&
            (Target(pf - 1) == fin || Resolve(Target(pf - 1)) == Resolve(fin)))
            r.Fj = pf - 1;
        int valueEnd = r.ValueEnd;

        var tests = new List<int>();
        int pos = valueEnd - 1;
        while (pos >= 0)
        {
            if (pos + 1 < valueEnd && HasDeclarationAt(pos + 1)) break; // statement boundary
            if (inRegion[pos] is { } inner && inner.Fin <= valueEnd)
            {
                pos = inner.Start - 1;
                continue;
            }
            if (_pseudo[pos] && _code[pos].Op != LuaOpCode.Jmp) { pos--; continue; }
            var op = _code[pos].Op;
            if (op == LuaOpCode.Jmp && IsTestPartner(pos))
            {
                int p = pos - 1, tgt = Target(pos);
                var ti = _code[p];
                bool carries = (ti.Op == LuaOpCode.TestSet && ti.A == reg) || (ti.Op == LuaOpCode.Test && ti.A == reg);
                bool ok = (tgt == fin && carries && tgt > pos) ||
                          (tgt > pos + 1 && (tgt == pt || tgt == pf || tgt < valueEnd));
                if (!ok) break;
                if (ti.Op == LuaOpCode.TestSet && ti.A != reg) break;
                tests.Add(p);
                pos = p - 1;
                continue;
            }
            if (op == LuaOpCode.Jmp || !ExprOnly(pos, reg)) break;
            pos--;
        }
        if (tests.Count == 0) return null;
        tests.Reverse();
        r.Tests = tests;
        r.Start = tests[0];

        // Validate the shape.
        int last = tests[^1];
        if (pf >= 0 && r.Fj < 0 && last + 2 != pf) return null;
        if (last + 2 > valueEnd) return null;
        var starts = new HashSet<int>();
        for (int i = 1; i < tests.Count; i++) starts.Add(tests[i - 1] + 2);
        if (r.HasFinal) starts.Add(last + 2);
        bool seeded = false;
        foreach (int p in tests)
        {
            int tgt = Target(p + 1);
            if (tgt == fin || tgt == pt || tgt == pf) { seeded = true; continue; }
            if (!starts.Contains(tgt) || tgt <= p + 2) return null;
        }
        if (!seeded) return null;
        // No jumps from outside into the interior.
        for (int t = r.Start + 1; t <= fin - 1; t++)
            if (_jumpSources[t] is { } src)
                foreach (int s in src)
                    if (s < r.Start || s >= fin) return null;
        if (r.HasFinal && last + 2 < valueEnd && !ProducesInto(last + 2, valueEnd, reg)) return null;
        // Only the last operand may write the result register when it is a local.
        if (!IsTemp(reg, r.Start))
            for (int pc = r.Start; pc < last; pc++)
                if (!_pseudo[pc] && WritesReg(_code[pc], reg)) return null;
        return r;
    }

    private bool ProducesInto(int from, int to, int reg)
    {
        for (int pc = to - 1; pc >= from; pc--)
        {
            if (_pseudo[pc]) continue;
            var i = _code[pc];
            if (i.Op is LuaOpCode.SetList or LuaOpCode.SetTable && i.A == reg) return BuildsTable(reg, pc); // constructor
            return WritesReg(i, reg) || _regionAt[pc] is { } rr && rr.Reg == reg && rr.Fin == to;
        }
        return false;
    }

    // ------------------------------------------------------------------ blocks

    private List<LuaStmt> Block(int start, int end, Ctx ctx)
    {
        if (++_blockDepth > MaxDepth)
        {
            _blockDepth--;
            _d.Fallbacks++;
            return new List<LuaStmt> { Listing(start, end, "blocks nested too deeply") };
        }
        try { return BlockCore(start, end, ctx); }
        finally { _blockDepth--; }
    }

    private List<LuaStmt> BlockCore(int start, int end, Ctx ctx)
    {
        var saved = _out;
        var savedDecls = _levelDecls;
        var list = new List<LuaStmt>();
        _out = list;
        _levelDecls = new List<(int Local, int Stmt)>();
        try
        {
            FlushAssignments();
            int exit = Resolve(end);
            // 5.1 ends every local's scope before the function's implicit final return.
            int scopeEnd = _v51 && end == _n && _n > 0 && _code[_n - 1].Op == LuaOpCode.Return && _code[_n - 1].B == 1 ? _n - 1 : end;
            int pc = start;
            while (pc < end)
            {
                int before = pc, count = list.Count;
                try
                {
                    pc = Statement(pc, end, exit, ctx);
                    if (pc <= before)
                        throw new DecompileException("no progress");
                    if (NextRealPc(pc) < scopeEnd) CloseScopes(list, pc);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not InsufficientExecutionStackException)
                {
                    _assignBuffer.Clear();
                    if (list.Count > count) list.RemoveRange(count, list.Count - count);
                    _levelDecls.RemoveAll(d => d.Stmt >= count);
                    int resume = ResumePoint(before, end);
                    list.Add(Listing(before, resume, ex is DecompileException ? ex.Message : ex.GetType().Name + ": " + ex.Message));
                    _d.Fallbacks++;
                    Array.Clear(_pending);
                    pc = resume;
                }
            }
            // A local declared by the block's last statement has an empty scope at its end.
            if (end < _n && !_pseudo[end])
                foreach (int li in _locals.StartingAt(end))
                    if (_locals[li].EndPc <= end && Declarable(li, end))
                    {
                        DeclareLocals(end, onlyEmpty: true);
                        break;
                    }
            FlushAssignments();
        }
        finally
        {
            _out = saved;
            _levelDecls = savedDecls;
        }
        return list;
    }

    /// <summary>
    /// Locals declared in this block whose scope ended before the block did were inside a
    /// <c>do ... end</c>: wrap the statements from the first such declaration.
    /// </summary>
    private void CloseScopes(List<LuaStmt> list, int pc)
    {
        int first = int.MaxValue;
        foreach (var (li, stmt) in _levelDecls)
            if (_locals[li].EndPc <= pc && _locals[li].EndPc > _locals[li].StartPc)
                first = Math.Min(first, stmt);
        if (first == int.MaxValue || first >= list.Count) return;
        FlushAssignments();
        var body = list.GetRange(first, list.Count - first);
        list.RemoveRange(first, list.Count - first);
        list.Add(new DoStmt(body));
        _levelDecls.RemoveAll(d => d.Stmt >= first);
    }

    /// <summary>The smallest pc after <paramref name="from"/> that no jump crosses into or out of.</summary>
    private int ResumePoint(int from, int end)
    {
        int q = from + 1;
        for (int iter = 0; iter < 10000; iter++)
        {
            int nq = q;
            for (int s = from; s < nq && s < _n; s++)
            {
                if (_pseudo[s]) continue;
                var op = _code[s].Op;
                if (op is LuaOpCode.Jmp or LuaOpCode.ForPrep or LuaOpCode.ForLoop || (op == LuaOpCode.TForLoop && !_v51))
                {
                    int t = Target(s);
                    if (t > nq && t < end) nq = t;
                }
                if (IsTest(s) && s + 2 > nq) nq = s + 2;
                if (op == LuaOpCode.TForLoop && _v51 && s + 2 > nq) nq = s + 2;
            }
            for (int t = from + 1; t < nq; t++)
                if (_backJumps[t] is { } bj)
                    foreach (int j in bj)
                        if (j >= nq && j < end) nq = j + 1;
            while (nq < end && _pseudo[nq]) nq++;
            if (nq == q) break;
            q = nq;
        }
        return Math.Min(Math.Max(q, from + 1), end);
    }

    private int Statement(int pc, int end, int exit, Ctx ctx)
    {
        DeclareLocals(pc);
        if (_pseudo[pc]) return pc + 1;

        if (pc != ctx.SharedHeader && LastBackJump(pc, end) is var j and >= 0)
            return Loop(pc, j, end, ctx);

        if (_regionAt[pc] is { } region)
        {
            EvalRegion(region);
            return region.Fin;
        }

        var ins = _code[pc];
        switch (ins.Op)
        {
            case LuaOpCode.ForPrep:
                return NumericFor(pc, ctx);
            case LuaOpCode.Jmp:
                if (IsGenericForStart(pc))
                    return GenericFor(pc, ctx);
                return Jump(pc, end, exit, ctx);
            case LuaOpCode.Eq or LuaOpCode.Lt or LuaOpCode.Le or LuaOpCode.Test or LuaOpCode.TestSet:
                if (!IsTest(pc)) throw new DecompileException("test without jump");
                return If(pc, end, exit, ctx);
            case LuaOpCode.Return:
            {
                if (pc == _n - 1 && ins.B == 1) return pc + 1; // implicit final return
                var values = ReadRange(ins.A, ins.B == 0 ? -1 : ins.B - 1, pc);
                Emit(new ReturnStmt(values));
                return pc + 1;
            }
            case LuaOpCode.TailCall:
            {
                var call = BuildCall(pc);
                call.Results = -1;
                Emit(new ReturnStmt(new List<LuaExpr> { call }));
                return pc + 1 < _n && _code[pc + 1].Op == LuaOpCode.Return ? pc + 2 : pc + 1;
            }
            default:
                return Exec(pc);
        }
    }

    private List<LuaStmt> LoopBody(int start, int end, Ctx ctx, int firstVar = 0, int varCount = 0)
    {
        _loopDepth++;
        for (int k = 0; k < varCount; k++) _loopVarRegs.Add(firstVar + k);
        try { return Block(start, end, ctx); }
        finally
        {
            _loopDepth--;
            for (int k = 0; k < varCount; k++) _loopVarRegs.Remove(firstVar + k);
        }
    }

    private int LastBackJump(int h, int end)
    {
        if (_backJumps[h] is not { } list) return -1;
        int best = -1;
        foreach (int j in list)
            if (j >= h && j < end && j > best) best = j;
        return best;
    }

    private int Jump(int pc, int end, int exit, Ctx ctx)
    {
        int t = Target(pc);
        if (t == pc + 1) return pc + 1;
        int rt = Resolve(t);
        if (ctx.LoopExit >= 0 && rt == ctx.LoopExit)
        {
            Emit(new BreakStmt());
            return pc + 1;
        }
        bool last = pc == end - 1 || NextRealPc(pc + 1) >= end;
        if (last && (rt == exit || (ctx.Continue >= 0 && rt == Resolve(ctx.Continue))))
            return pc + 1;
        if (rt == exit && Unreachable(pc + 1, end))
            return end; // the rest of the block is dead code
        throw new DecompileException($"unstructured jump to {t + 1}");
    }

    /// <summary>Skips instructions with no effect on control flow (CLOSE, jumps over nothing but those).</summary>
    private int NextRealPc(int pc)
    {
        while (pc < _n)
        {
            if (_pseudo[pc] || _code[pc].Op == LuaOpCode.Close) { pc++; continue; }
            if (_code[pc].Op == LuaOpCode.Jmp && !IsTestPartner(pc) && Target(pc) > pc && NextRealPc(pc + 1) >= Target(pc))
            {
                pc = Target(pc);
                continue;
            }
            break;
        }
        return pc;
    }

    private bool Unreachable(int from, int to)
    {
        for (int pc = from; pc < to && pc < _n; pc++)
            if (_reachable[pc]) return false;
        return true;
    }

    // ------------------------------------------------------------------ if

    private sealed class Chain
    {
        public List<int> Tests = new();
        public int T;       // fallthrough after the last test (condition true)
        public int F;       // the other exit (raw target)
        public int[] TrueSucc = Array.Empty<int>();
        public int[] FalseSucc = Array.Empty<int>();
    }

    /// <summary>
    /// The longest run of tests starting at <paramref name="p"/>, separated only by
    /// expression code, whose jumps form a single short-circuit condition with two exits.
    /// </summary>
    private Chain FindChain(int p, int end)
    {
        var tests = new List<int> { p };
        int q = p + 2;
        while (q < end)
        {
            int r = q;
            bool found = false;
            while (r < end)
            {
                if (_backJumps[r] is not null) break;
                if (HasDeclarationAt(r)) break;
                if (_regionAt[r] is { } rg) { if (!IsTemp(rg.Reg, rg.Start)) break; r = rg.Fin; continue; }
                if (_pseudo[r]) { r++; continue; }
                if (IsTest(r)) { found = true; break; }
                if (!ExprOnly(r)) break;
                r++;
            }
            if (!found || r >= end) break;
            if (HasForeignJumpInto(q, r, p, r + 2)) break;
            tests.Add(r);
            q = r + 2;
        }
        for (int k = tests.Count; k >= 1; k--)
        {
            var c = TryChain(tests.GetRange(0, k));
            if (c is not null) return c;
        }
        throw new DecompileException("condition has more than two exits");
    }

    private Chain? TryChain(List<int> tests)
    {
        int k = tests.Count;
        int T = tests[^1] + 2;
        var starts = new Dictionary<int, int>();
        for (int i = 1; i < k; i++) starts[tests[i - 1] + 2] = i;
        int F = int.MinValue;
        var ts = new int[k];
        var fs = new int[k];
        for (int i = 0; i < k; i++)
        {
            int tgt = Target(tests[i] + 1);
            int succ;
            if (starts.TryGetValue(tgt, out int node) && node > i) succ = node;
            else if (tgt == T || Resolve(tgt) == Resolve(T)) succ = ExitTrue;
            else if (F == int.MinValue || tgt == F || Resolve(tgt) == Resolve(F))
            {
                if (F == int.MinValue || tgt < F) F = tgt;
                succ = ExitFalse;
            }
            else return null;
            ts[i] = succ;
            fs[i] = i + 1 < k ? i + 1 : ExitTrue;
        }
        for (int i = 0; i + 1 < k; i++)
            if (ts[i] == fs[i]) return null; // a test that skips nothing can't be inside a chain
        if (k > 1 && ReduceCondition(Enumerable.Range(0, k).Select(_ => (LuaExpr)ConstExpr.True).ToList(), ts, fs) is null)
            return null;
        if (F == int.MinValue)
        {
            // Every path reaches T: the condition is constant-true (e.g. `x == "a" or "b"`).
            return new Chain { Tests = tests, T = T, F = -1, TrueSucc = ts, FalseSucc = fs };
        }
        return new Chain { Tests = tests, T = T, F = F, TrueSucc = ts, FalseSucc = fs };
    }

    /// <summary>Evaluates the chain's operands and returns the condition under which control falls through to T.</summary>
    private LuaExpr ChainCondition(Chain c)
    {
        var atoms = new List<LuaExpr>();
        for (int i = 0; i < c.Tests.Count; i++)
        {
            if (i > 0) ExecRange(c.Tests[i - 1] + 2, c.Tests[i]);
            atoms.Add(TestAtom(c.Tests[i]));
        }
        if (c.F < 0 && atoms.Count == 1)
        {
            // Nothing to skip: either `if x then end` (jumps when false) or the start of an
            // `x or <constant>` chain (jumps when true).
            var t = _code[c.Tests[0]];
            bool jumpsWhenTrue = t.Op is LuaOpCode.Test or LuaOpCode.TestSet ? t.C != 0 : t.A != 0;
            return jumpsWhenTrue ? atoms[0] : Negate(atoms[0]);
        }
        return ReduceCondition(atoms, c.TrueSucc, c.FalseSucc) ?? throw new DecompileException("irreducible condition");
    }

    private int If(int p, int end, int exit, Ctx ctx)
    {
        var chain = FindChain(p, end);
        int T = chain.T, F = chain.F, rF = Resolve(F);

        int thenEnd, elseStart = -1, elseEnd = -1, after;
        bool breakForm = false;
        if (F < 0)
        {
            // Every path falls through (constant-true condition): nothing to skip.
            thenEnd = T; after = T;
        }
        else if (F > T && F <= end)
        {
            thenEnd = F;
            after = F;
            int e = F - 1;
            if (e >= T && _code[e].Op == LuaOpCode.Jmp && !_pseudo[e] && !IsTestPartner(e) && !IsGenericForStart(e))
            {
                int E = Target(e);
                if (E == F && (_v51 || _code[e].A == 0))
                {
                    thenEnd = e; elseStart = F; elseEnd = F; after = F; // `else` with nothing in it
                }
                else if (E > F)
                {
                    if (E <= end) { thenEnd = e; elseStart = F; elseEnd = E; after = E; }
                    else if (Resolve(E) == exit) { thenEnd = e; elseStart = F; elseEnd = end; after = end; }
                }
                else if (E < T && Resolve(E) == exit && (ctx.LoopExit < 0 || Resolve(E) != ctx.LoopExit))
                {
                    thenEnd = e; elseStart = F; elseEnd = end; after = end;
                }
            }
        }
        else if (F > end)
        {
            int lastJmp = end - 1;
            while (lastJmp > T && (_pseudo[lastJmp] || _code[lastJmp].Op == LuaOpCode.Close)) lastJmp--;
            if (rF == exit) { thenEnd = end; after = end; }
            else if (lastJmp > T && _code[lastJmp].Op == LuaOpCode.Jmp && !IsTestPartner(lastJmp) &&
                     Resolve(Target(lastJmp)) == rF)
            {
                // `if c then ... end; break`: the false exit was threaded through the break.
                thenEnd = lastJmp; after = lastJmp;
            }
            else if (ctx.LoopExit >= 0 && rF == ctx.LoopExit) { breakForm = true; thenEnd = T; after = T; }
            else throw new DecompileException($"condition exits to {F + 1}, outside its block");
        }
        else if (F == T)
        {
            thenEnd = T; after = T;
        }
        else
        {
            if (ctx.Continue >= 0 && rF == Resolve(ctx.Continue) && exit == rF) { thenEnd = end; after = end; }
            else if (ctx.LoopExit >= 0 && rF == ctx.LoopExit) { breakForm = true; thenEnd = T; after = T; }
            else throw new DecompileException($"condition jumps back to {F + 1}");
        }

        var cond = ChainCondition(chain);
        if (breakForm)
        {
            Emit(new IfStmt(Negate(cond), new List<LuaStmt> { new BreakStmt() }, null));
            return after;
        }
        var thenBlock = Block(T, thenEnd, ctx);
        var elseBlock = elseStart >= 0 ? Block(elseStart, elseEnd, ctx) : null;
        if (thenBlock.Count == 0 && elseBlock is { Count: > 0 } && elseBlock is not [IfStmt])
            Emit(new IfStmt(Negate(cond), elseBlock, null));
        else
            Emit(new IfStmt(cond, thenBlock, elseBlock));
        return after;
    }

    // ------------------------------------------------------------------ loops

    private int Loop(int h, int j, int end, Ctx ctx)
    {
        int loopExit = Resolve(j + 1);
        if (IsTestPartner(j))
            return Repeat(h, j, loopExit);

        // while <cond> do: expression code then a condition whose false exit leaves the loop.
        int p0 = h;
        bool clean = true;
        while (p0 < j)
        {
            if (p0 > h && (HasDeclarationAt(p0) || _jumpSources[p0] is not null)) { clean = false; break; }
            if (_regionAt[p0] is { } rg) { if (!IsTemp(rg.Reg, rg.Start)) { clean = false; break; } p0 = rg.Fin; continue; }
            if (_pseudo[p0]) { p0++; continue; }
            if (IsTest(p0) || !ExprOnly(p0)) break;
            p0++;
        }
        if (clean && p0 < j && IsTest(p0))
        {
            Chain? chain = null;
            try { chain = FindChain(p0, j); }
            catch (DecompileException) { }
            if (chain is not null && chain.F > chain.T && Resolve(chain.F) == loopExit && chain.T <= j)
            {
                ExecRange(h, p0);
                var cond = ChainCondition(chain);
                var body = LoopBody(chain.T, j, new Ctx(h, loopExit, -1));
                Emit(new WhileStmt(cond, body));
                return j + 1;
            }
        }
        var inner = LoopBody(h, j, new Ctx(h, loopExit, h));
        Emit(new WhileStmt(ConstExpr.True, inner));
        return j + 1;
    }

    private int Repeat(int h, int j, int loopExit)
    {
        int last = j - 1;
        var candidates = new List<int> { last };
        int pos = last - 1;
        while (pos > h)
        {
            if (_pseudo[pos]) { pos--; continue; }
            if (_code[pos].Op == LuaOpCode.Jmp && IsTestPartner(pos))
            {
                int tgt = Target(pos);
                if (tgt == h || (tgt > pos + 1 && tgt <= j + 1) || Resolve(tgt) == loopExit)
                {
                    candidates.Add(pos - 1);
                    pos -= 2;
                    continue;
                }
                break;
            }
            if (_code[pos].Op == LuaOpCode.Jmp || !ExprOnly(pos) || HasDeclarationAt(pos + 1)) break;
            pos--;
        }
        candidates.Reverse();
        for (int s = 0; s < candidates.Count; s++)
        {
            var tests = candidates.GetRange(s, candidates.Count - s);
            var chain = TryChain(tests);
            if (chain is null || chain.F != h) continue;
            if (HasForeignJumpInto(tests[0] + 1, j, tests[0], j + 1)) continue;
            var body = LoopBody(h, tests[0], new Ctx(-1, loopExit, -1));
            var cond = ChainCondition(chain);
            Emit(new RepeatStmt(body, cond));
            return j + 1;
        }
        throw new DecompileException("unrecognised repeat-until condition");
    }

    private int NumericFor(int p, Ctx ctx)
    {
        var ins = _code[p];
        int q = Target(p);
        if (q <= p || q >= _n || _code[q].Op != LuaOpCode.ForLoop || Target(q) != p + 1)
            throw new DecompileException("malformed numeric for");
        int a = ins.A;
        var start = Read(a, p);
        var limit = Read(a + 1, p);
        var step = Read(a + 2, p);
        int li = LoopVariable(a + 3, p + 1);
        string name = li >= 0 ? _locals[li].Name : "i";
        if (li >= 0) _suppressed.Add(li);
        var body = LoopBody(p + 1, q, new Ctx(q, Resolve(q + 1), -1), a + 3, 1);
        bool unitStep = step is ConstExpr { Value.Kind: LuaConstantKind.Number, Value.Number: 1 };
        Emit(new NumericForStmt(name, start, limit, unitStep ? null : step, body));
        return q + 1;
    }

    /// <summary>The loop variable in <paramref name="reg"/> whose scope starts at the body (possibly empty).</summary>
    private int LoopVariable(int reg, int bodyStart)
    {
        foreach (int li in _locals.StartingAt(bodyStart))
            if (_locals.RegisterOf(li) == reg && !_locals[li].Name.StartsWith('(')) return li;
        return _locals.IndexAt(reg, bodyStart);
    }

    private bool IsGenericForStart(int p)
    {
        if (_code[p].Op != LuaOpCode.Jmp || IsTestPartner(p)) return false;
        int q = Target(p);
        if (q <= p || q + 1 >= _n) return false;
        if (_v51)
            return _code[q].Op == LuaOpCode.TForLoop && _code[q + 1].Op == LuaOpCode.Jmp && Target(q + 1) == p + 1;
        return _code[q].Op == LuaOpCode.TForCall && _code[q + 1].Op == LuaOpCode.TForLoop && Target(q + 1) == p + 1;
    }

    private int GenericFor(int p, Ctx ctx)
    {
        int q = Target(p);
        var call = _code[q];
        int a = call.A, nvars = Math.Max(call.C, 1);
        var values = new List<LuaExpr?> { Read(a, p), Read(a + 1, p), Read(a + 2, p) };
        var list = CollapseMulti(values);
        while (list.Count > 1 && list[^1] is ConstExpr { IsNil: true }) list.RemoveAt(list.Count - 1);
        var names = new List<string>();
        for (int k = 0; k < nvars; k++)
        {
            int li = LoopVariable(a + 3 + k, p + 1);
            names.Add(li >= 0 ? _locals[li].Name : "v" + (k + 1));
            if (li >= 0) _suppressed.Add(li);
        }
        var body = LoopBody(p + 1, q, new Ctx(q, Resolve(q + 2), -1), a + 3, nvars);
        Emit(new GenericForStmt(names, list, body));
        return q + 2;
    }

    // ------------------------------------------------------------------ conditions

    /// <summary>
    /// Reduces a short-circuit graph (node i: jump to <paramref name="t"/>[i] when
    /// atom i holds, else to <paramref name="f"/>[i]; successors are node indices or
    /// ExitTrue/ExitFalse) to the condition under which control reaches ExitTrue.
    /// </summary>
    private LuaExpr? ReduceCondition(List<LuaExpr> atoms, int[] t, int[] f)
    {
        int k = atoms.Count;
        var cond = atoms.ToArray();
        var ts = (int[])t.Clone();
        var fs = (int[])f.Clone();
        var alive = Enumerable.Repeat(true, k).ToArray();

        int Preds(int y)
        {
            int c = 0;
            for (int i = 0; i < k; i++)
                if (alive[i]) { if (ts[i] == y) c++; if (fs[i] == y) c++; }
            return c;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int x = k - 1; x >= 0 && !changed; x--)
            {
                if (!alive[x]) continue;
                foreach (int y in new[] { ts[x], fs[x] })
                {
                    if (y < 0 || y == x || !alive[y] || Preds(y) != 1) continue;
                    LuaExpr? merged = null;
                    if (ts[x] == y && fs[x] == fs[y]) merged = And(cond[x], cond[y]);
                    else if (ts[x] == y && fs[x] == ts[y]) merged = Or(Negate(cond[x]), cond[y]);
                    else if (fs[x] == y && ts[x] == ts[y]) merged = Or(cond[x], cond[y]);
                    else if (fs[x] == y && ts[x] == fs[y]) merged = And(Negate(cond[x]), cond[y]);
                    if (merged is null) continue;
                    cond[x] = merged;
                    ts[x] = ts[y];
                    fs[x] = fs[y];
                    alive[y] = false;
                    changed = true;
                    break;
                }
            }
        }
        for (int i = 1; i < k; i++)
            if (alive[i]) return null;
        if (ts[0] == ExitTrue && fs[0] == ExitFalse) return cond[0];
        if (ts[0] == ExitTrue && fs[0] == ExitTrue) return cond[0]; // constant-true condition
        if (ts[0] == ExitFalse && fs[0] == ExitTrue) return Negate(cond[0]);
        return null;
    }

    private static LuaExpr And(LuaExpr a, LuaExpr b) => new BinaryExpr("and", a, b);
    private static LuaExpr Or(LuaExpr a, LuaExpr b) => new BinaryExpr("or", a, b);

    private static int NotCount(LuaExpr e) => e switch
    {
        UnaryExpr { Op: "not" } u => 1 + NotCount(u.Operand),
        BinaryExpr { Op: "and" or "or" } b => NotCount(b.Left) + NotCount(b.Right),
        _ => 0,
    };

    private static bool RightmostIsComparison(LuaExpr e) => e switch
    {
        BinaryExpr { Op: "and" or "or" } b => RightmostIsComparison(b.Right),
        UnaryExpr { Op: "not" } u => RightmostIsComparison(u.Operand),
        BinaryExpr { Op: "==" or "~=" or "<" or "<=" or ">" or ">=" } => true,
        _ => false,
    };

    internal static LuaExpr Negate(LuaExpr e)
    {
        switch (e)
        {
            case UnaryExpr { Op: "not" } u:
                return u.Operand;
            case ConstExpr { Value.Kind: LuaConstantKind.Boolean } c:
                return c.Value.Boolean ? ConstExpr.False : ConstExpr.True;
            case BinaryExpr { Op: "==" } b:
                return new BinaryExpr("~=", b.Left, b.Right);
            case BinaryExpr { Op: "~=" } b:
                return new BinaryExpr("==", b.Left, b.Right);
            case BinaryExpr { Op: "and" or "or" } b:
            {
                // Lua only compiles `not` into pure jumps when it applies to comparisons, so a
                // condition ending in a plain value test was written with the negation pushed in.
                var dm = new BinaryExpr(b.Op == "and" ? "or" : "and", Negate(b.Left), Negate(b.Right));
                if (!RightmostIsComparison(b)) return dm;
                return NotCount(dm) < NotCount(b) + 1 ? dm : new UnaryExpr("not", e);
            }
        }
        return new UnaryExpr("not", e);
    }

    /// <summary>The condition under which the test at <paramref name="pc"/> takes its jump.</summary>
    private LuaExpr TestAtom(int pc)
    {
        var i = _code[pc];
        switch (i.Op)
        {
            case LuaOpCode.Eq:
            case LuaOpCode.Lt:
            case LuaOpCode.Le:
            {
                var right = RK(i.C, pc);
                var left = RK(i.B, pc);
                string op = i.Op == LuaOpCode.Eq ? "==" : i.Op == LuaOpCode.Lt ? "<" : "<=";
                LuaExpr cmp;
                // `a > b` compiles to `b < a` with b evaluated second (higher register):
                // print it the way round that reproduces the evaluation order.
                // (Equality is never swapped by the compiler, so `nil == x` is kept as written.)
                bool swapped = i.Op != LuaOpCode.Eq &&
                               ((LuaInstruction.IsConstant(i.B) && !LuaInstruction.IsConstant(i.C)) ||
                                (!LuaInstruction.IsConstant(i.B) && !LuaInstruction.IsConstant(i.C) && i.B > i.C));
                if (swapped)
                    cmp = new BinaryExpr(op switch { "<" => ">", "<=" => ">=", _ => op }, right, left);
                else
                    cmp = new BinaryExpr(op, left, right);
                return i.A != 0 ? cmp : Negate(cmp);
            }
            case LuaOpCode.Test:
            {
                var v = Read(i.A, pc);
                return i.C != 0 ? v : Negate(v);
            }
            case LuaOpCode.TestSet:
            {
                // Outside a recognised expression the conditional copy would be lost.
                if (_captureReg < 0 && !_inRegion)
                    throw new DecompileException("conditional assignment outside an expression");
                var v = Read(i.B, pc);
                return i.C != 0 ? v : Negate(v);
            }
        }
        throw new DecompileException("not a test");
    }

    private bool _inRegion;

    private void EvalRegion(Region r)
    {
        bool saved = _inRegion;
        _inRegion = true;
        try { EvalRegionCore(r); }
        finally { _inRegion = saved; }
    }

    private void EvalRegionCore(Region r)
    {
        int k = r.Tests.Count;
        bool final = r.HasFinal;
        int nodes = k + (final ? 1 : 0);
        var atoms = new List<LuaExpr>();
        var ts = new int[nodes];
        var fs = new int[nodes];
        var starts = new Dictionary<int, int>();
        for (int i = 1; i < k; i++) starts[r.Tests[i - 1] + 2] = i;
        if (final) starts[r.Tests[^1] + 2] = k;

        for (int i = 0; i < k; i++)
        {
            int p = r.Tests[i];
            if (i > 0) ExecRange(r.Tests[i - 1] + 2, p);
            atoms.Add(TestAtom(p));
            int tgt = Target(p + 1);
            if (tgt == r.Fin) ts[i] = _code[p].C != 0 ? ExitTrue : ExitFalse;
            else if (tgt == r.Pt) ts[i] = ExitTrue;
            else if (tgt == r.Pf) ts[i] = ExitFalse;
            else ts[i] = starts.TryGetValue(tgt, out int node) ? node : throw new DecompileException("bad expression jump");
            fs[i] = i + 1 < nodes ? i + 1 : ExitFalse; // last comparison falls into LOADBOOL false
        }
        if (final)
        {
            int saved = _captureReg;
            _captureReg = r.Reg;
            try { ExecRange(r.Tests[^1] + 2, r.ValueEnd); }
            finally { _captureReg = saved; }
            // An empty final segment means the value was already in the register.
            var value = TakePending(r.Reg) ?? Read(r.Reg, r.ValueEnd);
            atoms.Add(value);
            ts[k] = ExitTrue;
            fs[k] = ExitFalse;
        }
        var expr = ReduceCondition(atoms, ts, fs) ?? throw new DecompileException("irreducible expression");
        Write(r.Reg, expr, r.Start);
    }

    /// <summary>Executes expression-only instructions in <c>[from, to)</c>.</summary>
    private void ExecRange(int from, int to)
    {
        int pc = from;
        while (pc < to)
        {
            if (_regionAt[pc] is { } rg)
            {
                if (rg.Reg != _captureReg && !IsTemp(rg.Reg, rg.Start))
                    throw new DecompileException("assignment inside an expression");
                EvalRegion(rg);
                pc = rg.Fin;
                continue;
            }
            if (_pseudo[pc]) { pc++; continue; }
            if (!ExprOnly(pc, _captureReg))
                throw new DecompileException("statement inside an expression");
            pc = Exec(pc);
        }
    }
}
