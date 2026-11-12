using System.Text;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Engine;
using TelltaleModding.Core.Projects;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// Runs a real ttarchext against the sample archives, one encrypted and one plain.
/// </summary>
public class EngineIntegrationTests
{
    private const int GameOfThronesNum = 57;

    private static TtarchRunner Runner() => new(EngineFactAttribute.FindRunnableEngine()!);

    [EngineFact]
    public async Task ListsRealUnencryptedArchive()
    {
        var listing = await Runner().ListAsync(GameOfThronesNum, SampleData.TestArchive);

        Assert.Equal(1706, listing.Count);
        Assert.Contains(listing.Files, f => f.Name == "obj_chairLYunkaiTavern.prop");
        // Files get sorted into categories.
        Assert.NotEmpty(listing.InCategory(AssetCategory.Skeleton));
        Assert.NotEmpty(listing.InCategory(AssetCategory.Scene));
    }

    [EngineFact]
    public async Task ListsRealEncryptedArchive()
    {
        var listing = await Runner().ListAsync(GameOfThronesNum, SampleData.GameOfThrones);

        Assert.Equal(1706, listing.Count);
        Assert.Contains(listing.Files, f => f.Name == "sk62_essosFemale.skl");
    }

    [EngineFact]
    public async Task ExtractsSingleFileByFilter()
    {
        using var outDir = new TempDir();
        var result = await Runner().ExtractAsync(
            GameOfThronesNum, SampleData.GameOfThrones, outDir.Path,
            new ExtractOptions { Filter = "obj_chairLYunkaiTavern.prop" });

        Assert.True(result.Success);
        var extracted = Path.Combine(outDir.Path, "obj_chairLYunkaiTavern.prop");
        Assert.True(File.Exists(extracted));

        var bytes = File.ReadAllBytes(extracted);
        Assert.Equal(197, bytes.Length);
        // Property files start with "5VSM".
        Assert.Equal("5VSM", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [EngineFact]
    public async Task PatchRoundTrip_BuildFromSwap_ThenListContainsTarget()
    {
        // 1. Extract a real asset to use as the replacement.
        using var work = new TempDir();
        var extractDir = Path.Combine(work.Path, "extracted");
        await Runner().ExtractAsync(
            GameOfThronesNum, SampleData.GameOfThrones, extractDir,
            new ExtractOptions { Filter = "obj_chestGoldTazall.prop" });
        var replacement = Path.Combine(extractDir, "obj_chestGoldTazall.prop");
        Assert.True(File.Exists(replacement));

        // 2. Build a patch from a one-swap project.
        var project = new ModProject
        {
            Name = "integration-test",
            GameNum = GameOfThronesNum,
            ReferenceArchivePath = SampleData.GameOfThrones,
            Swaps =
            {
                new AssetSwap
                {
                    TargetAssetName = "obj_chestGoldTazall.prop",
                    ReplacementFilePath = replacement,
                },
            },
        };

        var patchPath = Path.Combine(work.Path, "0.ttarch2");
        var result = await new PatchBuilder(Runner()).BuildAsync(
            project, new PatchBuildRequest(patchPath, Path.Combine(work.Path, "staging")));

        Assert.True(result.Success);
        Assert.Equal(1, result.StagedFileCount);
        Assert.Equal(7, result.Version); // read from the reference archive
        Assert.True(File.Exists(patchPath));

        // 3. The patch holds only the swapped asset.
        var listing = await Runner().ListAsync(GameOfThronesNum, patchPath);
        Assert.Single(listing.Files);
        Assert.Equal("obj_chestGoldTazall.prop", listing.Files[0].Name);
    }

    [EngineFact]
    public async Task CancellationKillsTheEngine()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner().ListAsync(GameOfThronesNum, SampleData.GameOfThrones, cancellationToken: cts.Token));
    }
}
