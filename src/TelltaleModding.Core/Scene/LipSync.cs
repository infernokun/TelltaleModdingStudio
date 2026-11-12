using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>One mouth shape in a voice line's lip-sync track: it eases in at <see cref="Time"/>, holds, then eases out.</summary>
public readonly record struct PhonemeKey(float Time, ulong Phoneme, float FadeIn, float Hold, float FadeOut, float Contribution)
{
    /// <summary>The key's strength at <paramref name="t"/> seconds into the line.</summary>
    public float WeightAt(float t)
    {
        float local = t - Time;
        if (local < 0)
            return 0;
        float w;
        if (local < FadeIn)
            w = FadeIn > 0 ? local / FadeIn : 1;
        else if ((local -= FadeIn) < Hold)
            w = 1;
        else if ((local -= Hold) < FadeOut)
            w = 1 - local / FadeOut;
        else
            return 0;
        return w * Contribution;
    }
}

/// <summary>
/// Reads the phoneme track of a voice line's lip-sync clip. Each sample is a time, an
/// interpolation flag, a tangent mode and a 28-byte key block (size, phoneme symbol,
/// fade in, hold, fade out, contribution).
/// </summary>
public static class PhonemeTrack
{
    public static readonly ulong ChannelType = TelltaleCrc64.HashSymbol("KeyframedValue<PhonemeKey>");

    /// <summary>The phoneme keys in a lip-sync clip, by time; empty for other clips.</summary>
    public static IReadOnlyList<PhonemeKey> Read(byte[] anm)
    {
        if (!AnimationFile.TryLoad(anm, out var clip, out _) || !clip!.Channels.Any(c => c.TypeHash == ChannelType))
            return Array.Empty<PhonemeKey>();

        var keys = new List<PhonemeKey>();
        var span = anm.AsSpan();
        for (int p = 9; p + 28 <= span.Length; p++)
        {
            // time (f32) · interpolate ('0'/'1') · tangent mode (u32) · block size 28
            if (BinaryPrimitives.ReadUInt32LittleEndian(span[p..]) != 28 || span[p - 5] is not ((byte)'0' or (byte)'1'))
                continue;
            float time = F32(span, p - 9);
            float fadeIn = F32(span, p + 12), hold = F32(span, p + 16), fadeOut = F32(span, p + 20), contribution = F32(span, p + 24);
            if (!Plausible(time, 0, 600) || !Plausible(fadeIn, 0, 10) || !Plausible(hold, 0, 10) ||
                !Plausible(fadeOut, 0, 10) || !Plausible(contribution, 0, 4))
                continue;
            keys.Add(new PhonemeKey(time, BinaryPrimitives.ReadUInt64LittleEndian(span[(p + 4)..]), fadeIn, hold, fadeOut, contribution));
            p += 27;
        }
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        return keys;
    }

    private static float F32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadSingleLittleEndian(span[at..]);

    private static bool Plausible(float v, float min, float max) => float.IsFinite(v) && v >= min && v <= max;
}

/// <summary>
/// A character's mouth shapes: one single-frame pose clip per phoneme, plus optional mood
/// variants. One name can hold sets for two face rigs, so a set only counts if its clips
/// drive the model's skeleton.
/// </summary>
public static partial class PhonemePoses
{
    /// <summary>The phoneme names the games use.</summary>
    public static readonly IReadOnlyList<string> Phonemes =
        new[] { "default", "AA", "AH", "EE", "FV", "I", "LL", "MM", "NN", "O", "SH", "SS", "TH", "U" };

    /// <summary>The prefixes that have phoneme clips among <paramref name="fileNames"/>.</summary>
    public static IReadOnlySet<string> Prefixes(IEnumerable<string> fileNames) =>
        new HashSet<string>(Sets(fileNames).Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>Each prefix's mouth pose sets among <paramref name="fileNames"/>, by mood (null for neutral).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string?>> Sets(IEnumerable<string> fileNames)
    {
        var sets = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames)
        {
            var m = PoseName().Match(name);
            if (!m.Success)
                continue;
            if (!sets.TryGetValue(m.Groups[1].Value, out var moods))
                sets[m.Groups[1].Value] = moods = new List<string?>();
            string? mood = m.Groups[2].Success ? m.Groups[2].Value : null;
            if (!moods.Contains(mood, StringComparer.OrdinalIgnoreCase))
                moods.Add(mood);
        }
        return sets.ToDictionary(s => s.Key, s => (IReadOnlyList<string?>)s.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The clip used to tell a set's face rig: its open mouth.</summary>
    public static string ProbeClip(string prefix, string? mood) => ClipName(prefix, "AA", mood);

    /// <summary>
    /// The set to lip-sync with: the first prefix with a set that drives the skeleton, and its
    /// set that drives the most bones, preferring calm moods. Null when none fits.
    /// </summary>
    /// <param name="bonesDriven">How many of the skeleton's bones a pose clip drives (0 when it is missing).</param>
    public static (string Prefix, string? Mood)? ChooseSet(IEnumerable<string> prefixes,
        IReadOnlyDictionary<string, IReadOnlyList<string?>> sets, Func<string, int> bonesDriven)
    {
        foreach (var prefix in prefixes)
        {
            if (!sets.TryGetValue(prefix, out var moods))
                continue;
            var best = moods
                .Select(mood => (Mood: mood, Bones: bonesDriven(ProbeClip(prefix, mood))))
                .Where(s => s.Bones > 0)
                .OrderByDescending(s => s.Bones)
                .ThenBy(s => Calmness(s.Mood))
                .ThenBy(s => s.Mood, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (best.Count > 0)
                return (prefix, best[0].Mood);
        }
        return null;
    }

    /// <summary>
    /// The pose clip file name for each phoneme in <paramref name="mood"/>, falling back per
    /// phoneme to <paramref name="baseMood"/> (null for neutral).
    /// </summary>
    public static Dictionary<ulong, string> ClipNames(string prefix, string? mood, Func<string, bool> exists, string? baseMood = null)
    {
        var clips = new Dictionary<ulong, string>();
        foreach (var phoneme in Phonemes)
        {
            var fallback = ClipName(prefix, phoneme, baseMood);
            var moody = mood is null ? null : ClipName(prefix, phoneme, mood);
            var name = moody is not null && exists(moody) ? moody : exists(fallback) ? fallback : null;
            if (name is not null)
                clips[TelltaleCrc64.HashSymbol(phoneme)] = name;
        }
        return clips;
    }

    // Moods from calm to strained, so characters without a neutral set don't gape on every line.
    private static readonly string[] CalmMoods = { "normal", "neutral", "think", "happy", "smug", "sad", "worr", "stern" };
    private static readonly string[] StrainedMoods = { "fear", "pain", "surprise", "shock", "angry", "shout", "crazy", "dying" };

    private static int Calmness(string? mood)
    {
        if (mood is null)
            return -1;
        int calm = Array.FindIndex(CalmMoods, m => mood.StartsWith(m, StringComparison.OrdinalIgnoreCase));
        if (calm >= 0)
            return calm;
        return StrainedMoods.Any(m => mood.StartsWith(m, StringComparison.OrdinalIgnoreCase)) ? CalmMoods.Length + 1 : CalmMoods.Length;
    }

    private static string ClipName(string prefix, string phoneme, string? mood) =>
        mood is null ? $"{prefix}_phoneme_{phoneme}.anm" : $"{prefix}_phoneme_{phoneme}_{mood}.anm";

    [GeneratedRegex(@"^(.+?)_phoneme_(?:default|AA|AH|EE|FV|I|LL|MM|NN|O|SH|SS|TH|U)(?:_([A-Za-z0-9]+))?\.anm$", RegexOptions.IgnoreCase)]
    private static partial Regex PoseName();
}

/// <summary>
/// Animates a talking character: optional idle, expression and chore performance, with the
/// line's phoneme keys blending the mouth from the closed <c>default</c> pose.
/// </summary>
public sealed class LipSyncPlayer
{
    private static readonly ulong DefaultPhoneme = TelltaleCrc64.HashSymbol("default");

    private readonly SkeletonFile _skeleton;
    private readonly MeshSkinner _skinner;
    private readonly Dictionary<ulong, Dictionary<ulong, (Vector3 Pos, Quaternion Rot)>> _poses = new();
    private readonly Dictionary<ulong, (Vector3 Pos, Quaternion Rot)> _expression = new();
    private readonly AnimationPlayer? _body;
    private readonly float _bodyLength;
    private readonly ChorePerformance? _performance;
    private readonly float _performanceStart;
    private IReadOnlyList<PhonemeKey> _keys = Array.Empty<PhonemeKey>();

    /// <param name="poses">Single-frame pose clips by phoneme symbol.</param>
    /// <param name="expression">A facial expression clip, sampled at its first frame.</param>
    /// <param name="body">A body clip (idle) that loops under the face.</param>
    /// <param name="performance">The line's chore performance, layered over the idle and expression.</param>
    /// <param name="performanceStart">Where the line starts in the chore, in seconds.</param>
    public LipSyncPlayer(MeshGeometry geometry, SkeletonFile skeleton, IReadOnlyDictionary<ulong, AnimationFile> poses,
        AnimationFile? expression = null, (AnimationFile Clip, string Name)? body = null,
        ChorePerformance? performance = null, float performanceStart = 0)
    {
        _performance = performance;
        _performanceStart = performanceStart;
        _skeleton = skeleton;
        _skinner = new MeshSkinner(geometry, skeleton);
        Geometry = geometry;

        foreach (var (phoneme, clip) in poses)
            _poses[phoneme] = SampleDriven(geometry, skeleton, clip, "pose");
        if (expression is not null)
            foreach (var (bone, local) in SampleDriven(geometry, skeleton, expression, "expression"))
                _expression[bone] = local;
        if (body is { } b)
        {
            _body = new AnimationPlayer(geometry, skeleton);
            _body.Load(b.Clip, b.Name);
            _bodyLength = _body.HasMotion ? _body.Length : 0;
            if (_bodyLength <= 0)
                _body = null;
        }
    }

    public MeshGeometry Geometry { get; }

    /// <summary>The skeleton the player poses.</summary>
    public SkeletonFile Skeleton => _skeleton;

    /// <summary>
    /// How far the performance has moved the character at <paramref name="time"/>, which
    /// <see cref="Pose"/> leaves out. Row-vector convention: a posed point is at <c>p · RootMotion</c>.
    /// </summary>
    public Matrix4x4 RootMotion(float time)
    {
        if (_performance is null || _skeleton.Bones.Count == 0 || _skeleton.Bones[0].ParentIndex >= 0)
            return Matrix4x4.Identity;
        var root = _skeleton.Bones[0];
        var moved = _performance.SampleRoot(root, _performanceStart + time, (root.LocalPosition, root.LocalRotation));
        var rest = Matrix4x4.CreateFromQuaternion(root.LocalRotation) * Matrix4x4.CreateTranslation(root.LocalPosition);
        Matrix4x4.Invert(rest, out var restInverse);
        return restInverse * (Matrix4x4.CreateFromQuaternion(moved.Rot) * Matrix4x4.CreateTranslation(moved.Pos));
    }

    /// <summary>Number of phoneme poses that move at least one bone.</summary>
    public int PoseCount => _poses.Count(p => p.Value.Count > 0);

    /// <summary>Sets the voice line's phoneme keys (replacing the previous line's).</summary>
    public void SetLine(IReadOnlyList<PhonemeKey> keys) => _keys = keys;

    /// <summary>Skins the model at <paramref name="time"/> seconds into the line.</summary>
    public void Evaluate(float time, Vector3[] outPositions, Vector3[] outNormals) =>
        _skinner.Skin(Pose(time), outPositions, outNormals);

    /// <summary>The skinning matrices at <paramref name="time"/> seconds into the line.</summary>
    public Matrix4x4[] SkinMatrices(float time) => _skinner.SkinMatrices(Pose(time));

    /// <summary>World transforms of every bone at <paramref name="time"/> seconds into the line.</summary>
    public Matrix4x4[] Pose(float time)
    {
        // Phoneme weights now; whatever is left goes to the closed mouth.
        var weights = new Dictionary<ulong, float>();
        float total = 0;
        foreach (var key in _keys)
        {
            if (key.Time > time)
                break;
            float w = key.WeightAt(time);
            if (w <= 0 || !_poses.ContainsKey(key.Phoneme))
                continue;
            weights[key.Phoneme] = weights.GetValueOrDefault(key.Phoneme) + w;
            total += w;
        }
        if (total > 1)
        {
            foreach (var k in weights.Keys.ToList())
                weights[k] /= total;
            total = 1;
        }

        float bodyTime = _bodyLength > 0 ? time % _bodyLength : 0;
        var world = _skeleton.ComposeWorld(bone =>
        {
            var local = _body?.SampleLocal(bone, bodyTime) ?? (bone.LocalPosition, bone.LocalRotation);
            if (_expression.TryGetValue(bone.JointSymbol, out var face))
                local = face;
            if (_performance is not null)
                local = _performance.SampleLocal(bone, _performanceStart + time, local);
            if (_poses.TryGetValue(DefaultPhoneme, out var closed) && closed.TryGetValue(bone.JointSymbol, out var rest))
                local = rest;

            float applied = 0;
            foreach (var (phoneme, w) in weights)
            {
                if (!_poses[phoneme].TryGetValue(bone.JointSymbol, out var target))
                    continue;
                applied += w;
                float t = w / Math.Max(applied, 1e-6f);
                local = (Vector3.Lerp(local.Pos, target.Pos, t), Quaternion.Slerp(local.Rot, target.Rot, t));
            }
            return local;
        });
        return world;
    }

    // The local transforms a clip sets at its first frame, for the bones it drives.
    private static Dictionary<ulong, (Vector3 Pos, Quaternion Rot)> SampleDriven(
        MeshGeometry geometry, SkeletonFile skeleton, AnimationFile clip, string name)
    {
        var player = new AnimationPlayer(geometry, skeleton);
        player.Load(clip, name);
        var result = new Dictionary<ulong, (Vector3 Pos, Quaternion Rot)>();
        foreach (var bone in skeleton.Bones)
            if (player.Animates(bone.JointSymbol))
                result[bone.JointSymbol] = player.SampleLocal(bone, 0);
        return result;
    }
}
