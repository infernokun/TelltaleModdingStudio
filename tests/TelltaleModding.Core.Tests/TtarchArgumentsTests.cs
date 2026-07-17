using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Tests;

public class TtarchArgumentsTests
{
    [Fact]
    public void Extract_ProducesGamenumArchiveOutputInOrder()
    {
        var args = TtarchArguments.Extract(24, @"C:\games\mi.ttarch", @"C:\out");
        // Default options: overwrite on.
        Assert.Equal(new[] { "-o", "24", @"C:\games\mi.ttarch", @"C:\out" }, args);
    }

    [Fact]
    public void Extract_PathsWithSpacesRemainSingleTokens()
    {
        var args = TtarchArguments.Extract(24, @"C:\my games\a.ttarch", @"C:\new folder");
        // The whole point: paths are discrete tokens, not a fragile concatenated string.
        Assert.Contains(@"C:\my games\a.ttarch", args);
        Assert.Contains(@"C:\new folder", args);
    }

    [Fact]
    public void Extract_ListFilterMetaAndVersionEmitFlags()
    {
        var args = TtarchArguments.Extract(1, "a", "b", new ExtractOptions
        {
            ListOnly = true,
            MetaExtract = true,
            Filter = "*.d3dmesh",
            Overwrite = false,
            VersionOverride = 7,
        });

        Assert.Contains("-l", args);
        Assert.Contains("-m", args);
        Assert.DoesNotContain("-o", args);

        int fi = args.ToList().IndexOf("-f");
        Assert.True(fi >= 0 && args[fi + 1] == "*.d3dmesh");

        int vi = args.ToList().IndexOf("-V");
        Assert.True(vi >= 0 && args[vi + 1] == "7");
    }

    [Fact]
    public void Build_EmitsRebuildAndVersionWithOutputBeforeInput()
    {
        var args = TtarchArguments.Build(57, @"C:\mods\0.ttarch2", @"C:\mods\staging",
            new BuildOptions { Version = 7 });

        Assert.Equal("-b", args[0]);
        Assert.Equal("-V", args[1]);
        Assert.Equal("7", args[2]);

        // Positional order for build is gamenum, OUTPUT, INPUT.
        var tail = args.Skip(args.Count - 3).ToArray();
        Assert.Equal(new[] { "57", @"C:\mods\0.ttarch2", @"C:\mods\staging" }, tail);
    }

    [Fact]
    public void Build_CompatibilityModeAddsXFlag()
    {
        var args = TtarchArguments.Build(57, "o", "i", new BuildOptions { CompatibilityMode = true });
        Assert.Contains("-x", args);
    }
}
