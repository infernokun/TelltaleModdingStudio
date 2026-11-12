using System.Numerics;

namespace TelltaleModding.Core.Scene;

/// <summary>How a light shines.</summary>
public enum LightKind { Directional, Point, Spot }

/// <summary>
/// One light. <see cref="Color"/> is linear RGB scaled by <see cref="Intensity"/>, and a
/// <see cref="Range"/> of 0 means unlimited.
/// </summary>
public sealed record LightSource(
    LightKind Kind,
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    float Range = 0,
    float InnerConeDegrees = 0,
    float OuterConeDegrees = 0,
    string? Name = null)
{
    /// <summary>False for lights already baked into lightmaps, which then light only surfaces without one.</summary>
    public bool AffectsLightmapped { get; init; } = true;

    /// <summary>True for a rim light from behind. Wrapped light is not applied to it, or it washes out the whole side.</summary>
    public bool Rim { get; init; }
}

/// <summary>The lights a model is shown under: a few direct lights plus a sky-to-ground ambient.</summary>
public sealed record LightRig(IReadOnlyList<LightSource> Lights, Vector3 AmbientSky, Vector3 AmbientGround)
{
    /// <summary>Where the rig came from, for status text.</summary>
    public string Source { get; init; } = "";

    /// <summary>When true, lights are in view space (z toward the viewer) and move with the camera.</summary>
    public bool FollowCamera { get; init; }

    /// <summary>Exposure applied before tone mapping (1 = as is).</summary>
    public float Exposure { get; init; } = 1f;

    /// <summary>The default rig for characters: key, fill and two rim lights that follow the camera.</summary>
    public static LightRig Studio { get; } = new(new[]
    {
        new LightSource(LightKind.Directional, Vector3.Zero, Vector3.Normalize(new Vector3(-0.55f, -0.62f, -0.56f)),
            new Vector3(1.0f, 0.94f, 0.86f), 1.25f, Name: "Key"),
        new LightSource(LightKind.Directional, Vector3.Zero, Vector3.Normalize(new Vector3(0.85f, -0.1f, -0.5f)),
            new Vector3(0.70f, 0.80f, 1.0f), 0.4f, Name: "Fill"),
        new LightSource(LightKind.Directional, Vector3.Zero, Vector3.Normalize(new Vector3(0.55f, -0.45f, 0.70f)),
            new Vector3(0.85f, 0.92f, 1.0f), 0.45f, Name: "Rim left") { Rim = true },
        new LightSource(LightKind.Directional, Vector3.Zero, Vector3.Normalize(new Vector3(-0.6f, -0.3f, 0.75f)),
            new Vector3(1.0f, 0.9f, 0.8f), 0.2f, Name: "Rim right") { Rim = true },
    }, new Vector3(0.30f, 0.31f, 0.34f), new Vector3(0.14f, 0.12f, 0.11f))
    {
        Source = "studio",
        FollowCamera = true,
    };
}
