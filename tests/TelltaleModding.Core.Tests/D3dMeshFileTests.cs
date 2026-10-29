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

    [SkippableFact]
    public void Parses_static_scenery_mesh()
    {
        var mesh = Load(SampleData.MeshSample);
        Assert.Equal("adv_arenaEntrance_meshesA.d3dmesh", mesh.Name);
        Assert.Equal(25, mesh.Version);
        Assert.Equal(6633, mesh.VertexCount);
        Assert.True(mesh.Batches.Count > 0);
        Assert.Single(mesh.TextureSymbols);
    }

    [SkippableFact]
    public void Parses_tiny_ui_mesh()
    {
        var mesh = Load(SampleData.TinyMeshSample);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(2, mesh.Batches[0].PrimitiveCount);
    }

    [SkippableFact]
    public void Parses_skinned_character_mesh()
    {
        var mesh = Load(SampleData.Sample("skB4_banditMarauder_body.d3dmesh"));
        Assert.Equal(7423, mesh.VertexCount);
        Assert.Contains(mesh.Attributes, a => a.Attribute == GfxVertexAttribute.BlendIndex);
    }

    [SkippableFact]
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

    [SkippableFact]
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

    [SkippableFact]
    public void Geometry_indices_are_valid_and_batches_cover_them()
    {
        var mesh = Load(SampleData.MeshSample);
        var geo = mesh.GetGeometry();

        Assert.All(geo.Indices, i => Assert.InRange(i, 0, mesh.VertexCount - 1));
        foreach (var batch in geo.Batches)
            Assert.True(batch.StartIndex + batch.PrimitiveCount * 3 <= geo.Indices.Length,
                "batch range exceeds index buffer");
    }

    [SkippableFact]
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

    [SkippableFact]
    public void Parses_definitive_series_v55_character_mesh()
    {
        var mesh = Load(SampleData.Sample("sk62_clementine400_head.d3dmesh"));
        Assert.Equal("sk62_clementine400_head.d3dmesh", mesh.Name);
        Assert.Equal(55, mesh.Version);
        Assert.Equal(5950, mesh.VertexCount);
        Assert.Equal(2, mesh.Batches.Count);
        Assert.Equal(86, mesh.BoneSymbols.Count);
        Assert.Single(mesh.MaterialResources);
        Assert.Contains(mesh.PlatformAttributes, a => a.Attribute == GfxVertexAttribute.Position &&
                                                      a.Format == GfxPlatformFormat.UN16x4);
    }

    [SkippableFact]
    public void V55_geometry_is_inside_bounds_with_valid_indices_and_unit_normals()
    {
        var mesh = Load(SampleData.Sample("sk62_clementine400_head.d3dmesh"));
        var geo = mesh.GetGeometry();

        Assert.Equal(mesh.VertexCount, geo.Positions.Length);
        // Positions are UN16-quantized, so allow a quantization step of slack.
        var min = mesh.BoundsMin - new Vector3(1e-3f);
        var max = mesh.BoundsMax + new Vector3(1e-3f);
        foreach (var p in geo.Positions)
            Assert.True(p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z &&
                        p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z,
                $"position {p} outside bounds {mesh.BoundsMin}..{mesh.BoundsMax}");

        Assert.All(geo.Indices, i => Assert.InRange(i, 0, mesh.VertexCount - 1));
        foreach (var batch in geo.Batches)
            Assert.True(batch.StartIndex + batch.PrimitiveCount * 3 <= geo.Indices.Length,
                "batch range exceeds index buffer");

        Assert.All(geo.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
        Assert.All(geo.TexCoords, uv => Assert.True(uv.X is >= -0.01f and <= 1.01f && uv.Y is >= -0.01f and <= 1.01f));
    }

    [SkippableTheory]
    [InlineData("sk62_clementine400_head.d3dmesh")]
    // Its shadow index buffer omits the eye-wetness batch, so reading batches from
    // the wrong buffer shifts every later batch onto another part's triangles.
    [InlineData("sk62_clementine400_eyesMouth.d3dmesh")]
    public void V55_batches_index_only_their_own_vertex_range(string sample)
    {
        var geo = Load(SampleData.Sample(sample)).GetGeometry();
        foreach (var batch in geo.Batches)
        {
            Assert.True(batch.StartIndex + batch.PrimitiveCount * 3 <= geo.Indices.Length,
                "batch range exceeds index buffer");
            for (int i = batch.StartIndex; i < batch.StartIndex + batch.PrimitiveCount * 3; i++)
                Assert.InRange(geo.Indices[i], batch.MinVertex, batch.MaxVertex);
        }
    }

    [SkippableFact]
    public void Combine_merges_parts_with_remapped_indices_batches_and_palettes()
    {
        var head = Load(SampleData.Sample("sk62_clementine400_head.d3dmesh")).GetGeometry();
        var body = Load(SampleData.Sample("skB4_banditMarauder_body.d3dmesh")).GetGeometry();
        var combined = MeshGeometry.Combine(new[] { head, body });

        Assert.Equal(head.Positions.Length + body.Positions.Length, combined.Positions.Length);
        Assert.Equal(head.Batches.Count + body.Batches.Count, combined.Batches.Count);
        Assert.All(combined.Indices, i => Assert.InRange(i, 0, combined.Positions.Length - 1));

        // The second part's batches point past the first part's indices and vertices.
        var firstBodyBatch = combined.Batches[head.Batches.Count];
        Assert.Equal(body.Batches[0].StartIndex + head.Indices.Length, firstBodyBatch.StartIndex);
        Assert.Equal(body.Batches[0].MinVertex + head.Positions.Length, firstBodyBatch.MinVertex);
        Assert.Equal(body.Indices[body.Batches[0].StartIndex] + head.Positions.Length,
            combined.Indices[firstBodyBatch.StartIndex]);
        Assert.Equal(body.Batches[0].BonePaletteIndex + head.BonePalettes!.Count, firstBodyBatch.BonePaletteIndex);

        // Both parts are skinned, so the merged model keeps per-vertex skinning data.
        Assert.True(combined.IsSkinned);
        Assert.Equal(combined.Positions.Length, combined.BlendWeights!.Length);
        Assert.Equal(Vector3.Min(head.BoundsMin, body.BoundsMin), combined.BoundsMin);
    }

    [SkippableFact]
    public void V55_material_references_textures_by_name_with_extension()
    {
        var mesh = Load(SampleData.Sample("sk62_clementine400_head.d3dmesh"));

        // The material lists grime, decal, wrinkle and blended-normal maps ahead of
        // the diffuse; v55 symbols hash the file name including ".d3dtx".
        string[] textureNames =
        {
            "sk62_clementine400_headWrinkleB_mask", "sk62_clementine400_head_nm", "sk62_clementine400_head_grime",
            "sk62_clementine400_headDecal", "sk62_clementine400_head_bnm", "sk62_clementine400_head",
            "sk62_clementine400_headDecal_nm", "sk62_clementine400_head_spec",
        };
        var index = textureNames.ToDictionary(
            n => TelltaleModding.Core.Meta.TelltaleCrc64.HashSymbol(n + ".d3dtx"), n => n + ".d3dtx");

        var (_, payload) = Assert.Single(mesh.MaterialResources);
        Assert.Equal("sk62_clementine400_head.d3dtx", MaterialTextureLocator.PickDiffuseTexture(payload, index));
        Assert.Equal("sk62_clementine400_head_nm.d3dtx", MaterialTextureLocator.PickNormalMap(payload, index));
    }

    [SkippableFact]
    public void Parses_wolf_among_us_legacy_mesh()
    {
        var mesh = Load(SampleData.Sample("sk54_bigby_head.d3dmesh"));
        Assert.Equal("sk54_bigby_head.d3dmesh", mesh.Name);
        Assert.Equal(13, mesh.Version);
        Assert.Equal(1185, mesh.VertexCount);
        var batch = Assert.Single(mesh.Batches);
        Assert.Equal(2068, batch.PrimitiveCount);

        // One interleaved buffer: float position, three float weights (the fourth implied),
        // signed-normalized normal, tangent and UVs.
        Assert.Single(mesh.VertexBuffers);
        Assert.Contains(mesh.LegacyComponents, c => c.Semantic == LegacyVertexSemantic.Position &&
                                                    c.Type == LegacyVertexType.Float && c.Count == 3);
        Assert.Contains(mesh.LegacyComponents, c => c.Semantic == LegacyVertexSemantic.BlendWeight &&
                                                    c.Type == LegacyVertexType.Float && c.Count == 3);

        // The batch's diffuse slot points at the head colour texture's handle.
        Assert.Equal(TelltaleModding.Core.Meta.TelltaleCrc64.HashSymbol("sk54_bigby_head.d3dtx"),
            mesh.TextureSymbols[batch.DiffuseTextureIndex]);
    }

    [SkippableFact]
    public void Parses_game_of_thrones_legacy_mesh()
    {
        var mesh = Load(SampleData.Sample("sk61_bannermanWhitehillLow_handsBare.d3dmesh"));
        Assert.Equal(17, mesh.Version);
        Assert.True(mesh.Batches.Count > 0);
        // Two buffers: skinning data in the first, UVs and tangents in the second.
        Assert.Equal(2, mesh.VertexBuffers.Count);
        Assert.Contains(mesh.LegacyComponents, c => c.Semantic == LegacyVertexSemantic.BlendWeight &&
                                                    c.Type == LegacyVertexType.UN16 && c.Count == 4);
        Assert.Contains(mesh.LegacyComponents, c => c.Semantic == LegacyVertexSemantic.TexCoord && c.BufferIndex == 1);
        Assert.All(mesh.Batches, b => Assert.InRange(b.DiffuseTextureIndex, 0, mesh.TextureSymbols.Count - 1));
        Assert.NotEmpty(mesh.BoneSymbols);
    }

    [SkippableTheory]
    [InlineData("sk54_bigby_head.d3dmesh")]
    [InlineData("sk61_bannermanWhitehillLow_handsBare.d3dmesh")]
    [InlineData("sk61_asher_swordSheath.d3dmesh")]
    public void Legacy_geometry_is_inside_bounds_with_valid_indices_unit_normals_and_uvs(string sample)
    {
        var mesh = Load(SampleData.Sample(sample));
        var geo = mesh.GetGeometry();

        Assert.Equal(mesh.VertexCount, geo.Positions.Length);
        var min = mesh.BoundsMin - new Vector3(1e-3f);
        var max = mesh.BoundsMax + new Vector3(1e-3f);
        foreach (var p in geo.Positions)
            Assert.True(p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z &&
                        p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z,
                $"position {p} outside bounds {mesh.BoundsMin}..{mesh.BoundsMax}");

        foreach (var batch in geo.Batches)
        {
            Assert.True(batch.StartIndex + batch.PrimitiveCount * 3 <= geo.Indices.Length,
                "batch range exceeds index buffer");
            for (int i = batch.StartIndex; i < batch.StartIndex + batch.PrimitiveCount * 3; i++)
                Assert.InRange(geo.Indices[i], batch.MinVertex, batch.MaxVertex);
        }

        Assert.All(geo.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
        // Signed-normalized UVs times the mesh's diffuse UV scale: The Wolf Among Us stores
        // some as -1..0 and some at half range (scale ~2), relying on the GPU's wrap.
        var scale = mesh.LegacyUvScales[0] + new Vector2(1e-3f);
        Assert.All(geo.TexCoords, uv => Assert.True(Math.Abs(uv.X) <= scale.X && Math.Abs(uv.Y) <= scale.Y, $"uv {uv}"));

        // Tangents are perpendicular to their normals.
        for (int i = 0; i < geo.Positions.Length; i++)
            if (geo.Tangents[i] != Vector4.Zero)
                Assert.InRange(Vector3.Dot(new Vector3(geo.Tangents[i].X, geo.Tangents[i].Y, geo.Tangents[i].Z), geo.Normals[i]), -0.05f, 0.05f);
    }

    [SkippableFact]
    public void Legacy_uv_scale_restores_half_range_uvs()
    {
        // Bigby's head stores U at half range with a diffuse UV scale of ~2; unscaled, it
        // would sample only half of its texture.
        var mesh = Load(SampleData.Sample("sk54_bigby_head.d3dmesh"));
        Assert.InRange(mesh.LegacyUvScales[0].X, 1.9f, 2.1f);
        var geo = mesh.GetGeometry();
        float uSpan = geo.TexCoords.Max(t => t.X) - geo.TexCoords.Min(t => t.X);
        Assert.True(uSpan > 0.9f, $"head UVs span {uSpan} of the texture width");
    }

    [SkippableFact]
    public void Rigid_legacy_part_binds_each_vertex_to_one_bone()
    {
        // The sword sheath stores blend indices but no weights.
        var mesh = Load(SampleData.Sample("sk61_asher_swordSheath.d3dmesh"));
        Assert.DoesNotContain(mesh.LegacyComponents, c => c.Semantic == LegacyVertexSemantic.BlendWeight);
        var geo = mesh.GetGeometry();
        Assert.True(geo.IsSkinned);
        Assert.All(geo.BlendWeights!, w => Assert.Equal(new Vector4(1, 0, 0, 0), w));
    }

    [SkippableFact]
    public void Rejects_texture_files()
    {
        Assert.False(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.TinyTextureSample), out _, out var error));
        Assert.Contains("D3DMesh", error);
    }

    [SkippableFact]
    public void Legacy_detail_stream_carries_the_ink_line_uvs()
    {
        // The Wolf Among Us draws its ink lines through a separate detail-map stream with
        // its own UV scale (1), not through the colour UVs (scaled ~2 on U).
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_bigby_head.d3dmesh")), out var mesh, out var error), error);
        var colour = mesh!.GetGeometry().TexCoords;
        var detail = mesh.GetTexCoords(1);
        Assert.NotNull(detail);
        Assert.Equal(colour.Length, detail!.Length);
        Assert.InRange(detail.Max(uv => uv.X), 0.5f, 1.05f);
        Assert.True(colour.Max(uv => uv.X) > 1.2f, "colour UVs keep their ×2 scale");
    }
}
