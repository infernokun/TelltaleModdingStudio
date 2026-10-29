using System.Text;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Games;

namespace TelltaleModding.Core.Tests;

public class ArchiveInspectorTests
{
    [SkippableFact]
    public void DetectsRealEcttSample_GameOfThrones()
    {
        var info = ArchiveInspector.Inspect(SampleData.GameOfThrones);
        Assert.Equal(ArchiveMagic.Ectt, info.Magic);
        Assert.Equal(ArchiveKind.Ttarch2, info.Kind);
        Assert.Equal(7, info.Version);
        Assert.False(info.OodleCompressed);
    }

    [SkippableFact]
    public void DetectsRealNcttSample_TestArchive()
    {
        var info = ArchiveInspector.Inspect(SampleData.TestArchive);
        Assert.Equal(ArchiveMagic.Nctt, info.Magic);
        Assert.Equal(ArchiveKind.Ttarch2, info.Kind);
        Assert.Equal(7, info.Version);
    }

    [SkippableTheory]
    [InlineData("ZCTT", ArchiveMagic.Zctt)]
    [InlineData("ECTT", ArchiveMagic.Ectt)]
    [InlineData("NCTT", ArchiveMagic.Nctt)]
    [InlineData("3ATT", ArchiveMagic.Att3)]
    [InlineData("4ATT", ArchiveMagic.Att4)]
    public void RecognisesTtarch2Tags(string tag, ArchiveMagic expected)
    {
        var info = ArchiveInspector.Inspect(Stream(tag));
        Assert.Equal(expected, info.Magic);
        Assert.Equal(ArchiveKind.Ttarch2, info.Kind);
        Assert.Equal(7, info.Version);
        Assert.False(info.OodleCompressed);
    }

    [SkippableFact]
    public void LowercaseFirstCharSignalsOodle()
    {
        // "zCTT" is the Oodle-compressed variant of ZCTT.
        var info = ArchiveInspector.Inspect(Stream("zCTT"));
        Assert.Equal(ArchiveMagic.Zctt, info.Magic);
        Assert.True(info.OodleCompressed);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(9)]
    public void ClassicArchiveReadsVersionFromHeader(int version)
    {
        var bytes = new byte[] { (byte)version, 0, 0, 0 };
        var info = ArchiveInspector.Inspect(new MemoryStream(bytes));
        Assert.Equal(ArchiveMagic.Classic, info.Magic);
        Assert.Equal(ArchiveKind.Ttarch, info.Kind);
        Assert.Equal(version, info.Version);
    }

    [SkippableFact]
    public void UnrecognisedHeaderReportsUnknown()
    {
        var info = ArchiveInspector.Inspect(new MemoryStream(new byte[] { 0xFF, 0xEE, 0xDD, 0xCC }));
        Assert.Equal(ArchiveMagic.Unknown, info.Magic);
    }

    [SkippableFact]
    public void TooShortReportsUnknown()
    {
        var info = ArchiveInspector.Inspect(new MemoryStream(new byte[] { 1, 2 }));
        Assert.Equal(ArchiveMagic.Unknown, info.Magic);
    }

    private static MemoryStream Stream(string ascii) =>
        new(Encoding.ASCII.GetBytes(ascii));
}
