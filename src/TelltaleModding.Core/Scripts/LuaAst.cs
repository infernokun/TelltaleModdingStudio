using System.Text;

namespace TelltaleModding.Core.Scripts;

// Syntax tree produced by the decompiler and turned back into source by LuaWriter.

internal abstract class LuaExpr
{
    /// <summary>Binding strength: 1 or, 2 and, 3 comparison, 4 .., 5 + -, 6 * / %, 7 unary, 8 ^, 10 primary.</summary>
    public virtual int Precedence => 10;
    /// <summary>True for expressions that can be called or indexed without parentheses.</summary>
    public virtual bool IsPrefix => false;
    /// <summary>True for calls and varargs that may produce several values.</summary>
    public virtual bool CanBeMulti => false;
    /// <summary>Nesting depth, used to refuse absurdly deep trees from malformed code.</summary>
    public int Depth { get; protected set; } = 1;

    protected static int DepthOf(params LuaExpr?[] children)
    {
        int d = 0;
        foreach (var c in children) if (c is not null && c.Depth > d) d = c.Depth;
        return d + 1;
    }

    protected static int DepthOf(IEnumerable<LuaExpr> children) => DepthOf(children.ToArray());
}

internal sealed class ConstExpr : LuaExpr
{
    public ConstExpr(LuaConstant value) { Value = value; }
    public LuaConstant Value { get; }
    public override int Precedence => Value.Kind == LuaConstantKind.Number && (Value.Number < 0 || double.IsNegative(Value.Number)) ? 7 : 10;
    public static readonly ConstExpr Nil = new(LuaConstant.Nil);
    /// <summary>A nil put in a register by LOADNIL (as opposed to a nil constant operand).</summary>
    public static readonly ConstExpr LoadedNil = new(LuaConstant.Nil);
    public static readonly ConstExpr True = new(LuaConstant.True);
    public static readonly ConstExpr False = new(LuaConstant.False);
    public bool IsNil => Value.Kind == LuaConstantKind.Nil;
}

internal sealed class VarArgExpr : LuaExpr
{
    public VarArgExpr(bool multi) { Multi = multi; }
    public bool Multi { get; }
    public override bool CanBeMulti => true;
}

internal sealed class GlobalExpr : LuaExpr
{
    public GlobalExpr(string name, bool env52) { Name = name; Env52 = env52; }
    public string Name { get; }
    public bool Env52 { get; }
    public override bool IsPrefix => true;
}

internal sealed class LocalExpr : LuaExpr
{
    public LocalExpr(string name) { Name = name; }
    public string Name { get; }
    public override bool IsPrefix => true;
}

internal sealed class UpvalueExpr : LuaExpr
{
    public UpvalueExpr(string name) { Name = name; }
    public string Name { get; }
    public override bool IsPrefix => true;
}

internal sealed class IndexExpr : LuaExpr
{
    public IndexExpr(LuaExpr obj, LuaExpr key) { Object = obj; Key = key; Depth = DepthOf(obj, key); }
    public LuaExpr Object { get; }
    public LuaExpr Key { get; }
    public override bool IsPrefix => true;
}

/// <summary>The <c>obj:name</c> half of a SELF instruction.</summary>
internal sealed class MethodRefExpr : LuaExpr
{
    public MethodRefExpr(LuaExpr obj, LuaExpr key) { Object = obj; Key = key; Depth = DepthOf(obj, key); }
    public LuaExpr Object { get; }
    public LuaExpr Key { get; }
    public override bool IsPrefix => true;
}

/// <summary>The implicit <c>self</c> argument a SELF instruction leaves in the next register.</summary>
internal sealed class SelfArgExpr : LuaExpr
{
    public SelfArgExpr(LuaExpr obj) { Object = obj; Depth = obj.Depth; }
    public LuaExpr Object { get; }
}

internal sealed class CallExpr : LuaExpr
{
    public CallExpr(LuaExpr function, List<LuaExpr> args) { Function = function; Args = args; Depth = DepthOf(args.Append(function)); }
    public LuaExpr Function { get; }
    public List<LuaExpr> Args { get; }
    /// <summary>Results requested: -1 = all, 0 = statement, n = exactly n.</summary>
    public int Results { get; set; } = 1;
    public override bool IsPrefix => true;
    public override bool CanBeMulti => true;
}

/// <summary>Result <c>Index</c> of a call or vararg that returns several values into consecutive registers.</summary>
internal sealed class MultiPartExpr : LuaExpr
{
    public MultiPartExpr(LuaExpr source, int index, int count) { Source = source; Index = index; Count = count; Depth = source.Depth; }
    public LuaExpr Source { get; }
    public int Index { get; }
    public int Count { get; }
    public override int Precedence => Source.Precedence;
    public override bool IsPrefix => Source.IsPrefix;
}

internal sealed class FunctionExpr : LuaExpr
{
    public FunctionExpr(LuaFunction proto, List<string> parameters, bool vararg, List<LuaStmt> body)
    {
        Proto = proto; Parameters = parameters; IsVararg = vararg; Body = body;
    }
    public LuaFunction Proto { get; }
    public List<string> Parameters { get; }
    public bool IsVararg { get; }
    public List<LuaStmt> Body { get; }
}

internal sealed class TableExpr : LuaExpr
{
    public List<TableItem> Items { get; } = new();

    public void Add(TableItem item)
    {
        Items.Add(item);
        Depth = Math.Max(Depth, DepthOf(item.Key, item.Value));
    }
}

internal sealed record TableItem(LuaExpr? Key, LuaExpr Value, int Order);

internal sealed class BinaryExpr : LuaExpr
{
    public BinaryExpr(string op, LuaExpr left, LuaExpr right) { Op = op; Left = left; Right = right; Depth = DepthOf(left, right); }
    public string Op { get; }
    public LuaExpr Left { get; }
    public LuaExpr Right { get; }
    public override int Precedence => PrecedenceOf(Op);
    public bool RightAssociative => Op is ".." or "^";

    public static int PrecedenceOf(string op) => op switch
    {
        "or" => 1,
        "and" => 2,
        "<" or ">" or "<=" or ">=" or "~=" or "==" => 3,
        ".." => 4,
        "+" or "-" => 5,
        "*" or "/" or "%" => 6,
        "^" => 8,
        _ => 0,
    };
}

internal sealed class UnaryExpr : LuaExpr
{
    public UnaryExpr(string op, LuaExpr operand) { Op = op; Operand = operand; Depth = operand.Depth + 1; }
    public string Op { get; }
    public LuaExpr Operand { get; }
    public override int Precedence => 7;
}

internal abstract class LuaStmt { }

internal sealed class LocalStmt : LuaStmt
{
    public LocalStmt(List<string> names, List<LuaExpr> values) { Names = names; Values = values; }
    public List<string> Names { get; }
    public List<LuaExpr> Values { get; }
    /// <summary>Declared with <c>local function</c> (the name is visible inside the body).</summary>
    public bool IsLocalFunction { get; init; }
}

internal sealed class AssignStmt : LuaStmt
{
    public AssignStmt(List<LuaExpr> targets, List<LuaExpr> values) { Targets = targets; Values = values; }
    public List<LuaExpr> Targets { get; }
    public List<LuaExpr> Values { get; }
}

internal sealed class CallStmt : LuaStmt
{
    public CallStmt(CallExpr call) { Call = call; }
    public CallExpr Call { get; }
}

internal sealed class ReturnStmt : LuaStmt
{
    public ReturnStmt(List<LuaExpr> values) { Values = values; }
    public List<LuaExpr> Values { get; }
}

internal sealed class IfStmt : LuaStmt
{
    public IfStmt(LuaExpr condition, List<LuaStmt> then, List<LuaStmt>? @else)
    {
        Condition = condition; Then = then; Else = @else;
    }
    public LuaExpr Condition { get; }
    public List<LuaStmt> Then { get; }
    public List<LuaStmt>? Else { get; }
}

internal sealed class WhileStmt : LuaStmt
{
    public WhileStmt(LuaExpr condition, List<LuaStmt> body) { Condition = condition; Body = body; }
    public LuaExpr Condition { get; }
    public List<LuaStmt> Body { get; }
}

internal sealed class RepeatStmt : LuaStmt
{
    public RepeatStmt(List<LuaStmt> body, LuaExpr condition) { Body = body; Condition = condition; }
    public List<LuaStmt> Body { get; }
    public LuaExpr Condition { get; }
}

internal sealed class NumericForStmt : LuaStmt
{
    public NumericForStmt(string variable, LuaExpr start, LuaExpr limit, LuaExpr? step, List<LuaStmt> body)
    {
        Variable = variable; Start = start; Limit = limit; Step = step; Body = body;
    }
    public string Variable { get; }
    public LuaExpr Start { get; }
    public LuaExpr Limit { get; }
    public LuaExpr? Step { get; }
    public List<LuaStmt> Body { get; }
}

internal sealed class GenericForStmt : LuaStmt
{
    public GenericForStmt(List<string> variables, List<LuaExpr> values, List<LuaStmt> body)
    {
        Variables = variables; Values = values; Body = body;
    }
    public List<string> Variables { get; }
    public List<LuaExpr> Values { get; }
    public List<LuaStmt> Body { get; }
}

internal sealed class BreakStmt : LuaStmt { }

internal sealed class DoStmt : LuaStmt
{
    public DoStmt(List<LuaStmt> body) { Body = body; }
    public List<LuaStmt> Body { get; }
}

internal sealed class GotoStmt : LuaStmt
{
    public GotoStmt(string label) { Label = label; }
    public string Label { get; }
}

internal sealed class LabelStmt : LuaStmt
{
    public LabelStmt(string label) { Label = label; }
    public string Label { get; }
}

/// <summary>Comment lines (used for fallback instruction listings).</summary>
internal sealed class CommentStmt : LuaStmt
{
    public CommentStmt(IReadOnlyList<string> lines) { Lines = lines; }
    public IReadOnlyList<string> Lines { get; }
}

/// <summary>Turns the syntax tree into indented Lua source.</summary>
internal sealed class LuaWriter
{
    private const string Indent = "  ";
    private readonly StringBuilder _sb = new();
    private readonly bool _single;
    private int _depth;

    public LuaWriter(bool singlePrecision) { _single = singlePrecision; }

    public override string ToString() => _sb.ToString();

    public void WriteLine(string text)
    {
        for (int i = 0; i < _depth; i++) _sb.Append(Indent);
        _sb.Append(text).Append('\n');
    }

    public void WriteBlankLine() => _sb.Append('\n');

    public void WriteBlock(List<LuaStmt> stmts, bool topLevel = false)
    {
        for (int i = 0; i < stmts.Count; i++)
        {
            var s = stmts[i];
            if (topLevel && i > 0 && (IsFunctionDecl(s) || IsFunctionDecl(stmts[i - 1])))
                WriteBlankLine();
            if (i < stmts.Count - 1 && s is ReturnStmt or BreakStmt && stmts[i + 1] is not CommentStmt)
            {
                // `return`/`break` must end a block; code after it is unreachable.
                WriteLine("do");
                _depth++;
                WriteStmt(s);
                _depth--;
                WriteLine("end");
                continue;
            }
            WriteStmt(s);
        }
    }

    private static bool IsFunctionDecl(LuaStmt s) =>
        s is AssignStmt { Values: [FunctionExpr] } or LocalStmt { Values: [FunctionExpr] };

    private void Nested(List<LuaStmt> body)
    {
        _depth++;
        WriteBlock(body);
        _depth--;
    }

    public void WriteStmt(LuaStmt s)
    {
        switch (s)
        {
            case LocalStmt l:
                if (l.Names.Count == 1 && l.Values.Count == 1 && l.Values[0] is FunctionExpr lf && l.IsLocalFunction)
                {
                    WriteFunction("local function " + l.Names[0], lf, false);
                    break;
                }
                WriteLine("local " + string.Join(", ", l.Names) +
                          (l.Values.Count > 0 ? " = " + ExprList(l.Values, false) : ""));
                break;
            case AssignStmt a:
                if (a.Targets.Count == 1 && a.Values.Count == 1 && a.Values[0] is FunctionExpr fn &&
                    FunctionName(a.Targets[0], fn) is { } named)
                {
                    WriteFunction("function " + named.Name, fn, named.Method);
                    break;
                }
                WriteLine(string.Join(", ", a.Targets.Select(t => Expr(t))) + " = " + ExprList(a.Values, false));
                break;
            case CallStmt c:
                WriteLine(Expr(c.Call));
                break;
            case ReturnStmt r:
                WriteLine(r.Values.Count == 0 ? "return" : "return " + ExprList(r.Values, true));
                break;
            case IfStmt i:
                WriteIf(i, "if ");
                WriteLine("end");
                break;
            case WhileStmt w:
                WriteLine("while " + Expr(w.Condition) + " do");
                Nested(w.Body);
                WriteLine("end");
                break;
            case RepeatStmt rp:
                WriteLine("repeat");
                Nested(rp.Body);
                WriteLine("until " + Expr(rp.Condition));
                break;
            case NumericForStmt nf:
                WriteLine($"for {nf.Variable} = {Expr(nf.Start)}, {Expr(nf.Limit)}" +
                          (nf.Step is null ? "" : ", " + Expr(nf.Step)) + " do");
                Nested(nf.Body);
                WriteLine("end");
                break;
            case GenericForStmt gf:
                WriteLine($"for {string.Join(", ", gf.Variables)} in {ExprList(gf.Values, false)} do");
                Nested(gf.Body);
                WriteLine("end");
                break;
            case BreakStmt:
                WriteLine("break");
                break;
            case DoStmt d:
                WriteLine("do");
                Nested(d.Body);
                WriteLine("end");
                break;
            case GotoStmt g:
                WriteLine("goto " + g.Label);
                break;
            case LabelStmt lb:
                WriteLine("::" + lb.Label + "::");
                break;
            case CommentStmt cm:
                foreach (var line in cm.Lines)
                    WriteLine("-- " + line);
                break;
        }
    }

    private void WriteIf(IfStmt i, string keyword)
    {
        WriteLine(keyword + Expr(i.Condition) + " then");
        Nested(i.Then);
        if (i.Else is null)
            return;
        if (i.Else is [IfStmt nested])
        {
            WriteIf(nested, "elseif ");
            return;
        }
        WriteLine("else");
        Nested(i.Else);
    }

    private void WriteFunction(string header, FunctionExpr f, bool method)
    {
        var ps = method && f.Parameters.Count > 0 ? f.Parameters.Skip(1) : f.Parameters;
        var plist = ps.ToList();
        if (f.IsVararg) plist.Add("...");
        WriteLine(header + "(" + string.Join(", ", plist) + ")");
        Nested(f.Body);
        WriteLine("end");
    }

    private (string Name, bool Method)? FunctionName(LuaExpr target, FunctionExpr f)
    {
        switch (target)
        {
            case GlobalExpr g when LuaText.IsIdentifier(g.Name):
                return (g.Name, false);
            case LocalExpr l:
                return (l.Name, false);
            case UpvalueExpr u when LuaText.IsIdentifier(u.Name):
                return (u.Name, false);
            case IndexExpr ix when ix.Key is ConstExpr { Value.IsString: true } k && LuaText.IsIdentifier(k.Value.Text) &&
                                   DottedName(ix.Object) is { } baseName:
                bool method = f.Parameters.Count > 0 && f.Parameters[0] == "self";
                return (baseName + (method ? ":" : ".") + k.Value.Text, method);
        }
        return null;
    }

    private static string? DottedName(LuaExpr e) => e switch
    {
        GlobalExpr g when LuaText.IsIdentifier(g.Name) => g.Name,
        LocalExpr l => l.Name,
        UpvalueExpr u when LuaText.IsIdentifier(u.Name) => u.Name,
        IndexExpr { Key: ConstExpr { Value.IsString: true } k } ix when LuaText.IsIdentifier(k.Value.Text) &&
                                                                       DottedName(ix.Object) is { } b => b + "." + k.Value.Text,
        _ => null,
    };

    // ---- expressions ----

    /// <summary>
    /// A comma-separated list. A trailing call or vararg limited to one value is wrapped in
    /// parentheses when <paramref name="openEnded"/> (it would otherwise expand).
    /// </summary>
    public string ExprList(List<LuaExpr> items, bool openEnded)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var e = items[i];
            bool last = i == items.Count - 1;
            if (last && openEnded && IsSingleValueMulti(e))
                sb.Append('(').Append(Expr(e)).Append(')');
            else
                sb.Append(Expr(e));
        }
        return sb.ToString();
    }

    private static bool IsSingleValueMulti(LuaExpr e) =>
        e is CallExpr { Results: 1 } || e is VarArgExpr { Multi: false };

    public string Expr(LuaExpr e, int minPrecedence = 0)
    {
        string s = ExprCore(e);
        return e.Precedence < minPrecedence ? "(" + s + ")" : s;
    }

    private string Prefix(LuaExpr e) => e.IsPrefix && e is not MultiPartExpr ? ExprCore(e) : "(" + ExprCore(e) + ")";

    private string ExprCore(LuaExpr e)
    {
        switch (e)
        {
            case ConstExpr c:
                return c.Value.ToLiteral(_single);
            case VarArgExpr:
                return "...";
            case GlobalExpr g:
                return LuaText.IsIdentifier(g.Name) ? g.Name : (g.Env52 ? "_ENV" : "_G") + "[" + LuaText.QuoteString(System.Text.Encoding.UTF8.GetBytes(g.Name)) + "]";
            case LocalExpr l:
                return l.Name;
            case UpvalueExpr u:
                return u.Name;
            case IndexExpr ix:
                return Prefix(ix.Object) + Key(ix.Key);
            case MethodRefExpr m:
                return Prefix(m.Object) + Key(m.Key);
            case SelfArgExpr sa:
                return Expr(sa.Object);
            case CallExpr call:
                return Call(call);
            case MultiPartExpr mp:
                return Expr(mp.Source);
            case FunctionExpr f:
                return InlineFunction(f);
            case TableExpr t:
                return Table(t);
            case BinaryExpr b:
            {
                int p = b.Precedence;
                string l = Expr(b.Left, b.RightAssociative ? p + 1 : p);
                // and/or are associative: `a or (b or c)` reads (and compiles) as `a or b or c`.
                bool assoc = b.Op is "and" or "or" && b.Right is BinaryExpr rb && rb.Op == b.Op;
                string r = Expr(b.Right, b.RightAssociative || assoc ? p : p + 1);
                return l + " " + b.Op + " " + r;
            }
            case UnaryExpr u:
            {
                string inner = Expr(u.Operand, 7);
                if (u.Op == "not") return "not " + inner;
                if (u.Op == "-" && inner.StartsWith('-')) return "- " + inner;
                return u.Op + inner;
            }
        }
        return "nil --[[?]]";
    }

    private string Key(LuaExpr key) =>
        key is ConstExpr { Value.IsString: true } k && LuaText.IsIdentifier(k.Value.Text)
            ? "." + k.Value.Text
            : "[" + Expr(key) + "]";

    private string Call(CallExpr call)
    {
        if (call.Function is MethodRefExpr m && call.Args.Count > 0 && call.Args[0] is SelfArgExpr &&
            m.Key is ConstExpr { Value.IsString: true } k && LuaText.IsIdentifier(k.Value.Text))
            return Prefix(m.Object) + ":" + k.Value.Text + "(" + ExprList(call.Args.Skip(1).ToList(), true) + ")";
        return Prefix(call.Function) + "(" + ExprList(call.Args, true) + ")";
    }

    private string Table(TableExpr t)
    {
        if (t.Items.Count == 0) return "{}";
        var parts = new List<string>();
        var ordered = t.Items.OrderBy(i => i.Order).ToList();
        int lastPositional = ordered.FindLastIndex(i => i.Key is null);
        for (int i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            if (item.Key is null)
                parts.Add(i == lastPositional && IsSingleValueMulti(item.Value) ? "(" + Expr(item.Value) + ")" : Expr(item.Value));
            else if (item.Key is ConstExpr { Value.IsString: true } k && LuaText.IsIdentifier(k.Value.Text))
                parts.Add(k.Value.Text + " = " + Expr(item.Value));
            else
                parts.Add("[" + Expr(item.Key) + "] = " + Expr(item.Value));
        }
        string oneLine = "{" + string.Join(", ", parts) + "}";
        if (oneLine.Length <= 100 && !oneLine.Contains('\n'))
            return oneLine;
        var pad = string.Concat(Enumerable.Repeat(Indent, _depth + 1));
        var close = string.Concat(Enumerable.Repeat(Indent, _depth));
        if (parts.All(p => p.Length <= 24 && !p.Contains('\n')))
        {
            // Lists of short items are packed several to a line.
            var lines = new List<string>();
            var line = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                string item = parts[i] + (i < parts.Count - 1 ? "," : "");
                if (line.Length > 0 && pad.Length + line.Length + item.Length > 100)
                {
                    lines.Add(pad + line.ToString().TrimEnd());
                    line.Clear();
                }
                line.Append(item).Append(' ');
            }
            lines.Add(pad + line.ToString().TrimEnd());
            return "{\n" + string.Join("\n", lines) + "\n" + close + "}";
        }
        // Otherwise one item per line.
        return "{\n" + string.Join(",\n", parts.Select(p => pad + p.Replace("\n", "\n" + Indent))) + "\n" + close + "}";
    }

    private string InlineFunction(FunctionExpr f)
    {
        var inner = new LuaWriter(_single) { _depth = _depth };
        var plist = new List<string>(f.Parameters);
        if (f.IsVararg) plist.Add("...");
        if (f.Body.Count == 0)
            return "function(" + string.Join(", ", plist) + ") end";
        inner._depth = _depth + 1;
        inner.WriteBlock(f.Body);
        var close = string.Concat(Enumerable.Repeat(Indent, _depth));
        return "function(" + string.Join(", ", plist) + ")\n" + inner.ToString() + close + "end";
    }
}
