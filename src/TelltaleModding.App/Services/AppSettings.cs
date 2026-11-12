using System.Text.Json;

namespace TelltaleModding.App.Services;

/// <summary>The user's game library and last session, saved as JSON in local app data.</summary>
public sealed class AppSettings
{
    /// <summary>Game install folders the user has added, in the order added.</summary>
    public List<string> GameFolders { get; set; } = new();

    /// <summary>The game that was open when the app last closed.</summary>
    public string? LastGameFolder { get; set; }

    /// <summary>Old setting for the game look in 3D viewports.</summary>
    public bool ViewportGameLook { get; set; }

    /// <summary>How the 3D viewports shade. Null in older settings.</summary>
    public Core.Scene.RenderMode? ViewportLook { get; set; }

    /// <summary>A release tag the user chose to skip.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Choices from the Settings page.</summary>
    public UserPreferences Preferences { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleModdingStudio", "settings.json");

    /// <summary>The file these settings were loaded from and save to.</summary>
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

    /// <summary>Saves settings. Failures are ignored.</summary>
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
