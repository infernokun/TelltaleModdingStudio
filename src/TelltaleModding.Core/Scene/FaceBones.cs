using System.Numerics;
using TelltaleModding.Core.Export;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Scene;

/// <summary>Where on the face a bone sits, for grouping controls.</summary>
public enum FaceBoneGroup { JawMouth, Lips, Brows, Eyes, Cheeks, Other }

/// <summary>A bone of the face rig with a readable label.</summary>
/// <param name="Index">Index in the skeleton's bones.</param>
/// <param name="Name">The recovered bone name, or <c>bone_&lt;hex&gt;</c>.</param>
public sealed record FaceBone(ulong Joint, int Index, string Name, string Label, FaceBoneGroup Group);

/// <summary>
/// Finds a skeleton's face bones: those the character's facial clips drive, plus bones whose
/// recovered names place them on the face (jaw, brows, eyes, lids, cheeks, lips…).
/// </summary>
public static class FaceBones
{
    /// <summary>The face bones of <paramref name="skeleton"/>, ordered by group then label.</summary>
    /// <param name="driven">Joint symbols moved by the character's expression and mouth clips.</param>
    public static IReadOnlyList<FaceBone> Find(SkeletonFile skeleton, IEnumerable<ulong>? driven = null)
    {
        var drivenSet = driven?.ToHashSet() ?? new HashSet<ulong>();
        var bones = skeleton.Bones;
        var groups = new FaceBoneGroup?[bones.Count];
        var namedAncestor = new int[bones.Count];
        var result = new List<FaceBone>();
        var unplaced = new List<int>();
        for (int i = 0; i < bones.Count; i++)
        {
            var name = BoneNames.TryGet(bones[i].JointSymbol);
            int parent = bones[i].ParentIndex;
            var group = name is null ? null : GroupOf(name);
            namedAncestor[i] = name is not null ? i : parent >= 0 ? namedAncestor[parent] : -1;
            // An unnamed bone sits with its nearest face-group ancestor.
            if (group is null && name is null && parent >= 0)
                group = groups[parent];
            groups[i] = group;

            bool include = name is null ? drivenSet.Contains(bones[i].JointSymbol) : group is not null;
            if (!include)
                continue;
            if (name is not null)
            {
                result.Add(new FaceBone(bones[i].JointSymbol, i, name, Label(name), group!.Value));
            }
            else if (group is not null && namedAncestor[i] >= 0)
            {
                var anchor = BoneNames.For(bones[namedAncestor[i]].JointSymbol);
                result.Add(new FaceBone(bones[i].JointSymbol, i, BoneNames.For(bones[i].JointSymbol), Label(anchor) + " ·", group.Value));
            }
            else
            {
                unplaced.Add(i);
            }
        }
        result.AddRange(PlaceByPosition(skeleton, unplaced));
        return Numbered(result).OrderBy(b => b.Group)
            .ThenBy(b => b.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Unnamed rigs: sort bones into face regions by where they sit relative to the head
    // (models are Y-up, facing +Z, with the character's left on +X).
    private static IEnumerable<FaceBone> PlaceByPosition(SkeletonFile skeleton, List<int> indices)
    {
        if (indices.Count == 0)
            yield break;
        var bones = skeleton.Bones;
        var world = skeleton.BindPoseWorld();
        int head = -1;
        for (int i = 0; i < bones.Count && head < 0; i++)
            if (BoneNames.TryGet(bones[i].JointSymbol) == "head")
                head = i;
        var origin = head >= 0 ? world[head].Translation
            : indices.Aggregate(Vector3.Zero, (sum, i) => sum + world[i].Translation) / indices.Count;
        var at = indices.ToDictionary(i => i, i => world[i].Translation - origin);
        bool Under(int bone, int ancestor)
        {
            for (int p = bones[bone].ParentIndex; p >= 0; p = bones[p].ParentIndex)
                if (p == ancestor)
                    return true;
            return false;
        }

        // Face bones sit on the centre line or in mirrored pairs; hair, ears and the like
        // usually don't, so they stay out of the face's extents.
        const float tolerance = 0.004f;
        bool Symmetric(int i) => MathF.Abs(at[i].X) < tolerance ||
            indices.Any(o => o != i && Vector3.Distance(at[o], at[i] * new Vector3(-1, 1, 1)) < tolerance);
        var face = indices.Where(i => (head < 0 || Under(i, head)) && Symmetric(i)).ToList();
        if (face.Count == 0)
            face = indices;
        foreach (int i in indices.Except(face))
            yield return new FaceBone(bones[i].JointSymbol, i, BoneNames.For(bones[i].JointSymbol), "Head", FaceBoneGroup.Other);
        indices = face;

        float yMin = indices.Min(i => at[i].Y), yMax = indices.Max(i => at[i].Y), size = yMax - yMin;
        float xMax = Math.Max(1e-4f, indices.Max(i => MathF.Abs(at[i].X))), zMax = indices.Max(i => at[i].Z);

        // The jaw: the lowest-region bone that carries other face bones.
        int jaw = -1;
        if (size > 1e-3f)
        {
            jaw = indices.Where(i => (at[i].Y - yMin) / size < 0.45f && indices.Any(c => bones[c].ParentIndex == i))
                .OrderBy(i => at[i].Z).DefaultIfEmpty(-1).First();
        }
        foreach (int i in indices)
        {
            var p = at[i];
            string side = MathF.Abs(p.X) < 0.08f * xMax ? "" : p.X > 0 ? " (left)" : " (right)";
            float v = size > 1e-3f ? (p.Y - yMin) / size : 0.5f;
            (FaceBoneGroup group, string label) = size <= 1e-3f ? (FaceBoneGroup.Other, "Face")
                : i == jaw ? (FaceBoneGroup.JawMouth, "Jaw")
                : v >= 0.6f ? (p.Y >= yMax - 0.06f * size ? (FaceBoneGroup.Brows, "Brow") : (FaceBoneGroup.Eyes, "Eye area"))
                : v >= 0.4f ? (MathF.Abs(p.X) >= 0.3f * xMax ? (FaceBoneGroup.Cheeks, "Cheek upper") : (FaceBoneGroup.Other, "Nose"))
                : MathF.Abs(p.X) >= 0.75f * xMax && p.Z < zMax - 0.3f * size ? (FaceBoneGroup.Cheeks, "Cheek lower")
                : jaw >= 0 && Under(i, jaw)
                    ? (side.Length == 0 && !indices.Any(o => o != i && Under(o, jaw) && at[o].Y < p.Y && MathF.Abs(at[o].X) < 0.08f * xMax)
                        ? (FaceBoneGroup.JawMouth, "Chin") : (FaceBoneGroup.Lips, "Lip lower"))
                    : (FaceBoneGroup.Lips, "Lip upper");
            yield return new FaceBone(bones[i].JointSymbol, i, BoneNames.For(bones[i].JointSymbol), label + side, group);
        }
    }

    // Repeated labels get numbers, in skeleton order.
    private static IEnumerable<FaceBone> Numbered(List<FaceBone> bones) =>
        bones.GroupBy(b => b.Label).SelectMany(g => g.Count() == 1 && !g.Key.EndsWith('·')
            ? g.Select(b => b with { Label = b.Label.TrimEnd(' ', '·') })
            : g.OrderBy(b => b.Index).Select((b, n) => b with { Label = $"{g.Key.TrimEnd(' ', '·')} {n + 1}" }));

    /// <summary>The face group of a recovered bone name, or null for bones off the face (head, neck…).</summary>
    public static FaceBoneGroup? GroupOf(string boneName)
    {
        var n = boneName.EndsWith("_L", StringComparison.Ordinal) || boneName.EndsWith("_R", StringComparison.Ordinal)
            ? boneName[..^2] : boneName;
        if (n is "jaw" or "chin" or "chinOuter" || n.StartsWith("tongue", StringComparison.Ordinal))
            return FaceBoneGroup.JawMouth;
        if (n.StartsWith("lip", StringComparison.Ordinal) || n.StartsWith("mouth", StringComparison.Ordinal))
            return FaceBoneGroup.Lips;
        if (n.StartsWith("brow", StringComparison.Ordinal) || n == "forehead")
            return FaceBoneGroup.Brows;
        if (n.StartsWith("eye", StringComparison.Ordinal))
            return FaceBoneGroup.Eyes;
        if (n.StartsWith("cheek", StringComparison.Ordinal))
            return FaceBoneGroup.Cheeks;
        if (n.StartsWith("nose", StringComparison.Ordinal) || n is "temple" or "ear")
            return FaceBoneGroup.Other;
        return null;
    }

    /// <summary>A group's heading ("Jaw & mouth").</summary>
    public static string GroupLabel(FaceBoneGroup group) => group switch
    {
        FaceBoneGroup.JawMouth => "Jaw & mouth",
        FaceBoneGroup.Lips => "Lips",
        FaceBoneGroup.Brows => "Brows",
        FaceBoneGroup.Eyes => "Eyes & lids",
        FaceBoneGroup.Cheeks => "Cheeks",
        _ => "Other",
    };

    /// <summary>"browOuter_L" → "Brow outer (left)".</summary>
    public static string Label(string boneName)
    {
        string side = "";
        if (boneName.EndsWith("_L", StringComparison.Ordinal))
            (boneName, side) = (boneName[..^2], " (left)");
        else if (boneName.EndsWith("_R", StringComparison.Ordinal))
            (boneName, side) = (boneName[..^2], " (right)");
        return FaceClipSet.Prettify(boneName) + side;
    }
}
