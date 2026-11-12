using TelltaleModding.Core.Archives;

namespace TelltaleModding.Core.Catalog;

/// <summary>Progress of a catalog build for the current phase.</summary>
public readonly record struct CatalogProgress(string Message, int Done, int Total);

/// <summary>One selectable model piece of a character.</summary>
/// <param name="Mesh">The <c>.d3dmesh</c> file the game uses.</param>
/// <param name="PartName">Name relative to the character, like <c>head</c>.</param>
/// <param name="Slot">One of <see cref="CharacterSlots"/>.</param>
/// <param name="DefaultOn">Part of the default outfit, from the character's <c>.prop</c> when available, otherwise guessed.</param>
public sealed record CharacterPart(ArchiveEntryRef Mesh, string PartName, string Slot, bool DefaultOn);

/// <summary>A character the game can show.</summary>
/// <param name="Id">Agent name, the <c>.prop</c> or skeleton stem.</param>
/// <param name="DisplayName">Person's name, shared by all their variants.</param>
/// <param name="Variant">What sets this entry apart from others with the same name, or null.</param>
/// <param name="Skeleton">The character's <c>.skl</c>, if found.</param>
/// <param name="Parts">The meshes the character can wear, in the game's list order.</param>
public sealed record CharacterEntry(
    string Id, string DisplayName, string? Variant, ArchiveEntryRef? Skeleton, IReadOnlyList<CharacterPart> Parts)
{
    /// <summary>True when the default outfit came from the game's <c>.prop</c> data.</summary>
    public bool DefaultsFromGameData { get; init; }

    /// <summary>The parts that make up the default outfit.</summary>
    public IEnumerable<CharacterPart> DefaultParts => Parts.Where(p => p.DefaultOn);

    /// <summary>Name with the variant in parentheses.</summary>
    public override string ToString() => Variant is null ? DisplayName : $"{DisplayName} ({Variant})";
}

/// <summary>An animation clip offered for a character.</summary>
/// <param name="File">The <c>.anm</c> file.</param>
/// <param name="DisplayName">File name without extension and skeleton-family prefix.</param>
/// <param name="NamedForCharacter">True when the clip's name mentions the character.</param>
public sealed record AnimationEntry(ArchiveEntryRef File, string DisplayName, bool NamedForCharacter);

/// <summary>Options for <see cref="GameCatalog.OpenAsync(GameInstall, Func{string, TelltaleModding.Core.Archives.IArchiveSource}, CatalogOptions, IProgress{CatalogProgress}?, CancellationToken)"/>.</summary>
public sealed record CatalogOptions
{
    /// <summary>Folder for the on-disk index cache, or null for <see cref="DefaultCacheFolder"/>.</summary>
    public string? CacheFolder { get; init; }

    /// <summary>Read and write the on-disk cache.</summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// ttarchext executable for decrypting the <c>_resdesc_</c> files that set archive priorities.
    /// Found automatically when null. Without it, priorities are guessed from archive names.
    /// </summary>
    public string? EnginePath { get; init; }

    /// <summary>How many archives to open or props to read at once.</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);

    /// <summary>Language whose localization sets are mounted.</summary>
    public string Language { get; init; } = "English";

    /// <summary>Default cache folder.</summary>
    public static string DefaultCacheFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelltaleModdingStudio", "catalogs");
}
