namespace TelltaleModding.Core.Projects;

/// <summary>
/// A single replacement: the game asset named <see cref="TargetAssetName"/> is to be
/// overridden by the local file at <see cref="ReplacementFilePath"/> when the patch is
/// built. The target name is exactly the name as stored inside the archive (it may
/// contain a relative path).
/// </summary>
public sealed class AssetSwap
{
    /// <summary>Name of the asset inside the archive to override, e.g. <c>adv_guybrush.d3dmesh</c>.</summary>
    public string TargetAssetName { get; set; } = "";

    /// <summary>Absolute path to the replacement file on disk.</summary>
    public string ReplacementFilePath { get; set; } = "";

    /// <summary>When false the swap is kept in the project but excluded from the built patch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional free-text note (e.g. "Guybrush -> pirate skin").</summary>
    public string? Note { get; set; }
}
