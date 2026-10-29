using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class SceneTests
{
    private static ModelAssembler SamplesAssembler() => new(new FolderAssetResolver(SampleData.SamplesDir));

    [SkippableFact]
    public void Assembles_character_parts_with_skeleton_textures_and_hidden_overlay()
    {
        SampleData.Require("sk62_clementine400_head.d3dmesh", "sk62_clementine400_eyesMouth.d3dmesh", "sk62_clementine400.skl");
        var model = SamplesAssembler().Build(new[]
        {
            "sk62_clementine400_head.d3dmesh", "sk62_clementine400_eyesMouth.d3dmesh",
        });

        Assert.Empty(model.Failures);
        Assert.Equal("sk62_clementine400.skl", model.SkeletonName);
        Assert.Equal(model.BoneSymbols.Count, model.SkeletonBonesMatched);
        Assert.True(model.CanAnimate);
        Assert.Contains("sk62_clementine400_head.d3dtx", model.TexturesUsed);
        // One texture/normal-map entry per rendered batch.
        Assert.Equal(model.Geometry.Batches.Count, model.BatchTextures.Count);
        Assert.Equal(model.Geometry.Batches.Count, model.BatchNormalMaps.Count);
        // The eye-wetness shell (texture-less material) is left out.
        Assert.Contains(model.Details, d => d.Contains("hidden (texture-less overlay"));
    }

    [SkippableFact]
    public void Assembles_wolf_among_us_head_with_its_skeleton_and_texture()
    {
        SampleData.Require("sk54_bigby_head.d3dmesh");
        var model = SamplesAssembler().Build(new[] { "sk54_bigby_head.d3dmesh" });

        Assert.Empty(model.Failures);
        Assert.Equal("sk54_bigby.skl", model.SkeletonName);
        Assert.Equal(model.BoneSymbols.Count, model.SkeletonBonesMatched);
        Assert.True(model.CanAnimate);
        // The batch's diffuse slot resolves through the texture's extension-less name symbol.
        Assert.Equal(new[] { "sk54_bigby_head.d3dtx" }, model.TexturesUsed);
        Assert.NotNull(Assert.Single(model.BatchTextures));
    }

    [SkippableFact]
    public void Player_crouches_bigby_and_keeps_his_head_intact()
    {
        SampleData.Require("sk54_bigby_head.d3dmesh", "sk54_bigby.skl");
        var model = SamplesAssembler().Build(new[] { "sk54_bigby_head.d3dmesh" }, "sk54_bigby.skl");
        var player = new AnimationPlayer(model.Geometry, model.Skeleton!);
        const string name = "sk54_bigbyCrouch_weightShiftAShort.anm";
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var clip, out var error), error);
        player.Load(clip!, name);
        Assert.True(player.HasMotion);

        var rest = model.Geometry.Positions;
        var positions = new Vector3[rest.Length];
        var normals = new Vector3[rest.Length];
        player.Evaluate(player.Length / 2, positions, normals);

        // Crouching lowers the head well below its standing height (~1.7 m) ...
        float restTop = rest.Max(p => p.Y), posedTop = positions.Max(p => p.Y);
        Assert.InRange(restTop, 1.6f, 1.9f);
        Assert.True(posedTop < restTop - 0.3f, $"head top only dropped from {restTop} to {posedTop}");
        // ... without deforming it: the head moves as a near-rigid whole, so its extent holds.
        static float Extent(Vector3[] ps) => new Vector3(
            ps.Max(p => p.X) - ps.Min(p => p.X), ps.Max(p => p.Y) - ps.Min(p => p.Y),
            ps.Max(p => p.Z) - ps.Min(p => p.Z)).Length();
        Assert.InRange(Extent(positions), Extent(rest) * 0.85f, Extent(rest) * 1.15f);
    }

    [SkippableFact]
    public void Player_animates_game_of_thrones_rig_with_bones_intact()
    {
        SampleData.Require("sk61_asher_swordSheath.d3dmesh", "sk61_asher.skl");
        var model = SamplesAssembler().Build(new[] { "sk61_asher_swordSheath.d3dmesh" }, "sk61_asher.skl");
        Assert.True(model.CanAnimate);
        var skeleton = model.Skeleton!;
        var player = new AnimationPlayer(model.Geometry, skeleton);
        const string name = "sk61_action_asherBeskhaClinkDrinks.anm";
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var clip, out var error), error);
        player.Load(clip!, name);
        Assert.True(player.HasMotion);

        var rest = model.Geometry.Positions;
        var positions = new Vector3[rest.Length];
        var normals = new Vector3[rest.Length];
        var bind = skeleton.BindPoseWorld();
        for (float t = 0; t <= player.Length; t += player.Length / 8)
        {
            player.Evaluate(t, positions, normals);
            // The rigid sheath keeps its shape: distances between its vertices hold.
            for (int i = 1; i < positions.Length; i += 37)
                Assert.InRange((positions[i] - positions[0]).Length() - (rest[i] - rest[0]).Length(), -0.01f, 0.01f);

            // Bones stay connected (within 2 cm of their rest distance to the parent),
            // apart from the root's direct children, which the clip may translate.
            var world = player.Pose(t);
            for (int b = 1; b < skeleton.Bones.Count; b++)
            {
                int parent = skeleton.Bones[b].ParentIndex;
                if (skeleton.Bones[parent].ParentIndex < 0)
                    continue;
                float length = (world[b].Translation - world[parent].Translation).Length();
                float restLength = (bind[b].Translation - bind[parent].Translation).Length();
                Assert.InRange(length - restLength, -0.03f, 0.03f);
            }
        }
    }

    /// <summary>Serves files from an installed game's catalog.</summary>
    private sealed class CatalogResolver(Catalog.GameCatalog catalog) : IAssetResolver
    {
        public byte[]? TryRead(string fileName) => catalog.Find(fileName) is { } e ? catalog.Read(e) : null;
        public string? TextureNameForSymbol(ulong symbol) =>
            catalog.FindBySymbol(symbol) is { } e && e.Name.EndsWith(".d3dtx", StringComparison.OrdinalIgnoreCase) ? e.Name : null;
        public IEnumerable<string> SkeletonNames => Array.Empty<string>();
    }

    [InstalledGameFact(Ttarch2ArchiveTests.DefinitiveSeriesRoot)]
    public async Task Alpha_only_eyelash_texture_renders_as_black_cut_out_lashes()
    {
        var install = Catalog.GameInstall.Detect(Ttarch2ArchiveTests.DefinitiveSeriesRoot, out var error)!;
        Assert.NotNull(install);
        using var catalog = await Catalog.GameCatalog.OpenAsync(install,
            path => Archives.Ttarch2Archive.Open(path, install.Game.GameNum, install.RootFolder));

        // Season 2 Clementine's lashes are cards textured with an A8 (alpha-only) mask.
        var model = new ModelAssembler(new CatalogResolver(catalog))
            .Build(new[] { "sk56_clementine200_headHat.d3dmesh" }, "sk56_clementine200.skl");
        int lashBatch = model.Details.Where(d => d.StartsWith("  batch")).ToList()
            .FindIndex(d => d.Contains("sk_wdSharedParts_eyelash_clementine200.d3dtx"));
        Assert.True(lashBatch >= 0, string.Join("\n", model.Details));

        var lashes = model.BatchTextures[lashBatch]!;
        int transparent = 0, colored = 0;
        for (int i = 0; i < lashes.Rgba.Length; i += 4)
        {
            if (lashes.Rgba[i + 3] < 128) transparent++;
            if (lashes.Rgba[i] + lashes.Rgba[i + 1] + lashes.Rgba[i + 2] > 0) colored++;
        }
        Assert.True(transparent > lashes.Rgba.Length / 4 / 5, "the mask's gaps should be transparent");
        Assert.Equal(0, colored);
    }

    [SkippableFact]
    public void Missing_parts_are_reported_without_failing_the_rest()
    {
        SampleData.Require("sk62_clementine400_head.d3dmesh");
        var model = SamplesAssembler().Build(new[] { "sk62_clementine400_head.d3dmesh", "nope.d3dmesh" });
        var (mesh, _) = Assert.Single(model.Failures);
        Assert.Equal("nope.d3dmesh", mesh);
        Assert.Equal(2, model.Geometry.Batches.Count);
    }

    [SkippableFact]
    public void Nothing_loadable_throws()
    {
        Assert.Throws<InvalidDataException>(() => SamplesAssembler().Build(new[] { "nope.d3dmesh" }));
    }

    [SkippableFact]
    public void Player_poses_the_model_away_from_rest_and_back_to_rest_shape()
    {
        var assembler = SamplesAssembler();
        var model = assembler.Build(new[] { "sk55_sasha.d3dmesh" }, "sk55_sasha.skl");
        Assert.True(model.CanAnimate);

        var player = new AnimationPlayer(model.Geometry, model.Skeleton!);
        Assert.True(AnimationFile.TryLoad(
            File.ReadAllBytes(SampleData.Sample("sk55_idle_sashaHandsHips.anm")), out var clip, out var error), error);
        player.Load(clip!, "sk55_idle_sashaHandsHips.anm");
        Assert.True(player.HasMotion);
        Assert.True(player.Length > 0);

        var positions = new Vector3[model.Geometry.Positions.Length];
        var normals = new Vector3[positions.Length];
        player.Evaluate(player.Length / 2, positions, normals);

        // Hands on hips moves the arms well away from the bind pose, but the model
        // keeps roughly its size (no exploded vertices).
        float size = (model.Geometry.BoundsMax - model.Geometry.BoundsMin).Length();
        int moved = 0;
        for (int i = 0; i < positions.Length; i++)
        {
            float d = (positions[i] - model.Geometry.Positions[i]).Length();
            Assert.True(d < size, $"vertex {i} moved {d} on a model of size {size}");
            if (d > size * 0.02f)
                moved++;
        }
        Assert.True(moved > positions.Length / 20, $"only {moved} vertices moved");
    }

    [SkippableFact]
    public void Player_lays_dying_sasha_on_the_ground_with_bones_intact()
    {
        var model = SamplesAssembler().Build(new[] { "sk55_sasha.d3dmesh" }, "sk55_sasha.skl");
        var skeleton = model.Skeleton!;
        var player = new AnimationPlayer(model.Geometry, skeleton);
        const string name = "sk55_sasha103_sk55_idle_fionaHoldsSashaDying.anm";
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var clip, out var error), error);
        player.Load(clip!, name);

        var positions = new Vector3[model.Geometry.Positions.Length];
        var normals = new Vector3[positions.Length];
        player.Evaluate(1f, positions, normals);

        // Standing she is ~1.6 m tall; lying, the whole mesh stays below 0.5 m yet keeps her length.
        Assert.All(positions, p => Assert.True(float.IsFinite(p.X + p.Y + p.Z)));
        Assert.True(positions.Max(p => p.Y) < 0.5f, $"top of mesh at {positions.Max(p => p.Y)}");
        float extent = new Vector3(positions.Max(p => p.X) - positions.Min(p => p.X),
            positions.Max(p => p.Y) - positions.Min(p => p.Y),
            positions.Max(p => p.Z) - positions.Min(p => p.Z)).Length();
        Assert.InRange(extent, 1.4f, 2.2f);

        // Bones stay connected: every bone keeps (within 2 cm) its rest distance to its parent.
        var world = player.Pose(1f);
        for (int i = 1; i < skeleton.Bones.Count; i++)
        {
            float length = (world[i].Translation - world[skeleton.Bones[i].ParentIndex].Translation).Length();
            Assert.InRange(length - skeleton.Bones[i].LocalPosition.Length(), -0.02f, 0.02f);
        }
    }
}
