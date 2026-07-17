using TelltaleModding.Core.Projects;

namespace TelltaleModding.Core.Tests;

public class ModProjectTests
{
    [Fact]
    public void RoundTripsThroughJson()
    {
        var project = new ModProject
        {
            Name = "Guybrush Reskin",
            GameNum = 24,
            ReferenceArchivePath = @"C:\games\mi.ttarch",
            Swaps =
            {
                new AssetSwap { TargetAssetName = "adv_guybrush.d3dtx", ReplacementFilePath = @"C:\r\skin.d3dtx", Note = "pirate" },
                new AssetSwap { TargetAssetName = "walk.anm", ReplacementFilePath = @"C:\r\walk.anm", Enabled = false },
            },
        };

        var reloaded = ModProject.FromJson(project.ToJson());

        Assert.Equal("Guybrush Reskin", reloaded.Name);
        Assert.Equal(24, reloaded.GameNum);
        Assert.Equal(2, reloaded.Swaps.Count);
        Assert.Single(reloaded.EnabledSwaps);
        Assert.Equal("pirate", reloaded.Swaps[0].Note);
    }

    [Fact]
    public void GameResolvesFromNumber()
    {
        var project = new ModProject { GameNum = 54 };
        Assert.Equal("The Wolf Among Us", project.Game?.Name);
    }

    [Fact]
    public void ValidateFlagsInvalidGame()
    {
        var project = new ModProject { GameNum = 999 };
        Assert.Contains(project.Validate(), i => i.Contains("not a valid game"));
    }

    [Fact]
    public void ValidateFlagsNoEnabledSwaps()
    {
        var project = new ModProject { GameNum = 24 };
        Assert.Contains(project.Validate(), i => i.Contains("no enabled swaps"));
    }

    [Fact]
    public void ValidateFlagsMissingReplacementFile()
    {
        var project = new ModProject
        {
            GameNum = 24,
            Swaps = { new AssetSwap { TargetAssetName = "a.d3dtx", ReplacementFilePath = "/does/not/exist.d3dtx" } },
        };
        Assert.Contains(project.Validate(), i => i.Contains("Replacement file not found"));
    }

    [Fact]
    public void ValidateFlagsDuplicateTargets()
    {
        using var tmp = new TempFile();
        var project = new ModProject
        {
            GameNum = 24,
            Swaps =
            {
                new AssetSwap { TargetAssetName = "a.d3dtx", ReplacementFilePath = tmp.Path },
                new AssetSwap { TargetAssetName = "a.d3dtx", ReplacementFilePath = tmp.Path },
            },
        };
        Assert.Contains(project.Validate(), i => i.Contains("Duplicate target"));
    }

    [Fact]
    public void ValidatePassesForWellFormedProject()
    {
        using var tmp = new TempFile();
        var project = new ModProject
        {
            GameNum = 24,
            Swaps = { new AssetSwap { TargetAssetName = "a.d3dtx", ReplacementFilePath = tmp.Path } },
        };
        Assert.Empty(project.Validate());
    }
}
