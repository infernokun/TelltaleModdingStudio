namespace TelltaleModding.Core.Engine;

/// <summary>Thrown when ttarchext can't be found or fails to run.</summary>
public sealed class TtarchException : Exception
{
    public TtarchException(string message) : base(message) { }
    public TtarchException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Result of one ttarchext run.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="Output">Captured stdout and stderr lines, in order.</param>
public sealed record TtarchResult(int ExitCode, IReadOnlyList<string> Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Options for an extract (or list) operation.</summary>
public sealed record ExtractOptions
{
    /// <summary>List the contents without writing files (<c>-l</c>).</summary>
    public bool ListOnly { get; init; }

    /// <summary>Only extract files matching this wildcard (<c>-f</c>).</summary>
    public string? Filter { get; init; }

    /// <summary>Overwrite existing files (<c>-o</c>).</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Decode meta files like textures and audio on extract (<c>-m</c>).
    /// Don't use it if the files will be rebuilt into an archive.
    /// </summary>
    public bool MetaExtract { get; init; }

    /// <summary>Force a format version (<c>-V</c>).</summary>
    public int? VersionOverride { get; init; }
}

/// <summary>Options for building an archive.</summary>
public sealed record BuildOptions
{
    /// <summary>Format version to write (<c>-V</c>). 7 for .ttarch2 games.</summary>
    public int Version { get; init; } = 7;

    /// <summary>Overwrite the output archive if it exists (<c>-o</c>).</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Compatibility build for v7+ archives (<c>-x</c>), for when the game crashes on the rebuilt one.</summary>
    public bool CompatibilityMode { get; init; }
}
