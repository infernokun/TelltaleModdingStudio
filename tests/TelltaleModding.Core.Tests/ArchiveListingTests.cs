using TelltaleModding.Core.Archives;

namespace TelltaleModding.Core.Tests;

public class ArchiveListingTests
{
    // Representative ttarchext -l output: status lines, a verbose dump, and file lines
    // in the "  {offset:x8} {size,-10} {name}" format.
    private static readonly string[] SampleOutput =
    {
        "",
        "Telltale TTARCH files extractor/rebuilder 0.3.2",
        "- open file mi.ttarch",
        "- version    7",
        "  offset        0001a2b0",
        "  size          00000200",
        "  name          debug_only.txt",
        "  0001a2b0 512        adv_guybrush.d3dmesh",
        "  0001a4b0 2048       adv_guybrush.skl",
        "  0001aab0 4096       walk_cycle.anm",
        "  0001bab0 128        ui/font_main.font",
        "  0001bd30 900        my folder/has spaces.d3dtx",
        "- 5 files found",
        "- done",
    };

    [Fact]
    public void ParsesOnlyFileLines()
    {
        var listing = ArchiveListing.Parse(SampleOutput);
        Assert.Equal(5, listing.Count);
    }

    [Fact]
    public void ParsesOffsetSizeAndName()
    {
        var listing = ArchiveListing.Parse(SampleOutput);
        var first = listing.Files[0];
        Assert.Equal("adv_guybrush.d3dmesh", first.Name);
        Assert.Equal(0x0001a2b0, first.Offset);
        Assert.Equal(512, first.Size);
    }

    [Fact]
    public void PreservesNamesContainingSpaces()
    {
        var listing = ArchiveListing.Parse(SampleOutput);
        Assert.Contains(listing.Files, f => f.Name == "my folder/has spaces.d3dtx");
    }

    [Fact]
    public void ClassifiesAssetsByExtension()
    {
        var listing = ArchiveListing.Parse(SampleOutput);
        Assert.Single(listing.InCategory(AssetCategory.Model));
        Assert.Single(listing.InCategory(AssetCategory.Skeleton));
        Assert.Single(listing.InCategory(AssetCategory.Animation));
        Assert.Equal(2, listing.InCategory(AssetCategory.Texture).Count()); // .font + .d3dtx
    }

    [Fact]
    public void IgnoresVerboseDumpLinesThatAreNotFileEntries()
    {
        var listing = ArchiveListing.Parse(SampleOutput);
        Assert.DoesNotContain(listing.Files, f => f.Name.Contains("debug_only"));
    }
}
