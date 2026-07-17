namespace TelltaleModding.Core.Archives;

/// <summary>
/// Coarse asset grouping used to filter archive contents in the UI. Derived purely
/// from the file extension of an entry inside a Telltale archive.
/// </summary>
public enum AssetCategory
{
    Other,
    Model,      // .d3dmesh
    Skeleton,   // .skl
    Animation,  // .anm
    Performance,// .chore / .dlog  (scene performances, incl. facial animation)
    Texture,    // .d3dtx / .dds / .font
    Audio,      // .aud / .ogg / .bank / .vox / .wav
    Script,     // .lua / .lenc
    Scene,      // .scene / .prop
}

/// <summary>Maps file extensions to <see cref="AssetCategory"/> values.</summary>
public static class AssetClassifier
{
    private static readonly Dictionary<string, AssetCategory> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".d3dmesh"] = AssetCategory.Model,
        [".skl"] = AssetCategory.Skeleton,
        [".anm"] = AssetCategory.Animation,
        [".chore"] = AssetCategory.Performance,
        [".dlog"] = AssetCategory.Performance,
        [".d3dtx"] = AssetCategory.Texture,
        [".dds"] = AssetCategory.Texture,
        [".font"] = AssetCategory.Texture,
        [".aud"] = AssetCategory.Audio,
        [".ogg"] = AssetCategory.Audio,
        [".bank"] = AssetCategory.Audio,
        [".vox"] = AssetCategory.Audio,
        [".wav"] = AssetCategory.Audio,
        [".lua"] = AssetCategory.Script,
        [".lenc"] = AssetCategory.Script,
        [".scene"] = AssetCategory.Scene,
        [".prop"] = AssetCategory.Scene,
    };

    /// <summary>Classifies a file name (or path) by its extension.</summary>
    public static AssetCategory Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return string.IsNullOrEmpty(ext) ? AssetCategory.Other
            : Map.TryGetValue(ext, out var cat) ? cat
            : AssetCategory.Other;
    }
}
