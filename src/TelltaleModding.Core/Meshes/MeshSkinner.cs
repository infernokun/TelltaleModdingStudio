using System.Numerics;

namespace TelltaleModding.Core.Meshes;

/// <summary>
/// CPU skinning of a mesh against a skeleton pose. Mesh vertices are authored in
/// model space at the skeleton's bind pose, so skinning applies
/// inverse-bind × posed-world per influencing bone: with the rest pose in, the
/// output equals the input (verified in tests; this pins down the palette
/// mapping and the bind-pose source).
/// </summary>
public sealed class MeshSkinner
{
    private readonly MeshGeometry _geometry;
    private readonly SkeletonFile _skeleton;
    private readonly Matrix4x4[] _inverseBind;

    /// <summary>Per-vertex bone indices resolved into skeleton bone indices (4 per vertex, -1 = none).</summary>
    private readonly int[] _vertexBones;

    /// <summary>Blend weights with the slots that name no bone cleared (see <see cref="ResolveWeights"/>).</summary>
    private readonly Vector4[] _weights;

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

        _vertexBones = ResolveVertexBones(geometry, skeleton);
        _weights = ResolveWeights(geometry.BlendWeights!, _vertexBones);
    }

    /// <summary>
    /// Each vertex's palette-relative blend indices resolved into skeleton bone indices
    /// (4 per vertex, -1 = none), through the palette of the batch that draws the vertex.
    /// A palette bone the skeleton lacks resolves to its stand-in (see <see cref="StandInBones"/>),
    /// so the vertices it moves follow the skeleton instead of staying at rest.
    /// </summary>
    public static int[] ResolveVertexBones(MeshGeometry geometry, SkeletonFile skeleton)
    {
        var bones = new int[geometry.Positions.Length * 4];
        Array.Fill(bones, -1);
        if (geometry.BlendIndices is null || geometry.BonePalettes is null)
            return bones;
        var bySymbol = skeleton.BoneIndexBySymbol;
        var standIns = StandInBones(geometry, skeleton);
        var vertexPalettes = geometry.VertexPaletteIndices();
        Span<byte> src = stackalloc byte[4];
        for (int v = 0; v < vertexPalettes.Length; v++)
        {
            if (vertexPalettes[v] < 0)
                continue;
            var palette = geometry.BonePalettes[vertexPalettes[v]];
            (src[0], src[1], src[2], src[3]) = geometry.BlendIndices[v];
            for (int k = 0; k < 4; k++)
            {
                if (src[k] >= palette.Count)
                    continue;
                ulong symbol = palette[src[k]];
                bones[v * 4 + k] =
                    bySymbol.TryGetValue(symbol, out int boneIndex) ? boneIndex
                    : standIns.TryGetValue(symbol, out int standIn) ? standIn
                    : -1;
            }
        }
        return bones;
    }

    /// <summary>
    /// The bones the mesh is skinned to that <paramref name="skeleton"/> lacks (a part made
    /// for another rig, or extra bones such as shoe or accessory bones), each mapped to the
    /// skeleton bone that lies nearest the vertices it moves (at rest), among the bones
    /// the mesh is skinned to. Without a stand-in those vertices would keep their rest
    /// position while the rest of the model animates.
    /// </summary>
    public static IReadOnlyDictionary<ulong, int> StandInBones(MeshGeometry geometry, SkeletonFile skeleton)
    {
        var result = new Dictionary<ulong, int>();
        if (geometry.BlendIndices is null || geometry.BlendWeights is null || geometry.BonePalettes is null)
            return result;
        var bySymbol = skeleton.BoneIndexBySymbol;
        var missing = new HashSet<ulong>();
        var candidates = new HashSet<int>();
        foreach (var palette in geometry.BonePalettes)
        {
            foreach (var symbol in palette)
            {
                if (bySymbol.TryGetValue(symbol, out int index))
                    candidates.Add(index);
                else
                    missing.Add(symbol);
            }
        }
        if (missing.Count == 0 || skeleton.Bones.Count == 0)
            return result;

        // Where each missing bone acts: the weighted centre of the vertices it moves.
        var centres = new Dictionary<ulong, (Vector3 Sum, float Weight)>();
        var vertexPalettes = geometry.VertexPaletteIndices();
        Span<byte> src = stackalloc byte[4];
        Span<float> ws = stackalloc float[4];
        for (int v = 0; v < vertexPalettes.Length; v++)
        {
            if (vertexPalettes[v] < 0)
                continue;
            var palette = geometry.BonePalettes[vertexPalettes[v]];
            (src[0], src[1], src[2], src[3]) = geometry.BlendIndices[v];
            var w = geometry.BlendWeights[v];
            (ws[0], ws[1], ws[2], ws[3]) = (w.X, w.Y, w.Z, w.W);
            for (int k = 0; k < 4; k++)
            {
                if (ws[k] <= 0f || src[k] >= palette.Count || !missing.Contains(palette[src[k]]))
                    continue;
                var (sum, weight) = centres.GetValueOrDefault(palette[src[k]]);
                centres[palette[src[k]]] = (sum + geometry.Positions[v] * ws[k], weight + ws[k]);
            }
        }

        var world = skeleton.BindPoseWorld();
        if (candidates.Count == 0)
            candidates.UnionWith(Enumerable.Range(0, skeleton.Bones.Count));
        // A bone's vertices lie along it, from its joint toward its children's joints, so
        // distance is measured to those segments rather than to the joint alone.
        var children = new List<int>?[skeleton.Bones.Count];
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            int parent = skeleton.Bones[i].ParentIndex;
            if (parent >= 0 && parent < children.Length)
                (children[parent] ??= new List<int>()).Add(i);
        }
        float DistanceToBone(int bone, Vector3 point)
        {
            var start = world[bone].Translation;
            float best = Vector3.DistanceSquared(start, point);
            foreach (int child in children[bone] ?? Enumerable.Empty<int>())
            {
                var segment = world[child].Translation - start;
                float lengthSquared = segment.LengthSquared();
                if (lengthSquared < 1e-12f)
                    continue;
                float t = Math.Clamp(Vector3.Dot(point - start, segment) / lengthSquared, 0f, 1f);
                best = Math.Min(best, Vector3.DistanceSquared(start + segment * t, point));
            }
            return best;
        }

        foreach (var (symbol, (sum, weight)) in centres)
        {
            if (weight <= 0f)
                continue;
            var centre = sum / weight;
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (int i in candidates)
            {
                float d = DistanceToBone(i, centre);
                if (d < bestDistance || (d == bestDistance && i < best))
                    (best, bestDistance) = (i, d);
            }
            if (best >= 0)
                result[symbol] = best;
        }
        return result;
    }

    /// <summary>
    /// The blend weights with the slots that name no skeleton bone (-1 in
    /// <paramref name="vertexBones"/>) set to zero, so a vertex blends only the bones that
    /// move it rather than shrinking toward the origin by the weight of the missing ones.
    /// </summary>
    public static Vector4[] ResolveWeights(IReadOnlyList<Vector4> weights, IReadOnlyList<int> vertexBones)
    {
        var result = new Vector4[weights.Count];
        for (int v = 0; v < result.Length; v++)
        {
            var w = weights[v];
            result[v] = new Vector4(
                vertexBones[v * 4] >= 0 ? w.X : 0f,
                vertexBones[v * 4 + 1] >= 0 ? w.Y : 0f,
                vertexBones[v * 4 + 2] >= 0 ? w.Z : 0f,
                vertexBones[v * 4 + 3] >= 0 ? w.W : 0f);
        }
        return result;
    }

    /// <summary>Each vertex's skeleton bone indices (4 per vertex, -1 = none), as <see cref="ResolveVertexBones"/> gives them.</summary>
    public IReadOnlyList<int> VertexBones => _vertexBones;

    /// <summary>
    /// The per-bone skinning matrices (inverse bind × posed world) for the given posed world
    /// transforms; a vertex's position is the weighted sum of its bones' matrices applied to
    /// its rest position (row-vector convention), divided by the sum of its weights.
    /// </summary>
    public Matrix4x4[] SkinMatrices(Matrix4x4[] posedWorld)
    {
        var skin = new Matrix4x4[_inverseBind.Length];
        for (int i = 0; i < skin.Length; i++)
            skin[i] = _inverseBind[i] * posedWorld[i];
        return skin;
    }

    /// <summary>
    /// Skins positions (and normals, rotation-only) for the given posed world
    /// transforms (from <see cref="SkeletonFile.ComposeWorld"/>).
    /// </summary>
    public void Skin(Matrix4x4[] posedWorld, Vector3[] outPositions, Vector3[] outNormals)
    {
        var skin = SkinMatrices(posedWorld);

        var positions = _geometry.Positions;
        var normals = _geometry.Normals;
        var weights = _weights;
        Span<float> ws = stackalloc float[4];
        for (int v = 0; v < positions.Length; v++)
        {
            var w = weights[v];
            float total = w.X + w.Y + w.Z + w.W;
            if (total < 1e-4f)
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
