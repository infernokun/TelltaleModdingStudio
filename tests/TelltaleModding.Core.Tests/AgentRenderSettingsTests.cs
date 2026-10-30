using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>An agent's render keys (hologram Handsome Jack's blue tint, glow and opacity) read from its property set.</summary>
public class AgentRenderSettingsTests
{
    /// <summary>Writes a version-2 PropertySet with the given parents and type groups.</summary>
    private static MaterialData Properties(string[] parents, params (string Type, Action<BinaryWriter>[] Values)[] groups) =>
        MaterialData.TryParse(PropertyBytes(parents, groups))!;

    private static byte[] PropertyBytes(string[] parents, params (string Type, Action<BinaryWriter>[] Values)[] groups)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write(parents.Length);
            foreach (var parent in parents)
                w.Write(TelltaleCrc64.HashSymbol(parent));
            w.Write(groups.Length);
            foreach (var (type, values) in groups)
            {
                w.Write(TelltaleCrc64.HashSymbol(type));
                w.Write(values.Length);
                foreach (var value in values)
                    value(w);
            }
        }
        using var file = new MemoryStream();
        using var f = new BinaryWriter(file);
        f.Write(2u);                                             // version
        f.Write(0u);                                             // flags
        f.Write((int)body.Length + 4);                           // block size counts itself
        f.Write(body.ToArray());
        return file.ToArray();
    }

    private static Action<BinaryWriter> Color(string key, float r, float g, float b) => w =>
    {
        w.Write(TelltaleCrc64.HashSymbol(key));
        w.Write(r); w.Write(g); w.Write(b); w.Write(1f);
    };

    private static Action<BinaryWriter> Float(string key, float value) => w =>
    {
        w.Write(TelltaleCrc64.HashSymbol(key));
        w.Write(value);
    };

    private static Action<BinaryWriter> Bool(string key, bool value) => w =>
    {
        w.Write(TelltaleCrc64.HashSymbol(key));
        w.Write((byte)(value ? '1' : '0'));
    };

    [Fact]
    public void Hologram_agent_tints_glows_and_fades_its_meshes()
    {
        // As skB1_handsomeJackAI102.prop sets them.
        var props = Properties(new[] { "character.prop" },
            ("float", new[] { Float("Render Constant Alpha", 0.85f) }),
            ("bool", new[] { Bool("Game Selectable", false), Bool("Render 3D Alpha", true) }),
            ("Color", new[] { Color("Render Diffuse Color", 0.231373f, 0.603922f, 1f), Color("Render Emission Color", 1f, 1f, 1f) }));

        var agent = AgentRenderSettings.FromProperties(props);

        Assert.Equal(new Vector3(0.231373f, 0.603922f, 1f), agent.DiffuseColor);
        Assert.Equal(Vector3.One, agent.EmissionColor);
        Assert.Equal(0.85f, agent.Opacity);
        Assert.True(agent.DrawAsOneLayer);
        Assert.False(agent.IsDefault);
        Assert.True(agent.IsEffect);
    }

    [Fact]
    public void A_color_alone_is_scene_grading_not_an_effect()
    {
        // As sk54_carver203.prop sets it: grey, nothing else.
        var props = Properties(new[] { "character.prop" },
            ("Color", new[] { Color("Render Diffuse Color", 0.8117648f, 0.8117648f, 0.8117648f) }));

        var agent = AgentRenderSettings.FromProperties(props);

        Assert.False(agent.IsDefault);
        Assert.False(agent.IsEffect);
    }

    [Fact]
    public void Unset_keys_come_from_the_parent_and_otherwise_change_nothing()
    {
        // module_renderable.prop's defaults: white, no glow, alpha -1 (unset), not one layer.
        var parent = Properties(Array.Empty<string>(),
            ("float", new[] { Float("Render Constant Alpha", -1f) }),
            ("Color", new[] { Color("Render Diffuse Color", 0.5f, 0.5f, 0.5f) }));
        var props = Properties(new[] { "parent.prop" },
            ("bool", new[] { Bool("Game Selectable", false) }));

        Assert.True(AgentRenderSettings.FromProperties(props).IsDefault);
        var inherited = AgentRenderSettings.FromProperties(props,
            s => s == TelltaleCrc64.HashSymbol("parent.prop") ? parent : null);
        Assert.Equal(new Vector3(0.5f), inherited.DiffuseColor);
        Assert.Equal(1f, inherited.Opacity);
        Assert.Equal(Vector3.Zero, inherited.EmissionColor);
    }

    [Fact]
    public void A_scene_agent_reads_its_own_set_before_the_props_it_inherits()
    {
        // As adv_heliosJacksOffice.scene sets GhostFiona01: a glow and one layer on the agent
        // itself, over a parent that sets a color and leaves the alpha unset.
        var own = ScenePropertySet.TryParse(PropertyBytes(new[] { "ghost.prop" },
            ("bool", new[] { Bool("Render 3D Alpha", true) }),
            ("Color", new[] { Color("Render Emission Color", 0.776471f, 0.776471f, 0.776471f) })))!;
        var parent = ScenePropertySet.TryParse(PropertyBytes(Array.Empty<string>(),
            ("float", new[] { Float("Render Constant Alpha", -1f) }),
            ("bool", new[] { Bool("Render 3D Alpha", false) }),
            ("Color", new[] { Color("Render Diffuse Color", 0.5f, 0.5f, 1f), Color("Render Emission Color", 0f, 0f, 0f) })))!;

        var agent = AgentRenderSettings.FromProperties(new[] { own, parent });

        Assert.Equal(new Vector3(0.776471f), agent.EmissionColor);
        Assert.True(agent.DrawAsOneLayer);
        Assert.Equal(new Vector3(0.5f, 0.5f, 1f), agent.DiffuseColor);
        Assert.Equal(1f, agent.Opacity);
        Assert.True(agent.IsEffect);
        Assert.Equal(new Vector3(MathF.Pow(0.5f, 2.2f), MathF.Pow(0.5f, 2.2f), 1f), agent.LinearTint);

        // The parent alone is an ordinary agent: its color is left out.
        var plain = AgentRenderSettings.FromProperties(new[] { parent });
        Assert.False(plain.IsEffect);
        Assert.Equal(Vector3.One, plain.LinearTint);
        Assert.Equal(Vector3.Zero, plain.LinearGlow);
    }
}
