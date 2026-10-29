using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

public class MeshCombineTests
{
    private static MeshGeometry Triangle(Vector3 at) => new(
        new[] { at, at + Vector3.UnitX * 0.01f, at + Vector3.UnitY * 0.01f },
        new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ },
        new Vector2[3], new Vector4[3], new[] { 0, 1, 2 },
        new[] { new MeshBatch(0, 1, 0, 0, 2, -1, -1) },
        at, at + new Vector3(0.01f, 0.01f, 0));

    private static (MeshGeometry Head, SkeletonFile Skeleton) ClementineHead()
    {
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk62_clementine400_head.d3dmesh")), out var mesh, out var error), error);
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk62_clementine400.skl")), out var skeleton, out error), error);
        return (mesh!.GetGeometry(), skeleton!);
    }

    [SkippableFact]
    public void Combining_a_skinned_and_an_unskinned_part_stays_skinned()
    {
        var (head, _) = ClementineHead();
        var rigid = Triangle(new Vector3(0, 1.5f, 0));
        Assert.False(rigid.IsSkinned);

        var combined = MeshGeometry.Combine(new[] { head, rigid });

        Assert.True(combined.IsSkinned);
        Assert.Equal(head.Positions.Length + 3, combined.BlendWeights!.Length);
        // The unskinned part's vertices carry no weights and its batch no palette: it stays at rest.
        Assert.All(combined.BlendWeights[^3..], w => Assert.Equal(Vector4.Zero, w));
        Assert.Equal(-1, combined.Batches[^1].BonePaletteIndex);
        Assert.Equal(head.BonePalettes!.Count, combined.BonePalettes!.Count);
    }

    [SkippableFact]
    public void A_rigidly_bound_part_follows_its_bone()
    {
        var (head, skeleton) = ClementineHead();
        var bind = skeleton.BindPoseWorld();
        int bone = Enumerable.Range(0, skeleton.Bones.Count).First(i => head.BonePalettes![0].Contains(skeleton.Bones[i].JointSymbol));
        var rigid = Triangle(bind[bone].Translation).BindRigid(skeleton.Bones[bone].JointSymbol);
        Assert.True(rigid.IsSkinned);

        var combined = MeshGeometry.Combine(new[] { head, rigid });
        var posed = (Matrix4x4[])bind.Clone();
        posed[bone] = bind[bone] * Matrix4x4.CreateTranslation(1, 0, 0);
        var positions = new Vector3[combined.Positions.Length];
        var normals = new Vector3[combined.Positions.Length];
        new MeshSkinner(combined, skeleton).Skin(posed, positions, normals);

        for (int v = combined.Positions.Length - 3; v < combined.Positions.Length; v++)
            Assert.True(Vector3.Distance(combined.Positions[v] + Vector3.UnitX, positions[v]) < 1e-3f,
                $"vertex {v}: {combined.Positions[v]} -> {positions[v]}");
    }

    [SkippableFact]
    public void Packed_10_bit_positions_fill_the_declared_bounds()
    {
        // Definitive Series props store positions as UN10x3_UN2 fractions of the bounds.
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("obj_tableMedical.d3dmesh")), out var mesh, out var error), error);
        Assert.Contains(mesh!.PlatformAttributes, a => a.Attribute == GfxVertexAttribute.Position && a.Format == GfxPlatformFormat.UN10x3_UN2);
        var geometry = mesh.GetGeometry();

        var min = geometry.Positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
        var max = geometry.Positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
        var tolerance = (mesh.BoundsMax - mesh.BoundsMin) * 0.01f + new Vector3(1e-3f);
        Assert.True(Vector3.Abs(min - mesh.BoundsMin).X <= tolerance.X && Vector3.Abs(min - mesh.BoundsMin).Y <= tolerance.Y && Vector3.Abs(min - mesh.BoundsMin).Z <= tolerance.Z,
            $"min {min} vs bounds {mesh.BoundsMin}");
        Assert.True(Vector3.Abs(max - mesh.BoundsMax).X <= tolerance.X && Vector3.Abs(max - mesh.BoundsMax).Y <= tolerance.Y && Vector3.Abs(max - mesh.BoundsMax).Z <= tolerance.Z,
            $"max {max} vs bounds {mesh.BoundsMax}");
    }

    [SkippableFact]
    public void A_mesh_without_vertex_states_loads_as_empty_geometry()
    {
        // Alex's eyesMouth part ships with no geometry at all.
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk56_alex_eyesMouth.d3dmesh")), out var mesh, out var error), error);
        var geometry = mesh!.GetGeometry();
        Assert.Empty(geometry.Positions);
        Assert.Empty(geometry.Batches);
    }

    [SkippableFact]
    public void Index_only_rigid_parts_decode_as_skinned()
    {
        // A sheath stores blend indices without weights: every vertex follows one bone.
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk61_asher_swordSheath.d3dmesh")), out var mesh, out var error), error);
        var geometry = mesh!.GetGeometry();
        Assert.True(geometry.IsSkinned);
        Assert.All(geometry.BlendWeights!, w => Assert.Equal(1f, w.X));
    }
}
