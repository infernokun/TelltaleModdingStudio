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

    [Fact]
    public void Character_mesh_exposes_valid_skinning_data()
    {
        var (geometry, skeleton) = LoadSasha();
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

    [Fact]
    public void Rest_pose_skinning_is_identity()
    {
        var (geometry, skeleton) = LoadSasha();
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
}
