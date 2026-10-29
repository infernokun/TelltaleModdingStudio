using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// Property keys of scene lights. The Definitive Series uses <c>EnvLight - …</c> keys
/// (<c>module_env_light.prop</c>) and <c>CinLight - …</c> keys for character rig lights
/// (<c>module_cin_light.prop</c>); older games (Wolf Among Us, Tales from the Borderlands,
/// Game of Thrones) use <c>Light …</c> keys (<c>module_light.prop</c>).
/// </summary>
public static class SceneLightKeys
{
    private static ulong H(string name) => TelltaleCrc64.HashSymbol(name);

    // Definitive Series environment lights.
    public static readonly ulong EnvType = H("EnvLight - Type");
    public static readonly ulong EnvColor = H("EnvLight - Color");
    public static readonly ulong EnvIntensity = H("EnvLight - Intensity");
    public static readonly ulong EnvIntensityDimmer = H("EnvLight - Intensity Dimmer");
    public static readonly ulong EnvIntensityDiffuse = H("EnvLight - Intensity Diffuse");
    public static readonly ulong EnvRadius = H("EnvLight - Radius");
    public static readonly ulong EnvDistanceFalloff = H("EnvLight - Distance Falloff");
    public static readonly ulong EnvSpotInner = H("EnvLight - Spot Angle Inner");
    public static readonly ulong EnvSpotOuter = H("EnvLight - Spot Angle Outer");
    public static readonly ulong EnvEnabled = H("EnvLight - Enabled");
    public static readonly ulong EnvGroups = H("EnvLight - Groups");
    public static readonly ulong EnvMobility = H("EnvLight - Mobility");
    public static readonly ulong EnvLocalPosition = H("EnvLight - Local Position");
    public static readonly ulong EnvLocalRotation = H("EnvLight - Local Rotation");

    // Definitive Series character (cinematic) rig lights.
    public static readonly ulong CinColor = H("CinLight - Color");
    public static readonly ulong CinIntensity = H("CinLight - Intensity");
    public static readonly ulong CinDimmer = H("CinLight - Dimmer");
    public static readonly ulong CinIntensityDiffuse = H("CinLight - Intensity Diffuse");
    public static readonly ulong CinEnabled = H("CinLight - Enabled");

    // Older games.
    public static readonly ulong Type = H("Light Type");
    public static readonly ulong Color = H("Light Color");
    public static readonly ulong Intensity = H("Light Intensity");
    public static readonly ulong Dimmer = H("Light Dimmer");
    public static readonly ulong IntensityDiffuse = H("Light Intensity Diffuse");
    public static readonly ulong MaxDistance = H("Light Max Distance");
    public static readonly ulong SpotInnerRadius = H("Light Spot Inner Radius");
    public static readonly ulong SpotOuterRadius = H("Light Spot Outer Radius");
    public static readonly ulong TurnedOn = H("Light Turned On");
    public static readonly ulong Static = H("Light Static");
    public static readonly ulong DynamicOnLightMap = H("Dynamic on LightMap");
    public static readonly ulong Groups = H("Light Groups");

    /// <summary>The light groups a mesh agent is lit by (older games); none means <see cref="DefaultGroup"/>.</summary>
    public static readonly ulong RenderLightingGroups = H("Render Lighting Groups");

    /// <summary>The group lights and meshes belong to unless they name others.</summary>
    public static readonly ulong DefaultGroup = H("DEFAULT");

    // The scene agent's light environment.
    public static readonly ulong AmbientColor = H("Ambient Color");
    public static readonly ulong LightEnvEnabled = H("LightEnv Enabled");
    public static readonly ulong LightEnvIntensity = H("LightEnv Intensity");
    public static readonly ulong LightEnvTint = H("LightEnv Tint");
    public static readonly ulong LightEnvProbeData = H("LightEnv Probe Data");
    public static readonly ulong LightEnvReflectionTexture = H("LightEnv Reflection Texture");
    public static readonly ulong HdrLightmapsIntensity = H("HDR lightmaps Intensity");
}
