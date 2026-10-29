using System.Numerics;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class SceneFileTests
{
    private static SceneFile LoadSample(string name) => SceneFile.Load(File.ReadAllBytes(SampleData.Sample(name)));

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual}");

    [SkippableFact]
    public void Parses_definitive_series_scene_with_location_info_transforms()
    {
        var scene = LoadSample("scene_wdc_adv_bellTower.scene");

        Assert.Equal("adv_bellTower.scene", scene.Name);
        Assert.Equal(MetaStreamFormat.Msv6, scene.Format);
        Assert.Equal(230, scene.Agents.Count);
        Assert.Equal("adv_bellTower.scene", scene.Agents[0].Name);

        // An environment mesh agent sits at the origin and inherits its own .prop.
        var tower = scene.Find("adv_towerBellTower")!;
        Assert.Equal(Vector3.Zero, tower.LocalPosition);
        Assert.Equal(TelltaleCrc64.HashSymbol("adv_towerBellTower.prop"), Assert.Single(tower.Properties.Parents));

        // A door placed by its LocationInfo transform.
        var door = scene.Find("obj_doorEntranceBellTower")!;
        AssertNear(new Vector3(1.869658f, 0.553911f, 0.30378f), door.LocalPosition);
        Assert.Equal(Quaternion.Identity, door.LocalRotation);
        Assert.Null(door.Parent);
        Assert.True(door.StartVisible);

        // Star-chart lines start hidden.
        Assert.False(scene.Find("ui_line_fish1-2")!.StartVisible);
    }

    [SkippableFact]
    public void Composes_world_transforms_through_the_parent_chain()
    {
        var scene = LoadSample("scene_wdc_adv_bellTower.scene");
        var group = scene.Find("ui_star_fishGroup")!;
        var button = scene.Find("ui_star_fishButton2")!;

        Assert.Same(group, button.Parent);
        Assert.Equal("ui_star_fishGroup", button.ParentName);
        AssertNear(new Vector3(0.5921631f, 0.34869385f, 0f), button.LocalPosition);
        // The group is rotated, so the child's offset turns with it.
        Assert.NotEqual(Quaternion.Identity, group.LocalRotation);

        var world = SceneFile.WorldTransform(button);
        var expected = Vector3.Transform(button.LocalPosition, Matrix4x4.CreateFromQuaternion(group.LocalRotation)) + group.LocalPosition;
        AssertNear(expected, world.Translation);
        AssertNear(new Vector3(-10.955424f, 11.775866f, 3.0805404f), world.Translation);

        // The first button sits on the group's origin.
        AssertNear(group.LocalPosition, SceneFile.WorldTransform(scene.Find("ui_star_fishButton1")!).Translation);
    }

    [SkippableFact]
    public void Parses_borderlands_scene_with_start_position_keys()
    {
        var scene = LoadSample("scene_tftb_adv_arenaEntrance.scene");

        Assert.Equal("adv_arenaEntrance.scene", scene.Name);
        Assert.Equal(121, scene.Agents.Count);
        var meshes = scene.Find("adv_arenaEntrance_meshesA")!;
        Assert.Equal("group_arenaEntranceParent", meshes.ParentName);
        Assert.NotNull(meshes.Parent);
        Assert.Equal(TelltaleCrc64.HashSymbol("adv_arenaEntrance_meshesA.prop"), Assert.Single(meshes.Properties.Parents));

        var glow = scene.Find("obj_glowEntranceArenaEntrance")!;
        AssertNear(new Vector3(11.169826f, 6.119132f, 33.76413f), glow.LocalPosition);
        Assert.True(Math.Abs(glow.LocalRotation.Length() - 1f) < 1e-4f);

        Assert.False(scene.Find("Zer0")!.StartVisible);
    }

    [SkippableFact]
    public void Resolves_agents_through_their_props_and_assembles_the_environment()
    {
        var scene = LoadSample("scene_tftb_adv_arenaEntrance.scene");
        var resolver = new SceneFolderAssetResolver(SampleData.SamplesDir, new Dictionary<string, string>
        {
            ["adv_arenaEntrance_meshesA.prop"] = "scene_tftb_adv_arenaEntrance_meshesA.prop",
        });
        var assembler = new SceneAssembler(resolver);
        var layout = assembler.Describe(scene);

        Assert.Equal(scene.Agents.Count, layout.Objects.Count);
        var env = layout.Objects.Single(o => o.Name == "adv_arenaEntrance_meshesA");
        Assert.Equal(new[] { "adv_arenaEntrance_meshesA.d3dmesh" }, env.VisibleMeshes);
        Assert.Equal(SceneAgentKind.Environment, env.Kind);
        Assert.True(env.ShownByDefault);
        Assert.Equal("adv_arenaEntrance_meshesA.prop", env.PropertyFiles[0]);

        var built = assembler.Build(layout, o => o.Name == "adv_arenaEntrance_meshesA");
        Assert.Empty(built.Model.Failures);
        Assert.Equal(1, built.MeshInstances);
        Assert.True(built.TriangleCount > 1000, $"{built.TriangleCount} triangles");
        Assert.All(built.BatchObjects, i => Assert.Equal(env.Agent.Index, i));
        Assert.Equal(built.Model.Geometry.Batches.Count, built.Model.BatchTextures.Count);
        // The framing bounds lie inside the full bounds.
        Assert.True(built.Model.Geometry.BoundsMax.Y <= built.FullBoundsMax.Y + 1f);
    }

    [SkippableFact]
    public void Names_scenes_and_episodes_readably()
    {
        Assert.Equal("Boarding School Exterior Dusk", SceneNaming.DisplayName("adv_boardingSchoolExteriorDusk.scene"));
        Assert.Equal("Arena Gortys Core Silo 102", SceneNaming.DisplayName("adv_arenaGortysCoreSilo102.scene"));
        Assert.Equal("Walking Dead 401", SceneNaming.Episode(@"C:\x\WDC_pc_WalkingDead401_data.ttarch2"));
        Assert.Equal("Borderlands 101", SceneNaming.Episode("Borderlands_pc_Borderlands101_data.ttarch2"));
        Assert.True(SceneNaming.IsLocation("adv_bellTower.scene"));
        Assert.False(SceneNaming.IsLocation("Menu_main.scene"));
        Assert.True(SceneAssembler.IsLightmap("adv_arenaHangar_meshesA_000.d3dtx", "adv_arenaHangar_meshesA.d3dmesh"));
        Assert.False(SceneAssembler.IsLightmap("env_gateArenaHangarB.d3dtx", "adv_arenaHangar_meshesA.d3dmesh"));
    }
}
