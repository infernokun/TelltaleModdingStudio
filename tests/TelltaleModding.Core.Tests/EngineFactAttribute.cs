using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// A fact that only runs when a ttarchext that works on this OS is present
/// (build one with <c>make -C engine/ttarchext</c>).
/// </summary>
public sealed class EngineFactAttribute : SkippableFactAttribute
{
    public EngineFactAttribute()
    {
        var path = FindRunnableEngine();
        if (path is null)
            Skip = "No runnable ttarchext engine on this host (build one with: make -C engine/ttarchext).";
    }

    /// <summary>The engine path for integration tests, or null if there isn't one.</summary>
    public static string? FindRunnableEngine()
    {
        var path = EngineLocator.TryLocate(SampleData.RepoRoot);
        return path is not null && EngineLocator.CanExecute(path) ? path : null;
    }
}
