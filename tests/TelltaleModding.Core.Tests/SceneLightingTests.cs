using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;
using Xunit;

namespace TelltaleModding.Core.Tests;

public class SceneLightingTests
{
    private static SceneLayout Describe(string sceneSample, Dictionary<string, string> aliases)
    {
        var resolver = new SceneFolderAssetResolver(SampleData.SamplesDir, aliases);
        var scene = SceneFile.Load(File.ReadAllBytes(SampleData.Sample(sceneSample)));
        return new SceneAssembler(resolver).Describe(scene);
    }

    private static SceneLayout BellTower() => Describe("scene_wdc_adv_bellTower.scene", new()
    {
        ["module_env_light.prop"] = "scene_wdc_module_env_light.prop",
        ["module_cin_light.prop"] = "scene_wdc_module_cin_light.prop",
    });

    private static SceneLayout ArenaEntrance() => Describe("scene_tftb_adv_arenaEntrance.scene", new()
    {
        ["light.prop"] = "scene_tftb_light.prop",
        ["module_light.prop"] = "scene_tftb_module_light.prop",
    });

    [SkippableFact]
    public void Reads_definitive_series_environment_lights()
    {
        var lighting = SceneLighting.Build(BellTower());

        Assert.Equal(29, lighting.Lights.Count);
        var moon = lighting.Lights.Single(l => l.Name == "light_ENV_exterior_moonKey");
        Assert.Equal(SceneLightType.Directional, moon.Type);
        Assert.Equal(SceneLightFamily.Environment, moon.Family);
        Assert.Equal(2.05f, moon.Intensity, 0.001f);
        Assert.True(moon.Direction.Y < -0.5f, $"moon should shine downwards, got {moon.Direction}");
        Assert.True(moon.Color.Z > moon.Color.X, "moonlight is blue");
        Assert.True(SceneLighting.IsOnByDefault(moon));

        var ambient = lighting.Lights.Single(l => l.Name == "light_SKY_amb");
        Assert.Equal(SceneLightType.Ambient, ambient.Type);
        Assert.Equal(3000f, ambient.Range);
        Assert.False(ambient.AffectsMainGeometry); // sky dome group only

        var spot = lighting.Lights.Single(l => l.Name == "light_ENV_exterior_moon_spot");
        Assert.Equal(SceneLightType.Spot, spot.Type);
        Assert.Equal(30f, spot.SpotInnerDegrees, 0.001f);
        Assert.Equal(40f, spot.SpotOuterDegrees, 0.001f);
        Assert.Equal(40f, spot.ToLightSource().OuterConeDegrees, 0.001f);
    }

    [SkippableFact]
    public void Character_rig_lights_are_kept_but_left_out_of_the_default_rig()
    {
        var lighting = SceneLighting.Build(BellTower());

        var rim = lighting.Lights.Single(l => l.Name == "light_CHAR_CC_Willy_Rim");
        Assert.Equal(SceneLightFamily.Cinematic, rim.Family);
        Assert.True(rim.IsCharacterLight);
        Assert.Equal(2f, rim.Intensity, 0.001f);
        Assert.DoesNotContain(lighting.DefaultLights, l => l.IsCharacterLight);
    }

    [SkippableFact]
    public void Default_rig_holds_at_most_eight_lights_directional_first()
    {
        var lighting = SceneLighting.Build(BellTower());
        var rig = lighting.BuildRig();

        Assert.InRange(rig.Lights.Count, 1, SceneLighting.MaxRigLights);
        Assert.Equal(LightKind.Directional, rig.Lights[0].Kind);
        Assert.Equal("light_ENV_exterior_moonKey", rig.Lights[0].Name);
        Assert.Equal("adv_bellTower.scene", rig.Source);
        Assert.Equal(3, lighting.BuildRig(lighting.DefaultLights, maxLights: 3).Lights.Count);
        Assert.Empty(lighting.BuildRig(Array.Empty<SceneLight>()).Lights);
    }

    [SkippableFact]
    public void Reads_older_game_lights_with_inherited_defaults()
    {
        var lighting = SceneLighting.Build(ArenaEntrance());

        Assert.Equal(27, lighting.Lights.Count);
        Assert.All(lighting.Lights, l => Assert.Equal(SceneLightFamily.Legacy, l.Family));

        var amb = lighting.Lights.Single(l => l.Name == "light_arenaEntrance_amb");
        Assert.Equal(SceneLightType.Ambient, amb.Type);
        Assert.Equal(2, amb.RawType);
        Assert.Equal(100f, amb.Range); // inherited from light.prop

        var glow = lighting.Lights.Single(l => l.Name == "lightENV_glowEntrance");
        Assert.Equal(SceneLightType.Point, glow.Type);
        Assert.Equal(15f, glow.Intensity, 0.001f);
        Assert.Equal(20f, glow.Range);

        var shadow = lighting.Lights.Single(l => l.Name == "lightrig_light_char_shared_shadow");
        Assert.Equal(SceneLightType.Directional, shadow.Type);
        Assert.True(shadow.IsCharacterLight);

        // A negative "darkening" light never enters a rig.
        Assert.Contains(lighting.Lights, l => l.Intensity < 0);
        Assert.All(lighting.BuildRig(lighting.Lights).Lights, l => Assert.True(l.Intensity > 0));
    }

    [SkippableFact]
    public void Ambient_lights_fold_into_the_rig_ambient()
    {
        var lighting = SceneLighting.Build(ArenaEntrance(), boundsMin: new Vector3(-20), boundsMax: new Vector3(20));
        var amb = lighting.Lights.Single(l => l.Name == "light_arenaEntrance_amb");

        var (sky, ground) = lighting.Ambient(new[] { amb });
        Assert.Equal(amb.Color * amb.Intensity * amb.Coverage, sky);
        Assert.Equal(sky, ground);
        Assert.True(amb.Coverage > 0.5f, $"a 100-unit ambient light covers a 40-unit box, got {amb.Coverage}");
        Assert.Equal(Vector3.Zero, lighting.BuildRig(Array.Empty<SceneLight>()).AmbientSky);
    }

    [SkippableFact]
    public void Light_probe_snapshot_gives_brighter_sky_than_ground()
    {
        var probe = SceneProbeAmbient.FromTexture(File.ReadAllBytes(
            SampleData.Sample("scene_wdc_adv_boardingSchoolBasement_module_lightprobe.d3dtx")));

        Assert.NotNull(probe);
        Assert.True(SceneLighting.Luminance(probe!.Value.Sky) > SceneLighting.Luminance(probe.Value.Ground));
        Assert.True(probe.Value.Sky.Z > 0);
        Assert.Equal("adv_x_module_lightprobe.d3dtx", SceneProbeAmbient.TextureName("adv_x.scene"));
    }

    [SkippableFact]
    public void Reads_lightmap_uvs_and_atlas()
    {
        var bytes = File.ReadAllBytes(SampleData.Sample("scene_tftb_adv_arenaGortysCoreSilo102_meshesBGroundCover.d3dmesh"));
        Assert.True(D3dMeshFile.TryLoad(bytes, out var mesh, out var error), error);

        var uvs = SceneLightmapReader.ReadUvs(bytes, mesh!);
        Assert.NotNull(uvs);
        Assert.Equal(mesh!.VertexCount, uvs!.Length);
        Assert.All(uvs, uv => Assert.True(uv.X is >= 0 and <= 1 && uv.Y is >= 0 and <= 1, $"{uv}"));

        var atlas = SceneLightmapReader.Decode(File.ReadAllBytes(
            SampleData.Sample("scene_tftb_adv_arenaGortysCoreSilo102_meshesBGroundCover_000.d3dtx")));
        Assert.NotNull(atlas);
        Assert.Equal(255, atlas!.Rgba[3]);
    }

    [SkippableFact]
    public void Assembled_scene_carries_lightmaps_for_lightmapped_meshes()
    {
        const string meshName = "adv_arenaGortysCoreSilo102_meshesBGroundCover.d3dmesh";
        var resolver = new SceneFolderAssetResolver(SampleData.SamplesDir, new Dictionary<string, string>
        {
            [meshName] = "scene_tftb_" + meshName,
            ["adv_arenaGortysCoreSilo102_meshesBGroundCover_000.d3dtx"] = "scene_tftb_adv_arenaGortysCoreSilo102_meshesBGroundCover_000.d3dtx",
        });
        var scene = SceneFile.Load(File.ReadAllBytes(SampleData.Sample("scene_tftb_adv_arenaEntrance.scene")));
        var layout = new SceneLayout
        {
            Scene = scene,
            Objects = new[]
            {
                new SceneObject
                {
                    Agent = scene.Agents[1],
                    Kind = SceneAgentKind.Environment,
                    Meshes = new[] { meshName },
                    VisibleMeshes = new[] { meshName },
                    PropertyFiles = Array.Empty<string>(),
                },
            },
        };

        var built = new SceneAssembler(resolver).Build(layout, _ => true);

        Assert.NotNull(built.Lightmaps);
        Assert.Equal(built.Model.Geometry.Positions.Length, built.Lightmaps!.Uv2.Length);
        Assert.Equal(built.Model.Geometry.Batches.Count, built.Lightmaps.BatchLightmaps.Count);
        Assert.Equal(built.Lightmaps.BatchLightmaps.Count, built.Lightmaps.LightmappedBatches);

        var without = new SceneAssembler(resolver, new SceneAssemblerOptions { Lightmaps = false }).Build(layout, _ => true);
        Assert.Null(without.Lightmaps);
    }
}
