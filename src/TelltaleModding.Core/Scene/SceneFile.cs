using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>One named object of a scene: where it starts, what it is attached to, and its properties.</summary>
public sealed class SceneAgent
{
    /// <summary>Position in the scene's agent list.</summary>
    public required int Index { get; init; }

    public required string Name { get; init; }

    /// <summary>The agent's own property set; its parents are the <c>.prop</c> files it inherits from.</summary>
    public required ScenePropertySet Properties { get; init; }

    /// <summary>Name of the agent this one is attached to, or null at the scene root.</summary>
    public string? ParentName { get; init; }

    /// <summary>Symbol of the parent's skeleton node this agent rides on (0 for the parent's origin).</summary>
    public ulong ParentNode { get; init; }

    /// <summary>Start position relative to the parent.</summary>
    public Vector3 LocalPosition { get; init; }

    /// <summary>Start rotation relative to the parent.</summary>
    public Quaternion LocalRotation { get; init; } = Quaternion.Identity;

    /// <summary>"Scene: Start Visibility" (true when the scene does not say).</summary>
    public bool StartVisible { get; init; } = true;

    /// <summary>The parent agent, when it exists in the same scene.</summary>
    public SceneAgent? Parent { get; internal set; }

    /// <summary>Local transform (row-vector convention: <c>v' = v · M</c>).</summary>
    public Matrix4x4 LocalMatrix => Matrix4x4.CreateFromQuaternion(LocalRotation) * Matrix4x4.CreateTranslation(LocalPosition);

    public override string ToString() => Name;
}

/// <summary>
/// A parsed <c>.scene</c>: the agents of a location (environment meshes, props, characters,
/// cameras, lights, walk boxes…) with their start transforms and property sets.
/// </summary>
/// <remarks>
/// Default section (MSV5 and MSV6 alike):
/// <c>bool active, block(String name), block(DCArray&lt;Handle&lt;Scene&gt;&gt; referenced scenes),
/// block(u32 count, { block(String agent name), block(PropertySet) }…)</c>, then lighting and
/// audio data this reader ignores. An agent's transform lives in its property set: as
/// <c>"Scene: Location Info"</c> (LocationInfo: attached agent, node, Transform) in the
/// Definitive Series, or as separate <c>"Scene: Start Position"</c> / <c>"Scene: Start Quaterion"</c>
/// (sic) / <c>"Scene: Attach Agent"</c> / <c>"Scene: Attach Node"</c> keys in older games.
/// </remarks>
public sealed class SceneFile
{
    /// <summary>Property keys a scene agent's set may hold.</summary>
    public static class Keys
    {
        public static readonly ulong LocationInfo = TelltaleCrc64.HashSymbol("Scene: Location Info");
        public static readonly ulong StartPosition = TelltaleCrc64.HashSymbol("Scene: Start Position");
        public static readonly ulong StartRotation = TelltaleCrc64.HashSymbol("Scene: Start Quaterion");
        public static readonly ulong StartVisibility = TelltaleCrc64.HashSymbol("Scene: Start Visibility");
        public static readonly ulong AttachAgent = TelltaleCrc64.HashSymbol("Scene: Attach Agent");
        public static readonly ulong AttachNode = TelltaleCrc64.HashSymbol("Scene: Attach Node");
    }

    private Dictionary<string, SceneAgent>? _byName;

    /// <summary>The scene's own name (e.g. <c>adv_bellTower.scene</c>).</summary>
    public string Name { get; private init; } = "";

    public MetaStreamFormat Format { get; private init; }

    /// <summary>Agents in stored order. The first is usually the scene's own agent.</summary>
    public IReadOnlyList<SceneAgent> Agents { get; private init; } = Array.Empty<SceneAgent>();

    /// <summary>Symbols of other scenes this one references.</summary>
    public IReadOnlyList<ulong> ReferencedScenes { get; private init; } = Array.Empty<ulong>();

    /// <summary>Finds an agent by name (case-insensitive).</summary>
    public SceneAgent? Find(string name)
    {
        _byName ??= Agents.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return _byName.GetValueOrDefault(name);
    }

    /// <summary>
    /// World transform of an agent: its local transform composed down the parent chain
    /// (row-vector convention). <paramref name="nodeOffset"/> can supply the transform of
    /// the parent's node an agent rides on (relative to the parent), e.g. a skeleton bone
    /// for a prop held in a hand; by default agents ride on their parent's origin.
    /// </summary>
    public static Matrix4x4 WorldTransform(SceneAgent agent, Func<SceneAgent, Matrix4x4?>? nodeOffset = null)
    {
        var world = agent.LocalMatrix;
        var seen = new HashSet<SceneAgent> { agent };
        for (var child = agent; child.Parent is { } parent && seen.Add(parent); child = parent)
        {
            if (child.ParentNode != 0 && nodeOffset?.Invoke(child) is { } node)
                world *= node;
            world *= parent.LocalMatrix;
        }
        return world;
    }

    /// <summary>Parses a .scene file's bytes, throwing on malformed input.</summary>
    public static SceneFile Load(byte[] data) =>
        TryLoad(data, out var scene, out var error) ? scene! : throw new InvalidDataException(error);

    /// <summary>Attempts to parse a .scene file's bytes.</summary>
    public static bool TryLoad(byte[] data, out SceneFile? scene, out string? error)
    {
        scene = null;
        if (!MetaStreamHeader.TryParse(data, out var header, out error, isCompleteFile: false))
            return false;
        int p = header!.DefaultSectionOffset;
        int end = (int)Math.Min(data.Length, (long)p + header.DefaultSectionSize);

        try
        {
            p += 1; // bool (active)
            if (!ReadBlock(data, ref p, end, out int nameStart, out int nameEnd) ||
                !ScenePropertySet.TryReadString(data, nameStart, nameEnd, out var sceneName, out _))
                throw new InvalidDataException("scene name not found");

            if (!ReadBlock(data, ref p, end, out int refStart, out int refEnd))
                throw new InvalidDataException("referenced scene list not found");
            var referenced = ReadHandleArray(data, refStart, refEnd);

            if (!ReadBlock(data, ref p, end, out int listStart, out int listEnd) || listEnd - listStart < 4)
                throw new InvalidDataException("agent list not found");
            int count = (int)ScenePropertySet.U32(data, listStart);
            if (count is < 0 or > 100_000)
                throw new InvalidDataException($"implausible agent count {count}");

            var agents = new List<SceneAgent>(count);
            int q = listStart + 4;
            for (int i = 0; i < count; i++)
            {
                if (!ReadBlock(data, ref q, listEnd, out int agentNameStart, out int agentNameEnd) ||
                    !ScenePropertySet.TryReadString(data, agentNameStart, agentNameEnd, out var agentName, out _))
                    throw new InvalidDataException($"agent {i}: name not found");
                if (!ReadBlock(data, ref q, listEnd, out int propsStart, out int propsEnd))
                    throw new InvalidDataException($"agent {i} ({agentName}): property set not found");
                var props = ScenePropertySet.TryParse(data, propsStart, propsEnd)
                    ?? throw new InvalidDataException($"agent {i} ({agentName}): unreadable property set");
                agents.Add(CreateAgent(i, agentName!, props));
            }

            var byName = new Dictionary<string, SceneAgent>(StringComparer.OrdinalIgnoreCase);
            foreach (var agent in agents)
                byName.TryAdd(agent.Name, agent);
            foreach (var agent in agents)
                if (agent.ParentName is { } parentName && byName.TryGetValue(parentName, out var parent) && parent != agent)
                    agent.Parent = parent;

            scene = new SceneFile
            {
                Name = sceneName!,
                Format = header.Format,
                Agents = agents,
                ReferencedScenes = referenced,
                _byName = byName,
            };
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static SceneAgent CreateAgent(int index, string name, ScenePropertySet props)
    {
        string? parentName;
        ulong node;
        Vector3 position;
        Quaternion rotation;
        if (props.GetLocationInfo(Keys.LocationInfo) is { } location)
        {
            parentName = location.AttachedAgent;
            node = location.AttachedNode;
            position = location.Position;
            rotation = location.Rotation;
        }
        else
        {
            parentName = props.GetString(Keys.AttachAgent);
            node = props.GetSymbol(Keys.AttachNode) ?? 0;
            position = props.GetVector3(Keys.StartPosition) ?? Vector3.Zero;
            rotation = props.GetQuaternion(Keys.StartRotation) ?? Quaternion.Identity;
        }
        return new SceneAgent
        {
            Index = index,
            Name = name,
            Properties = props,
            ParentName = string.IsNullOrEmpty(parentName) || parentName.Equals(name, StringComparison.OrdinalIgnoreCase)
                ? null
                : parentName,
            ParentNode = node,
            LocalPosition = position,
            LocalRotation = rotation,
            StartVisible = props.GetBool(Keys.StartVisibility) ?? true,
        };
    }

    /// <summary>Reads <c>u32 size</c> (counting itself) and returns the block's content range.</summary>
    private static bool ReadBlock(byte[] data, ref int p, int end, out int contentStart, out int contentEnd)
    {
        contentStart = contentEnd = 0;
        if (p + 4 > end)
            return false;
        uint size = ScenePropertySet.U32(data, p);
        if (size < 4 || p + (long)size > end)
            return false;
        contentStart = p + 4;
        contentEnd = p + (int)size;
        p = contentEnd;
        return true;
    }

    /// <summary>Reads <c>u32 count</c> + handles (8 or 16 bytes each), keeping the name symbols.</summary>
    private static IReadOnlyList<ulong> ReadHandleArray(byte[] data, int start, int end)
    {
        if (end - start < 4)
            return Array.Empty<ulong>();
        int count = (int)ScenePropertySet.U32(data, start);
        if (count <= 0 || (end - start - 4) % count != 0)
            return Array.Empty<ulong>();
        int stride = (end - start - 4) / count;
        if (stride < 8)
            return Array.Empty<ulong>();
        var list = new ulong[count];
        for (int i = 0; i < count; i++)
            list[i] = ScenePropertySet.U64(data, start + 4 + i * stride);
        return list;
    }
}
