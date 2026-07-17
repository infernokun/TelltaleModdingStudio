namespace TelltaleModding.Core.Engine;

/// <summary>Raised when the ttarchext engine cannot be located or fails to run.</summary>
public sealed class TtarchException : Exception
{
    public TtarchException(string message) : base(message) { }
    public TtarchException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Outcome of a single ttarchext invocation.</summary>
/// <param name="ExitCode">Process exit code (0 = success).</param>
/// <param name="Output">All captured stdout+stderr lines, in order.</param>
public sealed record TtarchResult(int ExitCode, IReadOnlyList<string> Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Options for an extract (or list) operation.</summary>
public sealed record ExtractOptions
{
    /// <summary>List the archive contents without writing files (<c>-l</c>).</summary>
    public bool ListOnly { get; init; }

    /// <summary>Only extract files matching this wildcard, e.g. <c>*.d3dmesh</c> (<c>-f</c>).</summary>
    public string? Filter { get; init; }

    /// <summary>Overwrite existing output files without prompting (<c>-o</c>). Default on.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Auto-decode meta files: D3DTX/FONT to DDS, AUD to OGG, LENC to LUA (<c>-m</c>).
    /// Do NOT use when the extracted files will be rebuilt into an archive.
    /// </summary>
    public bool MetaExtract { get; init; }

    /// <summary>Force a specific format version (<c>-V</c>). Usually auto-detected instead.</summary>
    public int? VersionOverride { get; init; }
}

/// <summary>Options for a rebuild (archive-build) operation.</summary>
public sealed record BuildOptions
{
    /// <summary>Format version to write (<c>-V</c>). 7 for all .ttarch2 games.</summary>
    public int Version { get; init; } = 7;

    /// <summary>Overwrite the output archive if it already exists (<c>-o</c>). Default on.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Compatibility rebuild for v7+ archives (<c>-x</c>). Try this if the game crashes
    /// when loading the rebuilt archive.
    /// </summary>
    public bool CompatibilityMode { get; init; }
}
