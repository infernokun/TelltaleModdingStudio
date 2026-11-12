using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// One animation an agent plays in a chore, placed on the chore's timeline in seconds.
/// <see cref="Groups"/> holds the resource-group weights that limit it to part of the skeleton.
/// </summary>
public sealed record PerformanceClip(string Name, float Start, float End, bool Looping, float Scale, int Priority, bool Additive,
    ResourceControl? Control = null, IReadOnlyList<IReadOnlyDictionary<ulong, float>>? Groups = null);

/// <summary>
/// How a resource plays in its chore: one block's span, its length and its curves, under
/// the <see cref="Parent"/> resource when the chore is nested.
/// </summary>
public sealed record ResourceControl(float Start, float End, float Length, float Scale,
    ChoreCurve? Time, ChoreCurve? Contribution, ResourceControl? Parent)
{
    /// <summary>True when this resource or a chore it is nested in has a time or contribution curve.</summary>
    public bool HasCurves => Time is not null || Contribution is not null || (Parent?.HasCurves ?? false);

    /// <summary>
    /// Seconds into the resource and its weight at <paramref name="time"/> in the outermost
    /// chore. Past its block it counts nothing unless <paramref name="hold"/> is set.
    /// </summary>
    public (float Time, float Weight) At(float time, bool hold)
    {
        var (t, weight) = Parent?.At(time, hold) ?? (time, 1f);
        if (weight <= 0 || t < Start)
            return (0, 0);
        if (t > End)
        {
            if (!hold)
                return (0, 0);
            t = End;
        }
        weight *= Math.Clamp(Contribution?.Evaluate(t) ?? 1, 0, 1);
        float local = Time is { } curve ? curve.Evaluate(t) * Length : (t - Start) * (Scale > 0 ? Scale : 1);
        return (local, weight);
    }
}

/// <summary>Where a voice line sits in a chore: who says it, and when.</summary>
public sealed record ChoreLine(ChoreFile Chore, int Resource, string Agent, float Start, float End);

/// <summary>
/// Replays an agent's part of a chore on its skeleton, mixed like the engine's skeleton
/// mixer. Body clips go by priority, lowest first: one priority averages by weight, then
/// covers the pose below by its largest weight. Additive clips chain from the highest
/// priority down, each in its bone's own frame.
/// </summary>
public sealed class ChorePerformance
{
    private const float Fade = 0.3f;
    private const int MaxDepth = 2;

    // Body layers by priority, lowest first.
    private readonly Layer[][] _body;
    // Additive layers, highest priority first.
    private readonly Layer[] _additive;

    private sealed record Layer(PerformanceClip Spec, AnimationPlayer Player, float Length, float HoldUntil)
    {
        // Per-bone weight from the chore's resource groups, or null for every bone.
        public IReadOnlyDictionary<ulong, float>? Mask { get; init; }

        private ResourceControl? Curves => Spec.Control is { HasCurves: true } control ? control : null;

        // Body clips hold their last frame; gestures end.
        private bool Holds => !Spec.Additive;

        public float ClipTime(float t)
        {
            float local = Curves is { } control
                ? control.At(t, Holds).Time
                : (t - Spec.Start) * (Spec.Scale > 0 ? Spec.Scale : 1);
            local = Math.Max(0, local);
            return Spec.Looping && Length > 0 ? local % Length : Math.Min(local, Length);
        }

        public float Weight(float t)
        {
            if (Curves is { } control)
                return Holds && t > HoldUntil ? 0 : control.At(t, Holds).Weight;
            float end = Math.Max(Spec.End, HoldUntil);
            if (t < Spec.Start - Fade || t > end + Fade)
                return 0;
            float fadeIn = Math.Clamp((t - Spec.Start) / Fade + 1, 0, 1);
            float fadeOut = float.IsPositiveInfinity(end) ? 1 : Math.Clamp((end - t) / Fade + 1, 0, 1);
            return Math.Min(fadeIn, fadeOut);
        }

        public float WeightOn(SkeletonBone bone, float t)
        {
            if (!Player.Animates(bone.JointSymbol))
                return 0;
            float mask = Mask is null ? 1 : Mask.GetValueOrDefault(bone.JointSymbol);
            return mask <= 0 ? 0 : Weight(t) * mask;
        }
    }

    /// <param name="clips">The agent's clips with their loaded animations (see <see cref="Collect"/>).</param>
    public ChorePerformance(MeshGeometry geometry, SkeletonFile skeleton, IEnumerable<(PerformanceClip Spec, AnimationFile Clip)> clips)
    {
        var body = new List<(PerformanceClip Spec, AnimationPlayer Player)>();
        var additive = new List<Layer>();
        foreach (var (spec, clip) in clips)
        {
            var player = new AnimationPlayer(geometry, skeleton);
            player.Load(clip, spec.Name);
            if (!player.HasMotion)
                continue;
            if (spec.Additive)
                additive.Add(new Layer(spec, player, player.Length, spec.End) { Mask = BoneMask(skeleton, spec.Groups) });
            else
                body.Add((spec, player));
        }

        // Each body clip holds until the next starts, unless the chore keys its weight.
        body.Sort((a, b) => a.Spec.Start != b.Spec.Start ? a.Spec.Start.CompareTo(b.Spec.Start) : a.Spec.Priority.CompareTo(b.Spec.Priority));
        _body = body.Select((b, i) => new Layer(b.Spec, b.Player, b.Player.Length,
                b.Spec.Looping ? b.Spec.End
                : b.Spec.Control is { HasCurves: true } || i + 1 >= body.Count ? float.PositiveInfinity
                : body[i + 1].Spec.Start + Fade) { Mask = BoneMask(skeleton, b.Spec.Groups) })
            .GroupBy(l => l.Spec.Priority)
            .OrderBy(g => g.Key)
            .Select(g => g.ToArray())
            .ToArray();
        // A stable sort keeps clips of one priority in the order the chore lists them.
        _additive = additive.OrderByDescending(a => a.Spec.Priority).ToArray();
    }

    // Per-bone weight like the engine: each group set sums weight times membership, clamped
    // to 0-1, and nested sets multiply. Null when the clip counts on every bone.
    private static IReadOnlyDictionary<ulong, float>? BoneMask(SkeletonFile skeleton, IReadOnlyList<IReadOnlyDictionary<ulong, float>>? groups)
    {
        if (groups is not { Count: > 0 })
            return null;
        var mask = new Dictionary<ulong, float>(skeleton.Bones.Count);
        bool partial = false;
        foreach (var bone in skeleton.Bones)
        {
            float weight = 1;
            foreach (var include in groups)
            {
                float sum = 0;
                if (bone.ResourceGroups is { } membership)
                    foreach (var (group, value) in include)
                        if (membership.TryGetValue(group, out float member))
                            sum += value * member;
                weight *= Math.Clamp(sum, 0, 1);
            }
            mask.TryAdd(bone.JointSymbol, weight);
            partial |= weight < 1;
        }
        return partial ? mask : null;
    }

    /// <summary>Number of clips that move this skeleton.</summary>
    public int ClipCount => _body.Sum(g => g.Length) + _additive.Length;

    /// <summary>A bone's local transform at <paramref name="time"/> in the chore, layered over <paramref name="under"/>.</summary>
    public (Vector3 Pos, Quaternion Rot) SampleLocal(SkeletonBone bone, float time, (Vector3 Pos, Quaternion Rot) under) =>
        // Skip the root, which would walk the character out of frame.
        bone.ParentIndex < 0 ? under : Mix(bone, time, under);

    /// <summary>The root bone's local transform at <paramref name="time"/>, with the motion <see cref="SampleLocal"/> leaves out.</summary>
    public (Vector3 Pos, Quaternion Rot) SampleRoot(SkeletonBone root, float time, (Vector3 Pos, Quaternion Rot) under) =>
        Mix(root, time, under);

    private (Vector3 Pos, Quaternion Rot) Mix(SkeletonBone bone, float time, (Vector3 Pos, Quaternion Rot) under)
    {
        var local = under;
        foreach (var group in _body)
        {
            float total = 0, cover = 0;
            (Vector3 Pos, Quaternion Rot) mix = default;
            foreach (var layer in group)
            {
                float w = layer.WeightOn(bone, time);
                if (w <= 0)
                    continue;
                var s = layer.Player.SampleLocal(bone, layer.ClipTime(time));
                total += w;
                cover = Math.Max(cover, w);
                mix = total == w ? s : (Vector3.Lerp(mix.Pos, s.Pos, w / total), Quaternion.Slerp(mix.Rot, s.Rot, w / total));
            }
            if (total > 0)
                local = (Vector3.Lerp(local.Pos, mix.Pos, Math.Min(cover, 1)), Quaternion.Slerp(local.Rot, mix.Rot, Math.Min(cover, 1)));
        }

        // Additive clips are rest pose plus a change; changes chain in the bone's frame.
        var restInverse = Quaternion.Inverse(bone.LocalRotation);
        var (addPos, addRot) = (Vector3.Zero, Quaternion.Identity);
        foreach (var layer in _additive)
        {
            float w = Math.Min(layer.WeightOn(bone, time), 1);
            if (w <= 0)
                continue;
            var s = layer.Player.SampleLocal(bone, layer.ClipTime(time));
            var move = Vector3.Transform(s.Pos - bone.LocalPosition, restInverse) * w;
            var turn = Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(Quaternion.Multiply(restInverse, s.Rot)), w);
            addPos += Vector3.Transform(move, addRot);
            addRot = Quaternion.Normalize(Quaternion.Multiply(addRot, turn));
        }
        return (local.Pos + Vector3.Transform(addPos, local.Rot), Quaternion.Normalize(Quaternion.Multiply(local.Rot, addRot)));
    }

    /// <summary>
    /// The chore resource that voices <paramref name="lineSymbol"/>, with its speaker and span.
    /// Lines in embedded chores use the embedded chore's timeline.
    /// </summary>
    public static ChoreLine? FindLine(ChoreFile chore, ulong lineSymbol) =>
        Lines(chore).FirstOrDefault(l => l.Chore.Resources[l.Resource].Name == lineSymbol);

    /// <summary>Every voice line in <paramref name="chore"/>, then those of its embedded chores.</summary>
    public static IEnumerable<ChoreLine> Lines(ChoreFile chore) => Lines(chore, 0);

    private static IEnumerable<ChoreLine> Lines(ChoreFile chore, int depth)
    {
        for (int i = 0; i < chore.Resources.Count; i++)
        {
            var r = chore.Resources[i];
            if (r.Type != ChoreFile.LanguageResType || r.Blocks.Count == 0)
                continue;
            if (chore.AgentOf(i) is { } agent)
                yield return new ChoreLine(chore, i, agent.Name, r.Blocks.Min(b => b.Start), r.Blocks.Max(b => b.End));
        }
        if (depth >= MaxDepth)
            yield break;
        foreach (var r in chore.Resources)
        {
            if (r.EmbeddedChore is { } embedded)
                foreach (var line in Lines(embedded, depth + 1))
                    yield return line;
        }
    }

    /// <summary>
    /// The animations <paramref name="agent"/> plays in <paramref name="chore"/>, including
    /// nested chores, placed on this chore's timeline.
    /// </summary>
    /// <param name="nameOf">The file name of a resource symbol, or null when the game lacks it.</param>
    /// <param name="loadChore">Reads a chore file by name, or null.</param>
    public static IReadOnlyList<PerformanceClip> Collect(ChoreFile chore, string agent,
        Func<ulong, string?> nameOf, Func<string, ChoreFile?> loadChore)
    {
        var clips = new List<PerformanceClip>();
        var owner = chore.Agents.FirstOrDefault(a => a.Name.Equals(agent, StringComparison.OrdinalIgnoreCase));
        if (owner is null)
            return clips;
        foreach (int index in owner.Resources)
            Add(chore.Resources[index], 0, float.PositiveInfinity, null, null, [], 0);
        return clips;

        // Outer chores' group weights, then the resource's own.
        static IReadOnlyList<IReadOnlyDictionary<ulong, float>> Limit(
            IReadOnlyList<IReadOnlyDictionary<ulong, float>> outer, ChoreResource resource) =>
            resource.GroupInclude is { } own ? [.. outer, own] : outer;

        void Add(ChoreResource resource, float offset, float limit, int? priority, ResourceControl? parent,
            IReadOnlyList<IReadOnlyDictionary<ulong, float>> groups, int depth)
        {
            if (!resource.Enabled)
                return;
            foreach (var block in resource.Blocks)
            {
                float start = offset + block.Start, end = Math.Min(offset + block.End, limit);
                if (end <= start)
                    continue;
                var control = new ResourceControl(block.Start, block.End, resource.Length, block.Scale,
                    resource.TimeCurve, resource.Contribution, parent);
                if (resource.Type == ChoreFile.AnimationType && nameOf(resource.Name) is { } name)
                {
                    bool additive = Path.GetFileNameWithoutExtension(name).EndsWith("_add", StringComparison.OrdinalIgnoreCase);
                    clips.Add(new PerformanceClip(name, start, end, block.Looping, block.Scale,
                        priority ?? resource.Priority, additive, control, Limit(groups, resource)));
                }
                else if (resource.Type == ChoreFile.ChoreType && depth < MaxDepth)
                {
                    var nested = resource.EmbeddedChore ?? (nameOf(resource.Name) is { } file ? loadChore(file) : null);
                    if (nested is null)
                        continue;
                    foreach (var inner in nested.Resources)
                        if (inner.Type == ChoreFile.AnimationType || inner.Type == ChoreFile.ChoreType)
                            Add(inner, start, end, priority ?? resource.Priority, control, Limit(groups, resource), depth + 1);
                }
            }
        }
    }
}
