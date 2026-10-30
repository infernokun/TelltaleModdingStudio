using CommunityToolkit.Mvvm.ComponentModel;

namespace TelltaleModding.App.Services;

/// <summary>
/// The user's choices from the Settings page, saved with <see cref="AppSettings"/>. Each
/// workspace reads these as its defaults and follows changes while open.
/// </summary>
public sealed partial class UserPreferences : ObservableObject
{
    /// <summary>The preferences in use (set by the shell when it loads settings).</summary>
    public static UserPreferences Current { get; set; } = new();

    // ---- General ------------------------------------------------------------

    /// <summary>Open the last game again when the app starts.</summary>
    [ObservableProperty] private bool _reopenLastGame = true;

    /// <summary>The workspace a game opens in: Characters, Scenes, Dialogue, Scripts or Assets.</summary>
    [ObservableProperty] private string _startWorkspace = "Characters";

    /// <summary>Look for a newer release on GitHub when the app starts.</summary>
    [ObservableProperty] private bool _checkForUpdatesOnStart = true;

    // ---- 3D viewport ----------------------------------------------------------

    /// <summary>Vertical field of view in degrees.</summary>
    [ObservableProperty] private double _fieldOfView = 45;

    /// <summary>Orbit (left-drag) speed multiplier.</summary>
    [ObservableProperty] private double _orbitSpeed = 1;

    /// <summary>Scroll dolly speed multiplier.</summary>
    [ObservableProperty] private double _dollySpeed = 1;

    /// <summary>
    /// How far graphic black reaches in the Graphic black look: 0 = off, 1 = full graphic
    /// black, 2 = twice as far. The Game look keeps each game's own.
    /// </summary>
    [ObservableProperty] private double _graphicBlackIntensity = 1;

    /// <summary>
    /// How far the game's own graphic black reaches in the Game look (games that use it, such
    /// as the Definitive Series): 0 = off, 1 = the game's default, 2 = twice as far.
    /// </summary>
    [ObservableProperty] private double _engineDefaultIntensity = 1;

    /// <summary>Fly speed (WASD), in model sizes per second.</summary>
    [ObservableProperty] private double _flySpeed = 1;

    /// <summary>Moving the mouse up looks down when flying and orbits the other way.</summary>
    [ObservableProperty] private bool _invertLook;

    /// <summary>Viewport background: Dark, Black, Grey or Light.</summary>
    [ObservableProperty] private string _background = "Dark";

    /// <summary>Show the skeleton overlay in the Characters viewport by default.</summary>
    [ObservableProperty] private bool _showSkeleton;

    // ---- Animation --------------------------------------------------------------

    /// <summary>Clips loop when they reach the end.</summary>
    [ObservableProperty] private bool _loopClips = true;

    /// <summary>Playback speed clips start at.</summary>
    [ObservableProperty] private double _playbackSpeed = 1;

    // ---- Dialogue & audio ---------------------------------------------------------

    /// <summary>Audio volume, 0-100.</summary>
    [ObservableProperty] private double _volume = 100;

    /// <summary>Subtitle language to show when the game has it.</summary>
    [ObservableProperty] private string _subtitleLanguage = "english";

    /// <summary>List only lines that have a recording.</summary>
    [ObservableProperty] private bool _voicedOnly = true;

    /// <summary>Keep playing the next line when one ends.</summary>
    [ObservableProperty] private bool _continuousPlayback = true;

    /// <summary>Show the lip-synced speaker beside the dialogue list.</summary>
    [ObservableProperty] private bool _showSpeaker = true;

    // ---- Scripts --------------------------------------------------------------------

    /// <summary>Show compiled scripts as decompiled Lua (otherwise the instruction listing).</summary>
    [ObservableProperty] private bool _scriptsDecompiled = true;

    /// <summary>Code font size in points.</summary>
    [ObservableProperty] private double _codeFontSize = 13;

    /// <summary>Wrap long code lines.</summary>
    [ObservableProperty] private bool _codeWordWrap;

    // ---- Scenes ---------------------------------------------------------------------

    /// <summary>Largest texture side in scenes (0 = full size).</summary>
    [ObservableProperty] private int _sceneTextureSize = 1024;

    /// <summary>Light scenes with their own lights (otherwise the studio rig).</summary>
    [ObservableProperty] private bool _sceneLighting = true;

    /// <summary>Use baked lightmaps where a scene has them.</summary>
    [ObservableProperty] private bool _sceneLightmaps = true;

    // ---- Export ---------------------------------------------------------------------

    /// <summary>Folder save and export pickers start in (empty = the system's choice).</summary>
    [ObservableProperty] private string _exportFolder = "";

    /// <summary>Frames per second when baking animation into glTF.</summary>
    [ObservableProperty] private int _gltfFramesPerSecond = 30;

    /// <summary>Include normal maps in glTF exports.</summary>
    [ObservableProperty] private bool _gltfNormalMaps = true;

    /// <summary>Bulk export converts to everyday formats (PNG, MP3/Ogg, Lua, transcripts).</summary>
    [ObservableProperty] private bool _convertOnExport = true;

    /// <summary>The viewport background color for <see cref="Background"/> (linear-ish RGB, 0-1).</summary>
    public System.Numerics.Vector3 BackgroundColor => Background switch
    {
        "Black" => new(0.0f, 0.0f, 0.0f),
        "Grey" => new(0.22f, 0.23f, 0.25f),
        "Light" => new(0.80f, 0.82f, 0.85f),
        _ => new(0.082f, 0.094f, 0.118f),
    };
}
