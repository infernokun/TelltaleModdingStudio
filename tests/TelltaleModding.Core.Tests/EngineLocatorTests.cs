using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Tests;

public class EngineLocatorTests
{
    [SkippableFact]
    public void LocatesEngineFromTestBinaryDirectory()
    {
        // The test binary sits as deep as the app's output folder, so this is the same search.
        var path = EngineLocator.TryLocate();
        Assert.NotNull(path);
    }

    [SkippableFact]
    public void ToleratesTrailingDirectorySeparator()
    {
        // AppContext.BaseDirectory usually ends with a separator.
        var start = AppContext.BaseDirectory;
        if (!Path.EndsInDirectorySeparator(start))
            start += Path.DirectorySeparatorChar;

        Assert.NotNull(EngineLocator.TryLocate(start));
    }
}
