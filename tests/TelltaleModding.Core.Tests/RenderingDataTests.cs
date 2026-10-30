using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

/// <summary>BC4/BC5 decoding, material shading data, ink lookup, light rigs and game-look presets.</summary>
public class RenderingDataTests
{
    private static byte[] Dds(int width, int height, string fourCc, byte[] body, uint dxgi = 0)
    {
        bool dx10 = fourCc == "DX10";
        var data = new byte[128 + (dx10 ? 20 : 0) + body.Length];
        void Put(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), v);
        Put(0, 0x20534444);
        Put(4, 124);
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(76, 32);
        Put(80, 0x4);
        Put(84, (uint)(fourCc[0] | fourCc[1] << 8 | fourCc[2] << 16 | fourCc[3] << 24));
        if (dx10)
            Put(128, dxgi);
        body.CopyTo(data, dx10 ? 148 : 128);
        return data;
    }

    /// <summary>An interpolated-channel block: endpoints, then 16 three-bit indices.</summary>
    private static byte[] ChannelBlock(byte e0, byte e1, int index)
    {
        var block = new byte[8];
        block[0] = e0;
        block[1] = e1;
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
            bits |= (ulong)index << (i * 3);
        for (int i = 0; i < 6; i++)
            block[2 + i] = (byte)(bits >> (i * 8));
        return block;
    }

    [SkippableFact]
    public void Bc4_decodes_one_channel_as_grey()
    {
        var img = DdsImage.Decode(Dds(4, 4, "ATI1", ChannelBlock(200, 100, 0)));
        Assert.Equal(DdsPixelFormat.Bc4, img.Format);
        Assert.Equal(new byte[] { 200, 200, 200, 255 }, img.Rgba[..4]);

        // Index 2 of an 8-value palette is 6/7 of the way from the first endpoint.
        var mid = DdsImage.Decode(Dds(4, 4, "BC4U", ChannelBlock(210, 0, 2)));
        Assert.Equal(180, mid.Rgba[0]);
    }

    [SkippableFact]
    public void Bc5_decodes_normal_xy_in_red_and_green_and_rebuilds_z_in_blue()
    {
        // X = +1 (255), Y = 0 (128): a normal tilted fully sideways, so Z is 0.
        var tilted = ChannelBlock(255, 255, 0).Concat(ChannelBlock(128, 128, 0)).ToArray();
        var img = DdsImage.Decode(Dds(4, 4, "ATI2", tilted));
        Assert.Equal(DdsPixelFormat.Bc5, img.Format);
        Assert.Equal(255, img.Rgba[0]);
        Assert.Equal(128, img.Rgba[1]);
        Assert.InRange(img.Rgba[2], 120, 136);
        Assert.Equal(255, img.Rgba[3]);

        // A flat normal (128, 128) points straight out: Z = 1.
        var flat = ChannelBlock(128, 128, 0).Concat(ChannelBlock(128, 128, 0)).ToArray();
        var up = DdsImage.Decode(Dds(4, 4, "BC5U", flat));
        Assert.InRange(up.Rgba[2], 250, 255);
    }

    [SkippableFact]
    public void Bc5_decodes_from_a_dx10_header()
    {
        var flat = ChannelBlock(128, 128, 0).Concat(ChannelBlock(64, 64, 0)).ToArray();
        var img = DdsImage.Decode(Dds(4, 4, "DX10", flat, dxgi: 83));
        Assert.Equal(DdsPixelFormat.Bc5, img.Format);
        Assert.Equal(64, img.Rgba[1]);
    }

    [SkippableFact]
    public void Definitive_series_material_names_its_ink_and_specular_maps()
    {
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk62_clementine400_head.d3dmesh")), out var mesh, out var error), error);
        var payload = mesh!.MaterialResources.First(r => r.NameSymbol == mesh.MaterialHandles[0]).Payload;
        var material = MaterialData.TryParse(payload)!;
        var names = new[] { "sk62_clementine400_head_ink.d3dtx", "sk62_clementine400_head_spec.d3dtx" }
            .ToDictionary(n => TelltaleCrc64.HashSymbol(n));
        string? Lookup(ulong symbol) => names.GetValueOrDefault(symbol);

        Assert.Equal(("sk62_clementine400_head_ink.d3dtx", InkMapKind.Shadow), MaterialTextureLocator.PickInkTexture(material, Lookup));
        Assert.Equal("sk62_clementine400_head_spec.d3dtx", MaterialTextureLocator.PickSpecularMap(material, Lookup));
        // The ink texture's compiled role is a single-channel detail map.
        Assert.True(material.Textures.Single(t => t.Key == MaterialTextureLocator.InkTextureKey).Types
            .HasFlag(MaterialTextureTypes.SingleChannelDetailMap));

        var shading = MaterialShading.FromMaterial(material);
        Assert.NotNull(shading.SpecularColor);
        Assert.InRange(shading.SpecularColor!.Value.X, 0.4f, 0.6f);
        Assert.Equal(Vector3.One, shading.DiffuseColor);
    }

    [SkippableFact]
    public void Older_meshes_find_maps_named_after_their_color_texture()
    {
        var textures = new[]
        {
            "sk54_bigby_head.d3dtx", "sk54_bigby_head_nm.d3dtx", "sk54_bigby_inkLinesHead.d3dtx",
            "sk54_bigby_inkLinesBody.d3dtx", "map_gradientMale.d3dtx",
        };
        Assert.Equal(("sk54_bigby_head_nm.d3dtx", "sk54_bigby_inkLinesHead.d3dtx"),
            MaterialTextureLocator.PickLegacyMaps("sk54_bigby_head.d3dtx", textures));

        var got = new[] { "sk61_asher_head.d3dtx", "sk61_asher_head_brushnormal.d3dtx", "sk61_asher_head_ao.d3dtx" };
        Assert.Equal(("sk61_asher_head_brushnormal.d3dtx", (string?)null),
            MaterialTextureLocator.PickLegacyMaps("sk61_asher_head.d3dtx", got));
    }

    private static DdsImage Solid(byte r, byte g, byte b, byte a)
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a;
        }
        var data = new byte[128 + px.Length];
        void Put(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), v);
        Put(0, 0x20534444); Put(4, 124); Put(12, 4); Put(16, 4); Put(76, 32); Put(80, 0x41); Put(88, 32);
        Put(92, 0xFF); Put(96, 0xFF00); Put(100, 0xFF0000); Put(104, 0xFF000000);
        px.CopyTo(data, 128);
        return DdsImage.Decode(data);
    }

    [SkippableFact]
    public void Specular_maps_are_told_apart_by_their_channels()
    {
        Assert.Equal(SpecularMapLayout.Packed, ModelAssembler.ClassifySpecularMap(Solid(0, 0, 120, 80)));
        Assert.Equal(SpecularMapLayout.Intensity, ModelAssembler.ClassifySpecularMap(Solid(60, 60, 60, 40)));
    }

    // Channel averages measured on real Definitive Series maps.
    [Theory]
    [InlineData(8, 48, 122, 29, SpecularMapLayout.Packed)]     // sk63_aj_bodyUpperJacketDiscoDog_spec: a big green metal mask
    [InlineData(0, 88, 50, 51, SpecularMapLayout.Packed)]      // sk63_willy_bodyUpper_spec
    [InlineData(24, 0, 101, 82, SpecularMapLayout.Packed)]     // sk61_zombie400_bodyChubby_spec
    [InlineData(8, 0, 25, 48, SpecularMapLayout.Packed)]       // sk62_clementine400_hands_spec
    [InlineData(39, 35, 33, 10, SpecularMapLayout.Intensity)]  // sk54_lee_head_spec (Season 1)
    [InlineData(61, 57, 55, 57, SpecularMapLayout.Intensity)]  // sk54_lee_skin_spec
    [InlineData(10, 10, 12, 30, SpecularMapLayout.Intensity)]  // a dark grey map
    public void Packed_specular_maps_are_found_by_red_and_blue(byte r, byte g, byte b, byte a, SpecularMapLayout expected)
    {
        Assert.Equal(expected, ModelAssembler.ClassifySpecularMap(Solid(r, g, b, a)));
    }

    [SkippableFact]
    public void Studio_rig_follows_the_camera_with_a_key_fill_and_rims()
    {
        var rig = LightRig.Studio;
        Assert.True(rig.FollowCamera);
        Assert.InRange(rig.Lights.Count, 3, 8);
        Assert.All(rig.Lights, l => Assert.InRange(l.Direction.Length(), 0.99f, 1.01f));
        // The key comes from in front (travels away from the viewer); rims from behind.
        Assert.True(rig.Lights[0].Direction.Z < 0);
        Assert.Contains(rig.Lights, l => l.Direction.Z > 0);
        Assert.All(rig.Lights, l => Assert.True(l.AffectsLightmapped));
    }

    [SkippableFact]
    public void Each_game_gets_its_look()
    {
        Assert.Same(GameLookStyle.WalkingDeadDefinitive, GameLookStyle.ForGame(67));
        Assert.Same(GameLookStyle.TalesFromTheBorderlands, GameLookStyle.ForGame(56));
        Assert.Same(GameLookStyle.WolfAmongUs, GameLookStyle.ForGame(54));
        Assert.Same(GameLookStyle.GameOfThrones, GameLookStyle.ForGame(57));
        Assert.Same(GameLookStyle.Default, GameLookStyle.ForGame(3));

        // Only the Definitive Series inks its shadows black.
        Assert.True(GameLookStyle.WalkingDeadDefinitive.InkStrength > 0.9f);
        Assert.Equal(0f, GameLookStyle.TalesFromTheBorderlands.InkStrength);
        foreach (var style in new[] { GameLookStyle.Default, GameLookStyle.WalkingDeadDefinitive, GameLookStyle.WalkingDead,
                     GameLookStyle.TalesFromTheBorderlands, GameLookStyle.WolfAmongUs, GameLookStyle.GameOfThrones })
        {
            Assert.InRange(style.BandThresholds.Count, 0, 4);
            Assert.Equal(style.BandThresholds.OrderBy(t => t), style.BandThresholds);
            // The Definitive Series draws its lines from the ink textures alone.
            Assert.Equal(style != GameLookStyle.WalkingDeadDefinitive, style.OutlineWidth > 0);
        }
    }

    [SkippableFact]
    public void Definitive_series_graphic_black_follows_the_game()
    {
        var s = GameLookStyle.WalkingDeadDefinitive;
        // A scene's rig is tested as the game's scenes set it: threshold about 0.008, the
        // shader's minimum softness, fading in over the first 2 m from the eye.
        Assert.InRange(s.InkSceneThreshold, 0.006f, 0.013f);
        Assert.Equal(0.001f, s.InkSceneSoftness);
        Assert.Equal(0f, s.InkSceneFadeNear);
        Assert.Equal(2f, s.InkSceneFadeFar);
        Assert.False(s.HullOutline);
        Assert.Equal(Vector3.Zero, s.InkColor);
    }

    [SkippableFact]
    public void Graphic_black_inks_any_game_and_the_game_look_is_softer()
    {
        foreach (var style in new[] { GameLookStyle.Default, GameLookStyle.WalkingDeadDefinitive, GameLookStyle.WalkingDead,
                     GameLookStyle.TalesFromTheBorderlands, GameLookStyle.WolfAmongUs, GameLookStyle.GameOfThrones })
        {
            var full = style.WithGraphicBlack();
            Assert.Equal(1f, full.InkStrength);
            Assert.Equal(GameLookStyle.FullGraphicBlackThreshold, full.InkThreshold);
            Assert.Equal(0f, full.InkKeyWrap);
            // The rest of the game's look is kept.
            Assert.Equal(style.HullOutline, full.HullOutline);
            Assert.Equal(style.OutlineWidth, full.OutlineWidth);
            Assert.Equal(style.BandThresholds, full.BandThresholds);
        }

        // The Definitive Series' own look keeps black to the far side of the key.
        var game = GameLookStyle.WalkingDeadDefinitive;
        Assert.True(game.InkThreshold < GameLookStyle.FullGraphicBlackThreshold);
        Assert.True(game.InkKeyWrap > 0f);
    }
}
