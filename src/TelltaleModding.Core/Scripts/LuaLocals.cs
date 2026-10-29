namespace TelltaleModding.Core.Scripts;

/// <summary>
/// Maps registers to local variables using the debug records: at any pc the active locals
/// (<c>StartPc &lt;= pc &lt; EndPc</c>) occupy registers 0, 1, 2, ... in declaration order.
/// </summary>
internal sealed class LuaLocals
{
    private readonly LuaLocalVariable[] _locals;
    private readonly int[] _register;
    private readonly List<int>[] _byRegister;
    private readonly List<int>[] _startingAt;
    private readonly int[] _active;

    public LuaLocals(LuaFunction f) : this(f, f.Locals) { }

    /// <summary>Uses <paramref name="locals"/> in place of the function's own debug records.</summary>
    public LuaLocals(LuaFunction f, LuaLocalVariable[] locals)
    {
        _locals = locals;
        int n = _locals.Length;
        _register = new int[n];
        int maxReg = Math.Max(f.MaxStackSize, 1);
        for (int i = 0; i < n; i++)
        {
            int start = _locals[i].StartPc, reg = 0;
            bool empty = _locals[i].EndPc <= start; // declared by a block's last statement
            for (int j = 0; j < i; j++)
                if (_locals[j].StartPc <= start && (start < _locals[j].EndPc || (empty && start == _locals[j].EndPc)))
                    reg++;
            _register[i] = reg;
            maxReg = Math.Max(maxReg, reg + 1);
        }
        _byRegister = new List<int>[maxReg];
        for (int r = 0; r < maxReg; r++) _byRegister[r] = new List<int>();
        for (int i = 0; i < n; i++) _byRegister[_register[i]].Add(i);

        int codeLen = f.Code.Length;
        _startingAt = new List<int>[codeLen + 1];
        for (int i = 0; i < n; i++)
        {
            int s = Math.Clamp(_locals[i].StartPc, 0, codeLen);
            (_startingAt[s] ??= new List<int>()).Add(i);
        }

        _active = new int[codeLen + 2];
        foreach (var l in _locals)
        {
            int s = Math.Clamp(l.StartPc, 0, codeLen + 1), e = Math.Clamp(l.EndPc, 0, codeLen + 1);
            if (e > s) { _active[s]++; _active[e]--; }
        }
        for (int i = 1; i < _active.Length; i++) _active[i] += _active[i - 1];
    }

    public int Count => _locals.Length;
    /// <summary>Highest register any local occupies (-1 when there are none).</summary>
    public int MaxRegister => _register.Length == 0 ? -1 : _register.Max();
    public LuaLocalVariable this[int index] => _locals[index];
    public int RegisterOf(int index) => _register[index];

    /// <summary>Index of the local held in <paramref name="reg"/> at <paramref name="pc"/>, or -1.</summary>
    public int IndexAt(int reg, int pc)
    {
        if (reg < 0 || reg >= _byRegister.Length) return -1;
        var list = _byRegister[reg];
        for (int k = list.Count - 1; k >= 0; k--)
        {
            var l = _locals[list[k]];
            if (l.StartPc <= pc && pc < l.EndPc)
                return list[k];
        }
        return -1;
    }

    public LuaLocalVariable? At(int reg, int pc)
    {
        int i = IndexAt(reg, pc);
        return i < 0 ? null : _locals[i];
    }

    /// <summary>Number of locals active at <paramref name="pc"/>.</summary>
    public int ActiveCount(int pc) => pc >= 0 && pc < _active.Length ? _active[pc] : 0;

    /// <summary>Indices of locals whose scope begins at <paramref name="pc"/>.</summary>
    public IReadOnlyList<int> StartingAt(int pc) =>
        pc >= 0 && pc < _startingAt.Length && _startingAt[pc] is { } l ? l : Array.Empty<int>();
}
