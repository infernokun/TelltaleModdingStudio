using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>A camera at one moment in the speaker's model space, with its vertical field of view in degrees.</summary>
public readonly record struct CameraView(Vector3 Eye, Vector3 Forward, Vector3 Up, float FieldOfView);

/// <summary>
/// The camera the game shows a line from, picked as the chore-keyed camera that keeps the
/// speaker in its 16:9 frame longest. Cameras look down their +Z axis.
/// </summary>
public sealed class GameCamera
{
    private const float DefaultFieldOfView = 35f;
    private const int MaxDepth = 2;
    private static readonly ulong FieldOfViewKey = TelltaleCrc64.HashSymbol("Field of View");

    // A resource keyed for an agent, and the control of the nested chore it came from, if any.
    private sealed record Keyed(ChoreResource Resource, ResourceControl? Via);

    private readonly Dictionary<string, List<Keyed>> _keys;
    private readonly SceneAgent _camera, _speaker;
    private readonly float _start;
    private readonly Func<float, Matrix4x4>? _rootMotion;
    private readonly (TransformTrack Track, float Length, bool Looping, ResourceControl Control)[] _drift;

    private GameCamera(Dictionary<string, List<Keyed>> keys, SceneAgent camera, SceneAgent speaker, float start,
        Func<float, Matrix4x4>? rootMotion, Func<ulong, AnimationFile?>? clips)
    {
        (_keys, _camera, _speaker, _start, _rootMotion) = (keys, camera, speaker, start, rootMotion);
        _drift = clips is null ? [] : KeysOf(camera)
            .Where(k => k.Resource.Type == ChoreFile.AnimationType && k.Resource.Enabled)
            .SelectMany(k => clips(k.Resource.Name) is { TransformTracks.Count: > 0 } clip
                ? k.Resource.Blocks.Select(b => (clip.TransformTracks[0], Math.Max(0.05f, clip.Length), b.Looping,
                    new ResourceControl(b.Start, b.End, k.Resource.Length, b.Scale, k.Resource.TimeCurve, k.Resource.Contribution, k.Via)))
                : [])
            .ToArray();
    }

    /// <summary>The scene's camera agent the line is shown from.</summary>
    public string CameraName => _camera.Name;

    /// <summary>
    /// The camera that frames <paramref name="head"/> longest during a line spoken by
    /// <paramref name="agent"/>, or null when none frames it at all.
    /// </summary>
    /// <param name="rootMotion">The speaker's root motion by seconds into the line (see <see cref="LipSyncPlayer.RootMotion"/>).</param>
    /// <param name="chores">Reads a chore the line's chore plays, by its symbol.</param>
    /// <param name="clips">Reads a clip a camera plays, by its symbol.</param>
    public static GameCamera? Find(ChoreFile chore, SceneFile scene, string agent, float start, float end, Vector3 head,
        Func<float, Matrix4x4>? rootMotion = null, Func<ulong, ChoreFile?>? chores = null, Func<ulong, AnimationFile?>? clips = null) =>
        Rank(chore, scene, agent, start, end, head, rootMotion, chores, clips).FirstOrDefault(c => c.Share > 0).Camera;

    /// <summary>Every keyed camera in the scene with the share of the line it frames <paramref name="head"/>, best first.</summary>
    public static IReadOnlyList<(GameCamera Camera, float Share)> Rank(ChoreFile chore, SceneFile scene, string agent,
        float start, float end, Vector3 head, Func<float, Matrix4x4>? rootMotion = null,
        Func<ulong, ChoreFile?>? chores = null, Func<ulong, AnimationFile?>? clips = null)
    {
        var ranked = new List<(GameCamera, float)>();
        if ((scene.Find(agent) ?? scene.Agents.FirstOrDefault(a => Loose(a.Name) == Loose(agent))) is not { } speaker)
            return ranked;
        var keys = Index(chore, chores);
        foreach (var name in keys.Keys)
        {
            if (!name.StartsWith("cam", StringComparison.OrdinalIgnoreCase) || scene.Find(name) is not { } camera)
                continue;
            var candidate = new GameCamera(keys, camera, speaker, start, rootMotion, clips);
            const int Samples = 8;
            int framed = 0;
            for (int i = 0; i < Samples; i++)
                if (candidate.Frames(head, (end - start) * (i + 0.5f) / Samples))
                    framed++;
            ranked.Add((candidate, framed / (float)Samples));
        }
        return ranked.OrderByDescending(c => c.Item2).ToList();
    }

    // Everything the line's chore and its nested chores key, by agent name.
    private static Dictionary<string, List<Keyed>> Index(ChoreFile chore, Func<ulong, ChoreFile?>? chores)
    {
        var keys = new Dictionary<string, List<Keyed>>(StringComparer.OrdinalIgnoreCase);
        var played = new HashSet<(ChoreFile, int)>();
        Add(chore, null, 0);
        return keys;

        void Add(ChoreFile owner, ResourceControl? via, int depth)
        {
            foreach (var agent in owner.Agents)
            {
                if (!keys.TryGetValue(agent.Name, out var list))
                    keys[agent.Name] = list = [];
                foreach (int index in agent.Resources)
                {
                    var resource = owner.Resources[index];
                    list.Add(new Keyed(resource, via));
                    if (resource.Type != ChoreFile.ChoreType || !resource.Enabled || depth >= MaxDepth || !played.Add((owner, index)))
                        continue;
                    if ((resource.EmbeddedChore ?? chores?.Invoke(resource.Name)) is not { } nested)
                        continue;
                    foreach (var block in resource.Blocks)
                        Add(nested, new ResourceControl(block.Start, block.End, resource.Length, block.Scale,
                            resource.TimeCurve, resource.Contribution, via), depth + 1);
                }
            }
        }
    }

    /// <summary>The camera at <paramref name="time"/> seconds into the line, in the speaker's space.</summary>
    public CameraView At(float time)
    {
        float t = _start + time;
        var speaker = (_rootMotion?.Invoke(time) ?? Matrix4x4.Identity) * World(_speaker, t);
        Matrix4x4.Invert(speaker, out var speakerInverse);
        var camera = Drift(t) * World(_camera, t) * speakerInverse;
        return new CameraView(camera.Translation,
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, camera)),
            Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, camera)),
            FieldOfView(t));
    }

    /// <summary>
    /// True when the camera frames <paramref name="head"/> and is in front of a speaker facing
    /// <paramref name="facing"/> radians. Over-the-shoulder shots would only show the back of the head.
    /// </summary>
    public bool ShowsFace(Vector3 head, float facing, float time)
    {
        if (!Frames(head, time))
            return false;
        var toCamera = At(time).Eye - head;
        var front = new Vector3(MathF.Sin(facing), 0, MathF.Cos(facing));
        return Vector3.Dot(Vector3.Normalize(new Vector3(toCamera.X, 0, toCamera.Z)), front) > MathF.Cos(MathF.PI * 0.55f);
    }

    /// <summary>True when <paramref name="head"/> is in the camera's 16:9 frame at <paramref name="time"/>.</summary>
    public bool Frames(Vector3 head, float time)
    {
        var view = At(time);
        var d = head - view.Eye;
        float depth = Vector3.Dot(d, view.Forward);
        if (depth <= 0.1f)
            return false;
        var right = Vector3.Normalize(Vector3.Cross(view.Forward, view.Up));
        float halfHeight = MathF.Tan(view.FieldOfView * MathF.PI / 360f) * depth;
        return MathF.Abs(Vector3.Dot(d, view.Up)) <= halfHeight && MathF.Abs(Vector3.Dot(d, right)) <= halfHeight * 16f / 9f;
    }

    // Handheld drift and shake clips, applied in the camera's own frame.
    private Matrix4x4 Drift(float t)
    {
        var drift = Matrix4x4.Identity;
        foreach (var (track, length, looping, control) in _drift)
        {
            var (clipTime, weight) = control.At(t, hold: false);
            if (weight <= 0)
                continue;
            var (rotation, position) = track.Sample(looping ? clipTime % length : Math.Min(clipTime, length));
            drift = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(Quaternion.Identity, rotation, weight))
                    * Matrix4x4.CreateTranslation(position * weight) * drift;
        }
        return drift;
    }

    private float FieldOfView(float t) =>
        Current(_camera, t, k => k.FieldOfView, (curve, time) => curve.Evaluate(time)) is { } fov ? fov
        : _camera.Properties.GetFloat(FieldOfViewKey) is > 1 and < 170 and var start ? start : DefaultFieldOfView;

    // An agent's world transform at chore time t, using keyed transforms where there are any.
    private Matrix4x4 World(SceneAgent agent, float t)
    {
        var world = Local(agent, t);
        var seen = new HashSet<SceneAgent> { agent };
        for (var parent = agent.Parent; parent is not null && seen.Add(parent); parent = parent.Parent)
            world *= Local(parent, t);
        return world;
    }

    private Matrix4x4 Local(SceneAgent agent, float t) =>
        Current(agent, t, k => k.Transform, (track, time) =>
        {
            var (rotation, position) = track.Evaluate(time);
            return Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        }) ?? agent.LocalMatrix;

    // A keyed value at chore time t. The last nested chore to reach it wins over the line's
    // own chore, and holds its last moment once it ends.
    private TResult? Current<TValue, TResult>(SceneAgent agent, float t, Func<ChoreResource, TValue?> value,
        Func<TValue, float, TResult> evaluate) where TValue : class where TResult : struct
    {
        TResult? own = null, played = null;
        foreach (var key in KeysOf(agent))
        {
            if (value(key.Resource) is not { } keyed)
                continue;
            if (key.Via is null)
                own ??= evaluate(keyed, t);
            else if (key.Via.At(t, hold: true) is { Weight: > 0 } at)
                played = evaluate(keyed, at.Time);
        }
        return played ?? own;
    }

    // Names compared without case, spaces or punctuation.
    private static string Loose(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private IEnumerable<Keyed> KeysOf(SceneAgent agent) =>
        _keys.TryGetValue(agent.Name, out var list) ? list : [];
}
