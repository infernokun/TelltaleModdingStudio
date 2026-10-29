using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Tests;

public class EngineLocatorTests
{
    [SkippableFact]
    public void LocatesEngineFromTestBinaryDirectory()
    {
        // The test binary lives five folders below the repo root
        // (tests/Project/bin/Debug/net8.0), the same depth as the desktop app's
        // output folder, so this is the exact walk the app performs at startup.
        var path = EngineLocator.TryLocate();
        Assert.NotNull(path);
    }

    [SkippableFact]
    public void ToleratesTrailingDirectorySeparator()
    {
        // AppContext.BaseDirectory ends with a separator on real hosts; the walk-up
        // must not burn an iteration re-visiting the same directory because of it.
        var start = AppContext.BaseDirectory;
        if (!Path.EndsInDirectorySeparator(start))
            start += Path.DirectorySeparatorChar;

        Assert.NotNull(EngineLocator.TryLocate(start));
    }
}
