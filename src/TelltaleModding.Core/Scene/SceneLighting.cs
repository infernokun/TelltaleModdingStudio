using System.Numerics;

namespace TelltaleModding.Core.Scene;

/// <summary>What a scene light does.</summary>
public enum SceneLightType
{
    Directional,
    Point,
    Spot,

    /// <summary>Flat light within a radius.</summary>
    Ambient,

    /// <summary>Ambient light that is brighter on surfaces facing its direction.</summary>
    DirectionalAmbient,
}

/// <summary>Which property module a light was read from.</summary>
public enum SceneLightFamily
{
    /// <summary><c>EnvLight</c> keys (Definitive Series environment lights).</summary>
    Environment,

    /// <summary><c>CinLight</c> keys (Definitive Series character rig lights).</summary>
    Cinematic,

    /// <summary><c>Light …</c> keys (older games).</summary>
    Legacy,
}

/// <summary>A light agent of a scene, resolved through its property files and placed in the world.</summary>
public sealed class SceneLight
{
    public required string Name { get; init; }

    /// <summary>Index of the agent in <see cref="SceneLayout.Objects"/>.</summary>
    public required int ObjectIndex { get; init; }

    public required SceneLightType Type { get; init; }

    public required SceneLightFamily Family { get; init; }

    /// <summary>The stored type value, or -1 when absent.</summary>
    public int RawType { get; init; } = -1;

    public Vector3 Position { get; init; }

    /// <summary>Where the light points: the agent's local +Z in world space.</summary>
    public Vector3 Direction { get; init; } = -Vector3.UnitY;

    /// <summary>Linear RGB color.</summary>
    public Vector3 Color { get; init; } = Vector3.One;

    /// <summary>Intensity with dimmers applied. Negative lights darken.</summary>
    public float Intensity { get; init; } = 1;

    /// <summary>Reach of a point, spot or local ambient light (0 = unlimited).</summary>
    public float Range { get; init; }

    /// <summary>Distance falloff exponent (Definitive Series).</summary>
    public float Falloff { get; init; } = 2;

    /// <summary>Full apex angle of a spot light's bright core, in degrees.</summary>
    public float SpotInnerDegrees { get; init; }

    /// <summary>Full apex angle of a spot light's cone, in degrees.</summary>
    public float SpotOuterDegrees { get; init; }

    /// <summary>On when the scene starts.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A light of a character lighting rig, which lights characters only.</summary>
    public bool IsCharacterLight { get; init; }

    /// <summary>True when the light's groups reach the location's main geometry.</summary>
    public bool AffectsMainGeometry { get; init; } = true;

    /// <summary>
    /// The light also shines on lightmapped surfaces. Older games only do this when
    /// "Dynamic on LightMap" is set.
    /// </summary>
    public bool LightsLightmapped { get; init; } = true;

    /// <summary>A static light already baked into lightmaps or Enlighten data.</summary>
    public bool IsBaked { get; init; }

    /// <summary>Light group symbols (older games) or the group flag bits (Definitive Series) the light affects.</summary>
    public IReadOnlyList<ulong> Groups { get; init; } = Array.Empty<ulong>();

    /// <summary>Shown mesh objects in the light's groups (older games only), or null when not limited.</summary>
    public IReadOnlyList<int>? AffectedObjects { get; init; }

    /// <summary>How much of the scene's visible geometry the light reaches (0-1), set by <see cref="SceneLighting.Build"/>.</summary>
    public float Coverage { get; init; } = 1;

    /// <summary>How much the light matters, from intensity, luminance and coverage.</summary>
    public float Importance => MathF.Abs(Intensity) * SceneLighting.Luminance(Color) * Coverage;

    /// <summary>True for lights a direct-light shader can draw (directional, point, spot).</summary>
    public bool IsDirect => Type is SceneLightType.Directional or SceneLightType.Point or SceneLightType.Spot;

    /// <summary>The light as a <see cref="LightSource"/>.</summary>
    public LightSource ToLightSource() => new(
        Type switch
        {
            SceneLightType.Directional => LightKind.Directional,
            SceneLightType.Spot => LightKind.Spot,
            _ => LightKind.Point,
        },
        Position, Direction, Color, Intensity, Range,
        Type == SceneLightType.Spot ? SpotInnerDegrees : 0,
        Type == SceneLightType.Spot ? SpotOuterDegrees : 0,
        Name) { AffectsLightmapped = LightsLightmapped };

    public override string ToString() => $"{Name} ({Type}, {Intensity:0.###})";
}

/// <summary>The lights of a scene and its ambient light, turned into <see cref="LightRig"/>s.</summary>
/// <remarks>
/// Directional and spot lights point along the agent's local +Z. Ambient comes from ambient
/// lights, the scene's <c>Ambient Color</c> and, in the Definitive Series, the light-probe
/// snapshot texture. The <c>.probe</c> files hold no light, only probe positions.
/// </remarks>
public sealed class SceneLighting
{
    /// <summary>Most direct lights a rig carries.</summary>
    public const int MaxRigLights = 8;

    /// <summary>Every light agent, in scene order.</summary>
    public required IReadOnlyList<SceneLight> Lights { get; init; }

    /// <summary>Ambient light that does not come from light agents: sky term.</summary>
    public Vector3 BaseAmbientSky { get; init; }

    /// <summary>Ambient light that does not come from light agents: ground term.</summary>
    public Vector3 BaseAmbientGround { get; init; }

    /// <summary>Where <see cref="BaseAmbientSky"/> came from.</summary>
    public string AmbientSource { get; init; } = "none";

    /// <summary>The scene's name, used as the rig's source.</summary>
    public string SceneName { get; init; } = "";

    /// <summary>The lights a rig uses by default: enabled, not character rig lights, adding light.</summary>
    public IEnumerable<SceneLight> DefaultLights => Lights.Where(IsOnByDefault);

    /// <summary>True when a light is in the default rig.</summary>
    public static bool IsOnByDefault(SceneLight light) =>
        light.Enabled && light.AffectsMainGeometry && !light.IsCharacterLight && !LooksLikeEffectLight(light.Name) &&
        light.Intensity > 0 && light.Coverage > 0;

    /// <summary>Lights of effects and props that chores switch on.</summary>
    public static bool LooksLikeEffectLight(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith("light_fx", StringComparison.Ordinal) || n.Contains("_fx_", StringComparison.Ordinal) ||
               n.Contains("vfx", StringComparison.Ordinal) || n.Contains("muzzle", StringComparison.Ordinal);
    }

    /// <summary>
    /// The rig for lightmapped surfaces: only lights that also shine on them, and no ambient
    /// since the lightmap carries it.
    /// </summary>
    public LightRig BuildLightmappedRig(IEnumerable<SceneLight> chosen, int maxLights = MaxRigLights)
    {
        var rig = BuildRig(chosen.Where(l => l.LightsLightmapped), maxLights);
        return rig with { AmbientSky = Vector3.Zero, AmbientGround = Vector3.Zero };
    }

    /// <summary>The rig of the default lights.</summary>
    public LightRig BuildRig() => BuildRig(DefaultLights);

    /// <summary>A rig from the chosen lights: the most important direct lights plus ambient.</summary>
    public LightRig BuildRig(IEnumerable<SceneLight> chosen, int maxLights = MaxRigLights)
    {
        var list = chosen.ToList();
        var direct = list.Where(l => l.IsDirect && l.Intensity > 0)
            .OrderBy(l => l.Type == SceneLightType.Directional ? 0 : 1)
            .ThenByDescending(l => l.Importance)
            .Take(Math.Max(0, maxLights))
            .Select(l => l.ToLightSource())
            .ToList();
        var (sky, ground) = Ambient(list);
        return new LightRig(direct, sky, ground) { Source = SceneName };
    }

    /// <summary>Sky and ground ambient from the base ambient plus the given lights' ambient terms.</summary>
    public (Vector3 Sky, Vector3 Ground) Ambient(IEnumerable<SceneLight> chosen)
    {
        Vector3 sky = BaseAmbientSky, ground = BaseAmbientGround;
        foreach (var light in chosen)
        {
            var c = light.Color * light.Intensity * light.Coverage;
            if (light.Type == SceneLightType.Ambient)
            {
                sky += c;
                ground += c;
            }
            else if (light.Type == SceneLightType.DirectionalAmbient)
            {
                float fromAbove = Math.Clamp(-light.Direction.Y, -1f, 1f);
                sky += c * (0.5f + 0.5f * fromAbove);
                ground += c * (0.5f - 0.5f * fromAbove);
            }
        }
        return (Vector3.Max(sky, Vector3.Zero), Vector3.Max(ground, Vector3.Zero));
    }

    /// <summary>
    /// Reads every light agent of <paramref name="layout"/>. <paramref name="boundsMin"/> and
    /// <paramref name="boundsMax"/> are used to judge how much of the scene each light reaches.
    /// </summary>
    public static SceneLighting Build(SceneLayout layout, ISceneAssetResolver? assets = null,
        Vector3? boundsMin = null, Vector3? boundsMax = null)
    {
        var sceneObject = layout.Objects.FirstOrDefault(o =>
            o.Name.Equals(layout.Scene.Name, StringComparison.OrdinalIgnoreCase)) ?? layout.Objects.FirstOrDefault();
        var sceneChain = sceneObject?.PropertyChain ?? Array.Empty<ScenePropertySet>();
        float envIntensity = First(sceneChain, s => s.GetFloat(SceneLightKeys.LightEnvIntensity)) ?? 1f;
        var envTint = First(sceneChain, s => s.GetColor(SceneLightKeys.LightEnvTint)) is { } tint
            ? new Vector3(tint.X, tint.Y, tint.Z)
            : Vector3.One;

        var (min, max) = boundsMin is { } bmin && boundsMax is { } bmax ? (bmin, bmax) : DefaultBounds(layout);

        // Older games limit lights to the meshes sharing one of their light groups.
        var shown = Enumerable.Range(0, layout.Objects.Count).Where(i => layout.Objects[i].ShownByDefault).ToList();
        var characters = Enumerable.Range(0, layout.Objects.Count)
            .Where(i => layout.Objects[i].Kind == SceneAgentKind.Character && layout.Objects[i].HasMeshes).ToList();
        var groupsOf = new Dictionary<int, HashSet<ulong>>();
        HashSet<ulong> GroupsOf(int i)
        {
            if (!groupsOf.TryGetValue(i, out var set))
            {
                var list = FirstRef(layout.Objects[i].PropertyChain, s => s.GetSymbolList(SceneLightKeys.RenderLightingGroups) is { Count: > 0 } l ? l : null);
                groupsOf[i] = set = list is null ? new HashSet<ulong> { SceneLightKeys.DefaultGroup } : new HashSet<ulong>(list);
            }
            return set;
        }

        var lights = new List<SceneLight>();
        for (int i = 0; i < layout.Objects.Count; i++)
        {
            var obj = layout.Objects[i];
            if (obj == sceneObject || obj.HasMeshes)
                continue;
            if (Read(obj, i, envIntensity, envTint) is not { } light)
                continue;
            float share = 1;
            if (light.Family == SceneLightFamily.Legacy && shown.Count > 0)
            {
                var groups = light.Groups.Count > 0 ? light.Groups : new[] { SceneLightKeys.DefaultGroup };
                var affected = shown.Where(o => groups.Any(GroupsOf(o).Contains)).ToList();
                bool litsCharacters = characters.Any(o => groups.Any(GroupsOf(o).Contains));
                share = (float)affected.Count / shown.Count;
                light = Copy(light, light.Coverage, affected, light.IsCharacterLight || (affected.Count == 0 && litsCharacters));
            }
            var placed = WithCoverage(light, min, max);
            lights.Add(share < 1 ? Copy(placed, placed.Coverage * share, placed.AffectedObjects, placed.IsCharacterLight) : placed);
        }

        // Base ambient: the scene's ambient color plus the light-probe snapshot.
        var sceneAmbient = First(sceneChain, s => s.GetColor(SceneLightKeys.AmbientColor)) is { } ac
            ? SrgbToLinear(new Vector3(ac.X, ac.Y, ac.Z))
            : Vector3.Zero;
        Vector3 sky = sceneAmbient, ground = sceneAmbient;
        string source = sceneAmbient != Vector3.Zero ? "scene ambient color" : "none";
        if (assets is not null && SceneProbeAmbient.Find(layout, assets) is { } probe)
        {
            sky += probe.Sky;
            ground += probe.Ground;
            source = "light probe";
        }

        return new SceneLighting
        {
            Lights = lights,
            BaseAmbientSky = sky,
            BaseAmbientGround = ground,
            AmbientSource = source,
            SceneName = layout.Scene.Name,
        };
    }

    /// <summary>Reads one agent as a light; null when it carries no light properties.</summary>
    public static SceneLight? Read(SceneObject obj, int index, float envIntensity = 1, Vector3? envTint = null)
    {
        var chain = obj.PropertyChain;
        if (chain.Count == 0)
            return null;
        int? envType = First(chain, s => s.GetInt32(SceneLightKeys.EnvType));
        if (envType is not null || chain.Any(s => s.Contains(SceneLightKeys.EnvColor)))
            return ReadEnvironment(obj, index, envType ?? 0, envIntensity, envTint ?? Vector3.One);
        if (chain.Any(s => s.Contains(SceneLightKeys.CinIntensity) || s.Contains(SceneLightKeys.CinColor)))
            return ReadCinematic(obj, index);
        int? legacyType = First(chain, s => s.GetInt32(SceneLightKeys.Type));
        if (legacyType is not null || chain.Any(s => s.Contains(SceneLightKeys.Color)))
            return ReadLegacy(obj, index, legacyType ?? 1);
        return null;
    }

    private static SceneLight ReadEnvironment(SceneObject obj, int index, int envType, float envIntensity, Vector3 envTint)
    {
        var chain = obj.PropertyChain;
        var localPosition = First(chain, s => s.GetVector3(SceneLightKeys.EnvLocalPosition)) ?? Vector3.Zero;
        var localRotation = First(chain, s => s.GetQuaternion(SceneLightKeys.EnvLocalRotation)) ?? Quaternion.Identity;
        var local = Matrix4x4.CreateFromQuaternion(localRotation) * Matrix4x4.CreateTranslation(localPosition);
        var world = local * obj.World;
        float intensity = (First(chain, s => s.GetFloat(SceneLightKeys.EnvIntensity)) ?? 1f) *
                          (First(chain, s => s.GetFloat(SceneLightKeys.EnvIntensityDimmer)) ?? 1f) *
                          (First(chain, s => s.GetFloat(SceneLightKeys.EnvIntensityDiffuse)) ?? 1f) * envIntensity;
        // Group flags: bit 0 is the main geometry, bit 3 characters. Bits 16-19 are ambient
        // groups that feed baked indirect light.
        uint groups = (uint)(First(chain, s => s.GetInt32(SceneLightKeys.EnvGroups)) ?? 1);
        bool ambientGroup = (groups & 0xF0000u) != 0;
        var type = envType switch
        {
            1 => SceneLightType.Spot,
            2 => SceneLightType.Directional,
            3 => SceneLightType.Ambient,
            4 => SceneLightType.DirectionalAmbient,
            _ => SceneLightType.Point,
        };
        return new SceneLight
        {
            Name = obj.Name,
            ObjectIndex = index,
            Family = SceneLightFamily.Environment,
            RawType = envType,
            Type = type,
            Position = world.Translation,
            Direction = Forward(world),
            Color = ColorOf(chain, SceneLightKeys.EnvColor) * envTint,
            Intensity = intensity,
            Range = ambientGroup && type == SceneLightType.Ambient
                ? 0
                : Math.Max(0, First(chain, s => s.GetFloat(SceneLightKeys.EnvRadius)) ?? 0),
            Falloff = First(chain, s => s.GetFloat(SceneLightKeys.EnvDistanceFalloff)) ?? 2,
            SpotInnerDegrees = Math.Clamp(First(chain, s => s.GetFloat(SceneLightKeys.EnvSpotInner)) ?? 0, 0, 180),
            SpotOuterDegrees = Math.Clamp(First(chain, s => s.GetFloat(SceneLightKeys.EnvSpotOuter)) ?? 45, 0, 180),
            Enabled = obj.StartVisible && (First(chain, s => s.GetBool(SceneLightKeys.EnvEnabled)) ?? true),
            IsCharacterLight = LooksLikeCharacterLight(obj.Name) || groups == 1u << 3,
            AffectsMainGeometry = (groups & 1u) != 0,
            IsBaked = (First(chain, s => s.GetInt32(SceneLightKeys.EnvMobility)) ?? 1) == 0,
            Groups = Enumerable.Range(0, 32).Where(b => (groups & (1u << b)) != 0).Select(b => (ulong)b).ToList(),
        };
    }

    private static SceneLight ReadCinematic(SceneObject obj, int index)
    {
        var chain = obj.PropertyChain;
        float intensity = (First(chain, s => s.GetFloat(SceneLightKeys.CinIntensity)) ?? 1f) *
                          (First(chain, s => s.GetFloat(SceneLightKeys.CinDimmer)) ?? 1f) *
                          (First(chain, s => s.GetFloat(SceneLightKeys.CinIntensityDiffuse)) ?? 1f);
        return new SceneLight
        {
            Name = obj.Name,
            ObjectIndex = index,
            Family = SceneLightFamily.Cinematic,
            Type = SceneLightType.Directional,
            Position = obj.World.Translation,
            Direction = Forward(obj.World),
            Color = ColorOf(chain, SceneLightKeys.CinColor),
            Intensity = intensity,
            Enabled = obj.StartVisible && (First(chain, s => s.GetBool(SceneLightKeys.CinEnabled)) ?? true),
            IsCharacterLight = true,
        };
    }

    private static SceneLight ReadLegacy(SceneObject obj, int index, int type)
    {
        var chain = obj.PropertyChain;
        float intensity = (First(chain, s => s.GetFloat(SceneLightKeys.Intensity)) ?? 1f) *
                          (First(chain, s => s.GetFloat(SceneLightKeys.Dimmer)) ?? 1f) *
                          (First(chain, s => s.GetFloat(SceneLightKeys.IntensityDiffuse)) ?? 1f);
        // Spot radii are on a projection plane one unit out.
        float inner = First(chain, s => s.GetFloat(SceneLightKeys.SpotInnerRadius)) ?? 0.2f;
        float outer = First(chain, s => s.GetFloat(SceneLightKeys.SpotOuterRadius)) ?? 0.9f;
        bool isStatic = First(chain, s => s.GetBool(SceneLightKeys.Static)) ?? false;
        bool dynamicOnLightmap = First(chain, s => s.GetBool(SceneLightKeys.DynamicOnLightMap)) ?? false;
        return new SceneLight
        {
            Name = obj.Name,
            ObjectIndex = index,
            Family = SceneLightFamily.Legacy,
            RawType = type,
            Type = type switch
            {
                0 or 3 or 9 => SceneLightType.Directional,
                2 or 8 => SceneLightType.Ambient,
                5 or 6 => SceneLightType.Spot,
                _ => SceneLightType.Point,
            },
            Position = obj.World.Translation,
            Direction = Forward(obj.World),
            Color = ColorOf(chain, SceneLightKeys.Color),
            Intensity = intensity,
            Range = Math.Max(0, First(chain, s => s.GetFloat(SceneLightKeys.MaxDistance)) ?? 0),
            SpotInnerDegrees = 2 * MathF.Atan(Math.Max(0, inner)) * 180 / MathF.PI,
            SpotOuterDegrees = 2 * MathF.Atan(Math.Max(0, outer)) * 180 / MathF.PI,
            // Type 10 is "Lightmap Specular", which only adds highlights.
            Enabled = type != 10 && obj.StartVisible && (First(chain, s => s.GetBool(SceneLightKeys.TurnedOn)) ?? true),
            IsCharacterLight = LooksLikeCharacterLight(obj.Name),
            IsBaked = isStatic && !dynamicOnLightmap,
            LightsLightmapped = dynamicOnLightmap,
            Groups = FirstRef(chain, s => s.GetSymbolList(SceneLightKeys.Groups)) ?? Array.Empty<ulong>(),
        };
    }

    /// <summary>True for lights of character lighting rigs, judged by name.</summary>
    public static bool LooksLikeCharacterLight(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith("lightrig", StringComparison.Ordinal) || n.StartsWith("light_char", StringComparison.Ordinal) ||
               n.Contains("_char_", StringComparison.Ordinal) || n.StartsWith("keylight", StringComparison.Ordinal);
    }

    /// <summary>How much of the box a light reaches (0-1): directional and unlimited lights reach all of it.</summary>
    private static SceneLight WithCoverage(SceneLight light, Vector3 min, Vector3 max)
    {
        if (light.Type is SceneLightType.Directional or SceneLightType.DirectionalAmbient || light.Range <= 0)
            return Copy(light, 1, light.AffectedObjects, light.IsCharacterLight);
        var size = max - min;
        float radius = Math.Max(1e-3f, size.Length() / 2);
        var nearest = Vector3.Clamp(light.Position, min, max);
        float reach = light.Range - Vector3.Distance(light.Position, nearest);
        if (reach <= 0)
            return Copy(light, 0, light.AffectedObjects, light.IsCharacterLight);
        float coverage = MathF.Min(1, reach / radius);
        coverage *= coverage;
        if (light.Type == SceneLightType.Spot)
            coverage *= Math.Clamp(1 - MathF.Cos(light.SpotOuterDegrees * MathF.PI / 360), 0.05f, 1);
        return Copy(light, coverage, light.AffectedObjects, light.IsCharacterLight);
    }

    private static SceneLight Copy(SceneLight l, float coverage, IReadOnlyList<int>? affected, bool characterLight) => new()
    {
        Name = l.Name, ObjectIndex = l.ObjectIndex, Type = l.Type, Family = l.Family, RawType = l.RawType,
        Position = l.Position, Direction = l.Direction, Color = l.Color, Intensity = l.Intensity, Range = l.Range,
        Falloff = l.Falloff, SpotInnerDegrees = l.SpotInnerDegrees, SpotOuterDegrees = l.SpotOuterDegrees,
        Enabled = l.Enabled, IsCharacterLight = characterLight, IsBaked = l.IsBaked, Groups = l.Groups,
        LightsLightmapped = l.LightsLightmapped,
        Coverage = coverage, AffectedObjects = affected,
        AffectsMainGeometry = affected is null ? l.AffectsMainGeometry : affected.Count > 0,
    };

    /// <summary>Bounds of the shown objects' and lights' positions when no geometry bounds are given.</summary>
    private static (Vector3, Vector3) DefaultBounds(SceneLayout layout)
    {
        var points = layout.Objects.Where(o => o.ShownByDefault).Select(o => o.WorldPosition).ToList();
        if (points.Count == 0)
            return (new Vector3(-10), new Vector3(10));
        var min = points.Aggregate(Vector3.Min) - new Vector3(5);
        var max = points.Aggregate(Vector3.Max) + new Vector3(5);
        return (min, max);
    }

    private static Vector3 Forward(Matrix4x4 world)
    {
        var d = Vector3.TransformNormal(Vector3.UnitZ, world);
        return d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : -Vector3.UnitY;
    }

    private static Vector3 ColorOf(IReadOnlyList<ScenePropertySet> chain, ulong key) =>
        First(chain, s => s.GetColor(key)) is { } c ? SrgbToLinear(new Vector3(c.X, c.Y, c.Z)) : Vector3.One;

    private static T? First<T>(IReadOnlyList<ScenePropertySet> chain, Func<ScenePropertySet, T?> get) where T : struct
    {
        foreach (var set in chain)
            if (get(set) is { } value)
                return value;
        return null;
    }

    private static T? FirstRef<T>(IReadOnlyList<ScenePropertySet> chain, Func<ScenePropertySet, T?> get) where T : class
    {
        foreach (var set in chain)
            if (get(set) is { } value)
                return value;
        return null;
    }

    /// <summary>sRGB to linear, per channel.</summary>
    public static Vector3 SrgbToLinear(Vector3 c) => new(ToLinear(c.X), ToLinear(c.Y), ToLinear(c.Z));

    private static float ToLinear(float v) =>
        v <= 0 ? 0 : v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>Rec. 709 luminance.</summary>
    public static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
}
