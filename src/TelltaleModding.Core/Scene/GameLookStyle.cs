using System.Numerics;

namespace TelltaleModding.Core.Scene;

/// <summary>How a 3D viewport shades its model.</summary>
public enum RenderMode
{
    /// <summary>Neutral, physically-minded lighting for inspecting a model.</summary>
    Studio,

    /// <summary>The game's stylised look: ink outlines, the game's light model, ink and graphic black.</summary>
    GameLook,

    /// <summary>
    /// The game's look with full graphic black (see <see cref="GameLookStyle.WithGraphicBlack"/>):
    /// every game's engine supports it, though most of their scenes leave it off.
    /// </summary>
    GraphicBlack,
}

/// <summary>
/// The parameters of a game's stylised look, following the games' shaders: an inverted-hull
/// outline pushed out along the vertex normals, screen-space edge lines, the light model
/// (wrapped diffuse, optionally through a toon ramp), ink textures, graphic black and a
/// final color grade. Colors are sRGB 0-1; screen widths are logical pixels (scaled by
/// the display's DPI); distances are scene units (metres).
/// </summary>
public sealed record GameLookStyle
{
    /// <summary>Display name, e.g. "The Wolf Among Us".</summary>
    public string Name { get; init; } = "Default";

    // ---- Inverted-hull outline (the games' SceneToonOutline2 pass) ------------

    /// <summary>
    /// Draws the mesh again, back faces only, pushed out along its normals: the games'
    /// toon outline. Offset in clip space is <c>HullWidth / (tan(31.25°) × clamp(depth, HullZRange))</c>:
    /// constant world thickness inside the range, constant screen thickness outside.
    /// </summary>
    public bool HullOutline { get; init; }

    /// <summary>
    /// Hull width before the depth division: the shader's <c>S × COLOR1 × 0.05</c>
    /// (Tales from the Borderlands' S = 0.075 with a per-vertex width of 1 gives 0.00375,
    /// about 3.3 px at 1 m on a 1080-line view).
    /// </summary>
    public float HullWidth { get; init; } = 0.00375f;

    /// <summary>The material's "Outline Z Range": depths the hull width is clamped between.</summary>
    public Vector2 HullZRange { get; init; } = new(0.25f, 2f);

    // ---- Screen-space edges ---------------------------------------------------

    /// <summary>Screen-space edge line width in logical pixels (0 = none).</summary>
    public float OutlineWidth { get; init; } = 1.5f;

    /// <summary>Color of hull and edge lines (the material's "Outline Color").</summary>
    public Vector3 OutlineColor { get; init; } = Vector3.Zero;

    /// <summary>How opaque the edge lines are (0-1).</summary>
    public float OutlineOpacity { get; init; } = 1f;

    /// <summary>
    /// Relative depth step that draws an edge line: the fraction a neighbour lies behind
    /// the pixel, beyond what the surface's own slope explains (0.02 = 2 %).
    /// </summary>
    public float DepthThreshold { get; init; } = 0.02f;

    /// <summary>Change of surface direction (1 − cos of the angle) that draws a crease line; 2 disables.</summary>
    public float NormalThreshold { get; init; } = 0.45f;

    /// <summary>Crease line width as a fraction of <see cref="OutlineWidth"/>.</summary>
    public float CreaseWidthScale { get; init; } = 0.5f;

    /// <summary>
    /// Distance, in multiples of the model's size, beyond which edge lines thin with distance
    /// (0 = constant width), so far scenery is not drowned in ink.
    /// </summary>
    public float OutlineFalloffDistance { get; init; } = 3f;

    /// <summary>Thinnest a distant edge line may get, as a fraction of <see cref="OutlineWidth"/>.</summary>
    public float OutlineMinScale { get; init; } = 0.35f;

    // ---- Light model ------------------------------------------------------------

    /// <summary>Diffuse wrap: light is <c>saturate((N·L + wrap) / (1 + wrap))</c>.</summary>
    public float Wrap { get; init; } = 0.15f;

    /// <summary>
    /// Toon ramp: where direct light steps to the next band (wrapped N·L × attenuation, 0-1),
    /// ascending. Empty keeps smooth light.
    /// </summary>
    public IReadOnlyList<float> BandThresholds { get; init; } = Array.Empty<float>();

    /// <summary>
    /// Light level of each band, one more than <see cref="BandThresholds"/> (null spreads
    /// them evenly from 0 to 1).
    /// </summary>
    public IReadOnlyList<float>? BandLevels { get; init; }

    /// <summary>Half-width of the blend at each band edge (0 = hard step).</summary>
    public float BandSoftness { get; init; } = 0.01f;

    /// <summary>How much smooth light is kept under the bands (0 = flat bands).</summary>
    public float BandSmoothness { get; init; }

    /// <summary>
    /// When true only the brightest light is diffuse (through the ramp), as the toon light
    /// model does; the others add nothing but highlights.
    /// </summary>
    public bool ToonKeyLightOnly { get; init; }

    /// <summary>How much normal-map detail shapes the light (0 = the smooth surface, 1 = every bump).</summary>
    public float NormalMapInfluence { get; init; } = 1f;

    /// <summary>Multiplies ambient light, the color of shadowed areas.</summary>
    public Vector3 ShadowTint { get; init; } = Vector3.One;

    /// <summary>Strength of a view-facing rim highlight on the silhouette (0 = none).</summary>
    public float RimStrength { get; init; }

    /// <summary>Rim width: 1 − N·V above which the rim shows (0-1, smaller is wider).</summary>
    public float RimThreshold { get; init; } = 0.6f;

    /// <summary>
    /// Specular highlights become hard shapes above this value of the smooth highlight
    /// (0 = smooth highlights).
    /// </summary>
    public float SpecularThreshold { get; init; }

    /// <summary>Strength of highlights relative to the material's (0 = no specular).</summary>
    public float SpecularStrength { get; init; } = 1f;

    // ---- Ink --------------------------------------------------------------------

    /// <summary>
    /// Strength of ink-texture lines (the Definitive Series' <c>_ink</c> maps on UV set 3, drawn
    /// in <see cref="InkColor"/> where the filtered value passes 0.5, regardless of light, before
    /// graphic black).
    /// </summary>
    public float InkLineStrength { get; init; } = 1f;

    /// <summary>How dark drawn line art (detail textures, ink where alpha &lt; 0.5) prints (0-1).</summary>
    public float LineArtStrength { get; init; } = 1f;

    /// <summary>
    /// Graphic-black alpha (0 = off): where the lit color's luminance (linear,
    /// <c>dot(c, (0.3, 0.59, 0.11))</c>) is below <see cref="InkThreshold"/> it turns black.
    /// </summary>
    public float InkStrength { get; init; }

    /// <summary>
    /// Graphic-black threshold under the studio rig. The games test about 0.008 against their
    /// shadow-mapped scene light; the studio rig has no shadows and its wrapped fill and rim
    /// lights reach everywhere, so this is tested against the key light alone, without wrap
    /// (albedo × key × max(N·L, 0) plus highlights): black past the key's terminator, a little
    /// sooner on dark materials.
    /// </summary>
    public float InkThreshold { get; init; } = 0.03f;

    /// <summary>
    /// Wrap of the key light in the studio rig's graphic-black test (0 = unwrapped, black from
    /// the terminator on). Larger values light the test past the terminator, so black keeps to
    /// the side turned furthest from the key.
    /// </summary>
    public float InkKeyWrap { get; init; }

    /// <summary>
    /// Graphic-black threshold under a scene's own lights, tested against the full lit
    /// luminance including ambient, as the game does (its scenes use 0.006-0.013).
    /// </summary>
    public float InkSceneThreshold { get; init; } = 0.008f;

    /// <summary>Graphic-black edge width under the studio rig (the shader uses at least 0.001).</summary>
    public float InkSoftness { get; init; } = 0.004f;

    /// <summary>Graphic-black edge width under a scene's lights (the scenes' "Graphic Black Softness", 0 → the shader's 0.001).</summary>
    public float InkSceneSoftness { get; init; } = 0.001f;

    /// <summary>Color of ink lines and graphic black (the materials' "Ink Color", black).</summary>
    public Vector3 InkColor { get; init; } = Vector3.Zero;

    /// <summary>
    /// Distance from the eye where graphic black starts to fade in under the studio rig; with
    /// <see cref="InkFadeFar"/> not above it, it is always full.
    /// </summary>
    public float InkFadeNear { get; init; }

    /// <summary>Distance where graphic black is full under the studio rig.</summary>
    public float InkFadeFar { get; init; }

    /// <summary>
    /// Distance from the eye where graphic black starts to fade in under a scene's lights (the
    /// scenes' "Graphic Black Near"; most set 0).
    /// </summary>
    public float InkSceneFadeNear { get; init; }

    /// <summary>
    /// Distance where graphic black is full under a scene's lights (the scenes' "Graphic Black
    /// Far"; most set 2 m, so it is partial in close-ups). Not above <see cref="InkSceneFadeNear"/>: always full.
    /// </summary>
    public float InkSceneFadeFar { get; init; }

    // ---- Grade --------------------------------------------------------------------

    /// <summary>Color saturation (1 = unchanged).</summary>
    public float Saturation { get; init; } = 1f;

    /// <summary>Contrast around mid grey (1 = unchanged).</summary>
    public float Contrast { get; init; } = 1f;

    /// <summary>Exposure multiplier before tone mapping.</summary>
    public float Exposure { get; init; } = 1f;

    // ---- Presets --------------------------------------------------------------------

    /// <summary>A neutral comic look for games without a preset: hull outlines and two soft bands.</summary>
    public static GameLookStyle Default { get; } = new()
    {
        HullOutline = true,
        OutlineWidth = 1.2f,
        BandThresholds = new[] { 0.3f },
        BandLevels = new[] { 0.15f, 1f },
        BandSoftness = 0.03f,
        NormalMapInfluence = 0.35f,
        SpecularThreshold = 0.35f,
        SpecularStrength = 0.6f,
        RimStrength = 0.2f,
        Saturation = 1.1f,
        Contrast = 1.05f,
    };

    /// <summary>
    /// The Walking Dead: The Telltale Definitive Series: soft wrapped physical light (no bands,
    /// no rim), the materials' ink lines, and a softened graphic black that keeps to the side
    /// turned furthest from the key (<see cref="RenderMode.GraphicBlack"/> shows it at full
    /// strength). Scenes use the game's own threshold. No edge lines: the PC build ships no shaders for
    /// the engine's generated NPR lines (nor the screen-space lines), and the characters'
    /// materials have no toon-outline pass, so in the game every line comes from the ink textures.
    /// </summary>
    public static GameLookStyle WalkingDeadDefinitive { get; } = new()
    {
        Name = "The Walking Dead: The Telltale Definitive Series",
        OutlineWidth = 0f,
        Wrap = 1f,
        SpecularStrength = 0.8f,
        InkStrength = 1f,
        InkThreshold = 0.006f,
        InkSoftness = 0.004f,
        InkKeyWrap = 0.4f,
        InkSceneThreshold = 0.008f,
        InkSceneSoftness = 0.001f,
        InkSceneFadeNear = 0f,
        InkSceneFadeFar = 2f,
    };

    /// <summary>
    /// The Walking Dead seasons before the Definitive Series (Season Two, Michonne, A New
    /// Frontier): the same ink lines with thin edge lines, no graphic black.
    /// </summary>
    public static GameLookStyle WalkingDead { get; } = WalkingDeadDefinitive with
    {
        Name = "The Walking Dead",
        OutlineWidth = 1.5f,
        DepthThreshold = 0.015f,
        NormalThreshold = 0.6f,
        InkStrength = 0f,
    };

    /// <summary>
    /// Tales from the Borderlands: the black inverted-hull outline, wrapped Lambert light with
    /// no specular, and the materials' hand-drawn line art.
    /// </summary>
    public static GameLookStyle TalesFromTheBorderlands { get; } = new()
    {
        Name = "Tales from the Borderlands",
        HullOutline = true,
        HullWidth = 0.075f * 0.05f,
        HullZRange = new Vector2(0.25f, 2f),
        OutlineWidth = 1f,
        DepthThreshold = 0.03f,
        NormalThreshold = 2f,
        Wrap = 0.45f,
        SpecularStrength = 0f,
        Saturation = 1.05f,
    };

    /// <summary>
    /// The Wolf Among Us: the inverted-hull outline and the toon light model, one key light
    /// through the character ramp (0, 0.773 from 0.289, 1 from 0.52), hard highlights, and
    /// the ink-line textures.
    /// </summary>
    public static GameLookStyle WolfAmongUs { get; } = new()
    {
        Name = "The Wolf Among Us",
        HullOutline = true,
        HullZRange = new Vector2(0.25f, 6f),
        OutlineWidth = 1f,
        DepthThreshold = 0.03f,
        NormalThreshold = 2f,
        Wrap = 0f,
        BandThresholds = new[] { 0.289f, 0.52f },
        BandLevels = new[] { 0f, 0.773f, 1f },
        BandSoftness = 0.004f,
        ToonKeyLightOnly = true,
        // The ramp's dark band is 0, so shadowed sides show only ambient; in the game the
        // scene's ambient fills them with a soft violet. Lift it to match.
        ShadowTint = new Vector3(1.55f, 1.4f, 1.85f),
        NormalMapInfluence = 0.5f,
        SpecularThreshold = 0.5f,
        SpecularStrength = 1f,
        Saturation = 1.1f,
    };

    /// <summary>
    /// Game of Thrones: the inverted-hull outline plus a soft brush outline from depth and
    /// normal edges, over smooth light shaped by the painted brush normals.
    /// </summary>
    public static GameLookStyle GameOfThrones { get; } = new()
    {
        Name = "Game of Thrones",
        HullOutline = true,
        HullZRange = new Vector2(0.25f, 6f),
        OutlineWidth = 1.2f,
        OutlineColor = new Vector3(0.12f, 0.09f, 0.07f),
        OutlineOpacity = 0.7f,
        DepthThreshold = 0.025f,
        NormalThreshold = 0.6f,
        CreaseWidthScale = 0.5f,
        Wrap = 0.3f,
        SpecularStrength = 0.6f,
    };

    /// <summary>Graphic-black threshold of <see cref="WithGraphicBlack"/> under the studio rig.</summary>
    public const float FullGraphicBlackThreshold = 0.012f;

    /// <summary>
    /// This look with full graphic black: black from the key light's terminator on, and on
    /// dark materials a little before it; scenes keep their own threshold and fade.
    /// </summary>
    public GameLookStyle WithGraphicBlack() => this with
    {
        Name = Name + " (graphic black)",
        InkStrength = 1f,
        InkThreshold = FullGraphicBlackThreshold,
        InkSoftness = 0.002f,
        InkKeyWrap = 0f,
    };

    /// <summary>The preset for a game, by its ttarchext index (see <c>GameDatabase</c>).</summary>
    public static GameLookStyle ForGame(int gameNum) => gameNum switch
    {
        67 => WalkingDeadDefinitive,
        52 or 55 or 59 or 61 => WalkingDead,
        56 => TalesFromTheBorderlands,
        54 => WolfAmongUs,
        57 => GameOfThrones,
        _ => Default,
    };
}
