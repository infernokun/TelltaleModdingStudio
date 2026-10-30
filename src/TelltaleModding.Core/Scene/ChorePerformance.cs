using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// One animation an agent plays in a chore, placed on the chore's timeline (seconds).
/// <see cref="Scale"/> is the playback speed; <see cref="Additive"/> clips (<c>_add</c>)
/// layer on top of the pose rather than replacing it.
/// </summary>
public sealed record PerformanceClip(string Name, float Start, float End, bool Looping, float Scale, int Priority, bool Additive);

/// <summary>Where a voice line sits in a chore: who says it, and when.</summary>
public sealed record ChoreLine(ChoreFile Chore, int Resource, string Agent, float Start, float End);

/// <summary>
/// Replays an agent's part of a chore on its skeleton: body animations (turns, arm
/// crosses, sitting down) and additive gestures (breaths, nods, glances, blinks) at the
/// times the chore plays them. Approximations of the engine: blends are linear fades at
/// the edges of each span rather than the chore's contribution curves; a body animation
/// holds its last frame until the next one starts (in game a style idle takes over); and
/// root motion is left out, so the character turns and gestures in place.
/// </summary>
public sealed class ChorePerformance
{
    private const float Fade = 0.3f;
    private const int MaxDepth = 2;

    private readonly Layer[] _body;
    private readonly Layer[] _additive;

    private sealed record Layer(PerformanceClip Spec, AnimationPlayer Player, float Length, float HoldUntil)
    {
        public float ClipTime(float t)
        {
            float local = Math.Max(0, (t - Spec.Start) * (Spec.Scale > 0 ? Spec.Scale : 1));
            return Spec.Looping && Length > 0 ? local % Length : Math.Min(local, Length);
        }

        public float Weight(float t)
        {
            float end = Math.Max(Spec.End, HoldUntil);
            if (t < Spec.Start - Fade || t > end + Fade)
                return 0;
            float fadeIn = Math.Clamp((t - Spec.Start) / Fade + 1, 0, 1);
            float fadeOut = float.IsPositiveInfinity(end) ? 1 : Math.Clamp((end - t) / Fade + 1, 0, 1);
            return Math.Min(fadeIn, fadeOut);
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
                additive.Add(new Layer(spec, player, player.Length, spec.End));
            else
                body.Add((spec, player));
        }

        // Later body animations take over from earlier ones; each holds until the next starts.
        body.Sort((a, b) => a.Spec.Start != b.Spec.Start ? a.Spec.Start.CompareTo(b.Spec.Start) : a.Spec.Priority.CompareTo(b.Spec.Priority));
        _body = body.Select((b, i) => new Layer(b.Spec, b.Player, b.Player.Length,
                b.Spec.Looping ? b.Spec.End : i + 1 < body.Count ? body[i + 1].Spec.Start + Fade : float.PositiveInfinity))
            .ToArray();
        _additive = additive.OrderBy(a => a.Spec.Priority).ToArray();
    }

    /// <summary>Number of clips that move this skeleton.</summary>
    public int ClipCount => _body.Length + _additive.Length;

    /// <summary>
    /// A bone's local transform at <paramref name="time"/> seconds into the chore, layered
    /// over <paramref name="under"/> (the pose without the chore: rest or an idle).
    /// </summary>
    public (Vector3 Pos, Quaternion Rot) SampleLocal(SkeletonBone bone, float time, (Vector3 Pos, Quaternion Rot) under)
    {
        // The root carries the walk and turn motion that would take the character out of frame.
        if (bone.ParentIndex < 0)
            return under;

        var local = under;
        foreach (var layer in _body)
        {
            float w = layer.Weight(time);
            if (w <= 0 || !layer.Player.Animates(bone.JointSymbol))
                continue;
            var s = layer.Player.SampleLocal(bone, layer.ClipTime(time));
            local = (Vector3.Lerp(local.Pos, s.Pos, w), Quaternion.Slerp(local.Rot, s.Rot, w));
        }

        // Additive clips are authored as rest pose plus a change; apply just the change.
        var restInverse = Quaternion.Inverse(bone.LocalRotation);
        foreach (var layer in _additive)
        {
            float w = layer.Weight(time);
            if (w <= 0 || !layer.Player.Animates(bone.JointSymbol))
                continue;
            var s = layer.Player.SampleLocal(bone, layer.ClipTime(time));
            var delta = Quaternion.Normalize(Quaternion.Multiply(restInverse, s.Rot));
            local = (local.Pos + (s.Pos - bone.LocalPosition) * w,
                     Quaternion.Normalize(Quaternion.Multiply(local.Rot, Quaternion.Slerp(Quaternion.Identity, delta, w))));
        }
        return local;
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
            Add(chore.Resources[index], 0, float.PositiveInfinity, null, 0);
        return clips;

        void Add(ChoreResource resource, float offset, float limit, int? priority, int depth)
        {
            if (!resource.Enabled)
                return;
            foreach (var block in resource.Blocks)
            {
                float start = offset + block.Start, end = Math.Min(offset + block.End, limit);
                if (end <= start)
                    continue;
                if (resource.Type == ChoreFile.AnimationType && nameOf(resource.Name) is { } name)
                {
                    bool additive = Path.GetFileNameWithoutExtension(name).EndsWith("_add", StringComparison.OrdinalIgnoreCase);
                    clips.Add(new PerformanceClip(name, start, end, block.Looping, block.Scale,
                        priority ?? resource.Priority, additive));
                }
                else if (resource.Type == ChoreFile.ChoreType && depth < MaxDepth)
                {
                    var nested = resource.EmbeddedChore ?? (nameOf(resource.Name) is { } file ? loadChore(file) : null);
                    if (nested is null)
                        continue;
                    foreach (var inner in nested.Resources)
                        if (inner.Type == ChoreFile.AnimationType || inner.Type == ChoreFile.ChoreType)
                            Add(inner, start, end, priority ?? resource.Priority, depth + 1);
                }
            }
        }
    }
}
