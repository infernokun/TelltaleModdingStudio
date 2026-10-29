using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// A fact that only runs when a ttarchext engine runnable on this OS is present
/// (e.g. the native build produced by <c>make -C engine/ttarchext</c>). Skipped
/// otherwise, so the unit suite stays green on hosts without a compiler.
/// </summary>
public sealed class EngineFactAttribute : SkippableFactAttribute
{
    public EngineFactAttribute()
    {
        var path = FindRunnableEngine();
        if (path is null)
            Skip = "No runnable ttarchext engine on this host (build one with: make -C engine/ttarchext).";
    }

    /// <summary>The engine path integration tests should use, or null when unavailable.</summary>
    public static string? FindRunnableEngine()
    {
        var path = EngineLocator.TryLocate(SampleData.RepoRoot);
        return path is not null && EngineLocator.CanExecute(path) ? path : null;
    }
}
