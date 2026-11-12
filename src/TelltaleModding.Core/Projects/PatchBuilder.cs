using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Engine;
using TelltaleModding.Core.Games;

namespace TelltaleModding.Core.Projects;

/// <summary>Inputs for building a patch from a <see cref="ModProject"/>.</summary>
/// <param name="OutputArchivePath">Full path of the patch archive to write.</param>
/// <param name="StagingDirectory">Folder to collect the swapped files in, or null for a temp folder. It gets wiped first.</param>
/// <param name="CompatibilityMode">Pass ttarchext's <c>-x</c>, needed by some v7+ archives.</param>
public sealed record PatchBuildRequest(
    string OutputArchivePath,
    string? StagingDirectory = null,
    bool CompatibilityMode = false);

/// <summary>Outcome of a patch build.</summary>
/// <param name="Success">Whether ttarchext reported success.</param>
/// <param name="OutputArchivePath">Where the patch was written.</param>
/// <param name="StagedFileCount">Number of swapped assets included.</param>
/// <param name="Version">The format version used for the build.</param>
/// <param name="Output">Captured engine output.</param>
public sealed record PatchBuildResult(
    bool Success,
    string OutputArchivePath,
    int StagedFileCount,
    int Version,
    IReadOnlyList<string> Output);

/// <summary>
/// Builds a small patch archive from a <see cref="ModProject"/> holding only the swapped
/// files. The game loads it on top of its own archives.
/// </summary>
public sealed class PatchBuilder
{
    private readonly TtarchRunner _runner;

    public PatchBuilder(TtarchRunner runner) => _runner = runner;

    /// <summary>The conventional patch file name for a game's archive kind.</summary>
    public static string ConventionalPatchName(ArchiveKind kind) =>
        kind == ArchiveKind.Ttarch2 ? "0.ttarch2" : "0.ttarch";

    /// <summary>
    /// Picks the format version: the override, else the reference archive's version,
    /// else the game's default.
    /// </summary>
    public static int ResolveBuildVersion(ModProject project, GameEntry game)
    {
        if (project.BuildVersionOverride is int v)
            return v;

        if (!string.IsNullOrWhiteSpace(project.ReferenceArchivePath) &&
            File.Exists(project.ReferenceArchivePath))
            return ArchiveInspector.Inspect(project.ReferenceArchivePath).Version;

        return game.Kind == ArchiveKind.Ttarch2 ? 7 : 1;
    }

    /// <summary>
    /// Copies each enabled swap's file into <paramref name="stagingDir"/> under its target
    /// name and returns the staged names. Doesn't clear the folder first.
    /// </summary>
    public static IReadOnlyList<string> Stage(ModProject project, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);
        var staged = new List<string>();

        foreach (var swap in project.EnabledSwaps)
        {
            var rel = NormalizeRelative(swap.TargetAssetName);
            var dest = Path.GetFullPath(Path.Combine(stagingDir, rel));

            // Don't let a bad target name write outside the staging folder.
            var root = Path.GetFullPath(stagingDir) + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Target asset name '{swap.TargetAssetName}' escapes the staging folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(swap.ReplacementFilePath, dest, overwrite: true);
            staged.Add(rel);
        }

        return staged;
    }

    /// <summary>Validates the project, stages its swaps and runs ttarchext to build the patch.</summary>
    public async Task<PatchBuildResult> BuildAsync(
        ModProject project,
        PatchBuildRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = project.Validate();
        if (issues.Count > 0)
            throw new InvalidOperationException(
                "Project is not ready to build:" + Environment.NewLine +
                string.Join(Environment.NewLine, issues));

        var game = project.Game!;
        int version = ResolveBuildVersion(project, game);

        var staging = request.StagingDirectory
                      ?? Path.Combine(Path.GetTempPath(), "ttmod-stage-" + Guid.NewGuid().ToString("N"));

        // Fresh staging so the patch contains only the current swaps.
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        var staged = Stage(project, staging);

        var outDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputArchivePath));
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);

        var options = new BuildOptions
        {
            Version = version,
            Overwrite = true,
            CompatibilityMode = request.CompatibilityMode,
        };

        var result = await _runner
            .BuildAsync(game.GameNum, request.OutputArchivePath, staging, options, progress, cancellationToken)
            .ConfigureAwait(false);

        return new PatchBuildResult(result.Success, request.OutputArchivePath, staged.Count, version, result.Output);
    }

    private static string NormalizeRelative(string targetName)
    {
        var rel = targetName.Replace('\\', '/').TrimStart('/');
        rel = rel.Replace('/', Path.DirectorySeparatorChar);
        return rel;
    }
}
