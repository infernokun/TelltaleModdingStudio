namespace TelltaleModding.Core.Dialogue;

/// <summary>What <see cref="DlogWalker.Next"/> stopped at.</summary>
public enum DlogStepKind
{
    /// <summary>A line is said (<see cref="DlogStep.LineId"/>).</summary>
    Line,

    /// <summary>The walk needs a pick from <see cref="DlogStep.Options"/> (<see cref="DlogWalker.Choose"/>).</summary>
    Decision,

    /// <summary>The conversation is over (<see cref="DlogStep.End"/> says why).</summary>
    End,
}

/// <summary>Why a walk ended.</summary>
public enum DlogEndReason
{
    None,

    /// <summary>An exit node.</summary>
    Exit,

    /// <summary>The conversation's chain ran out.</summary>
    Finished,

    /// <summary>A jump continues in another <c>.dlog</c> (<see cref="DlogStep.OtherDialog"/>).</summary>
    OtherDialog,

    /// <summary>A jump whose target is not in this dialog.</summary>
    UnresolvedJump,

    /// <summary>Too many steps without saying anything; the graph loops on game state.</summary>
    Loop,
}

/// <summary>What a decision is about.</summary>
public enum DlogDecisionKind
{
    /// <summary>A choice menu: the player picks.</summary>
    Choice,

    /// <summary>A conditional: the game picks from its state; a walker lets the user pick.</summary>
    Condition,

    /// <summary>A sequence plays one element per visit.</summary>
    Variant,
}

/// <summary>One pickable option of a decision.</summary>
/// <param name="Branch">The branch followed: a choice option, a case, a sequence element.</param>
/// <param name="Case">For a choice whose branch opens with a conditional, the case taken inside it.</param>
/// <param name="TextId">The option's text line id, when it has one.</param>
public sealed record DlogOption(DlogChild Branch, DlogChild? Case, uint? TextId)
{
    /// <summary>True for a choice taken when the timer runs out.</summary>
    public bool IsTimeout => Branch.IsTimeout;
}

/// <summary>One stop of a walk.</summary>
public sealed record DlogStep(DlogStepKind Kind, DlogNode? Node, uint LineId, DlogDecisionKind Decision,
    IReadOnlyList<DlogOption> Options, DlogEndReason End, ulong OtherDialog)
{
    internal static DlogStep Ended(DlogEndReason reason, DlogNode? node = null, ulong other = 0) =>
        new(DlogStepKind.End, node, 0, DlogDecisionKind.Choice, Array.Empty<DlogOption>(), reason, other);
}

/// <summary>
/// Walks a conversation the way the game plays it: lines in order, choice menus and
/// conditionals as decisions, jumps followed, and branches returning to their owner
/// (a choice option continues with the menu's post-choice branch, then after the menu).
/// Game state is not evaluated: conditionals and sequences are offered as decisions.
/// </summary>
public sealed class DlogWalker
{
    private const int MaxSilentSteps = 4000;

    private readonly DlogFile _dialog;
    private readonly Stack<Frame> _frames = new();
    private readonly Queue<uint> _lines = new();
    private DlogNode? _current;
    private DlogNode? _lineNode;
    private DlogNode? _decisionNode;
    private DlogDecisionKind _decisionKind;
    private List<DlogOption> _options = new();
    private DlogStep? _ended;

    public DlogWalker(DlogFile dialog) => _dialog = dialog;

    /// <summary>The options of the pending decision, if any.</summary>
    public IReadOnlyList<DlogOption> PendingOptions => _options;

    /// <summary>Starts at a conversation's (or any branch's) first node.</summary>
    public void Start(DlogChild conversation)
    {
        Reset();
        EnterBranch(conversation);
    }

    /// <summary>Starts at <paramref name="node"/>, returning to its owners as the game would.</summary>
    public void StartAt(DlogNode node)
    {
        Reset();
        JumpInto(node);
    }

    private void Reset()
    {
        _frames.Clear();
        _lines.Clear();
        _options = new List<DlogOption>();
        _current = null;
        _ended = null;
    }

    /// <summary>Runs to the next line, decision or end.</summary>
    public DlogStep Next()
    {
        if (_ended is not null)
            return _ended;
        if (_options.Count > 0)
            return new DlogStep(DlogStepKind.Decision, _decisionNode, 0, _decisionKind, _options, DlogEndReason.None, 0);
        for (int steps = 0; steps < MaxSilentSteps; steps++)
        {
            if (_lines.Count > 0)
                return new DlogStep(DlogStepKind.Line, _lineNode, _lines.Dequeue(), DlogDecisionKind.Choice, Array.Empty<DlogOption>(), DlogEndReason.None, 0);
            if (_current is null)
            {
                if (!Pop())
                    return _ended = DlogStep.Ended(DlogEndReason.Finished);
                continue;
            }
            var node = _current;
            if (Execute(node) is { } stop)
                return stop;
        }
        return _ended = DlogStep.Ended(DlogEndReason.Loop, _current);
    }

    /// <summary>Picks option <paramref name="index"/> of the pending decision.</summary>
    public void Choose(int index)
    {
        if (index < 0 || index >= _options.Count || _decisionNode is not { } owner)
            return;
        var option = _options[index];
        _options = new List<DlogOption>();
        _frames.Push(_decisionKind == DlogDecisionKind.Choice ? Frame.AfterOption(owner) : Frame.After(owner));
        if (option.Case is { } inner && _dialog.Head(option.Branch) is { } conditional)
        {
            _frames.Push(Frame.After(conditional));
            EnterBranch(inner);
        }
        else
        {
            EnterBranch(option.Branch);
        }
    }

    // Does one node; returns a step when the walk must stop there.
    private DlogStep? Execute(DlogNode node)
    {
        switch (node.Kind)
        {
            case DlogNodeKind.Exchange:
            case DlogNodeKind.Text:
                foreach (var id in node.LineIds)
                    _lines.Enqueue(id);
                _lineNode = node;
                _current = _dialog.Next(node);
                return null;

            case DlogNodeKind.Choices:
            {
                // The pre-choice branch runs first, then the menu shows.
                if (_frames.TryPeek(out var top) && top.Kind == FrameKind.ShowMenu && top.Node == node)
                {
                    _frames.Pop();
                }
                else if (node.Children.FirstOrDefault(c => c.Kind == DlogChildKind.PreChoice) is { } pre && _dialog.Head(pre) is not null)
                {
                    _frames.Push(Frame.Menu(node));
                    EnterBranch(pre);
                    return null;
                }
                return Offer(node, DlogDecisionKind.Choice, ChoiceOptions(node));
            }

            case DlogNodeKind.Conditional:
            {
                var options = node.Children.Select(c => new DlogOption(c, null, c.TextIds.Count > 0 ? c.TextIds[0] : null)).ToList();
                if (options.Count == 1)
                {
                    _frames.Push(Frame.After(node));
                    EnterBranch(options[0].Branch);
                    return null;
                }
                return Offer(node, DlogDecisionKind.Condition, options);
            }

            case DlogNodeKind.Sequence:
            {
                var options = node.Children.Select(c => new DlogOption(c, null, c.TextIds.Count > 0 ? c.TextIds[0] : null)).ToList();
                if (options.Count <= 1)
                {
                    _frames.Push(Frame.After(node));
                    if (options.Count == 1)
                        EnterBranch(options[0].Branch);
                    else
                        _current = null;
                    return null;
                }
                return Offer(node, DlogDecisionKind.Variant, options);
            }

            case DlogNodeKind.Parallel:
            {
                // Elements play together; walk them one after another.
                _frames.Push(Frame.After(node));
                for (int i = node.Children.Count - 1; i >= 1; i--)
                    _frames.Push(Frame.Enter(node.Children[i]));
                if (node.Children.Count > 0)
                    EnterBranch(node.Children[0]);
                else
                    _current = null;
                return null;
            }

            case DlogNodeKind.Jump:
            {
                var info = node.Jump;
                if (info is { OtherDialog: not 0 })
                    return _ended = DlogStep.Ended(DlogEndReason.OtherDialog, node, info.OtherDialog);
                if (info is { Behaviour: DlogJumpBehaviour.Return })
                {
                    _current = null;
                    return null;
                }
                var target = _dialog.JumpTarget(node);
                if (target is null)
                    return _ended = DlogStep.Ended(DlogEndReason.UnresolvedJump, node);
                if (info?.Behaviour == DlogJumpBehaviour.JumpExecuteAndReturn)
                {
                    _frames.Push(Frame.After(node));
                    _current = target;
                }
                else
                {
                    JumpInto(target);
                }
                return null;
            }

            case DlogNodeKind.Exit:
                return _ended = DlogStep.Ended(DlogEndReason.Exit, node);

            default:
                _current = _dialog.Next(node);
                return null;
        }
    }

    private DlogStep Offer(DlogNode node, DlogDecisionKind kind, List<DlogOption> options)
    {
        _current = null;
        if (options.Count == 0)
        {
            _current = _dialog.Next(node);
            return Next();
        }
        _decisionNode = node;
        _decisionKind = kind;
        _options = options;
        return new DlogStep(DlogStepKind.Decision, node, 0, kind, options, DlogEndReason.None, 0);
    }

    /// <summary>A menu's options; a textless choice that opens with a conditional offers its cases.</summary>
    public List<DlogOption> ChoiceOptions(DlogNode menu)
    {
        var options = new List<DlogOption>();
        foreach (var choice in menu.Options)
        {
            if (choice.TextIds.Count == 0 && _dialog.Head(choice) is { Kind: DlogNodeKind.Conditional } conditional
                && conditional.Children.Any(c => c.TextIds.Count > 0))
            {
                foreach (var @case in conditional.Children)
                    options.Add(new DlogOption(choice, @case, @case.TextIds.Count > 0 ? @case.TextIds[0] : null));
                continue;
            }
            options.Add(new DlogOption(choice, null, choice.TextIds.Count > 0 ? choice.TextIds[0] : null));
        }
        return options;
    }

    private void EnterBranch(DlogChild branch) => _current = _dialog.Head(branch);

    // Rebuilds the return frames from the target's owners, outermost first.
    private void JumpInto(DlogNode target)
    {
        _frames.Clear();
        var owners = new List<(DlogChild Branch, DlogNode Owner)>();
        var branch = target.Branch;
        for (int depth = 0; branch?.Owner is { } owner && depth < 256; depth++)
        {
            owners.Add((branch, owner));
            branch = owner.Branch;
        }
        for (int i = owners.Count - 1; i >= 0; i--)
        {
            var (b, owner) = owners[i];
            _frames.Push(b.Kind switch
            {
                DlogChildKind.Choice => Frame.AfterOption(owner),
                DlogChildKind.PreChoice => Frame.Menu(owner),
                _ => Frame.After(owner),
            });
        }
        _current = target;
    }

    // A branch ran out: continue where its owner says.
    private bool Pop()
    {
        while (_frames.Count > 0)
        {
            var frame = _frames.Pop();
            switch (frame.Kind)
            {
                case FrameKind.ShowMenu:
                    // Execute sees the frame on top and shows the menu instead of its pre-choice branch.
                    _frames.Push(frame);
                    _current = frame.Node;
                    return true;
                case FrameKind.AfterOption:
                    var post = frame.Node!.Children.FirstOrDefault(c => c.Kind == DlogChildKind.PostChoice);
                    _frames.Push(Frame.After(frame.Node));
                    if (post is not null && _dialog.Head(post) is { } head)
                    {
                        _current = head;
                        return true;
                    }
                    continue;
                case FrameKind.After:
                    _current = _dialog.Next(frame.Node!);
                    if (_current is not null)
                        return true;
                    continue;
                case FrameKind.Enter:
                    _current = _dialog.Head(frame.Branch!);
                    if (_current is not null)
                        return true;
                    continue;
            }
        }
        return false;
    }

    private enum FrameKind { After, AfterOption, ShowMenu, Enter }

    private readonly record struct Frame(FrameKind Kind, DlogNode? Node, DlogChild? Branch)
    {
        public static Frame After(DlogNode node) => new(FrameKind.After, node, null);
        public static Frame AfterOption(DlogNode menu) => new(FrameKind.AfterOption, menu, null);
        public static Frame Menu(DlogNode menu) => new(FrameKind.ShowMenu, menu, null);
        public static Frame Enter(DlogChild branch) => new(FrameKind.Enter, null, branch);
    }
}
