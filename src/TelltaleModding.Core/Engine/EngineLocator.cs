using System.Runtime.InteropServices;

namespace TelltaleModding.Core.Engine;

/// <summary>
/// Finds the ttarchext executable. Two flavours exist: the shipped Windows binary
/// (<c>ttarchext.exe</c>, required for Oodle-compressed archives since it loads the
/// game's own oo2core DLL) and a native build (<c>ttarchext</c>, produced by the
/// Makefile in engine/ttarchext) that handles zlib/uncompressed archives on any OS.
/// </summary>
public static class EngineLocator
{
    /// <summary>Environment variable that, if set, points directly at the executable.</summary>
    public const string PathEnvVar = "TTARCHEXT_PATH";

    private const string WindowsExeName = "ttarchext.exe";
    private const string NativeExeName = "ttarchext";

    /// <summary>
    /// Resolves the ttarchext executable path, or returns null if it cannot be found.
    /// Search order: the <see cref="PathEnvVar"/> env var, then layout-relative
    /// locations starting at <paramref name="baseDirectory"/> (defaults to the app
    /// base), preferring the flavour that can actually run on this OS.
    /// </summary>
    public static string? TryLocate(string? baseDirectory = null)
    {
        var fromEnv = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        var start = baseDirectory ?? AppContext.BaseDirectory;
        string? fallback = null;
        foreach (var candidate in CandidatePaths(start))
        {
            if (!File.Exists(candidate))
                continue;
            if (CanExecute(candidate))
                return Path.GetFullPath(candidate);
            fallback ??= Path.GetFullPath(candidate);
        }
        // Nothing runnable — return whatever exists so callers can explain why.
        return fallback;
    }

    /// <summary>Like <see cref="TryLocate"/> but throws a descriptive exception on failure.</summary>
    public static string Locate(string? baseDirectory = null)
    {
        var path = TryLocate(baseDirectory);
        if (path is not null)
            return path;

        throw new TtarchException(
            $"Could not find the ttarchext engine. Set the {PathEnvVar} environment variable to its " +
            $"full path, or place it under an 'engine/ttarchext/' folder next to the application.");
    }

    /// <summary>
    /// True when the executable at <paramref name="path"/> can run on this OS: any
    /// flavour on Windows; only a native (non-.exe) build elsewhere.
    /// </summary>
    public static bool CanExecute(string path) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> CandidatePaths(string start)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // Prefer the flavour that runs on this OS.
        var names = windows
            ? new[] { WindowsExeName, NativeExeName }
            : new[] { NativeExeName, WindowsExeName };

        // Walk up a few levels so this works from bin/ output dirs and the repo root.
        var dir = start;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            foreach (var name in names)
            {
                yield return Path.Combine(dir, "engine", "ttarchext", name);
                yield return Path.Combine(dir, "ttarchext", name);
                yield return Path.Combine(dir, name);
            }
            dir = Path.GetDirectoryName(dir);
        }
    }
}
