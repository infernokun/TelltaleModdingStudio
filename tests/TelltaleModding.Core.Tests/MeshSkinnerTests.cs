using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

public class MeshSkinnerTests
{
    private static (MeshGeometry Geometry, SkeletonFile Skeleton) LoadSasha()
    {
        Assert.True(D3dMeshFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk55_sasha.d3dmesh")), out var mesh, out var meshError), meshError);
        Assert.True(SkeletonFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk55_sasha.skl")), out var skeleton, out var sklError), sklError);
        return (mesh!.GetGeometry(), skeleton!);
    }

    private static (MeshGeometry Geometry, SkeletonFile Skeleton) LoadClementine()
    {
        Assert.True(D3dMeshFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk62_clementine400_head.d3dmesh")), out var mesh, out var meshError), meshError);
        Assert.True(SkeletonFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk62_clementine400.skl")), out var skeleton, out var sklError), sklError);
        return (mesh!.GetGeometry(), skeleton!);
    }

    // Legacy (MSV5) meshes: The Wolf Among Us (three float weights), Game of Thrones
    // (four UN16 weights) and a rigid Game of Thrones part (blend indices only).
    private static readonly Dictionary<string, (string Mesh, string Skeleton)> LegacyCharacters = new()
    {
        ["bigby"] = ("sk54_bigby_head.d3dmesh", "sk54_bigby.skl"),
        ["bannerman"] = ("sk61_bannermanWhitehillLow_handsBare.d3dmesh", "sk61_bannermanForresterLow.skl"),
        ["asherSheath"] = ("sk61_asher_swordSheath.d3dmesh", "sk61_asher.skl"),
        // Episode 5 build: batch vertex ranges of its two palettes overlap.
        ["sasha105"] = ("sk55_sasha105.d3dmesh", "sk55_sasha.skl"),
    };

    private static (MeshGeometry Geometry, SkeletonFile Skeleton) LoadPair(string meshName, string skeletonName)
    {
        Assert.True(D3dMeshFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample(meshName)), out var mesh, out var meshError), meshError);
        Assert.True(SkeletonFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample(skeletonName)), out var skeleton, out var sklError), sklError);
        return (mesh!.GetGeometry(), skeleton!);
    }

    public static TheoryData<string> Characters => new() { "sasha", "sasha105", "clementine", "bigby", "bannerman", "asherSheath" };

    private static (MeshGeometry Geometry, SkeletonFile Skeleton) Load(string character) => character switch
    {
        "sasha" => LoadSasha(),
        "clementine" => LoadClementine(),
        _ => LoadPair(LegacyCharacters[character].Mesh, LegacyCharacters[character].Skeleton),
    };

    [SkippableFact]
    public void V55_blend_weights_decode_sorted_largest_first()
    {
        // The packed format stores each weight relative to its largest possible share,
        // which only makes sense for sorted weights; a wrong decoding breaks the order.
        var (geometry, _) = LoadClementine();
        Assert.All(geometry.BlendWeights!, w =>
        {
            Assert.True(w.X >= w.Y - 2e-3f && w.Y >= w.Z - 2e-3f && w.Z >= w.W - 2e-3f, $"unsorted weights {w}");
        });
    }

    [SkippableTheory]
    [MemberData(nameof(Characters))]
    public void Character_mesh_exposes_valid_skinning_data(string character)
    {
        var (geometry, skeleton) = Load(character);
        Assert.True(geometry.IsSkinned);
        Assert.True(geometry.BonePalettes!.Count > 0);

        // Every batch palette resolves fully into the skeleton.
        foreach (var batch in geometry.Batches)
        {
            if (batch.BonePaletteIndex < 0)
                continue;
            var palette = geometry.BonePalettes[batch.BonePaletteIndex];
            Assert.All(palette, symbol => Assert.Contains(symbol, skeleton.JointSymbols));
        }

        // Blend weights are sane: non-negative and summing to ~1 where present.
        int weighted = 0;
        foreach (var w in geometry.BlendWeights!)
        {
            float sum = w.X + w.Y + w.Z + w.W;
            if (sum < 1e-4f)
                continue;
            weighted++;
            Assert.InRange(sum, 0.95f, 1.05f);
            Assert.True(w.X >= -1e-3f && w.Y >= -1e-3f && w.Z >= -1e-3f && w.W >= -1e-3f);
        }
        Assert.True(weighted > geometry.Positions.Length / 2, "most vertices should be weighted");
    }

    // The sword sheath is excluded: it hangs rigidly off a bone at its far end.
    public static TheoryData<string> DeformingCharacters => new() { "sasha", "sasha105", "clementine", "bigby", "bannerman" };

    [SkippableTheory]
    [MemberData(nameof(DeformingCharacters))]
    public void Vertices_sit_near_their_strongest_bone(string character)
    {
        // Rest-pose skinning is the identity whichever bones the indices name, so check
        // the mapping itself: a vertex's most-weighted bone must be close by (a misread
        // index scale or palette would bind vertices to bones across the body).
        var (geometry, skeleton) = Load(character);
        var bind = skeleton.BindPoseWorld();
        var vertexPalettes = geometry.VertexPaletteIndices();
        int checkedVertices = 0, far = 0;
        for (int v = 0; v < vertexPalettes.Length; v++)
        {
            if (vertexPalettes[v] < 0)
                continue;
            var palette = geometry.BonePalettes![vertexPalettes[v]];
            var w = geometry.BlendWeights![v];
            var (a, b, c, d) = geometry.BlendIndices![v];
            (float weight, byte index)[] influences = { (w.X, a), (w.Y, b), (w.Z, c), (w.W, d) };
            var strongest = influences.MaxBy(i => i.weight);
            Assert.InRange(strongest.index, 0, palette.Count - 1);
            int bone = skeleton.BoneIndexBySymbol[palette[strongest.index]];
            checkedVertices++;
            if ((bind[bone].Translation - geometry.Positions[v]).Length() > 0.6f)
                far++;
        }
        Assert.True(checkedVertices > 0);
        Assert.True(far < checkedVertices / 50, $"{far}/{checkedVertices} vertices far from their strongest bone");
    }

    [SkippableTheory]
    [MemberData(nameof(Characters))]
    public void Rest_pose_skinning_is_identity(string character)
    {
        var (geometry, skeleton) = Load(character);
        var skinner = new MeshSkinner(geometry, skeleton);

        var outPos = new Vector3[geometry.Positions.Length];
        var outNrm = new Vector3[geometry.Positions.Length];
        skinner.Skin(skeleton.BindPoseWorld(), outPos, outNrm);

        float size = (geometry.BoundsMax - geometry.BoundsMin).Length();
        int drifted = 0;
        for (int i = 0; i < outPos.Length; i++)
        {
            if ((outPos[i] - geometry.Positions[i]).Length() > size * 0.01f)
                drifted++;
        }
        Assert.True(drifted < outPos.Length / 100,
            $"{drifted}/{outPos.Length} vertices drifted from bind pose under rest-pose skinning");
    }

    [SkippableFact]
    public void Vertices_take_the_palette_of_the_batch_that_draws_them()
    {
        // Batches of different palettes can span overlapping vertex ranges; the drawing
        // batch decides. A range-based lookup binds 478 of this mesh's vertices wrongly.
        var (geometry, _) = Load("sasha105");
        var vertexPalettes = geometry.VertexPaletteIndices();
        foreach (var batch in geometry.Batches)
        {
            for (int i = batch.StartIndex; i < batch.StartIndex + batch.PrimitiveCount * 3; i++)
                Assert.Equal(batch.BonePaletteIndex, vertexPalettes[geometry.Indices[i]]);
        }

        var lastRange = new int[geometry.Positions.Length];
        foreach (var batch in geometry.Batches)
            Array.Fill(lastRange, batch.BonePaletteIndex, batch.MinVertex, batch.MaxVertex - batch.MinVertex + 1);
        Assert.Equal(478, vertexPalettes.Where((p, v) => p != lastRange[v]).Count());
    }

    [SkippableFact]
    public void Walk_cycle_keeps_the_surface_together()
    {
        // A vertex skinned through the wrong palette follows an unrelated bone: the sleeve
        // stayed at its bind-pose spot while the arm walked away, tearing edges by ~0.3.
        // Correctly bound, no edge's length changes by more than ~0.035 over the cycle.
        var (geometry, skeleton) = Load("sasha105");
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk55_move_sashaWalk.anm")), out var clip, out var error), error);
        var player = new TelltaleModding.Core.Scene.AnimationPlayer(geometry, skeleton);
        player.Load(clip!, "sk55_move_sashaWalk.anm");

        var positions = new Vector3[geometry.Positions.Length];
        var normals = new Vector3[geometry.Positions.Length];
        var rest = geometry.Positions;
        var indices = geometry.Indices;
        float worst = 0;
        for (int frame = 0; frame < 8; frame++)
        {
            player.Evaluate(player.Length * frame / 8f, positions, normals);
            for (int i = 0; i < indices.Length; i += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    int a = indices[i + e], b = indices[i + (e + 1) % 3];
                    float change = Math.Abs((positions[a] - positions[b]).Length() - (rest[a] - rest[b]).Length());
                    worst = Math.Max(worst, change);
                }
            }
        }
        Assert.True(worst < 0.08f, $"an edge changed length by {worst:F3} during the walk");
    }

    [SkippableFact]
    public void Skin_matrices_reproduce_the_cpu_skin_with_the_shader_blend()
    {
        // The viewport skins in the vertex shader from SkinMatrices and VertexBones:
        // sum of weight × (rest × matrix) over the resolved bones, divided by the weight
        // sum; vertices without weights or an unresolved first bone stay at rest.
        var (geometry, skeleton) = Load("sasha105");
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk55_move_sashaWalk.anm")), out var clip, out var error), error);
        var player = new TelltaleModding.Core.Scene.AnimationPlayer(geometry, skeleton);
        player.Load(clip!, "sk55_move_sashaWalk.anm");
        var skinner = new MeshSkinner(geometry, skeleton);
        var bones = skinner.VertexBones;

        float time = player.Length * 0.37f;
        var expected = new Vector3[geometry.Positions.Length];
        var normals = new Vector3[geometry.Positions.Length];
        player.Evaluate(time, expected, normals);
        var skin = player.SkinMatrices(time);

        for (int v = 0; v < geometry.Positions.Length; v++)
        {
            var w = geometry.BlendWeights![v];
            float total = w.X + w.Y + w.Z + w.W;
            Vector3 p = geometry.Positions[v];
            if (total >= 1e-4f && bones[v * 4] >= 0)
            {
                p = Vector3.Zero;
                float[] ws = { w.X, w.Y, w.Z, w.W };
                for (int k = 0; k < 4; k++)
                {
                    int bone = bones[v * 4 + k];
                    if (ws[k] > 0 && bone >= 0)
                        p += Vector3.Transform(geometry.Positions[v], skin[bone]) * ws[k];
                }
                p /= total;
            }
            Assert.True((p - expected[v]).Length() < 1e-4f, $"vertex {v}: {p} vs {expected[v]}");
        }
    }
}
