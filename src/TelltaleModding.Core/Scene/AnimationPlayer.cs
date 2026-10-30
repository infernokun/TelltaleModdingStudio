using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// Poses a skeleton from one animation clip and skins a model with it. Handles the
/// three clip encodings: keyframed transform tracks, compressed pose keys (GoT /
/// Tales from the Borderlands era) and compressed pose keys 2 (Definitive Series).
/// The clip plays on its own over the rest pose: the engine's runtime IK, layering of
/// several clips and root motion (the mover node) are not reproduced, so foot-planted or
/// layered clips can differ from the game.
/// </summary>
public sealed class AnimationPlayer
{
    private readonly SkeletonFile _skeleton;
    private MeshSkinner? _skinner;
    private readonly Dictionary<ulong, (Quaternion Correction, float Length)> _translationFrames = new();

    private Dictionary<ulong, TransformTrack> _tracks = new();
    private CspkClip? _cspk;
    private Dictionary<ulong, int>? _cspkBoneIndex;
    private PoseKeys2Clip? _pose2;
    private Dictionary<ulong, int>? _pose2BoneIndex;
    private bool _additive;

    /// <param name="geometry">A skinned model (see <see cref="MeshGeometry.IsSkinned"/>).</param>
    /// <param name="skeleton">The skeleton the model is bound to.</param>
    public AnimationPlayer(MeshGeometry geometry, SkeletonFile skeleton)
    {
        _skeleton = skeleton;
        Geometry = geometry;

        // How each bone maps a compressed-pose translation to its local offset (bone length
        // and retarget rotation); the engine works these out once when a skeleton loads.
        foreach (var bone in skeleton.Bones)
            _translationFrames.TryAdd(bone.JointSymbol, CspkDecoder.TranslationFrame(bone));
    }

    /// <summary>The model being animated (rest pose).</summary>
    public MeshGeometry Geometry { get; }

    /// <summary>Length of the loaded clip in seconds (0 when none).</summary>
    public float Length { get; private set; }

    /// <summary>Short description of the loaded clip's encoding, for status text.</summary>
    public string ClipDescription { get; private set; } = "";

    /// <summary>
    /// Loads a clip, replacing the previous one. <paramref name="name"/> is the clip's
    /// file name: compressed pose v2 clips named "_add" layer onto the rest pose (the name
    /// is the only marker read for them so far); compressed pose clips carry their own
    /// additive flag.
    /// </summary>
    public void Load(AnimationFile clip, string name)
    {
        _tracks = clip.TransformTracks
            .Where(t => _skeleton.JointSymbols.Contains(t.BoneSymbol))
            .GroupBy(t => t.BoneSymbol)
            .ToDictionary(g => g.Key, g => g.First());

        bool cspkAdditive = clip.Channels.Any(c => c.TypeHash == KnownAnimationTypes.CompressedSkeletonPoseKeys
                                                   && (c.Flags & CspkDecoder.AdditiveFlag) != 0);
        _cspk = clip.PoseKeysPayload is { } blob ? CspkDecoder.TryDecode(blob, cspkAdditive) : null;
        _cspkBoneIndex = _cspk is null ? null : IndexBySymbol(_cspk.BoneSymbols);

        _pose2 = clip.PoseKeys2Payload is { } blob2 ? Cspk2Decoder.TryDecode(blob2) : null;
        _pose2BoneIndex = _pose2 is null ? null : IndexBySymbol(_pose2.BoneSymbols);

        _additive = Path.GetFileNameWithoutExtension(name).EndsWith("_add", StringComparison.OrdinalIgnoreCase);
        Length = Math.Max(0.05f, clip.Length);
        ClipDescription = _pose2 is { } p2
            ? $"compressed pose v2 · {p2.BoneSymbols.Count} bones{(_additive ? " · additive" : "")}"
            : _cspk is { } c
                ? $"{_tracks.Count} keyframed tracks + compressed pose · {c.BoneSymbols.Count(s => s != 0)} bones"
                  + (c.IsAdditive ? " · additive" : "")
                : $"{_tracks.Count} keyframed tracks";
    }

    /// <summary>True when the loaded clip animates at least one bone of this skeleton.</summary>
    public bool HasMotion =>
        _tracks.Count > 0 ||
        (_cspkBoneIndex?.Keys.Any(_skeleton.JointSymbols.Contains) ?? false) ||
        (_pose2BoneIndex?.Keys.Any(_skeleton.JointSymbols.Contains) ?? false);

    private static Dictionary<ulong, int> IndexBySymbol(IReadOnlyList<ulong> symbols)
    {
        var index = new Dictionary<ulong, int>();
        for (int i = 0; i < symbols.Count; i++)
            if (symbols[i] != 0)
                index.TryAdd(symbols[i], i);
        return index;
    }

    // Built on first use: a player that only samples bones (layered under another) never skins.
    private MeshSkinner Skinner => _skinner ??= new MeshSkinner(Geometry, _skeleton);

    /// <summary>Skins the model at <paramref name="time"/> seconds into the outputs.</summary>
    public void Evaluate(float time, Vector3[] outPositions, Vector3[] outNormals) =>
        Skinner.Skin(Pose(time), outPositions, outNormals);

    /// <summary>The skinning matrices at <paramref name="time"/> (see <see cref="MeshSkinner.SkinMatrices"/>).</summary>
    public Matrix4x4[] SkinMatrices(float time) => Skinner.SkinMatrices(Pose(time));

    /// <summary>World transforms of every skeleton bone at <paramref name="time"/>.</summary>
    public Matrix4x4[] Pose(float time) => _skeleton.ComposeWorld(bone => SampleLocal(bone, time));

    /// <summary>True when the loaded clip drives the bone named by <paramref name="jointSymbol"/>.</summary>
    public bool Animates(ulong jointSymbol) =>
        _tracks.ContainsKey(jointSymbol) ||
        (_cspkBoneIndex?.ContainsKey(jointSymbol) ?? false) ||
        (_pose2BoneIndex?.ContainsKey(jointSymbol) ?? false);

    /// <summary>A bone's local transform at <paramref name="time"/> (its rest transform when the clip leaves it alone).</summary>
    public (Vector3 Pos, Quaternion Rot) SampleLocal(SkeletonBone bone, float time)
    {
        if (_tracks.TryGetValue(bone.JointSymbol, out var track))
        {
            var (rot, pos) = track.Sample(time);
            // Next to compressed pose keys the tracks hold the same kind of values (the root
            // track repeats the pose keys' root slot) and the engine applies them the same way.
            return _cspk is null
                ? (pos, rot)
                : EngineLocal(bone, pos, rot, (track.Flags & CspkDecoder.AdditiveFlag) != 0);
        }

        // Compressed pose v2: rotations replace the local rotation; translations are
        // stored relative to the bone's rest length (1 = rest), so they scale by it.
        if (_pose2BoneIndex is not null && _pose2BoneIndex.TryGetValue(bone.JointSymbol, out int pi))
        {
            var clip2 = _pose2!;
            float restLength = bone.LocalPosition.Length();
            var rot2 = clip2.HasRotation(pi) ? clip2.SampleRotation(pi, time) : bone.LocalRotation;
            var pos2 = clip2.HasTranslation(pi) ? clip2.SampleTranslation(pi, time) * restLength : bone.LocalPosition;
            if (_additive)
                return (bone.LocalPosition + (clip2.HasTranslation(pi) ? pos2 : default),
                        Quaternion.Normalize(Quaternion.Multiply(bone.LocalRotation, rot2)));
            return (pos2, rot2);
        }

        // Compressed pose keys drive every bone they name, translation and rotation.
        if (_cspkBoneIndex is not null && _cspkBoneIndex.TryGetValue(bone.JointSymbol, out int ci))
            return EngineLocal(bone, _cspk!.SampleTranslation(ci, time), _cspk.SampleRotation(ci, time), _cspk.IsAdditive);

        return (bone.LocalPosition, bone.LocalRotation);
    }

    /// <summary>
    /// A bone's local transform from a compressed-pose-era value, the way the engine applies
    /// one: a normal value replaces the bone's rotation and (bone-length space) translation; an
    /// additive one layers onto the rest pose (translation added in the rest frame, rotation
    /// composed after the rest rotation). The translation then maps to a local offset through
    /// the bone's length and retarget rotation.
    /// </summary>
    private (Vector3 Pos, Quaternion Rot) EngineLocal(SkeletonBone bone, Vector3 translation, Quaternion rotation, bool additive)
    {
        var frame = _translationFrames[bone.JointSymbol];
        if (additive)
        {
            translation = CspkDecoder.FromLocalTranslation(bone.LocalPosition, frame)
                + Vector3.Transform(translation, bone.LocalRotation);
            rotation = Quaternion.Normalize(Quaternion.Multiply(bone.LocalRotation, rotation));
        }
        return (CspkDecoder.ToLocalTranslation(translation, frame), rotation);
    }
}
