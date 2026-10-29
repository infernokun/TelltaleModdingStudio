using System.Buffers.Binary;
using System.Numerics;
using System.Text.RegularExpressions;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// One mouth shape in a voice line's lip-sync track: the phoneme (a symbol of its name,
/// e.g. <c>AA</c>) eases in from <see cref="Time"/>, holds, eases out, at up to
/// <see cref="Contribution"/> strength.
/// </summary>
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
/// Reads the phoneme track of a voice line's lip-sync clip (<c>&lt;line id&gt;.anm</c>,
/// a <c>KeyframedValue&lt;PhonemeKey&gt;</c> channel). Each sample is a time, an
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
/// A character's mouth shapes: one single-frame pose clip per phoneme
/// (<c>rhys_phoneme_AA.anm</c>, or a mood variant such as <c>rhys_phoneme_AA_smugA.anm</c>)
/// and optional facial expressions (<c>rhys_phonemes_smugA.anm</c>).
/// </summary>
public static partial class PhonemePoses
{
    /// <summary>The phoneme names the games use.</summary>
    public static readonly IReadOnlyList<string> Phonemes =
        new[] { "default", "AA", "EE", "FV", "I", "LL", "MM", "NN", "O", "SH", "TH", "U" };

    /// <summary>
    /// The prefixes that have phoneme clips among <paramref name="fileNames"/>
    /// (<c>rhys</c> for <c>rhys_phoneme_AA.anm</c>).
    /// </summary>
    public static IReadOnlySet<string> Prefixes(IEnumerable<string> fileNames)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames)
        {
            var m = PoseName().Match(name);
            if (m.Success)
                prefixes.Add(m.Groups[1].Value);
        }
        return prefixes;
    }

    /// <summary>
    /// The pose clip file name for each phoneme of <paramref name="prefix"/> in the mood
    /// <paramref name="mood"/> (falling back to the neutral clip per phoneme).
    /// </summary>
    public static Dictionary<ulong, string> ClipNames(string prefix, string? mood, Func<string, bool> exists)
    {
        var clips = new Dictionary<ulong, string>();
        foreach (var phoneme in Phonemes)
        {
            var neutral = $"{prefix}_phoneme_{phoneme}.anm";
            var moody = mood is null ? null : $"{prefix}_phoneme_{phoneme}_{mood}.anm";
            var name = moody is not null && exists(moody) ? moody : exists(neutral) ? neutral : null;
            if (name is not null)
                clips[TelltaleCrc64.HashSymbol(phoneme)] = name;
        }
        return clips;
    }

    [GeneratedRegex(@"^(.+?)_phoneme_(?:default|AA|EE|FV|I|LL|MM|NN|O|SH|TH|U)\.anm$", RegexOptions.IgnoreCase)]
    private static partial Regex PoseName();
}

/// <summary>
/// Animates a talking character: an optional body clip loops underneath, an optional
/// expression clip sets the face, and the line's phoneme keys blend the mouth between
/// the character's phoneme poses (the <c>default</c> pose is the closed mouth they
/// blend from).
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
    private IReadOnlyList<PhonemeKey> _keys = Array.Empty<PhonemeKey>();

    /// <param name="poses">Phoneme symbol → its single-frame pose clip.</param>
    /// <param name="expression">A facial expression clip, sampled at its first frame.</param>
    /// <param name="body">A body clip (idle) that loops under the face.</param>
    public LipSyncPlayer(MeshGeometry geometry, SkeletonFile skeleton, IReadOnlyDictionary<ulong, AnimationFile> poses,
        AnimationFile? expression = null, (AnimationFile Clip, string Name)? body = null)
    {
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
        // Phoneme weights at this instant; whatever the keys leave over goes to the closed mouth.
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
            if (_poses.TryGetValue(DefaultPhoneme, out var closed) && closed.TryGetValue(bone.JointSymbol, out var rest))
                local = rest;

            // Blend toward each active mouth shape in turn, weighted by its share.
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
