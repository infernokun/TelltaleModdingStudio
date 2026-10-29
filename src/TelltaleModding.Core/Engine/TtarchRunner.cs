using System.Diagnostics;
using TelltaleModding.Core.Archives;

namespace TelltaleModding.Core.Engine;

/// <summary>
/// Async, cancellable wrapper around the ttarchext executable. Streams stdout and
/// stderr line-by-line through an optional <see cref="IProgress{T}"/> and returns the
/// full captured output. This replaces the legacy synchronous, UI-blocking,
/// stderr-dropping, unquoted-argument invocation.
/// </summary>
public sealed class TtarchRunner
{
    private readonly string _enginePath;

    /// <summary>Creates a runner, locating the engine automatically if no path is given.</summary>
    public TtarchRunner(string? enginePath = null)
    {
        _enginePath = enginePath ?? EngineLocator.Locate();
    }

    /// <summary>Full path to the ttarchext executable this runner drives.</summary>
    public string EnginePath => _enginePath;

    /// <summary>Lists an archive's contents without extracting, returning parsed entries.</summary>
    public async Task<ArchiveListing> ListAsync(
        int gameNum, string archivePath,
        ExtractOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var opts = (options ?? new ExtractOptions()) with { ListOnly = true };
        // ttarchext still wants an output-folder argument even for listing; "." is fine.
        var args = TtarchArguments.Extract(gameNum, archivePath, ".", opts);
        var result = await RunAsync(args, progress, cancellationToken).ConfigureAwait(false);
        return ArchiveListing.Parse(result.Output);
    }

    /// <summary>Extracts files from an archive to <paramref name="outputDir"/>.</summary>
    public Task<TtarchResult> ExtractAsync(
        int gameNum, string archivePath, string outputDir,
        ExtractOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDir);
        var args = TtarchArguments.Extract(gameNum, archivePath, outputDir, options);
        return RunAsync(args, progress, cancellationToken);
    }

    /// <summary>Rebuilds an archive at <paramref name="outputArchive"/> from a folder of files.</summary>
    public Task<TtarchResult> BuildAsync(
        int gameNum, string outputArchive, string inputFolder,
        BuildOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var args = TtarchArguments.Build(gameNum, outputArchive, inputFolder, options);
        return RunAsync(args, progress, cancellationToken);
    }

    /// <summary>Runs the engine with an explicit argument vector.</summary>
    public async Task<TtarchResult> RunAsync(
        IReadOnlyList<string> args,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!EngineLocator.CanExecute(_enginePath))
            throw new TtarchException(
                "The located engine is the Windows ttarchext.exe, which cannot run on this OS. " +
                "Build the native engine with 'make -C engine/ttarchext' (see its Makefile).");

        var psi = new ProcessStartInfo
        {
            FileName = _enginePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var output = new List<string>();
        var sync = new object();

        void Capture(string? line)
        {
            if (line is null) return;
            lock (sync) output.Add(line);
            progress?.Report(line);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new TtarchException($"Failed to start ttarchext at '{_enginePath}'.", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        List<string> captured;
        lock (sync) captured = new List<string>(output);
        return new TtarchResult(process.ExitCode, captured);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: the process may have exited between the check and the kill.
        }
    }
}
