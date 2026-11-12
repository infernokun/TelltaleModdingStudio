using System.Runtime.InteropServices;

namespace TelltaleModding.Core.Engine;

/// <summary>
/// Finds the ttarchext executable, either the Windows ttarchext.exe (needed for Oodle
/// archives) or a native build for other systems.
/// </summary>
public static class EngineLocator
{
    /// <summary>Environment variable that can point straight at the executable.</summary>
    public const string PathEnvVar = "TTARCHEXT_PATH";

    private const string WindowsExeName = "ttarchext.exe";
    private const string NativeExeName = "ttarchext";

    /// <summary>
    /// Finds the ttarchext path, or null. Checks <see cref="PathEnvVar"/> first, then
    /// folders around <paramref name="baseDirectory"/>, preferring a build that runs here.
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
        // Nothing runnable, so return what exists and let the caller explain.
        return fallback;
    }

    /// <summary>Like <see cref="TryLocate"/> but throws if nothing is found.</summary>
    public static string Locate(string? baseDirectory = null)
    {
        var path = TryLocate(baseDirectory);
        if (path is not null)
            return path;

        throw new TtarchException(
            $"Could not find the ttarchext engine. Set the {PathEnvVar} environment variable to its " +
            $"full path, or place it under an 'engine/ttarchext/' folder next to the application.");
    }

    /// <summary>True when the executable can run on this OS. Off Windows, .exe files can't.</summary>
    public static bool CanExecute(string path) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> CandidatePaths(string start)
    {
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var names = windows
            ? new[] { WindowsExeName, NativeExeName }
            : new[] { NativeExeName, WindowsExeName };

        // Walk up a few levels so this works from bin/ and the repo root. Trim the
        // trailing separator first or GetDirectoryName wastes one step on it.
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(start));
        for (int i = 0; i < 8 && dir is not null; i++)
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
