namespace TelltaleModding.Core.Projects;

/// <summary>One game asset replaced by a local file when the patch is built.</summary>
public sealed class AssetSwap
{
    /// <summary>Name of the asset to override, exactly as stored in the archive.</summary>
    public string TargetAssetName { get; set; } = "";

    /// <summary>Absolute path to the replacement file on disk.</summary>
    public string ReplacementFilePath { get; set; } = "";

    /// <summary>When false the swap stays in the project but is left out of the patch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional note.</summary>
    public string? Note { get; set; }
}
