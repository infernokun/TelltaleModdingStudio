using System.Text;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Dialogue;

/// <summary>What a dialog node does, from its engine class.</summary>
public enum DlogNodeKind
{
    /// <summary>Entry point of a conversation (<c>DlgNodeStart</c>).</summary>
    Start,

    /// <summary>One or more spoken lines in order (<c>DlgNodeExchange</c>).</summary>
    Exchange,

    /// <summary>A single line of text (<c>DlgNodeText</c>).</summary>
    Text,

    /// <summary>A choice menu; its options are <see cref="DlogChildKind.Choice"/> children.</summary>
    Choices,

    /// <summary>A branch on game state; each case is a child.</summary>
    Conditional,

    /// <summary>Sets or tests game logic.</summary>
    Logic,

    /// <summary>Continues at another node.</summary>
    Jump,

    /// <summary>Ends the conversation.</summary>
    Exit,
    Wait,
    Idle,
    Script,

    /// <summary>Plays one of its elements per visit.</summary>
    Sequence,

    /// <summary>Plays all of its elements at once.</summary>
    Parallel,
    Marker,
    Notes,
    Chore,
    CancelChoices,

    /// <summary>A class we have no layout for.</summary>
    Other,
}

/// <summary>The role of a child branch inside its owner.</summary>
public enum DlogChildKind
{
    /// <summary>A conversation listed in a folder.</summary>
    Conversation,

    /// <summary>A player option of a choice menu.</summary>
    Choice,

    /// <summary>Plays before a choice menu's options are shown.</summary>
    PreChoice,

    /// <summary>Plays after the chosen option's branch.</summary>
    PostChoice,

    /// <summary>One branch of a conditional.</summary>
    Case,

    /// <summary>One element of a sequence or parallel node.</summary>
    Element,
    Other,
}

/// <summary>Where a jump node continues.</summary>
public enum DlogJumpTarget
{
    /// <summary>The node named by <see cref="DlogJump.TargetName"/>.</summary>
    ToName = 1,

    /// <summary>Back to the node that owns the jump's branch.</summary>
    ToParent = 2,

    /// <summary>The wait node after an enclosing node, usually the choice menu.</summary>
    ToNodeAfterParentWait = 3,
}

/// <summary>What a jump does once it reaches its target.</summary>
public enum DlogJumpBehaviour
{
    JumpAndExecute = 1,
    JumpExecuteAndReturn = 2,
    Return = 3,
}

/// <summary>The target of a <see cref="DlogNodeKind.Jump"/> node.</summary>
/// <param name="TargetName">Name symbol of the target node.</param>
/// <param name="OtherDialog">Symbol of another <c>.dlog</c> the jump continues in, or 0 for this one.</param>
public sealed record DlogJump(DlogJumpTarget Target, DlogJumpBehaviour Behaviour, ulong TargetName, ulong OtherDialog);

/// <summary>A branch off a node or folder, like a conversation, choice option or conditional case.</summary>
public sealed class DlogChild
{
    internal DlogChild(ulong id, string className, DlogChildKind kind, ulong headId, ulong nameSymbol, IReadOnlyList<uint> textIds)
    {
        Id = id;
        ClassName = className;
        Kind = kind;
        HeadId = headId;
        NameSymbol = nameSymbol;
        TextIds = textIds;
    }

    public ulong Id { get; }

    /// <summary>The engine class name.</summary>
    public string ClassName { get; }

    public DlogChildKind Kind { get; }

    /// <summary>Id of the first node of this branch's chain.</summary>
    public ulong HeadId { get; }

    public ulong NameSymbol { get; }

    /// <summary>Language line ids on the branch, such as a choice option's text.</summary>
    public IReadOnlyList<uint> TextIds { get; }

    /// <summary>Condition classes a choice option waits on.</summary>
    public IReadOnlyList<string> Conditions { get; internal set; } = Array.Empty<string>();

    /// <summary>True for an option taken when the player lets the timer run out.</summary>
    public bool IsTimeout => Conditions.Contains("DlgConditionTime") && !Conditions.Contains("DlgConditionInput");

    /// <summary>The node that owns this branch, null for a folder's conversations.</summary>
    public DlogNode? Owner { get; internal set; }

    /// <summary>The folder that lists this branch, for conversations.</summary>
    public DlogFolder? Folder { get; internal set; }

    public override string ToString() => $"{ClassName} {Id:x16}";
}

/// <summary>One node of a dialog graph.</summary>
public sealed class DlogNode
{
    internal DlogNode(string className, int offset)
    {
        ClassName = className;
        Kind = KindOf(className);
        Offset = offset;
    }

    public ulong Id { get; internal set; }

    /// <summary>The engine class name.</summary>
    public string ClassName { get; }

    public DlogNodeKind Kind { get; }

    /// <summary>Byte offset of the node in the file.</summary>
    public int Offset { get; }

    /// <summary>Name hash of the node, or 0. Jumps target nodes by it.</summary>
    public ulong NameSymbol { get; internal set; }

    /// <summary>The node's name when <see cref="DlogFile.ResolveNames"/> found it.</summary>
    public string? Name { get; internal set; }

    public ulong PrevId { get; internal set; }

    /// <summary>The next node in the chain. The branch ends when it isn't a node.</summary>
    public ulong NextId { get; internal set; }

    /// <summary>The language line ids spoken by this node, in order.</summary>
    public IReadOnlyList<uint> LineIds { get; internal set; } = Array.Empty<uint>();

    /// <summary>Symbol of the <c>.chore</c> an exchange or chore node plays, or 0.</summary>
    public ulong ChoreSymbol { get; internal set; }

    /// <summary>Child branches in order, such as a choice menu's options or a conditional's cases.</summary>
    public IReadOnlyList<DlogChild> Children { get; internal set; } = Array.Empty<DlogChild>();

    public DlogJump? Jump { get; internal set; }

    /// <summary>A script node's Lua text or a notes node's note.</summary>
    public string? Script { get; internal set; }

    /// <summary>The branch whose chain this node is on.</summary>
    public DlogChild? Branch { get; internal set; }

    /// <summary>False when the node's layout wasn't understood and its fields were skipped.</summary>
    public bool IsComplete { get; internal set; } = true;

    /// <summary>The player options of a choice menu.</summary>
    public IEnumerable<DlogChild> Options => Children.Where(c => c.Kind == DlogChildKind.Choice);

    public override string ToString() => $"{ClassName} {Id:x16}";

    internal static DlogNodeKind KindOf(string className) => className switch
    {
        "DlgNodeStart" => DlogNodeKind.Start,
        "DlgNodeExchange" => DlogNodeKind.Exchange,
        "DlgNodeText" => DlogNodeKind.Text,
        "DlgNodeChoices" => DlogNodeKind.Choices,
        "DlgNodeConditional" => DlogNodeKind.Conditional,
        "DlgNodeLogic" => DlogNodeKind.Logic,
        "DlgNodeJump" => DlogNodeKind.Jump,
        "DlgNodeExit" => DlogNodeKind.Exit,
        "DlgNodeWait" => DlogNodeKind.Wait,
        "DlgNodeIdle" => DlogNodeKind.Idle,
        "DlgNodeScript" => DlogNodeKind.Script,
        "DlgNodeSequence" => DlogNodeKind.Sequence,
        "DlgNodeParallel" => DlogNodeKind.Parallel,
        "DlgNodeMarker" => DlogNodeKind.Marker,
        "DlgNodeNotes" => DlogNodeKind.Notes,
        "DlgNodeChore" => DlogNodeKind.Chore,
        "DlgNodeCancelChoices" => DlogNodeKind.CancelChoices,
        _ => DlogNodeKind.Other,
    };
}

/// <summary>A folder of conversations.</summary>
public sealed class DlogFolder
{
    internal DlogFolder(ulong id, ulong nameSymbol, IReadOnlyList<DlogChild> conversations)
    {
        Id = id;
        NameSymbol = nameSymbol;
        Conversations = conversations;
    }

    public ulong Id { get; }
    public ulong NameSymbol { get; }

    /// <summary>The folder's name when <see cref="DlogFile.ResolveNames"/> found it.</summary>
    public string? Name { get; internal set; }

    public IReadOnlyList<DlogChild> Conversations { get; }
}

/// <summary>
/// A <c>.dlog</c> dialog: folders of conversations, each a chain of nodes linked by id.
/// Spoken text is stored as line ids from the scene's <c>.landb</c>.
/// </summary>
/// <remarks>
/// The default section holds the <c>Dlg</c> members, then the folders, then the nodes,
/// each prefixed by its class hash. Non-intrinsic members are size-prefixed, so unknown
/// ones can be skipped. Older games add a unique-id block to dialogs, folders and nodes.
/// </remarks>
public sealed class DlogFile
{
    private readonly Dictionary<ulong, DlogNode> _nodes = new();
    private readonly Dictionary<ulong, DlogChild> _children = new();
    private readonly Dictionary<ulong, DlogNode> _byName = new();

    internal DlogFile(string name, MetaStreamFormat format, IReadOnlyList<DlogFolder> folders, IReadOnlyList<DlogNode> nodes, int skippedBytes)
    {
        Name = name;
        Format = format;
        Folders = folders;
        Nodes = nodes;
        SkippedBytes = skippedBytes;
        foreach (var node in nodes)
        {
            _nodes.TryAdd(node.Id, node);
            foreach (var child in node.Children)
            {
                child.Owner = node;
                _children.TryAdd(child.Id, child);
            }
        }
        foreach (var folder in folders)
        {
            foreach (var child in folder.Conversations)
            {
                child.Folder = folder;
                _children.TryAdd(child.Id, child);
            }
        }
        // A jump can share its target's name, so prefer other node kinds.
        foreach (var node in nodes.Where(n => n.NameSymbol != 0).OrderBy(n => n.Kind == DlogNodeKind.Jump ? 1 : 0))
            _byName.TryAdd(node.NameSymbol, node);
        AssignBranches();
    }

    /// <summary>The dialog's file name.</summary>
    public string Name { get; }

    public MetaStreamFormat Format { get; }

    public IReadOnlyList<DlogFolder> Folders { get; }

    /// <summary>Every node in file order.</summary>
    public IReadOnlyList<DlogNode> Nodes { get; }

    /// <summary>Bytes skipped after nodes whose layout wasn't understood.</summary>
    public int SkippedBytes { get; }

    /// <summary>Every conversation of every folder, in order.</summary>
    public IEnumerable<DlogChild> Conversations => Folders.SelectMany(f => f.Conversations);

    public DlogNode? FindNode(ulong id) => _nodes.GetValueOrDefault(id);

    public DlogChild? FindChild(ulong id) => _children.GetValueOrDefault(id);

    /// <summary>The node with the given name symbol, preferring non-jump nodes.</summary>
    public DlogNode? FindByName(ulong nameSymbol) => nameSymbol == 0 ? null : _byName.GetValueOrDefault(nameSymbol);

    /// <summary>The first node of a branch's chain.</summary>
    public DlogNode? Head(DlogChild child) => FindNode(child.HeadId);

    /// <summary>The node after <paramref name="node"/> in its chain, or null at the end of the branch.</summary>
    public DlogNode? Next(DlogNode node) => FindNode(node.NextId);

    /// <summary>The nodes of a branch's chain in order. Stops on a loop.</summary>
    public IEnumerable<DlogNode> Chain(DlogChild child)
    {
        var seen = new HashSet<ulong>();
        for (var node = Head(child); node is not null && seen.Add(node.Id); node = Next(node))
            yield return node;
    }

    /// <summary>The node that owns the branch <paramref name="node"/> is on, or null at the top level.</summary>
    public DlogNode? Parent(DlogNode node) => node.Branch?.Owner;

    /// <summary>The conversation <paramref name="node"/> belongs to.</summary>
    public DlogChild? ConversationOf(DlogNode node)
    {
        var branch = node.Branch;
        for (int depth = 0; branch is not null && depth < 256; depth++)
        {
            if (branch.Owner is null)
                return branch;
            branch = branch.Owner.Branch;
        }
        return null;
    }

    /// <summary>The node a jump continues at, or null if it leaves this dialog or the target is unknown.</summary>
    public DlogNode? JumpTarget(DlogNode jump)
    {
        if (jump.Jump is not { } info || info.OtherDialog != 0)
            return null;
        return info.Target switch
        {
            DlogJumpTarget.ToName => FindByName(info.TargetName),
            DlogJumpTarget.ToParent => Parent(jump),
            DlogJumpTarget.ToNodeAfterParentWait => AfterParentWait(jump),
            _ => null,
        };
    }

    // A choice menu is followed by some lines and then a wait node that holds until the
    // player picks. The jump resumes at that wait.
    private DlogNode? AfterParentWait(DlogNode node)
    {
        for (var parent = Parent(node); parent is not null; parent = Parent(parent))
        {
            var next = Next(parent);
            for (int i = 0; next is not null && i < 16; i++, next = Next(next))
            {
                if (next.Kind == DlogNodeKind.Wait)
                    return next;
            }
        }
        return null;
    }

    /// <summary>Every language line id the dialog refers to.</summary>
    public IEnumerable<uint> AllTextIds() =>
        Nodes.SelectMany(n => n.LineIds)
            .Concat(_children.Values.SelectMany(c => c.TextIds))
            .Distinct();

    /// <summary>The name of a branch's first node, or null.</summary>
    public string? NameOf(DlogChild branch) => Head(branch)?.Name;

    /// <summary>
    /// Fills in node and folder names whose hash matches one of <paramref name="candidates"/>.
    /// Returns how many were found.
    /// </summary>
    public int ResolveNames(IEnumerable<string> candidates)
    {
        var wanted = new HashSet<ulong>(Nodes.Select(n => n.NameSymbol).Concat(Folders.Select(f => f.NameSymbol)));
        wanted.Remove(0);
        var found = new Dictionary<ulong, string>();
        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0)
                continue;
            ulong hash = TelltaleCrc64.HashSymbol(candidate);
            if (wanted.Contains(hash))
                found.TryAdd(hash, candidate);
        }
        int resolved = 0;
        foreach (var node in Nodes)
        {
            if (node.NameSymbol != 0 && found.TryGetValue(node.NameSymbol, out var name))
            {
                node.Name = name;
                resolved++;
            }
        }
        foreach (var folder in Folders)
        {
            if (folder.NameSymbol != 0 && found.TryGetValue(folder.NameSymbol, out var name))
            {
                folder.Name = name;
                resolved++;
            }
        }
        return resolved;
    }

    /// <summary>
    /// Likely names for the dialog's symbols: strings found in <paramref name="data"/>,
    /// those with common prefixes, and common node names.
    /// </summary>
    public static IEnumerable<string> NameCandidates(byte[] data)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && data[i] is >= 0x20 and < 0x7F;
            if (printable)
            {
                if (start < 0)
                    start = i;
                continue;
            }
            if (start >= 0 && i - start >= 3 && i - start <= 96)
            {
                var text = Encoding.ASCII.GetString(data, start, i - start);
                words.Add(text);
                words.Add(text.TrimEnd('-', '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '.', '/', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ':', ' '));
                foreach (var token in text.Split(new[] { ' ', '"', '\'', '(', ')', ',', '=', ':', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    words.Add(token);
            }
            start = -1;
        }
        foreach (var name in CommonNames)
            yield return name;
        foreach (var word in words)
        {
            yield return word;
            foreach (var prefix in InteractionPrefixes)
                yield return prefix + word;
        }
    }

    private static readonly string[] InteractionPrefixes = { "use_", "Use_", "look_", "Look_", "talk_", "Talk_" };

    private static readonly string[] CommonNames =
    {
        "Default", "Text", "Dialog", "dialog", "Options", "Wait", "Blocker", "blank", "idle", "Idle", "timer",
        "transition", "logic", "Logic", "Setup", "reset", "checkpoint", "camera", "post", "pre", "Chore",
        "New", "End Episode", "End Scene", "Exit", "Enter", "cs_enter", "cs_exit", "cs_intro", "cs_outro",
        "random", "chores", "next", "Head", "Reticle", "Dialog exhausted", "Win/Lose", "missionUpdate",
    };

    private void AssignBranches()
    {
        var pending = new Queue<DlogChild>(Folders.SelectMany(f => f.Conversations));
        var seenChildren = new HashSet<ulong>();
        while (pending.Count > 0)
        {
            var child = pending.Dequeue();
            if (!seenChildren.Add(child.Id))
                continue;
            foreach (var node in Chain(child))
            {
                if (node.Branch is not null)
                    break;
                node.Branch = child;
                foreach (var sub in node.Children)
                    pending.Enqueue(sub);
            }
        }
        // Branches unreachable from a folder still own their chains.
        foreach (var child in _children.Values)
        {
            foreach (var node in Chain(child))
            {
                if (node.Branch is not null)
                    break;
                node.Branch = child;
            }
        }
    }

    /// <summary>Parses a <c>.dlog</c>. Returns false with a reason instead of throwing.</summary>
    public static bool TryRead(byte[] data, out DlogFile? dialog, out string? error)
    {
        dialog = null;
        try
        {
            return DlogReader.TryRead(data, out dialog, out error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = $"Unreadable dialog: {ex.Message}";
            return false;
        }
    }

    /// <summary>The lowercased scene name a <c>.dlog</c> shares with its <c>.landb</c>.</summary>
    public static string SceneOf(string fileName) => Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
}
