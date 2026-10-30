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

    /// <summary>A single line of text (<c>DlgNodeText</c>), usually UI or narration.</summary>
    Text,

    /// <summary>A choice menu; its options are <see cref="DlogChildKind.Choice"/> children.</summary>
    Choices,

    /// <summary>A branch on game state; each case is a child.</summary>
    Conditional,

    /// <summary>Sets or tests game logic.</summary>
    Logic,

    /// <summary>Continues at another node (see <see cref="DlogJump"/>).</summary>
    Jump,

    /// <summary>Ends the conversation.</summary>
    Exit,
    Wait,
    Idle,
    Script,

    /// <summary>Plays one of its elements per visit (random or in turn).</summary>
    Sequence,

    /// <summary>Plays all of its elements at once.</summary>
    Parallel,
    Marker,
    Notes,
    Chore,
    CancelChoices,

    /// <summary>A class this reader has no layout for; kept with its class name.</summary>
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

    /// <summary>Back to the node that owns the jump's branch (e.g. re-show a choice hub).</summary>
    ToParent = 2,

    /// <summary>The wait node that follows an enclosing node (usually the choice menu), and on from there.</summary>
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
/// <param name="TargetName">Name symbol of the target node (for <see cref="DlogJumpTarget.ToName"/>).</param>
/// <param name="OtherDialog">Symbol of another <c>.dlog</c> the jump continues in, or 0 for this one.</param>
public sealed record DlogJump(DlogJumpTarget Target, DlogJumpBehaviour Behaviour, ulong TargetName, ulong OtherDialog);

/// <summary>
/// A branch hanging off a node or folder: a conversation in a folder, a choice option,
/// a conditional case… <see cref="HeadId"/> links to the first node of its chain.
/// </summary>
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

    /// <summary>The engine class (<c>DlgChoice</c>, <c>DlgConditionalCase</c>, …).</summary>
    public string ClassName { get; }

    public DlogChildKind Kind { get; }

    /// <summary>Id of the first node of this branch's chain.</summary>
    public ulong HeadId { get; }

    public ulong NameSymbol { get; }

    /// <summary>Language line ids attached to the branch, a choice option's text, a case's label.</summary>
    public IReadOnlyList<uint> TextIds { get; }

    /// <summary>
    /// Condition classes a choice option waits on: <c>DlgConditionInput</c> (the player picks it),
    /// <c>DlgConditionTime</c> (taken when the timer runs out), <c>DlgConditionRule</c>.
    /// </summary>
    public IReadOnlyList<string> Conditions { get; internal set; } = Array.Empty<string>();

    /// <summary>True for an option taken when the player lets the timer run out.</summary>
    public bool IsTimeout => Conditions.Contains("DlgConditionTime") && !Conditions.Contains("DlgConditionInput");

    /// <summary>The node that owns this branch; null for a folder's conversations.</summary>
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

    /// <summary>The engine class (<c>DlgNodeExchange</c>, <c>DlgNodeChoices</c>, …).</summary>
    public string ClassName { get; }

    public DlogNodeKind Kind { get; }

    /// <summary>Byte offset of the node in the file.</summary>
    public int Offset { get; }

    /// <summary>Name symbol (hash) of the node; 0 when unnamed. Jumps target nodes by it.</summary>
    public ulong NameSymbol { get; internal set; }

    /// <summary>The node's name when <see cref="DlogFile.ResolveNames"/> found it.</summary>
    public string? Name { get; internal set; }

    public ulong PrevId { get; internal set; }

    /// <summary>The next node in the chain; ends the branch when it is not a node.</summary>
    public ulong NextId { get; internal set; }

    /// <summary>The language line ids spoken by this node, in order.</summary>
    public IReadOnlyList<uint> LineIds { get; internal set; } = Array.Empty<uint>();

    /// <summary>
    /// Symbol of the <c>.chore</c> file an exchange or chore node plays (the exchange's
    /// lines are voiced on its timeline); 0 for none.
    /// </summary>
    public ulong ChoreSymbol { get; internal set; }

    /// <summary>
    /// Child branches in order: a choice menu's pre-choice, options, then post-choice;
    /// a conditional's cases; a sequence's elements.
    /// </summary>
    public IReadOnlyList<DlogChild> Children { get; internal set; } = Array.Empty<DlogChild>();

    public DlogJump? Jump { get; internal set; }

    /// <summary>A script node's Lua text or a notes node's note.</summary>
    public string? Script { get; internal set; }

    /// <summary>The branch whose chain this node is on (null for unreachable nodes).</summary>
    public DlogChild? Branch { get; internal set; }

    /// <summary>False when the node's layout was not understood and its fields were skipped.</summary>
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

/// <summary>A folder of conversations, as the dialog editor groups them.</summary>
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
/// A <c>.dlog</c> dialog resource: folders of conversations, each a chain of nodes
/// (lines, choice menus, conditionals, jumps…) linked by id. Spoken text is stored as
/// language line ids that match the scene's <c>.landb</c> (same base name).
/// </summary>
/// <remarks>
/// MSV5/MSV6 meta stream. The default section holds the <c>Dlg</c> members (id, name,
/// version, default folder, an embedded language database, project/resource ids,
/// flags, dependencies and (from Tales from the Borderlands on) production props,
/// a Jira record block and a tool-data flag), then the folders, then the nodes, each
/// prefixed by its class hash. Non-intrinsic members are wrapped in size-prefixed
/// blocks, so unknown members are skipped by size; older games add a unique-id block
/// to dialogs, folders and nodes.
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
        // Jumps target names; a jump may share its target's name, so prefer other kinds.
        foreach (var node in nodes.Where(n => n.NameSymbol != 0).OrderBy(n => n.Kind == DlogNodeKind.Jump ? 1 : 0))
            _byName.TryAdd(node.NameSymbol, node);
        AssignBranches();
    }

    /// <summary>The dialog's own name, e.g. <c>env_heliosoffice.dlog</c>.</summary>
    public string Name { get; }

    public MetaStreamFormat Format { get; }

    public IReadOnlyList<DlogFolder> Folders { get; }

    /// <summary>Every node in file order.</summary>
    public IReadOnlyList<DlogNode> Nodes { get; }

    /// <summary>Bytes skipped while resynchronising after nodes whose layout was not understood.</summary>
    public int SkippedBytes { get; }

    /// <summary>Every conversation of every folder, in order.</summary>
    public IEnumerable<DlogChild> Conversations => Folders.SelectMany(f => f.Conversations);

    public DlogNode? FindNode(ulong id) => _nodes.GetValueOrDefault(id);

    public DlogChild? FindChild(ulong id) => _children.GetValueOrDefault(id);

    /// <summary>The node with the given name symbol (preferring non-jump nodes).</summary>
    public DlogNode? FindByName(ulong nameSymbol) => nameSymbol == 0 ? null : _byName.GetValueOrDefault(nameSymbol);

    /// <summary>The first node of a branch's chain.</summary>
    public DlogNode? Head(DlogChild child) => FindNode(child.HeadId);

    /// <summary>The node after <paramref name="node"/> in its chain, or null at the end of the branch.</summary>
    public DlogNode? Next(DlogNode node) => FindNode(node.NextId);

    /// <summary>The nodes of a branch's chain in order (stops on a loop).</summary>
    public IEnumerable<DlogNode> Chain(DlogChild child)
    {
        var seen = new HashSet<ulong>();
        for (var node = Head(child); node is not null && seen.Add(node.Id); node = Next(node))
            yield return node;
    }

    /// <summary>The node that owns the branch <paramref name="node"/> is on (null at a conversation's top level).</summary>
    public DlogNode? Parent(DlogNode node) => node.Branch?.Owner;

    /// <summary>The conversation (folder branch) that <paramref name="node"/> belongs to.</summary>
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

    /// <summary>
    /// The node a jump continues at: the named node, the owner of the jump's branch, or
    /// null when it leaves this dialog or the target is unknown.
    /// </summary>
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

    // A choice menu is followed by the lines said while it shows, then a wait node that
    // holds the conversation until a pick; the jump resumes there (the wait passes on).
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

    /// <summary>Every language line id the dialog refers to (spoken lines and option texts).</summary>
    public IEnumerable<uint> AllTextIds() =>
        Nodes.SelectMany(n => n.LineIds)
            .Concat(_children.Values.SelectMany(c => c.TextIds))
            .Distinct();

    /// <summary>The resolved name of a branch's first node (a conversation's start node), or null.</summary>
    public string? NameOf(DlogChild branch) => Head(branch)?.Name;

    /// <summary>
    /// Fills in <see cref="DlogNode.Name"/> and <see cref="DlogFolder.Name"/> for every
    /// name symbol that matches one of <paramref name="candidates"/> (the engine keeps only
    /// the hash of each name). Returns how many names were resolved.
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
    /// Likely names for the dialog's symbols: readable strings embedded in
    /// <paramref name="data"/> (agent names, rule names, script text), the same with the
    /// interaction prefixes the games use, and common node names.
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

    // Each branch's chain is owned by that branch; walking from every child marks them.
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

    /// <summary>Parses a <c>.dlog</c>; returns false with a reason for anything else. Never throws.</summary>
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

    /// <summary>
    /// The scene name a <c>.dlog</c> shares with its <c>.landb</c>
    /// (<c>env_heliosOffice.dlog</c> → <c>env_heliosoffice</c>, the form
    /// <see cref="LandbFile.SplitName"/> gives after lower-casing).
    /// </summary>
    public static string SceneOf(string fileName) => Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
}
