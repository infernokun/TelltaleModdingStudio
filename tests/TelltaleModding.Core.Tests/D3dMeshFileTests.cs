using System.Numerics;
using TelltaleModding.Core.Meshes;

namespace TelltaleModding.Core.Tests;

public class D3dMeshFileTests
{
    private static D3dMeshFile Load(string path)
    {
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(path), out var mesh, out var error), error);
        return mesh!;
    }

    [Fact]
    public void Parses_static_scenery_mesh()
    {
        var mesh = Load(SampleData.MeshSample);
        Assert.Equal("adv_arenaEntrance_meshesA.d3dmesh", mesh.Name);
        Assert.Equal(25, mesh.Version);
        Assert.Equal(6633, mesh.VertexCount);
        Assert.True(mesh.Batches.Count > 0);
        Assert.Single(mesh.TextureSymbols);
    }

    [Fact]
    public void Parses_tiny_ui_mesh()
    {
        var mesh = Load(SampleData.TinyMeshSample);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(2, mesh.Batches[0].PrimitiveCount);
    }

    [Fact]
    public void Parses_skinned_character_mesh()
    {
        var mesh = Load(SampleData.Sample("skB4_banditMarauder_body.d3dmesh"));
        Assert.Equal(7423, mesh.VertexCount);
        Assert.Contains(mesh.Attributes, a => a.Attribute == GfxVertexAttribute.BlendIndex);
    }

    [Fact]
    public void Geometry_positions_stay_inside_declared_bounds()
    {
        var mesh = Load(SampleData.MeshSample);
        var geo = mesh.GetGeometry();

        Assert.Equal(mesh.VertexCount, geo.Positions.Length);
        var min = mesh.BoundsMin - new Vector3(1e-3f);
        var max = mesh.BoundsMax + new Vector3(1e-3f);
        foreach (var p in geo.Positions)
        {
            Assert.True(p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z &&
                        p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z,
                $"position {p} outside bounds {mesh.BoundsMin}..{mesh.BoundsMax}");
        }
    }

    [Fact]
    public void Geometry_normals_are_unit_length()
    {
        var geo = Load(SampleData.MeshSample).GetGeometry();
        int checked_ = 0;
        foreach (var n in geo.Normals)
        {
            if (n == Vector3.Zero) continue;
            Assert.InRange(n.Length(), 0.9f, 1.1f);
            checked_++;
        }
        Assert.True(checked_ > 1000, "expected mostly non-zero normals");
    }

    [Fact]
    public void Geometry_indices_are_valid_and_batches_cover_them()
    {
        var mesh = Load(SampleData.MeshSample);
        var geo = mesh.GetGeometry();

        Assert.All(geo.Indices, i => Assert.InRange(i, 0, mesh.VertexCount - 1));
        foreach (var batch in geo.Batches)
            Assert.True(batch.StartIndex + batch.PrimitiveCount * 3 <= geo.Indices.Length,
                "batch range exceeds index buffer");
    }

    [Fact]
    public void Material_resources_resolve_to_per_region_diffuse_textures()
    {
        var mesh = Load(SampleData.Sample("skB4_banditMarauder_body.d3dmesh"));
        Assert.Equal(3, mesh.MaterialResources.Count);
        Assert.Equal(3, mesh.MaterialHandles.Count);

        // Simulated folder index containing this character's texture set.
        string[] textureNames =
        {
            "skB4_banditMarauder_bodyLowerColorA", "skB4_banditMarauder_bodyLower_nm", "skB4_banditMarauder_bodyLower_detail",
            "skB4_banditMarauder_bodyUpperColorA", "skB4_banditMarauder_bodyUpper_nm", "skB4_banditMarauder_bodyUpper_detail",
            "skB4_banditMarauder_headColorA", "skB4_banditMarauder_head_nm", "skB4_banditMarauder_head_detail",
        };
        var index = textureNames.ToDictionary(
            n => TelltaleModding.Core.Meta.TelltaleCrc64.HashSymbol(n), n => n + ".d3dtx");

        // Every batch's material handle must resolve to an embedded resource, and each
        // resource's diffuse must be the ColorA texture for its body region.
        var diffuses = new List<string>();
        foreach (var (nameSymbol, payload) in mesh.MaterialResources)
        {
            Assert.Contains(nameSymbol, mesh.MaterialHandles);
            var picked = MaterialTextureLocator.PickDiffuseTexture(payload, index);
            Assert.NotNull(picked);
            Assert.EndsWith("ColorA.d3dtx", picked);
            diffuses.Add(picked!);
        }
        Assert.Equal(3, diffuses.Distinct().Count());
    }

    [Fact]
    public void Rejects_texture_files()
    {
        Assert.False(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.TinyTextureSample), out _, out var error));
        Assert.Contains("D3DMesh", error);
    }
}
