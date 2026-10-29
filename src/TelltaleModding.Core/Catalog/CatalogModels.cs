using TelltaleModding.Core.Archives;

namespace TelltaleModding.Core.Catalog;

/// <summary>Progress of a catalog build: a status message and a done/total count for the current phase.</summary>
public readonly record struct CatalogProgress(string Message, int Done, int Total);

/// <summary>One selectable model piece of a character.</summary>
/// <param name="Mesh">The <c>.d3dmesh</c> file (the copy the game uses).</param>
/// <param name="PartName">Name relative to the character, e.g. <c>head</c>, <c>hairCap</c>, <c>bodyUpperBBackpack</c>.</param>
/// <param name="Slot">One of <see cref="CharacterSlots"/>: "Head", "Face", "Hair", "Upper body", "Lower body", "Hands", "Feet", "Accessory", "Other".</param>
/// <param name="DefaultOn">
/// Part of the default outfit. Taken from the character's <c>.prop</c> ("Mesh X - Visible"
/// keys) when available, otherwise from a one-plain-part-per-slot heuristic.
/// </param>
public sealed record CharacterPart(ArchiveEntryRef Mesh, string PartName, string Slot, bool DefaultOn);

/// <summary>A character the game can show.</summary>
/// <param name="Id">Agent name: the <c>.prop</c> (or skeleton) stem, e.g. <c>sk62_clementine400</c>.</param>
/// <param name="DisplayName">Person's name, e.g. "Clementine"; shared by all their variants.</param>
/// <param name="Variant">What distinguishes this entry from others with the same name ("400", "Future", "Hacking"), or null.</param>
/// <param name="Skeleton">The character's <c>.skl</c>, if found.</param>
/// <param name="Parts">The meshes the character can wear, in the game's list order.</param>
public sealed record CharacterEntry(
    string Id, string DisplayName, string? Variant, ArchiveEntryRef? Skeleton, IReadOnlyList<CharacterPart> Parts)
{
    /// <summary>True when the default outfit came from the game's own <c>.prop</c> data rather than the heuristic.</summary>
    public bool DefaultsFromGameData { get; init; }

    /// <summary>The parts that make up the default outfit.</summary>
    public IEnumerable<CharacterPart> DefaultParts => Parts.Where(p => p.DefaultOn);

    /// <summary>"Clementine (400)" style label.</summary>
    public override string ToString() => Variant is null ? DisplayName : $"{DisplayName} ({Variant})";
}

/// <summary>An animation clip offered for a character.</summary>
/// <param name="File">The <c>.anm</c> file.</param>
/// <param name="DisplayName">File name without extension and skeleton-family prefix, e.g. <c>clementine400_jog</c>.</param>
/// <param name="NamedForCharacter">True when the clip's name mentions the character; false for other clips of the same skeleton family.</param>
public sealed record AnimationEntry(ArchiveEntryRef File, string DisplayName, bool NamedForCharacter);

/// <summary>Knobs for <see cref="GameCatalog.OpenAsync(GameInstall, Func{string, TelltaleModding.Core.Archives.IArchiveSource}, CatalogOptions, IProgress{CatalogProgress}?, CancellationToken)"/>.</summary>
public sealed record CatalogOptions
{
    /// <summary>
    /// Folder for the on-disk index cache. Default:
    /// <c>%LOCALAPPDATA%\TelltaleModdingStudio\catalogs</c>.
    /// </summary>
    public string? CacheFolder { get; init; }

    /// <summary>Read and write the on-disk cache (default true).</summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// ttarchext executable used to decrypt the loose <c>_resdesc_</c> files that define
    /// archive priorities; located automatically when null. Without it, priorities are
    /// guessed from archive names.
    /// </summary>
    public string? EnginePath { get; init; }

    /// <summary>How many archives to open (or props to read) at once. Default: min(8, CPU count).</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>Language whose localization sets are mounted (default "English").</summary>
    public string Language { get; init; } = "English";

    /// <summary>Default cache folder.</summary>
    public static string DefaultCacheFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelltaleModdingStudio", "catalogs");
}
