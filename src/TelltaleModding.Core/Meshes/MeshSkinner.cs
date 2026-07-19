using System.Numerics;

namespace TelltaleModding.Core.Meshes;

/// <summary>
/// CPU skinning of a mesh against a skeleton pose. Mesh vertices are authored in
/// model space at the skeleton's bind pose, so skinning applies
/// inverse-bind × posed-world per influencing bone: with the rest pose in, the
/// output equals the input (verified in tests — this pins down the palette
/// mapping and the bind-pose source).
/// </summary>
public sealed class MeshSkinner
{
    private readonly MeshGeometry _geometry;
    private readonly SkeletonFile _skeleton;
    private readonly Matrix4x4[] _inverseBind;

    /// <summary>Per-vertex bone indices resolved into skeleton bone indices (4 per vertex, -1 = none).</summary>
    private readonly int[] _vertexBones;

    public MeshSkinner(MeshGeometry geometry, SkeletonFile skeleton)
    {
        if (!geometry.IsSkinned || geometry.BonePalettes is null)
            throw new ArgumentException("geometry carries no skinning data");
        _geometry = geometry;
        _skeleton = skeleton;

        var bind = skeleton.BindPoseWorld();
        _inverseBind = new Matrix4x4[bind.Length];
        for (int i = 0; i < bind.Length; i++)
            Matrix4x4.Invert(bind[i], out _inverseBind[i]);

        // Resolve each vertex's palette-relative blend indices to skeleton bones.
        // Batches carry the palette index; vertices are covered via batch vertex ranges.
        _vertexBones = new int[geometry.Positions.Length * 4];
        Array.Fill(_vertexBones, -1);
        var bySymbol = skeleton.BoneIndexBySymbol;
        Span<byte> src = stackalloc byte[4];
        foreach (var batch in geometry.Batches)
        {
            if (batch.BonePaletteIndex < 0 || batch.BonePaletteIndex >= geometry.BonePalettes.Count)
                continue;
            var palette = geometry.BonePalettes[batch.BonePaletteIndex];
            int lo = Math.Max(0, batch.MinVertex);
            int hi = Math.Min(geometry.Positions.Length - 1, batch.MaxVertex);
            for (int v = lo; v <= hi; v++)
            {
                (src[0], src[1], src[2], src[3]) = geometry.BlendIndices![v];
                for (int k = 0; k < 4; k++)
                {
                    _vertexBones[v * 4 + k] =
                        src[k] < palette.Count && bySymbol.TryGetValue(palette[src[k]], out int boneIndex)
                            ? boneIndex : -1;
                }
            }
        }
    }

    /// <summary>
    /// Skins positions (and normals, rotation-only) for the given posed world
    /// transforms (from <see cref="SkeletonFile.ComposeWorld"/>).
    /// </summary>
    public void Skin(Matrix4x4[] posedWorld, Vector3[] outPositions, Vector3[] outNormals)
    {
        var skin = new Matrix4x4[_inverseBind.Length];
        for (int i = 0; i < skin.Length; i++)
            skin[i] = _inverseBind[i] * posedWorld[i];

        var positions = _geometry.Positions;
        var normals = _geometry.Normals;
        var weights = _geometry.BlendWeights!;
        Span<float> ws = stackalloc float[4];
        for (int v = 0; v < positions.Length; v++)
        {
            var w = weights[v];
            float total = w.X + w.Y + w.Z + w.W;
            if (total < 1e-4f || _vertexBones[v * 4] < 0)
            {
                outPositions[v] = positions[v];
                outNormals[v] = normals[v];
                continue;
            }

            Vector3 p = Vector3.Zero, n = Vector3.Zero;
            (ws[0], ws[1], ws[2], ws[3]) = (w.X, w.Y, w.Z, w.W);
            for (int k = 0; k < 4; k++)
            {
                if (ws[k] <= 0f)
                    continue;
                int bone = _vertexBones[v * 4 + k];
                if (bone < 0)
                    continue;
                p += Vector3.Transform(positions[v], skin[bone]) * ws[k];
                n += Vector3.TransformNormal(normals[v], skin[bone]) * ws[k];
            }
            outPositions[v] = p / total;
            outNormals[v] = n == Vector3.Zero ? normals[v] : Vector3.Normalize(n);
        }
    }
}
