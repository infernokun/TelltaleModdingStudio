using System.Numerics;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>The Face tab's hooks into the character viewer.</summary>
public sealed partial class CharactersViewModel
{
    /// <summary>Facial poses, mouth shapes, gestures and lip-sync for the shown character.</summary>
    public FaceViewModel Face { get; } = new();

    /// <summary>Raised with a camera distance when the view should frame the face.</summary>
    public event Action<float>? FaceFrameRequested;

    private void InitFace()
    {
        // Face edits redraw the frame; while a clip plays its own timer does.
        Face.PoseChanged += () =>
        {
            if (!IsPlaying)
                RenderFrame((float)CurrentTime);
        };
        Face.FrameFaceRequested += distance => FaceFrameRequested?.Invoke(distance);
    }

    private void UseFaceSource(GameCatalog? catalog, IAssetResolver assets, IEnumerable<string> fileNames) =>
        Face.UseSource(catalog, assets.TryRead, fileNames);

    /// <summary>
    /// Every bone's local transform as shown (face over the body clip at the current time),
    /// for export as a single-frame "face pose" clip; null when the face isn't posed.
    /// </summary>
    private IReadOnlyDictionary<ulong, (Vector3 Pos, Quaternion Rot)>? FacePoseSnapshot()
    {
        if (Face.Rig is not { } rig || !Face.IsActive || rig.Geometry != _model?.Geometry)
            return null;
        rig.Body = _clip is null ? null : _player;
        float time = (float)CurrentTime;
        var pose = new Dictionary<ulong, (Vector3, Quaternion)>();
        foreach (var bone in rig.Skeleton.Bones)
            pose.TryAdd(bone.JointSymbol, rig.SampleLocal(bone, time));
        return pose;
    }

    /// <summary>
    /// The frame's skinning matrices through the face rig when the face differs from the
    /// body clip; null leaves the frame to the body clip alone.
    /// </summary>
    private Matrix4x4[]? FaceSkinMatrices(float time)
    {
        if (Face.Rig is not { } rig || !Face.IsActive || rig.Geometry != _player?.Geometry)
            return null;
        rig.Body = _clip is null ? null : _player;
        return rig.SkinMatrices(time);
    }
}
