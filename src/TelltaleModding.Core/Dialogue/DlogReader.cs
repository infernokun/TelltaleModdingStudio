using System.Buffers.Binary;
using System.Text;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Dialogue;

internal sealed class DlogFormatException : Exception
{
    public DlogFormatException(string message) : base(message) { }
}

/// <summary>Reads the <c>Dlg</c> object tree of a <c>.dlog</c> meta stream into a <see cref="DlogFile"/>.</summary>
internal static class DlogReader
{
    private static readonly string[] ClassNames =
    {
        "Dlg", "DlgFolder", "DlgFolderChild", "DlgChild", "DlgNode", "DlgNodeStart", "DlgNodeExchange",
        "DlgNodeText", "DlgNodeChoices", "DlgChoice", "DlgChoicesChildPre", "DlgChoicesChildPost",
        "DlgNodeConditional", "DlgConditionalCase", "DlgNodeLogic", "DlgNodeJump", "DlgNodeExit",
        "DlgNodeWait", "DlgNodeIdle", "DlgNodeScript", "DlgNodeSequence", "DlgNodeSequence::Element",
        "DlgNodeParallel", "DlgNodeParallel::PElement", "DlgNodeMarker", "DlgNodeNotes", "DlgNodeChore",
        "DlgNodeCancelChoices", "DlgNodeStoryBoard", "DlgNodeStats", "DlgNodeStats::Cohort",
        "DlgNodeCriteria", "DlgNodeInstanceCriteria", "DlgChildSetChoice", "DlgChildSetConditionalCase",
        "DlgChildSetChoicesChildPre", "DlgChildSetChoicesChildPost", "DlgCondition", "DlgConditionInput",
        "DlgConditionTime", "DlgConditionRule", "DlgConditionSet", "DlgNodeExchange::Entry", "DlgLine",
        "DlgLineCollection", "LanguageResProxy", "DlgObjID", "DlgObjIDOwner", "DlgObjectProps",
        "DlgObjectPropsOwner", "DlgNodeLink", "DlgChainHead", "DlgVisibilityConditions",
        "DlgVisibilityConditionsOwner", "DlgDownstreamVisibilityConditions", "Rule", "LogicGroup",
        "LogicGroup::LogicItem", "LanguageDB", "PropertySet", "Symbol", "Flags", "NoteCollection", "Note",
        "Note::Entry", "JiraRecordManager", "DependencyLoader<1>",
    };

    private static readonly Dictionary<ulong, string> NamesByHash =
        ClassNames.ToDictionary(TelltaleCrc64.HashSymbol, n => n);

    private static readonly ulong LanguageResProxyHash = TelltaleCrc64.HashSymbol("LanguageResProxy");

    private sealed class Layout
    {
        /// <summary>Dialogs, folders and nodes carry a unique-id block (games up to 2017).</summary>
        public bool UniqueIds;

        /// <summary>Production props and friends follow the dependencies (2014 on).</summary>
        public bool ProductionProps;

        public required Dictionary<ulong, string> Classes;
        public required HashSet<ulong> NodeClasses;
    }

    public static bool TryRead(byte[] data, out DlogFile? dialog, out string? error)
    {
        dialog = null;
        if (!MetaStreamHeader.TryParse(data, out var header, out error))
            return false;
        if (!header!.ContainsClass("Dlg"))
        {
            error = "Not a dialog resource.";
            return false;
        }

        var classes = new Dictionary<ulong, string>();
        foreach (var record in header.Classes)
            classes[record.TypeHash] = NamesByHash.GetValueOrDefault(record.TypeHash) ?? $"0x{record.TypeHash:X16}";
        var layout = new Layout
        {
            Classes = classes,
            NodeClasses = classes.Where(c => c.Value.StartsWith("DlgNode", StringComparison.Ordinal) && !c.Value.Contains("::")
                                             && c.Value is not ("DlgNodeLink" or "DlgNodeCriteria" or "DlgNodeInstanceCriteria"))
                .Select(c => c.Key).ToHashSet(),
        };

        var r = new Reader(data, header.DefaultSectionOffset, header.DefaultSectionOffset + header.DefaultSectionSize);

        // Dlg members.
        ReadObjId(r);
        if (r.Peek32(0) == 8 && r.Peek32(8) == r.Peek32(12) + 8)
        {
            r.Block();
            layout.UniqueIds = true;
        }
        string name = ReadString(r);
        r.U32(); // version
        r.Block(); // default folder id
        r.Block(); // embedded language database
        r.U32(); // project id
        r.U64(); // resource location
        r.U32(); // chronology
        r.U32(); // flags
        r.Block(); // dependencies
        if (LooksLikeProductionProps(r))
        {
            layout.ProductionProps = true;
            r.Block(); // production report props
            r.Block(); // jira records
            r.Byte(); // has tool-only data
        }
        else if (!LooksLikeFolders(r, r.Pos))
        {
            throw new DlogFormatException($"Unrecognised dialog header layout at 0x{r.Pos:X}.");
        }

        uint folderCount = r.U32();
        if (folderCount > 100_000)
            throw new DlogFormatException("Implausible folder count.");
        var folders = new List<DlogFolder>((int)folderCount);
        for (int i = 0; i < folderCount; i++)
            folders.Add(ReadFolder(r, layout));

        uint nodeCount = r.U32();
        if (nodeCount > 1_000_000)
            throw new DlogFormatException("Implausible node count.");
        var nodes = new List<DlogNode>((int)Math.Min(nodeCount, 100_000));
        int skipped = 0;
        for (int i = 0; i < nodeCount && r.Left >= 8; i++)
        {
            int start = r.Pos;
            var node = ReadNode(r, layout, out bool aligned);
            nodes.Add(node);
            if (aligned || i == nodeCount - 1)
                continue;
            // The node's tail was not understood: find the next node by its class hash.
            int next = FindNextNode(r, layout, Math.Max(r.Pos, start + 8));
            if (next < 0)
                break;
            node.IsComplete = false;
            skipped += next - r.Pos;
            r.Pos = next;
        }

        dialog = new DlogFile(name, header.Format, folders, nodes, skipped);
        error = null;
        return true;
    }

    // ---- Dialog structure ------------------------------------------------------

    private static DlogFolder ReadFolder(Reader r, Layout layout)
    {
        ulong id = ReadObjId(r);
        r.Block(); // object props
        var conversations = ReadChildSet(r.Block(), layout);
        if (layout.UniqueIds)
            r.Block();
        ulong name = r.U64();
        if (layout.ProductionProps)
            r.Block();
        return new DlogFolder(id, name, conversations);
    }

    private static DlogNode ReadNode(Reader r, Layout layout, out bool aligned)
    {
        int start = r.Pos;
        ulong hash = r.U64();
        string className = layout.Classes.GetValueOrDefault(hash) ?? $"0x{hash:X16}";
        var node = new DlogNode(className, start);
        aligned = false;
        try
        {
            switch (className)
            {
                case "DlgNodeExchange":
                    r.U32(); // priority
                    node.ChoreSymbol = ReadHandle(r);
                    ReadNodeBase(r.Block(), node);
                    ReadExchangeTail(r, layout, node);
                    break;
                case "DlgNodeChore":
                    node.ChoreSymbol = ReadHandle(r);
                    r.U32(); // priority
                    r.Byte(); // looping
                    ReadNodeBase(r.Block(), node);
                    break;
                default:
                    ReadNodeBase(r.Block(), node);
                    ReadNodeTail(r, layout, node);
                    break;
            }
            aligned = r.Left < 8 || layout.NodeClasses.Contains(r.Peek64());
        }
        catch (DlogFormatException)
        {
            node.IsComplete = false;
            r.Pos = Math.Max(r.Pos, start + 8);
        }
        return node;
    }

    private static void ReadNodeTail(Reader r, Layout layout, DlogNode node)
    {
        switch (node.ClassName)
        {
            case "DlgNodeStart":
                if (layout.ProductionProps)
                    r.Block();
                break;
            case "DlgNodeText":
            {
                var proxy = r.Block();
                node.LineIds = new[] { proxy.U32() };
                break;
            }
            case "DlgNodeChoices":
            {
                var choices = ReadWrappedChildSet(r.Block(), layout);
                var pre = ReadWrappedChildSet(r.Block(), layout);
                var post = ReadWrappedChildSet(r.Block(), layout);
                node.Children = pre.Concat(choices).Concat(post).ToList();
                break;
            }
            case "DlgNodeConditional":
                node.Children = ReadWrappedChildSet(r.Block(), layout);
                break;
            case "DlgNodeSequence":
                node.Children = ReadWrappedChildSet(r.Block(), layout);
                r.U32(); // playback mode
                r.U32(); // lifetime mode
                r.Block(); // element use criteria
                break;
            case "DlgNodeParallel":
                node.Children = ReadWrappedChildSet(r.Block(), layout);
                r.Block(); // element use criteria
                break;
            case "DlgNodeJump":
                ReadJump(r, node);
                break;
            case "DlgNodeIdle":
                r.Block(); // idle chore
                for (int i = 0; i < 5; i++)
                    r.U32();
                break;
            case "DlgNodeWait":
            case "DlgNodeLogic":
                r.Block(); // conditions / rule
                break;
            case "DlgNodeScript":
                node.Script = ReadString(r);
                r.Byte(); // blocking
                r.Byte(); // execute on retire
                break;
            case "DlgNodeNotes":
                node.Script = ReadString(r);
                break;
            case "DlgNodeCancelChoices":
                r.U32();
                break;
            case "DlgNodeStoryBoard":
                r.U64();
                break;
            case "DlgNodeStats":
                r.U32();
                r.Block();
                r.Block();
                r.Block();
                break;
            case "DlgNodeExit":
            case "DlgNodeMarker":
                break;
            default:
                throw new DlogFormatException($"No layout for {node.ClassName}.");
        }
    }

    private static void ReadJump(Reader r, DlogNode node)
    {
        ReadLink(r); // the link is a placeholder; jumps resolve by name
        ulong targetName = r.U64();
        // Target class, behaviour, choice transparency, visibility, then the other-dialog handle.
        var values = new List<uint>();
        while (values.Count < 8 && !(r.Peek32(0) == 12 && values.Count >= 3))
            values.Add(r.U32());
        ulong otherDialog = r.Block().U64();
        node.Jump = new DlogJump(
            values.Count > 0 ? (DlogJumpTarget)values[0] : DlogJumpTarget.ToName,
            values.Count > 1 ? (DlogJumpBehaviour)values[1] : DlogJumpBehaviour.JumpAndExecute,
            targetName, otherDialog);
    }

    // Exchange: entries (id, type), then collections of lines keyed by entry id.
    private static void ReadExchangeTail(Reader r, Layout layout, DlogNode node)
    {
        var entries = r.Block();
        uint count = entries.U32();
        var order = new List<uint>();
        for (int i = 0; i < count && entries.Left >= 8; i++)
        {
            uint id = entries.U32();
            entries.U32(); // type
            order.Add(id);
        }

        var lines = new Dictionary<uint, uint>();
        while (r.Left >= 8 && !layout.NodeClasses.Contains(r.Peek64()))
        {
            r.Block(); // next unique id
            var map = r.Block();
            try
            {
                uint n = map.U32();
                for (int i = 0; i < n; i++)
                {
                    uint key = map.U32();
                    // A line is its unique-id block, [object id block], then the 4-byte language proxy.
                    int blocks = 0;
                    Reader last;
                    do
                    {
                        last = map.Block();
                        blocks++;
                    } while (last.Left != 4 || blocks < 2);
                    lines.TryAdd(key, last.U32());
                }
            }
            catch (DlogFormatException)
            {
                // Not a line collection (notes); its block was already skipped.
            }
            if (r.Left <= 1)
                break;
        }
        node.LineIds = order.Where(lines.ContainsKey).Select(k => lines[k]).ToList();
    }

    private static void ReadNodeBase(Reader b, DlogNode node)
    {
        node.Id = ReadObjId(b);
        b.Block(); // visibility conditions
        b.Block(); // object props
        // Older games put a unique-id block here; the remaining fields are 64 bytes.
        while (b.Left > 64)
            b.Block();
        node.PrevId = ReadLink(b);
        node.NextId = ReadLink(b);
        node.NameSymbol = b.U64();
        b.U32(); // flags
        b.U32(); // chain context type
    }

    private static List<DlogChild> ReadWrappedChildSet(Reader b, Layout layout) => ReadChildSet(b.Block(), layout);

    private static List<DlogChild> ReadChildSet(Reader b, Layout layout)
    {
        uint count = b.U32();
        var children = new List<DlogChild>();
        for (int i = 0; i < count && b.Left >= 12; i++)
        {
            ulong hash = b.U64();
            string className = layout.Classes.GetValueOrDefault(hash) ?? $"0x{hash:X16}";
            var child = ReadChild(b.Block(), className);
            children.Add(child);
            switch (className)
            {
                case "DlgChoice":
                    child.Conditions = ReadConditionClasses(b.Block(), layout);
                    break;
                case "DlgNodeSequence::Element":
                    b.U32(); // repeat
                    b.U32(); // play position
                    break;
                case "DlgNodeStats::Cohort":
                    b.Block();
                    b.Block();
                    b.Block();
                    b.Block();
                    b.Block();
                    break;
                case "DlgFolderChild":
                case "DlgChoicesChildPre":
                case "DlgChoicesChildPost":
                case "DlgConditionalCase":
                case "DlgNodeParallel::PElement":
                    break;
                default:
                    // Unknown tail: skip to the next child of a known shape, or the end.
                    return children;
            }
        }
        return children;
    }

    // A condition set is a count, then each condition's class hash and block.
    private static IReadOnlyList<string> ReadConditionClasses(Reader b, Layout layout)
    {
        var names = new List<string>();
        try
        {
            uint count = b.U32();
            for (int i = 0; i < count && b.Left >= 12; i++)
            {
                ulong hash = b.U64();
                names.Add(layout.Classes.GetValueOrDefault(hash) ?? $"0x{hash:X16}");
                b.Block();
            }
        }
        catch (DlogFormatException)
        {
        }
        return names;
    }

    private static DlogChild ReadChild(Reader b, string className)
    {
        var head = b.Block();
        ulong id = ReadObjId(head);
        ulong headId = ReadLink(head);
        ulong name = b.U64();
        b.Block(); // visibility conditions
        var texts = PropertyTexts(b.Block());
        var kind = className switch
        {
            "DlgFolderChild" => DlogChildKind.Conversation,
            "DlgChoice" => DlogChildKind.Choice,
            "DlgChoicesChildPre" => DlogChildKind.PreChoice,
            "DlgChoicesChildPost" => DlogChildKind.PostChoice,
            "DlgConditionalCase" => DlogChildKind.Case,
            "DlgNodeSequence::Element" or "DlgNodeParallel::PElement" => DlogChildKind.Element,
            _ => DlogChildKind.Other,
        };
        return new DlogChild(id, className, kind, headId, name, texts);
    }

    // Object props hold typed property values; a choice's text is a LanguageResProxy value.
    private static IReadOnlyList<uint> PropertyTexts(Reader b)
    {
        List<uint>? ids = null;
        var span = b.Data.AsSpan(b.Pos, b.End - b.Pos);
        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(key, LanguageResProxyHash);
        int at = 0;
        while (true)
        {
            int found = span[at..].IndexOf(key);
            if (found < 0)
                break;
            int p = at + found + 8;
            if (p + 4 <= span.Length)
            {
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[p..]);
                p += 4;
                for (int i = 0; i < count && count < 64 && p + 12 <= span.Length; i++)
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(span[(p + 8)..]);
                    // Values are bare ids, or 8-byte blocks holding the id in older streams.
                    if (value == 8 && p + 16 <= span.Length)
                    {
                        value = BinaryPrimitives.ReadUInt32LittleEndian(span[(p + 12)..]);
                        p += 16;
                    }
                    else
                    {
                        p += 12;
                    }
                    if (value != 0)
                        (ids ??= new List<uint>()).Add(value);
                }
            }
            at += found + 8;
        }
        return ids ?? (IReadOnlyList<uint>)Array.Empty<uint>();
    }

    // ---- Primitives ------------------------------------------------------------

    private static ulong ReadObjId(Reader r) => r.Block().Block().U64();

    // A resource handle: a block holding the symbol of the file's name (0 for none).
    private static ulong ReadHandle(Reader r)
    {
        var handle = r.Block();
        return handle.Left >= 8 ? handle.U64() : 0;
    }

    private static ulong ReadLink(Reader r)
    {
        var link = r.Block();
        ulong id = ReadObjId(link);
        return id;
    }

    private static string ReadString(Reader r)
    {
        var b = r.Block();
        int length = (int)b.U32();
        if (length < 0 || length > b.Left)
            throw new DlogFormatException("Bad string length.");
        var text = Encoding.UTF8.GetString(b.Data, b.Pos, length);
        return text;
    }

    private static bool LooksLikeFolders(Reader r, int at) =>
        r.Peek32At(at) is < 100_000 && r.Peek32At(at + 4) == 16 && r.Peek32At(at + 8) == 12;

    private static bool LooksLikeProductionProps(Reader r)
    {
        int at = r.Pos;
        if (r.Peek32At(at) is not { } props || props < 4 || at + (long)props > r.End)
            return false;
        int jiraAt = at + (int)props;
        if (r.Peek32At(jiraAt) is not { } jira || jira < 4 || jiraAt + (long)jira >= r.End)
            return false;
        int flag = jiraAt + (int)jira;
        return r.Data[flag] is (byte)'0' or (byte)'1' && LooksLikeFolders(r, flag + 1);
    }

    // The next offset holding a node class hash followed by a readable node.
    private static int FindNextNode(Reader r, Layout layout, int from)
    {
        for (int p = from; p + 12 <= r.End; p++)
        {
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(r.Data.AsSpan(p));
            if (!layout.NodeClasses.Contains(hash))
                continue;
            var check = new Reader(r.Data, p, r.End);
            check.U64();
            try
            {
                string name = layout.Classes[hash];
                if (name == "DlgNodeExchange")
                {
                    check.U32();
                    check.Block();
                }
                else if (name == "DlgNodeChore")
                {
                    check.Block();
                    check.U32();
                    check.Byte();
                }
                ReadNodeBase(check.Block(), new DlogNode(name, p));
                return p;
            }
            catch (DlogFormatException)
            {
            }
        }
        return -1;
    }

    /// <summary>A bounded little-endian cursor; <see cref="Block"/> returns a reader over a size-prefixed block.</summary>
    private sealed class Reader
    {
        public Reader(byte[] data, int pos, int end)
        {
            Data = data;
            Pos = pos;
            End = Math.Min(end, data.Length);
        }

        public byte[] Data { get; }
        public int Pos { get; set; }
        public int End { get; }
        public int Left => End - Pos;

        public uint U32()
        {
            if (Pos + 4 > End)
                throw new DlogFormatException($"Unexpected end of data at 0x{Pos:X}.");
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        public ulong U64()
        {
            if (Pos + 8 > End)
                throw new DlogFormatException($"Unexpected end of data at 0x{Pos:X}.");
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(Pos));
            Pos += 8;
            return v;
        }

        public byte Byte()
        {
            if (Pos >= End)
                throw new DlogFormatException($"Unexpected end of data at 0x{Pos:X}.");
            return Data[Pos++];
        }

        public Reader Block()
        {
            int start = Pos;
            uint size = U32();
            if (size < 4 || start + (long)size > End)
                throw new DlogFormatException($"Bad block size {size} at 0x{start:X}.");
            Pos = start + (int)size;
            return new Reader(Data, start + 4, start + (int)size);
        }

        public uint? Peek32(int offset) => Peek32At(Pos + offset);

        public uint? Peek32At(int at) =>
            at >= 0 && at + 4 <= End ? BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(at)) : null;

        public ulong Peek64() => Pos + 8 <= End ? BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(Pos)) : 0;
    }
}
