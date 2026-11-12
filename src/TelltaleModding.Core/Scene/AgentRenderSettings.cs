using System.Numerics;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Scene;

/// <summary>
/// Agent-wide render settings from its <c>.prop</c>: tint, glow, opacity and one-layer
/// translucency. Colors are stored as display values, not linear.
/// </summary>
public sealed record AgentRenderSettings
{
    public static readonly ulong DiffuseColorKey = TelltaleCrc64.HashSymbol("Render Diffuse Color");
    public static readonly ulong EmissionColorKey = TelltaleCrc64.HashSymbol("Render Emission Color");
    public static readonly ulong ConstantAlphaKey = TelltaleCrc64.HashSymbol("Render Constant Alpha");
    public static readonly ulong Alpha3DKey = TelltaleCrc64.HashSymbol("Render 3D Alpha");

    /// <summary>Color multiplied over every material of the agent (white when unset).</summary>
    public Vector3 DiffuseColor { get; init; } = Vector3.One;

    /// <summary>Color the materials' emissive textures glow in (black when unset: no glow).</summary>
    public Vector3 EmissionColor { get; init; }

    /// <summary>Opacity of the whole agent (1 when unset).</summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>True when translucent surfaces show only their nearest layer (depth is drawn first).</summary>
    public bool DrawAsOneLayer { get; init; }

    /// <summary>Nothing set: the materials are drawn as they are.</summary>
    public static AgentRenderSettings Default { get; } = new();

    public bool IsDefault => Equals(Default);

    /// <summary>
    /// True when the agent glows or is see-through. Only then is its color applied, since on
    /// ordinary characters it is just a per-scene grade.
    /// </summary>
    public bool IsEffect => EmissionColor != Vector3.Zero || Opacity < 1f || DrawAsOneLayer;

    /// <summary>The color multiplied over the materials, in linear light (white unless <see cref="IsEffect"/>).</summary>
    public Vector3 LinearTint => IsEffect ? Linear(DiffuseColor) : Vector3.One;

    /// <summary><see cref="EmissionColor"/> in linear light.</summary>
    public Vector3 LinearGlow => Linear(EmissionColor);

    private static Vector3 Linear(Vector3 c) => new(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f));

    /// <summary>Reads the settings from an agent's property set, falling back to its parents through <paramref name="parent"/>.</summary>
    public static AgentRenderSettings FromProperties(MaterialData properties, Func<ulong, MaterialData?>? parent = null)
    {
        var chain = new List<MaterialData> { properties };
        for (int i = 0; i < chain.Count && chain.Count < 8 && parent is not null; i++)
            foreach (var symbol in chain[i].Parents)
                if (parent(symbol) is { } data && !chain.Contains(data))
                    chain.Add(data);

        Vector4? Color(ulong key) => chain.Select(c => c.Colors.TryGetValue(key, out var v) ? v : (Vector4?)null).FirstOrDefault(v => v is not null);
        float? alpha = chain.Select(c => c.Floats.TryGetValue(ConstantAlphaKey, out var v) ? v : (float?)null).FirstOrDefault(v => v is not null);
        bool? oneLayer = chain.Select(c => c.Bools.TryGetValue(Alpha3DKey, out var v) ? v : (bool?)null).FirstOrDefault(v => v is not null);
        return From(Color(DiffuseColorKey), Color(EmissionColorKey), alpha, oneLayer);
    }

    /// <summary>Reads the settings from a scene agent's <see cref="SceneObject.PropertyChain"/>. The nearest set with a key wins.</summary>
    public static AgentRenderSettings FromProperties(IReadOnlyList<ScenePropertySet> chain)
    {
        T? First<T>(Func<ScenePropertySet, T?> get) where T : struct =>
            chain.Select(get).FirstOrDefault(v => v is not null);
        return From(First(s => s.GetColor(DiffuseColorKey)), First(s => s.GetColor(EmissionColorKey)),
            First(s => s.GetFloat(ConstantAlphaKey)), First(s => s.GetBool(Alpha3DKey)));
    }

    private static AgentRenderSettings From(Vector4? diffuse, Vector4? emission, float? alpha, bool? oneLayer)
    {
        static Vector3 Rgb(Vector4 c) => Vector3.Clamp(new Vector3(c.X, c.Y, c.Z), Vector3.Zero, new Vector3(4f));
        return new AgentRenderSettings
        {
            DiffuseColor = diffuse is { } d ? Rgb(d) : Vector3.One,
            EmissionColor = emission is { } e ? Rgb(e) : Vector3.Zero,
            // Negative means unset; anything above 1 is opaque.
            Opacity = alpha is >= 0f ? Math.Min(alpha.Value, 1f) : 1f,
            DrawAsOneLayer = oneLayer ?? false,
        };
    }
}
