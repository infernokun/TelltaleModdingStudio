using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// One animation an agent plays in a chore, placed on the chore's timeline (seconds).
/// <see cref="Scale"/> is the playback speed; <see cref="Additive"/> clips (<c>_add</c>)
/// layer on top of the pose rather than replacing it. <see cref="Control"/> carries the
/// chore's curves for it, when it has any. <see cref="Groups"/> holds the resource-group
/// weights (see <see cref="ChoreResource.GroupInclude"/>) of the resource and of each chore it
/// is nested in that limits them, which narrow it to part of the skeleton.
/// </summary>
public sealed record PerformanceClip(string Name, float Start, float End, bool Looping, float Scale, int Priority, bool Additive,
    ResourceControl? Control = null, IReadOnlyList<IReadOnlyDictionary<ulong, float>>? Groups = null);

/// <summary>
/// How a resource plays in the chore that holds it: the span of one of its blocks, its
/// length, and the chore's time and contribution curves for it, under the resource that
/// plays that chore inside another (<see cref="Parent"/>), if any.
/// </summary>
public sealed record ResourceControl(float Start, float End, float Length, float Scale,
    ChoreCurve? Time, ChoreCurve? Contribution, ResourceControl? Parent)
{
    /// <summary>True when this resource or a chore it is nested in has a time or contribution curve.</summary>
    public bool HasCurves => Time is not null || Contribution is not null || (Parent?.HasCurves ?? false);

    /// <summary>
    /// Seconds into the resource and how much it counts at <paramref name="time"/> seconds into
    /// the outermost chore. Past the end of its block a resource counts nothing unless
    /// <paramref name="hold"/>, which keeps it at its last moment.
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
/// Replays an agent's part of a chore on its skeleton: body animations (turns, arm
/// crosses, sitting down) and additive gestures (breaths, nods, glances, blinks) at the
/// times the chore plays them, weighted and timed by the chore's contribution and time
/// curves, and mixed the way the engine's skeleton mixer does. Body animations go by
/// priority, lowest first: those of one priority average by weight, then cover the pose
/// below by the largest of their weights. Additive changes chain from the highest priority
/// down, each in its bone's own frame. Each clip counts on a bone only as far as the
/// chore's resource groups include it (a head gesture that leaves out the brows).
/// Approximations of the engine: clips without curves fade linearly at the edges of each
/// span; a body animation holds its last frame until the next one starts (in game a style
/// idle takes over); and root motion is left out, so the character turns and gestures in place.
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
        // How much the clip counts on each bone (joint symbol), from the chore's resource
        // groups; null when it counts on every bone it animates.
        public IReadOnlyDictionary<ulong, float>? Mask { get; init; }

        // The chore's curves, when it keys any: they set the weight and the moment in the clip.
        private ResourceControl? Curves => Spec.Control is { HasCurves: true } control ? control : null;

        // A body animation holds its last moment until the next one takes over; a gesture ends.
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

        // The clip's weight on one bone at a moment, 0 when it leaves the bone alone.
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

        // Later body animations take over from earlier ones; each holds until the next starts,
        // or, when the chore keys how much they count, for as long as the chore says.
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

    // How much a clip counts on each bone, as the engine works it out: for each set of group
    // weights, a bone gets the sum of weight times its membership in each group, clamped to
    // 0-1 (a large negative weight leaves the bone out whatever else includes it); a nested
    // chore's sets multiply. Null when the clip counts on every bone.
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

    /// <summary>
    /// A bone's local transform at <paramref name="time"/> seconds into the chore, layered
    /// over <paramref name="under"/> (the pose without the chore: rest or an idle).
    /// </summary>
    public (Vector3 Pos, Quaternion Rot) SampleLocal(SkeletonBone bone, float time, (Vector3 Pos, Quaternion Rot) under) =>
        // The root carries the walk and turn motion that would take the character out of frame.
        bone.ParentIndex < 0 ? under : Mix(bone, time, under);

    /// <summary>
    /// The root bone's local transform at <paramref name="time"/> seconds into the chore, with
    /// the walk and turn motion <see cref="SampleLocal"/> leaves out: where the character has
    /// moved to in the game.
    /// </summary>
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

        // Additive clips are authored as rest pose plus a change. The changes chain, highest
        // priority first, each scaled by its weight and taken in the bone's frame.
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
    /// A line voiced in a chore embedded in this one (a cut of the scene kept inside the
    /// chore) is placed on the embedded chore's own timeline.
    /// </summary>
    public static ChoreLine? FindLine(ChoreFile chore, ulong lineSymbol) =>
        Lines(chore).FirstOrDefault(l => l.Chore.Resources[l.Resource].Name == lineSymbol);

    /// <summary>
    /// Every voice line placed in <paramref name="chore"/>, then those of the chores embedded
    /// in it (each on its own timeline).
    /// </summary>
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
    /// those of the chores it plays (a blink, a move into a pose), placed on this chore's timeline.
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

        // The group weights a clip is under: those of the chores around it, then its own.
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
