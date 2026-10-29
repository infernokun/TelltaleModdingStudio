using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// The local transforms a single-frame clip sets, for the bones it drives (sampled at the
/// clip's first frame).
/// </summary>
public sealed class FacePose
{
    public static readonly FacePose Empty = new(new Dictionary<ulong, (Vector3, Quaternion)>());

    public FacePose(IReadOnlyDictionary<ulong, (Vector3 Pos, Quaternion Rot)> bones) => Bones = bones;

    /// <summary>Joint symbol → local transform.</summary>
    public IReadOnlyDictionary<ulong, (Vector3 Pos, Quaternion Rot)> Bones { get; }
}

/// <summary>
/// Poses a character's face in layers over a body clip, and skins the model with it:
/// <list type="number">
/// <item>base: the body clip at the given time (<see cref="Body"/>), or the rest pose;</item>
/// <item>expression: a mood's expression pose, blended in by <see cref="ExpressionStrength"/>;</item>
/// <item>mouth: mouth shape poses weighted by sliders and/or a voice line's phoneme keys
/// (weights summing past 1 are normalised; what is left over stays on the closed mouth);</item>
/// <item>gesture: a head/face gesture clip, applied additively when it is an <c>_add</c> clip;</item>
/// <item>manual: per-bone rotation (degrees about the bone's local axes) and translation offsets.</item>
/// </list>
/// </summary>
public sealed class FaceRig
{
    private readonly SkeletonFile _skeleton;
    private readonly MeshSkinner _skinner;
    private readonly AnimationPlayer _sampler;
    private readonly object _sampleLock = new();

    private FacePose? _expression;
    private Dictionary<string, FacePose> _mouth = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<ulong, string> _shapeBySymbol = new();
    private readonly Dictionary<string, float> _weights = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PhonemeKey> _line = Array.Empty<PhonemeKey>();
    private AnimationPlayer? _gesture;
    private readonly Dictionary<ulong, (Vector3 Degrees, Vector3 Translation)> _offsets = new();

    public FaceRig(MeshGeometry geometry, SkeletonFile skeleton)
    {
        _skeleton = skeleton;
        _skinner = new MeshSkinner(geometry, skeleton);
        _sampler = new AnimationPlayer(geometry, skeleton);
        Geometry = geometry;
    }

    public MeshGeometry Geometry { get; }

    public SkeletonFile Skeleton => _skeleton;

    /// <summary>The body clip underneath the face; null poses the rest of the body at rest.</summary>
    public AnimationPlayer? Body { get; set; }

    // ---- Clips -----------------------------------------------------------

    /// <summary>Samples a single-frame pose from <paramref name="clip"/> (safe to call off the UI thread).</summary>
    public FacePose SamplePose(AnimationFile clip, string name)
    {
        lock (_sampleLock)
        {
            _sampler.Load(clip, name);
            var bones = new Dictionary<ulong, (Vector3, Quaternion)>();
            foreach (var bone in _skeleton.Bones)
                if (_sampler.Animates(bone.JointSymbol))
                    bones[bone.JointSymbol] = _sampler.SampleLocal(bone, 0);
            return new FacePose(bones);
        }
    }

    /// <summary>The expression pose (null for none).</summary>
    public FacePose? Expression
    {
        get => _expression;
        set => _expression = value;
    }

    /// <summary>Sets the expression from a clip's first frame (null clears it).</summary>
    public void SetExpression(AnimationFile? clip, string name = "expression") =>
        _expression = clip is null ? null : SamplePose(clip, name);

    /// <summary>How strongly the expression shows, 0-1.</summary>
    public float ExpressionStrength { get; set; } = 1f;

    /// <summary>
    /// Sets the mouth shape poses, by shape name (<c>AA</c>, <c>MM</c>…); <c>default</c> is the
    /// closed mouth. Weights of shapes that remain are kept.
    /// </summary>
    public void SetMouthShapes(IReadOnlyDictionary<string, FacePose> poses)
    {
        _mouth = new Dictionary<string, FacePose>(poses, StringComparer.OrdinalIgnoreCase);
        _shapeBySymbol = _mouth.Keys.ToDictionary(TelltaleCrc64.HashSymbol, k => k);
    }

    /// <summary>Samples and sets the mouth shape poses from clips.</summary>
    public void SetMouthShapes(IReadOnlyDictionary<string, AnimationFile> clips) =>
        SetMouthShapes(clips.ToDictionary(kv => kv.Key, kv => SamplePose(kv.Value, kv.Key), StringComparer.OrdinalIgnoreCase));

    /// <summary>Shape names with a pose.</summary>
    public IReadOnlyCollection<string> MouthShapes => _mouth.Keys;

    /// <summary>A mouth shape's slider weight (0-1).</summary>
    public float GetMouthWeight(string shape) => _weights.GetValueOrDefault(shape);

    public void SetMouthWeight(string shape, float weight)
    {
        weight = Math.Clamp(weight, 0f, 1f);
        if (weight <= 0)
            _weights.Remove(shape);
        else
            _weights[shape] = weight;
    }

    public void ClearMouth() => _weights.Clear();

    /// <summary>Sets a voice line's phoneme keys (null or empty for none).</summary>
    public void SetLine(IReadOnlyList<PhonemeKey>? keys) => _line = keys ?? Array.Empty<PhonemeKey>();

    public bool HasLine => _line.Count > 0;

    /// <summary>Seconds into the voice line.</summary>
    public float LineTime { get; set; }

    /// <summary>Sets a gesture clip (null clears it); <c>_add</c> clips layer additively.</summary>
    public void SetGesture(AnimationFile? clip, string? name)
    {
        if (clip is null || name is null)
        {
            _gesture = null;
            GestureLength = 0;
            GestureIsAdditive = false;
            return;
        }
        var player = new AnimationPlayer(Geometry, _skeleton);
        player.Load(clip, name);
        GestureIsAdditive = Path.GetFileNameWithoutExtension(name).EndsWith("_add", StringComparison.OrdinalIgnoreCase)
                            || player.ClipDescription.Contains("additive", StringComparison.Ordinal);
        GestureLength = player.Length;
        _gesture = player.HasMotion ? player : null;
    }

    public bool HasGesture => _gesture is not null;

    public float GestureLength { get; private set; }

    public bool GestureIsAdditive { get; private set; }

    /// <summary>Seconds into the gesture.</summary>
    public float GestureTime { get; set; }

    /// <summary>How strongly the gesture applies, 0-1.</summary>
    public float GestureWeight { get; set; } = 1f;

    // ---- Manual offsets ----------------------------------------------------

    /// <summary>
    /// Offsets one bone: <paramref name="degrees"/> rotates it about its own axes (X pitch,
    /// Y yaw, Z roll), <paramref name="translation"/> moves it in its parent's space.
    /// </summary>
    public void SetBoneOffset(ulong joint, Vector3 degrees, Vector3 translation = default)
    {
        if (degrees == Vector3.Zero && translation == Vector3.Zero)
            _offsets.Remove(joint);
        else
            _offsets[joint] = (degrees, translation);
    }

    public (Vector3 Degrees, Vector3 Translation) GetBoneOffset(ulong joint) => _offsets.GetValueOrDefault(joint);

    public void ClearBoneOffsets() => _offsets.Clear();

    // ---- State -------------------------------------------------------------

    /// <summary>Bones the expression and mouth poses move.</summary>
    public IReadOnlySet<ulong> DrivenBones
    {
        get
        {
            var set = new HashSet<ulong>();
            if (_expression is not null)
                set.UnionWith(_expression.Bones.Keys);
            foreach (var pose in _mouth.Values)
                set.UnionWith(pose.Bones.Keys);
            return set;
        }
    }

    /// <summary>True when no layer changes the body pose.</summary>
    public bool IsNeutral =>
        (_expression is null || ExpressionStrength <= 0) && _weights.Count == 0 && _line.Count == 0 &&
        (_gesture is null || GestureWeight <= 0) && _offsets.Count == 0;

    /// <summary>Clears the expression, mouth weights, line, gesture and offsets (mouth poses stay loaded).</summary>
    public void ResetToNeutral()
    {
        _expression = null;
        _weights.Clear();
        _line = Array.Empty<PhonemeKey>();
        _gesture = null;
        GestureLength = 0;
        _offsets.Clear();
    }

    /// <summary>
    /// The mouth shape weights in effect at <paramref name="lineTime"/>: the sliders plus the
    /// line's keys, normalised when they sum past 1.
    /// </summary>
    public IReadOnlyDictionary<string, float> MouthWeightsAt(float lineTime)
    {
        var weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (shape, w) in _weights)
            if (_mouth.ContainsKey(shape))
                weights[shape] = w;
        foreach (var key in _line)
        {
            if (key.Time > lineTime)
                break;
            float w = key.WeightAt(lineTime);
            if (w > 0 && _shapeBySymbol.TryGetValue(key.Phoneme, out var shape))
                weights[shape] = weights.GetValueOrDefault(shape) + w;
        }
        float total = weights.Values.Sum();
        if (total > 1)
            foreach (var shape in weights.Keys.ToList())
                weights[shape] /= total;
        return weights;
    }

    // ---- Evaluation --------------------------------------------------------

    private sealed record Mix(
        FacePose? Expression, float Strength, FacePose? Closed, float ClosedWeight,
        List<(FacePose Pose, float Weight)> Shapes, float Leftover,
        AnimationPlayer? Gesture, float GestureTime, float GestureWeight, bool GestureAdditive,
        Dictionary<ulong, (Vector3 Degrees, Vector3 Translation)> Offsets);

    private Mix CurrentMix()
    {
        var weights = MouthWeightsAt(LineTime);
        float total = Math.Min(1f, weights.Values.Sum());
        var shapes = weights.Where(kv => kv.Value > 0).Select(kv => (_mouth[kv.Key], kv.Value)).ToList();
        _mouth.TryGetValue(FaceClipSet.ClosedShape, out var closed);
        // While a line plays the mouth rests closed between keys; sliders ease it in with their weight.
        float closedWeight = _line.Count > 0 ? 1f : total;
        var strength = _expression is null ? 0f : Math.Clamp(ExpressionStrength, 0f, 1f);
        return new Mix(_expression, strength, closed, closedWeight, shapes, 1f - total,
            _gesture, GestureTime, Math.Clamp(GestureWeight, 0f, 1f), GestureIsAdditive, _offsets);
    }

    private static bool Touches(ulong joint, Mix mix) =>
        (mix.Strength > 0 && mix.Expression!.Bones.ContainsKey(joint)) ||
        (mix.ClosedWeight > 0 && mix.Closed is not null && mix.Closed.Bones.ContainsKey(joint)) ||
        mix.Shapes.Any(s => s.Pose.Bones.ContainsKey(joint)) ||
        (mix.GestureWeight > 0 && mix.Gesture is not null && mix.Gesture.Animates(joint)) ||
        mix.Offsets.ContainsKey(joint);

    private (Vector3 Pos, Quaternion Rot) BaseLocal(SkeletonBone bone, float bodyTime) =>
        Body?.SampleLocal(bone, bodyTime) ?? (bone.LocalPosition, bone.LocalRotation);

    private (Vector3 Pos, Quaternion Rot) Compose(SkeletonBone bone, float bodyTime, Mix mix)
    {
        var joint = bone.JointSymbol;
        var local = BaseLocal(bone, bodyTime);

        if (mix.Strength > 0 && mix.Expression!.Bones.TryGetValue(joint, out var face))
            local = Blend(local, face, mix.Strength);

        // Mouth: ease toward the closed mouth, then a weighted average of the active shapes
        // with the remainder left on what is underneath.
        if (mix.ClosedWeight > 0 && mix.Closed is not null && mix.Closed.Bones.TryGetValue(joint, out var closed))
            local = Blend(local, closed, mix.ClosedWeight);
        float applied = mix.Leftover;
        foreach (var (pose, weight) in mix.Shapes)
        {
            if (!pose.Bones.TryGetValue(joint, out var target))
                continue;
            applied += weight;
            local = Blend(local, target, applied > 1e-6f ? weight / applied : 1f);
        }

        if (mix.GestureWeight > 0 && mix.Gesture is { } gesture && gesture.Animates(joint))
        {
            var g = gesture.SampleLocal(bone, mix.GestureTime);
            if (mix.GestureAdditive)
            {
                // Additive clips come out layered on the rest pose; carry that difference over.
                var deltaRot = Quaternion.Normalize(Quaternion.Conjugate(bone.LocalRotation) * g.Rot);
                var deltaPos = g.Pos - bone.LocalPosition;
                local = (local.Pos + deltaPos * mix.GestureWeight,
                         Quaternion.Normalize(local.Rot * Quaternion.Slerp(Quaternion.Identity, deltaRot, mix.GestureWeight)));
            }
            else
            {
                local = Blend(local, g, mix.GestureWeight);
            }
        }

        if (mix.Offsets.TryGetValue(joint, out var offset))
        {
            const float toRadians = MathF.PI / 180f;
            var turn = Quaternion.CreateFromYawPitchRoll(offset.Degrees.Y * toRadians, offset.Degrees.X * toRadians, offset.Degrees.Z * toRadians);
            local = (local.Pos + offset.Translation, Quaternion.Normalize(local.Rot * turn));
        }
        return local;
    }

    private static (Vector3 Pos, Quaternion Rot) Blend((Vector3 Pos, Quaternion Rot) from, (Vector3 Pos, Quaternion Rot) to, float t) =>
        t >= 1f ? to : (Vector3.Lerp(from.Pos, to.Pos, t), Quaternion.Slerp(from.Rot, to.Rot, t));

    /// <summary>A bone's local transform with every face layer applied.</summary>
    public (Vector3 Pos, Quaternion Rot) SampleLocal(SkeletonBone bone, float bodyTime) => Compose(bone, bodyTime, CurrentMix());

    /// <summary>
    /// World transforms of every bone: the body's own pose, with the bones the face layers touch
    /// (and everything below them) recomposed.
    /// </summary>
    public Matrix4x4[] Pose(float bodyTime)
    {
        var world = Body?.Pose(bodyTime) ?? _skeleton.BindPoseWorld();
        var mix = CurrentMix();
        var bones = _skeleton.Bones;
        var dirty = new bool[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            bool touched = Touches(bone.JointSymbol, mix);
            bool parentDirty = bone.ParentIndex >= 0 && dirty[bone.ParentIndex];
            if (!touched && !parentDirty)
                continue;
            var (pos, rot) = touched ? Compose(bone, bodyTime, mix) : BaseLocal(bone, bodyTime);
            var local = Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
            world[i] = bone.ParentIndex >= 0 ? local * world[bone.ParentIndex] : local;
            dirty[i] = true;
        }
        return world;
    }

    /// <summary>Skins the model with the face posed over the body at <paramref name="bodyTime"/>.</summary>
    public void Evaluate(float bodyTime, Vector3[] outPositions, Vector3[] outNormals) =>
        _skinner.Skin(Pose(bodyTime), outPositions, outNormals);

    /// <summary>The skinning matrices for the face pose over the body clip at <paramref name="bodyTime"/>.</summary>
    public Matrix4x4[] SkinMatrices(float bodyTime) => _skinner.SkinMatrices(Pose(bodyTime));
}
