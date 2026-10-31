using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>Where a camera is at one moment, in a speaker's own space (the model's space), and its vertical field of view in degrees.</summary>
public readonly record struct CameraView(Vector3 Eye, Vector3 Forward, Vector3 Up, float FieldOfView);

/// <summary>
/// The camera the game shows a line from. A line's chore, and the chores it plays (a cutscene's
/// pieces), key the scene's camera agents (<c>cam_station1</c>, <c>cam_cutsceneChore</c>…): their
/// transform over the chore, or that of the rig they are attached to, their field of view, and
/// handheld drift and shake clips on top. They key the characters too, the speaker among them.
/// The camera chosen is the one that keeps the speaker in its 16:9 frame for longest. Cameras
/// look down their +Z axis.
/// </summary>
public sealed class GameCamera
{
    private const float DefaultFieldOfView = 35f;
    private const int MaxDepth = 2;
    private static readonly ulong FieldOfViewKey = TelltaleCrc64.HashSymbol("Field of View");

    // What the chores key for each agent (by name): the resource, and for one in a chore the
    // line's chore plays, how that chore's time and weight follow from the line's.
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
    /// The camera for a line <paramref name="start"/> to <paramref name="end"/> seconds into
    /// <paramref name="chore"/>, spoken by <paramref name="agent"/> of <paramref name="scene"/>, or
    /// null when no camera has <paramref name="head"/> (a point in the speaker's space) in frame at
    /// any point of the line. The camera that frames it longest is chosen; <see cref="ShowsFace"/>
    /// tells the moments it does.
    /// </summary>
    /// <param name="rootMotion">By seconds into the line, how far the speaker's clips have walked
    /// and turned them from their place (see <see cref="LipSyncPlayer.RootMotion"/>), which the
    /// speaker as drawn leaves out.</param>
    /// <param name="chores">Reads a chore the line's chore plays, by its symbol.</param>
    /// <param name="clips">Reads a clip a camera plays (handheld drift), by its symbol.</param>
    public static GameCamera? Find(ChoreFile chore, SceneFile scene, string agent, float start, float end, Vector3 head,
        Func<float, Matrix4x4>? rootMotion = null, Func<ulong, ChoreFile?>? chores = null, Func<ulong, AnimationFile?>? clips = null) =>
        Rank(chore, scene, agent, start, end, head, rootMotion, chores, clips).FirstOrDefault(c => c.Share > 0).Camera;

    /// <summary>
    /// Every camera the chores key that is in the scene, with the share of the line it keeps
    /// <paramref name="head"/> in frame, best first (see <see cref="Find"/>).
    /// </summary>
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

    // Everything the line's chore and the chores it plays (two deep) key, by agent name.
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
    /// True when the camera shows the speaker's face <paramref name="time"/> seconds into the line:
    /// <paramref name="head"/> (in the speaker's space) is in its 16:9 frame and the camera is in
    /// front of a speaker facing <paramref name="facing"/> (radians, 0 = the front of the rest pose).
    /// The game cuts away from a speaker mid-line and shoots over their shoulder at whoever they
    /// talk to; with only the speaker drawn, those shots would show the back of their head.
    /// </summary>
    public bool ShowsFace(Vector3 head, float facing, float time)
    {
        if (!Frames(head, time))
            return false;
        var toCamera = At(time).Eye - head;
        var front = new Vector3(MathF.Sin(facing), 0, MathF.Cos(facing));
        return Vector3.Dot(Vector3.Normalize(new Vector3(toCamera.X, 0, toCamera.Z)), front) > MathF.Cos(MathF.PI * 0.55f);
    }

    /// <summary>True when <paramref name="head"/> (in the speaker's space) is in the camera's 16:9 frame <paramref name="time"/> seconds into the line.</summary>
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

    // The handheld drift and shake clips the chores play on the camera: small changes in the
    // camera's own frame, each weighted and timed by the chore's curves for it.
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

    // An agent's world transform at chore time t: each link's keyed transform where a chore
    // keys one, else its start transform in the scene.
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

    // A keyed value of an agent at chore time t: from a played chore that has reached it (the
    // last one to), else from the line's own chore, else null. A played chore holds its last
    // moment once it ends.
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

    // Names compared without case, spaces or punctuation ("LADYFORRESTER", "Lady_Forrester").
    private static string Loose(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private IEnumerable<Keyed> KeysOf(SceneAgent agent) =>
        _keys.TryGetValue(agent.Name, out var list) ? list : [];
}
