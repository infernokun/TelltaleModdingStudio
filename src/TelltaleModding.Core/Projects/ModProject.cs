using System.Text.Json;
using System.Text.Json.Serialization;
using TelltaleModding.Core.Games;

namespace TelltaleModding.Core.Projects;

/// <summary>A mod project: the target game and the asset swaps to build into a patch. Saved as JSON.</summary>
public sealed class ModProject
{
    /// <summary>Schema version of the project file.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Display name for the project.</summary>
    public string Name { get; set; } = "Untitled Mod";

    /// <summary>The ttarchext game index.</summary>
    public int GameNum { get; set; } = -1;

    /// <summary>Optional path to one of the game's archives, used to detect the format version.</summary>
    public string? ReferenceArchivePath { get; set; }

    /// <summary>Build version override. Null means detect it.</summary>
    public int? BuildVersionOverride { get; set; }

    /// <summary>The asset replacements that make up this mod.</summary>
    public List<AssetSwap> Swaps { get; set; } = new();

    /// <summary>Enabled swaps only.</summary>
    [JsonIgnore]
    public IEnumerable<AssetSwap> EnabledSwaps => Swaps.Where(s => s.Enabled);

    /// <summary>The game entry this project targets, or null if the number is invalid.</summary>
    [JsonIgnore]
    public GameEntry? Game => GameDatabase.ByNumber(GameNum);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes the project to JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Saves the project to a <c>.ttmod</c> file.</summary>
    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Loads a project from JSON text.</summary>
    public static ModProject FromJson(string json) =>
        JsonSerializer.Deserialize<ModProject>(json, JsonOptions)
        ?? throw new InvalidDataException("Project JSON deserialized to null.");

    /// <summary>Loads a project from a <c>.ttmod</c> file.</summary>
    public static ModProject Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Returns the project's problems. Empty means it's ready to build.</summary>
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();

        if (Game is null)
            issues.Add($"Game number {GameNum} is not a valid game.");

        var enabled = EnabledSwaps.ToList();
        if (enabled.Count == 0)
            issues.Add("The project has no enabled swaps to build.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var swap in enabled)
        {
            if (string.IsNullOrWhiteSpace(swap.TargetAssetName))
                issues.Add("A swap has an empty target asset name.");
            else if (!seen.Add(swap.TargetAssetName))
                issues.Add($"Duplicate target asset '{swap.TargetAssetName}'.");

            if (string.IsNullOrWhiteSpace(swap.ReplacementFilePath))
                issues.Add($"Swap for '{swap.TargetAssetName}' has no replacement file.");
            else if (!File.Exists(swap.ReplacementFilePath))
                issues.Add($"Replacement file not found: {swap.ReplacementFilePath}");
        }

        return issues;
    }
}
