using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>When a chore resource plays: seconds into the chore, and whether it loops in that span.</summary>
public readonly record struct ChoreBlock(float Start, float End, bool Looping, float Scale);

/// <summary>A key of a chore curve: its value at a time (seconds into the chore), and how it eases.</summary>
public readonly record struct ChoreCurveKey(float Time, float Value, int TangentMode);

/// <summary>
/// A float curve a chore keys for one of its resources (KeyframedValue&lt;float&gt;): how much
/// the resource contributes, or where in the resource the chore is at each moment.
/// </summary>
public sealed class ChoreCurve
{
    private const int Stepped = 1, Flat = 4;

    public ChoreCurve(IReadOnlyList<ChoreCurveKey> keys) => Keys = keys;

    public IReadOnlyList<ChoreCurveKey> Keys { get; }

    /// <summary>
    /// The value at <paramref name="time"/>, holding the end keys beyond them. Flat keys ease
    /// in and out, stepped ones hold until the next key, others run in a straight line.
    /// </summary>
    public float Evaluate(float time)
    {
        if (Keys.Count == 0)
            return 0;
        if (time <= Keys[0].Time)
            return Keys[0].Value;
        for (int i = 1; i < Keys.Count; i++)
        {
            var (a, b) = (Keys[i - 1], Keys[i]);
            if (time > b.Time)
                continue;
            if (a.TangentMode == Stepped || b.Time <= a.Time)
                return a.Value;
            float t = (time - a.Time) / (b.Time - a.Time);
            if (a.TangentMode == Flat || b.TangentMode == Flat)
                t = t * t * (3 - 2 * t);
            return a.Value + (b.Value - a.Value) * t;
        }
        return Keys[^1].Value;
    }
}

/// <summary>A key of a chore's transform track: the rotation and position at a time, and how it eases.</summary>
public readonly record struct ChoreTransformKey(float Time, Quaternion Rotation, Vector3 Position, int TangentMode);

/// <summary>
/// An agent's transform keyed in a chore (KeyframedValue&lt;Transform&gt;): where a camera rig,
/// for one, moves over the chore, relative to what it is attached to.
/// </summary>
public sealed class ChoreTransformTrack
{
    public ChoreTransformTrack(IReadOnlyList<ChoreTransformKey> keys) => Keys = keys;

    public IReadOnlyList<ChoreTransformKey> Keys { get; }

    /// <summary>The transform at <paramref name="time"/>, eased between keys like <see cref="ChoreCurve"/>.</summary>
    public (Quaternion Rotation, Vector3 Position) Evaluate(float time)
    {
        float t = 0;
        var (a, b) = (Keys[0], Keys[0]);
        if (time > Keys[0].Time)
        {
            a = b = Keys[^1];
            for (int i = 1; i < Keys.Count; i++)
            {
                if (time > Keys[i].Time)
                    continue;
                (a, b) = (Keys[i - 1], Keys[i]);
                if (a.TangentMode != 1 && b.Time > a.Time)
                {
                    t = (time - a.Time) / (b.Time - a.Time);
                    if (a.TangentMode == 4 || b.TangentMode == 4)
                        t = t * t * (3 - 2 * t);
                }
                break;
            }
        }
        return (Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, t)), Vector3.Lerp(a.Position, b.Position, t));
    }
}

/// <summary>
/// One resource a chore plays: an animation, a voice line (<c>LanguageRes</c>), a nested
/// chore and so on, named by the symbol of its file name, with the spans it plays in.
/// </summary>
/// <param name="Name">Symbol of the resource's name (<c>sk63_ajStandA_toArmsX.anm</c>).</param>
/// <param name="Type">Type hash of the resource (see <see cref="ChoreFile.AnimationType"/>), or 0 for none.</param>
/// <param name="Length">The resource's own length in seconds.</param>
/// <param name="Priority">Higher priorities win over lower ones on the same bones.</param>
/// <param name="Embedded">True when the resource's data lives inside the chore rather than in a file.</param>
public sealed record ChoreResource(
    ulong Name, ulong Type, float Length, int Priority, uint Flags, bool Embedded, bool Enabled, bool NoPose,
    IReadOnlyList<ChoreBlock> Blocks)
{
    /// <summary>The chore stored inside this resource, for an embedded chore.</summary>
    public ChoreFile? EmbeddedChore { get; init; }

    /// <summary>
    /// Where in the resource the chore is at each moment, as a fraction of <see cref="Length"/>
    /// (the chore can slow a resource down, speed it up or hold one frame of it), or null.
    /// </summary>
    public ChoreCurve? TimeCurve { get; init; }

    /// <summary>How much the resource counts at each moment (0 to 1), or null for fully.</summary>
    public ChoreCurve? Contribution { get; init; }

    /// <summary>
    /// For a resource that keys its agent itself (a camera), the "Field of View" the chore gives
    /// it over time (degrees), or null.
    /// </summary>
    public ChoreCurve? FieldOfView { get; init; }

    /// <summary>For a resource that keys its agent itself, the agent's transform over time, or null.</summary>
    public ChoreTransformTrack? Transform { get; init; }

    /// <summary>The resource groups the resource is limited to, or null for all.</summary>
    public IReadOnlyDictionary<ulong, float>? GroupInclude { get; init; }
}

/// <summary>A character or object a chore drives, with the indices of its resources.</summary>
public sealed record ChoreAgent(string Name, uint Flags, IReadOnlyList<int> Resources);

/// <summary>
/// Reads a <c>.chore</c> file (MSV6), the timeline the engine plays a cutscene or a
/// dialogue exchange from: a list of resources, each with its play spans, and the agents
/// that own them. Only the parts needed to replay animations and voice timing are kept.
/// An embedded resource is followed by its data, tagged with its type: an embedded chore
/// is read in full, and other data (procedural look-ats and such) is skipped by finding
/// the next resource by its shape.
/// </summary>
public sealed class ChoreFile
{
    public static readonly ulong AnimationType = TelltaleCrc64.HashSymbol("Animation");
    public static readonly ulong ChoreType = TelltaleCrc64.HashSymbol("Chore");
    public static readonly ulong LanguageResType = TelltaleCrc64.HashSymbol("LanguageRes");

    // Embedded data seen so far is a few dozen bytes; anything further away is a misread.
    private const int MaxEmbeddedSkip = 16 * 1024;

    public string Name { get; }
    public float Length { get; }
    public string SceneFile { get; }
    public IReadOnlyList<ChoreResource> Resources { get; }
    public IReadOnlyList<ChoreAgent> Agents { get; }

    private ChoreFile(string name, float length, string sceneFile, IReadOnlyList<ChoreResource> resources, IReadOnlyList<ChoreAgent> agents)
    {
        Name = name;
        Length = length;
        SceneFile = sceneFile;
        Resources = resources;
        Agents = agents;
    }

    /// <summary>The agent that owns resource <paramref name="index"/>, or null.</summary>
    public ChoreAgent? AgentOf(int index) => Agents.FirstOrDefault(a => a.Resources.Contains(index));

    /// <summary>Parses a chore; returns false with a reason for anything else or an unknown layout.</summary>
    public static bool TryRead(byte[] data, out ChoreFile? chore, out string? error)
    {
        chore = null;
        if (!MetaStreamHeader.TryParse(data, out var header, out error))
            return false;
        if (header!.Classes.Count == 0 || header.Classes[0].TypeHash != ChoreType)
        {
            error = "Not a chore.";
            return false;
        }
        int end = (int)Math.Min(data.Length, (long)header.DefaultSectionOffset + header.DefaultSectionSize);
        foreach (var layout in Layouts)
        {
            var r = new Reader(data, header.DefaultSectionOffset, end, layout);
            string stage = "header";
            try
            {
                chore = ReadChore(ref r, header.DefaultSectionOffset, ref stage);
                error = null;
                return true;
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // Report how the newest layout failed; older ones are fallbacks.
                error ??= $"Unrecognised chore layout: {e.Message} in {stage}.";
            }
        }
        return false;
    }

    /// <summary>
    /// The fields that differ between engine generations: newer chores (Michonne on) hold
    /// a localization block where older ones hold a symbol, block the auto-act status, and
    /// may add walk paths, a resource flag and an actor binding per agent.
    /// </summary>
    private readonly record struct Layout(bool LocalizationBlock, bool WalkPaths, int ResourceFlags, bool StatusBlock, int AgentBlocks);

    private static readonly Layout[] Layouts = BuildLayouts();

    private static Layout[] BuildLayouts()
    {
        var layouts = new List<Layout>();
        foreach (bool localization in new[] { true, false })
            foreach (bool walkPaths in new[] { true, false })
                foreach (bool status in new[] { true, false })
                    foreach (int flags in new[] { 8, 7 })
                        foreach (int agentBlocks in new[] { 3, 2 })
                            layouts.Add(new Layout(localization, walkPaths, flags, status, agentBlocks));
        return layouts.ToArray();
    }

    private static ChoreFile ReadChore(ref Reader r, int sectionStart, ref string stage)
    {
        var name = r.StringBlock();
        r.U32(); // flags
        float length = r.F32();
        int resourceCount = r.I32(), agentCount = r.I32();
        if (resourceCount is < 0 or > 100_000 || agentCount is < 0 or > 10_000)
            throw new FormatException("implausible counts");
        r.Block(); // editor properties
        var scene = r.StringBlock();
        r.U32(); // render delay
        if (r.Layout.LocalizationBlock)
            r.Block(); // localization sync
        else
            r.U64();
        r.Block(); // dependencies
        r.Bool(); // tool props
        if (r.Layout.WalkPaths)
            r.Block();

        var resources = new List<ChoreResource>(resourceCount);
        for (int i = 0; i < resourceCount; i++)
        {
            stage = $"resource {i} at {r.Pos - sectionStart}";
            var resource = ReadResource(ref r);
            if (resource.Embedded)
            {
                // The data's type, twice; a chore follows in full.
                ulong tag = r.U64At(r.Pos);
                if (tag == r.U64At(r.Pos + 8))
                    r.Pos += 16;
                if (tag == ChoreType)
                    resource = resource with { EmbeddedChore = ReadChore(ref r, sectionStart, ref stage) };
                else
                    SkipEmbedded(ref r, i == resourceCount - 1 ? resourceCount : null);
            }
            resources.Add(resource);
        }

        var agents = new List<ChoreAgent>(agentCount);
        for (int i = 0; i < agentCount; i++)
        {
            stage = $"agent {i} at {r.Pos - sectionStart}";
            agents.Add(ReadAgent(ref r, resourceCount));
        }
        return new ChoreFile(name, length, scene, resources, agents);
    }

    private static ChoreResource ReadResource(ref Reader r)
    {
        int version = r.I32();
        if (version is < 0 or > 16)
            throw new FormatException("bad resource version");
        ulong name = r.U64();
        float length = r.F32();
        int priority = r.I32();
        uint flags = r.U32();
        r.StringBlock(); // resource group

        // Handle: the resource's name symbol and, when it has one, its type.
        var (handleStart, handleEnd) = r.Block();
        ulong type = handleEnd - handleStart >= 16 ? r.U64At(handleStart + 8) : 0;

        var (controlStart, controlEnd) = r.Block();
        var (blocksStart, blocksEnd) = r.Block();
        int count = (int)r.U32At(blocksStart);
        if (count < 0 || 4 + count * 13 != blocksEnd - blocksStart)
            throw new FormatException("bad block list");
        var blocks = new ChoreBlock[count];
        for (int k = 0; k < count; k++)
        {
            int at = blocksStart + 4 + k * 13;
            blocks[k] = new ChoreBlock(r.F32At(at), r.F32At(at + 4), r.BoolAt(at + 8), r.F32At(at + 9));
        }

        bool noPose = r.Bool(), embedded = r.Bool(), enabled = r.Bool();
        for (int k = 3; k < r.Layout.ResourceFlags; k++)
            r.Bool(); // agent resource, view graphs, view empty graphs, view properties, view groups
        r.Block(); // resource properties
        var (includeStart, includeEnd) = r.Block();
        if (r.Layout.StatusBlock)
            r.Block(); // auto-act status
        else
            r.U32();
        var control = ReadControlValues(r, controlStart, controlEnd);
        var include = ReadGroupInclude(r, includeStart, includeEnd);
        return new ChoreResource(name, type, length, priority, flags, embedded, enabled, noPose, blocks)
        {
            TimeCurve = control.Time,
            Contribution = control.Contribution,
            FieldOfView = control.FieldOfView,
            Transform = control.Transform,
            GroupInclude = include,
        };
    }

    // The control animation is a small animation of keyframed values: "time" and
    // "contribution" for any resource, and for a resource that keys an agent itself (a camera)
    // its transform and values such as "Field of View". It holds a table of value types with
    // a count each, then the values of each type in turn. Each value:
    //   [u32 size [u32 size, u64 name, u32 flags]] min, max,
    //   [u32 size, u32 count, count x (f32 time, bool, u32 tangent mode, value)]
    // A type this reader does not know ends the reading with what it has so far.
    private static ControlValues ReadControlValues(Reader r, int start, int end)
    {
        var values = new ControlValues();
        try
        {
            int table = start + 4, types = 0;
            for (; table + 12 <= end; table++)
            {
                types = (int)r.U32At(table - 4);
                if (types is > 0 and <= 16 && Enumerable.Range(0, types).All(i =>
                        table + 12 * (i + 1) <= end && ValueSizes.ContainsKey(r.U64At(table + 12 * i))))
                    break;
            }
            if (table + 12 > end)
                return values;
            int at = table + 12 * types;
            for (int i = 0; i < types; i++)
            {
                ulong type = r.U64At(table + 12 * i);
                int count = (int)r.U32At(table + 12 * i + 8);
                for (int v = 0; v < count; v++)
                {
                    int header = (int)r.U32At(at);
                    if (header is < 16 or > 64 || r.U32At(at + 4) != header - 4)
                        return values;
                    ulong valueName = r.U64At(at + 8);
                    at += header;
                    at += ValueSize(r, type, at);                     // min
                    at += ValueSize(r, type, at);                     // max
                    int size = (int)r.U32At(at), keyCount = (int)r.U32At(at + 4);
                    if (keyCount < 0 || size < 8 || size > end - at)
                        return values;
                    if (type == KeyframedFloatType && size == 8 + 13 * keyCount && keyCount > 0)
                    {
                        var keys = new ChoreCurveKey[keyCount];
                        for (int k = 0; k < keyCount; k++)
                        {
                            int key = at + 8 + 13 * k;
                            keys[k] = new ChoreCurveKey(r.F32At(key), r.F32At(key + 9), (int)r.U32At(key + 5));
                        }
                        if (valueName == TimeValue)
                            values.Time = new ChoreCurve(keys);
                        else if (valueName == ContributionValue)
                            values.Contribution = new ChoreCurve(keys);
                        else if (valueName == FieldOfViewValue)
                            values.FieldOfView = new ChoreCurve(keys);
                    }
                    else if (type == KeyframedTransformType && keyCount > 0)
                    {
                        var keys = new ChoreTransformKey[keyCount];
                        int key = at + 8;
                        for (int k = 0; k < keyCount; k++)
                        {
                            int value = key + 9;                     // time, bool, tangent mode
                            keys[k] = new ChoreTransformKey(r.F32At(key),
                                new Quaternion(r.F32At(value + 4), r.F32At(value + 8), r.F32At(value + 12), r.F32At(value + 16)),
                                new Vector3(r.F32At(value + 20), r.F32At(value + 24), r.F32At(value + 28)),
                                (int)r.U32At(key + 5));
                            key = value + ValueSize(r, type, value);
                        }
                        values.Transform ??= new ChoreTransformTrack(keys);
                    }
                    at += size;
                }
            }
        }
        catch (FormatException)
        {
        }
        return values;
    }

    private sealed class ControlValues
    {
        public ChoreCurve? Time, Contribution, FieldOfView;
        public ChoreTransformTrack? Transform;
    }

    // Bytes one value of a keyframed type takes; a transform is a sized block.
    private static int ValueSize(Reader r, ulong type, int at) =>
        ValueSizes[type] is > 0 and var size ? size
        : r.U32At(at) is var block and >= 4 and <= 64 ? (int)block : throw new FormatException("bad transform");

    private static readonly ulong KeyframedTransformType = TelltaleCrc64.HashSymbol("KeyframedValue<Transform>");
    private static readonly Dictionary<ulong, int> ValueSizes = new()
    {
        [TelltaleCrc64.HashSymbol("KeyframedValue<float>")] = 4,
        [TelltaleCrc64.HashSymbol("KeyframedValue<bool>")] = 1,
        [TelltaleCrc64.HashSymbol("KeyframedValue<int>")] = 4,
        [TelltaleCrc64.HashSymbol("KeyframedValue<Symbol>")] = 8,
        [TelltaleCrc64.HashSymbol("KeyframedValue<Vector2>")] = 8,
        [TelltaleCrc64.HashSymbol("KeyframedValue<Vector3>")] = 12,
        [TelltaleCrc64.HashSymbol("KeyframedValue<Quaternion>")] = 16,
        [TelltaleCrc64.HashSymbol("KeyframedValue<Color>")] = 16,
        [TelltaleCrc64.HashSymbol("KeyframedValue<Transform>")] = 0,
    };
    private static readonly ulong FieldOfViewValue = TelltaleCrc64.HashSymbol("Field of View");

    // mResourceGroupInclude: after the block size, (u64 group symbol, f32 weight) per group.
    private static IReadOnlyDictionary<ulong, float>? ReadGroupInclude(Reader r, int start, int end)
    {
        int bytes = end - start - 4;
        if (bytes <= 0 || bytes % 12 != 0)
            return null;
        var groups = new Dictionary<ulong, float>(bytes / 12);
        for (int at = start + 4; at < end; at += 12)
            groups[r.U64At(at)] = r.F32At(at + 8);
        return groups;
    }

    private static readonly ulong KeyframedFloatType = TelltaleCrc64.HashSymbol("KeyframedValue<float>");
    private static readonly ulong TimeValue = TelltaleCrc64.HashSymbol("time");
    private static readonly ulong ContributionValue = TelltaleCrc64.HashSymbol("contribution");

    private static ChoreAgent ReadAgent(ref Reader r, int resourceCount)
    {
        var name = r.StringBlock();
        uint flags = r.U32();
        var (start, end) = r.Block();
        int count = (int)r.U32At(start);
        if (count < 0 || 4 + count * 4 != end - start)
            throw new FormatException("bad agent resource list");
        var resources = new int[count];
        for (int k = 0; k < count; k++)
        {
            resources[k] = (int)r.U32At(start + 4 + k * 4);
            if (resources[k] < 0 || resources[k] >= resourceCount)
                throw new FormatException("agent resource out of range");
        }
        for (int k = 0; k < r.Layout.AgentBlocks; k++)
            r.Block(); // attachment, actor binding, enabled rule
        return new ChoreAgent(name, flags, resources);
    }

    // Moves past an embedded resource's data to where the next resource (or, after the
    // last one, the first agent of a chore with that many resources) parses.
    private static void SkipEmbedded(ref Reader r, int? agentsOf)
    {
        int from = r.Pos, limit = Math.Min(r.End, from + MaxEmbeddedSkip);
        for (int at = from; at < limit; at++)
        {
            var probe = r.At(at);
            try
            {
                if (agentsOf is { } count)
                {
                    if (ReadAgent(ref probe, count).Name.Length == 0)
                        continue;
                }
                else
                {
                    ReadResource(ref probe);
                }
                r.Pos = at;
                return;
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
            }
        }
        if (agentsOf is not null && from == r.End)
            return;
        throw new FormatException("embedded resource data not understood");
    }

    private struct Reader
    {
        private readonly byte[] _data;

        public Reader(byte[] data, int pos, int end, Layout layout)
        {
            _data = data;
            Pos = pos;
            End = end;
            Layout = layout;
        }

        public int Pos;
        public int End { get; }
        public Layout Layout { get; }

        public Reader At(int pos) => new(_data, pos, End, Layout);

        private void Need(int count)
        {
            if (count < 0 || Pos + count > End)
                throw new FormatException("ran past the end");
        }

        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Pos)); Pos += 4; return v; }
        public int I32() => (int)U32();
        public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(Pos)); Pos += 8; return v; }
        public float F32() => BitConverter.Int32BitsToSingle(I32());

        public bool Bool()
        {
            Need(1);
            byte b = _data[Pos++];
            return b switch
            {
                (byte)'0' => false,
                (byte)'1' => true,
                _ => throw new FormatException("bad bool"),
            };
        }

        public uint U32At(int at) => at + 4 <= End ? BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at)) : throw new FormatException("ran past the end");
        public ulong U64At(int at) => at + 8 <= End ? BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(at)) : throw new FormatException("ran past the end");
        public float F32At(int at) => BitConverter.Int32BitsToSingle((int)U32At(at));
        public bool BoolAt(int at) => at < End && _data[at] == (byte)'1';

        /// <summary>A size-prefixed block: returns where its contents start and end, and moves past it.</summary>
        public (int Start, int End) Block()
        {
            int start = Pos;
            uint size = U32();
            if (size < 4 || start + size > End)
                throw new FormatException("bad block size");
            Pos = start + (int)size;
            return (start + 4, Pos);
        }

        public string StringBlock()
        {
            var (start, end) = Block();
            uint length = U32At(start);
            if (length != end - start - 4)
                throw new FormatException("bad string");
            return System.Text.Encoding.UTF8.GetString(_data, start + 4, (int)length);
        }
    }
}
