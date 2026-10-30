using System.Text.Json;

namespace TelltaleModding.App.Services;

/// <summary>
/// The user's game library and last session, persisted as JSON under
/// <c>%LOCALAPPDATA%\TelltaleModdingStudio\settings.json</c> (on Linux,
/// <c>~/.local/share/TelltaleModdingStudio/settings.json</c>).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Game install folders the user has added, in the order added.</summary>
    public List<string> GameFolders { get; set; } = new();

    /// <summary>The game that was open when the app last closed.</summary>
    public string? LastGameFolder { get; set; }

    /// <summary>True when the 3D viewports show the game's stylised look instead of studio lighting (older settings).</summary>
    public bool ViewportGameLook { get; set; }

    /// <summary>How the 3D viewports shade; null in settings saved before there were three looks.</summary>
    public Core.Scene.RenderMode? ViewportLook { get; set; }

    /// <summary>Choices from the Settings page.</summary>
    public UserPreferences Preferences { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleModdingStudio", "settings.json");

    /// <summary>Where <see cref="Save"/> writes; the file these settings were loaded from.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; set; } = DefaultPath;

    /// <summary>Loads settings, falling back to defaults when missing or unreadable.</summary>
    public static AppSettings Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        var settings = new AppSettings();
        try
        {
            if (File.Exists(file))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        settings.FilePath = file;
        return settings;
    }

    /// <summary>Saves settings; failures are ignored (settings are a convenience).</summary>
    public void Save()
    {
        try
        {
            var file = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
