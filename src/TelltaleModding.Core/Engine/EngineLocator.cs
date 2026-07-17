using System.Runtime.InteropServices;

namespace TelltaleModding.Core.Engine;

/// <summary>
/// Finds the ttarchext executable. It is a native Windows binary (it loads the
/// game's oo2core DLL), so on non-Windows hosts callers can still build commands
/// but cannot actually run them.
/// </summary>
public static class EngineLocator
{
    /// <summary>Environment variable that, if set, points directly at the executable.</summary>
    public const string PathEnvVar = "TTARCHEXT_PATH";

    private const string ExeName = "ttarchext.exe";

    /// <summary>
    /// Resolves the ttarchext executable path, or returns null if it cannot be found.
    /// Search order: the <see cref="PathEnvVar"/> env var, then a set of layout-relative
    /// locations starting at <paramref name="baseDirectory"/> (defaults to the app base).
    /// </summary>
    public static string? TryLocate(string? baseDirectory = null)
    {
        var fromEnv = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return Path.GetFullPath(fromEnv);

        var start = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var candidate in CandidatePaths(start))
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    /// <summary>Like <see cref="TryLocate"/> but throws a descriptive exception on failure.</summary>
    public static string Locate(string? baseDirectory = null)
    {
        var path = TryLocate(baseDirectory);
        if (path is not null)
            return path;

        throw new TtarchException(
            $"Could not find {ExeName}. Set the {PathEnvVar} environment variable to its full path, " +
            $"or place it under an 'engine/ttarchext/' folder next to the application.");
    }

    /// <summary>True when the current OS can actually execute the (Windows) engine.</summary>
    public static bool CanRunEngine => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static IEnumerable<string> CandidatePaths(string start)
    {
        // Walk up a few levels so this works from bin/ output dirs and from the repo root.
        var dir = start;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            yield return Path.Combine(dir, "engine", "ttarchext", ExeName);
            yield return Path.Combine(dir, "ttarchext", ExeName);
            yield return Path.Combine(dir, ExeName);
            dir = Path.GetDirectoryName(dir);
        }
    }
}
