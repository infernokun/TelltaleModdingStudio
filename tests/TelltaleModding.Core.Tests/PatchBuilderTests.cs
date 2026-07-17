using TelltaleModding.Core.Games;
using TelltaleModding.Core.Projects;

namespace TelltaleModding.Core.Tests;

public class PatchBuilderTests
{
    [Fact]
    public void ResolveVersion_OverrideWins()
    {
        var project = new ModProject { GameNum = 24, BuildVersionOverride = 3 };
        Assert.Equal(3, PatchBuilder.ResolveBuildVersion(project, project.Game!));
    }

    [Fact]
    public void ResolveVersion_FromReferenceArchive()
    {
        // Real ECTT sample -> version 7.
        var project = new ModProject { GameNum = 57, ReferenceArchivePath = SampleData.GameOfThrones };
        Assert.Equal(7, PatchBuilder.ResolveBuildVersion(project, project.Game!));
    }

    [Fact]
    public void ResolveVersion_DefaultsByKind()
    {
        var ttarch2 = new ModProject { GameNum = 54 }; // Wolf Among Us
        Assert.Equal(7, PatchBuilder.ResolveBuildVersion(ttarch2, ttarch2.Game!));

        var classic = new ModProject { GameNum = 0 }; // Wallace & Gromit ep1
        Assert.Equal(1, PatchBuilder.ResolveBuildVersion(classic, classic.Game!));
    }

    [Fact]
    public void Stage_CopiesEnabledSwapsUnderTargetNames()
    {
        using var staging = new TempDir();
        using var repl = new TempFile(new byte[] { 1, 2, 3, 4 });

        var project = new ModProject
        {
            GameNum = 24,
            Swaps =
            {
                new AssetSwap { TargetAssetName = "adv_guybrush.d3dtx", ReplacementFilePath = repl.Path },
                new AssetSwap { TargetAssetName = "skip.anm", ReplacementFilePath = repl.Path, Enabled = false },
            },
        };

        var staged = PatchBuilder.Stage(project, staging.Path);

        Assert.Single(staged);
        Assert.True(File.Exists(Path.Combine(staging.Path, "adv_guybrush.d3dtx")));
        Assert.False(File.Exists(Path.Combine(staging.Path, "skip.anm")));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(staging.Path, "adv_guybrush.d3dtx")));
    }

    [Fact]
    public void Stage_PreservesSubfolderTargetNames()
    {
        using var staging = new TempDir();
        using var repl = new TempFile();

        var project = new ModProject
        {
            GameNum = 24,
            Swaps = { new AssetSwap { TargetAssetName = @"chars\guybrush\face.d3dtx", ReplacementFilePath = repl.Path } },
        };

        PatchBuilder.Stage(project, staging.Path);

        var expected = Path.Combine(staging.Path, "chars", "guybrush", "face.d3dtx");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void Stage_RejectsPathTraversal()
    {
        using var staging = new TempDir();
        using var repl = new TempFile();

        var project = new ModProject
        {
            GameNum = 24,
            Swaps = { new AssetSwap { TargetAssetName = "../escape.d3dtx", ReplacementFilePath = repl.Path } },
        };

        Assert.Throws<InvalidOperationException>(() => PatchBuilder.Stage(project, staging.Path));
    }

    [Fact]
    public void ConventionalPatchName_MatchesKind()
    {
        Assert.Equal("0.ttarch2", PatchBuilder.ConventionalPatchName(ArchiveKind.Ttarch2));
        Assert.Equal("0.ttarch", PatchBuilder.ConventionalPatchName(ArchiveKind.Ttarch));
    }
}
