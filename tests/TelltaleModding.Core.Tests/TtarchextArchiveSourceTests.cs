using System.Text;
using TelltaleModding.Core.Archives;

namespace TelltaleModding.Core.Tests;

public class TtarchextArchiveSourceTests
{
    private const int GameOfThronesNum = 57;

    [SkippableTheory]
    [InlineData("sk62_a.prop|sk62_b.prop", "sk62_*.prop")]
    [InlineData("sk54_rhys.prop|sk55_sasha.prop|skB0_claptrap.prop", "sk*.prop")]
    [InlineData("abc.prop|abc.prop", "abc.prop*")]
    [InlineData("a.d3dtx|b.prop", "*")]
    [InlineData("ab|abab", "ab*")]
    public void BuildWildcard_CoversEveryName(string names, string expected)
    {
        var list = names.Split('|');
        Assert.Equal(expected, TtarchextArchiveSource.BuildWildcard(list));
    }

    [EngineFact]
    public void ListsAndReadsRealArchive()
    {
        using var source = TtarchextArchiveSource.Open(SampleData.GameOfThrones, GameOfThronesNum, EngineFactAttribute.FindRunnableEngine());

        Assert.Equal(1706, source.Entries.Count);
        Assert.All(source.Entries, e => Assert.Equal(source.ArchivePath, e.ArchivePath));

        var entry = source.Entries.Single(e => e.Name == "obj_chairLYunkaiTavern.prop");
        var bytes = source.Read(entry);
        Assert.Equal(197, bytes.Length);
        Assert.Equal(entry.Size, bytes.Length);
        Assert.Equal("5VSM", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [EngineFact]
    public async Task ConcurrentReadsAndPrefetchReturnTheRightBytes()
    {
        using var source = TtarchextArchiveSource.Open(SampleData.TestArchive, GameOfThronesNum, EngineFactAttribute.FindRunnableEngine());
        var props = source.Entries.Where(e => e.Name.EndsWith(".prop", StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
        Assert.NotEmpty(props);

        var direct = await Task.WhenAll(props.Take(4).Select(e => Task.Run(() => source.Read(e))));
        for (int i = 0; i < direct.Length; i++)
            Assert.Equal(props[i].Size, direct[i].Length);

        source.Prefetch(props);
        foreach (var e in props)
            Assert.Equal(e.Size, source.Read(e).Length);
        // Prefetched copies are handed out once; a second read extracts again.
        Assert.Equal(props[0].Size, source.Read(props[0]).Length);
    }

    [EngineFact]
    public void OpenFailsClearlyForMissingArchive() =>
        Assert.Throws<FileNotFoundException>(() => TtarchextArchiveSource.Open(@"Z:\nope\missing.ttarch2", 67));

    [EngineFact]
    public void ReadAfterDispose_Throws()
    {
        var source = TtarchextArchiveSource.Open(SampleData.TestArchive, GameOfThronesNum, EngineFactAttribute.FindRunnableEngine());
        var entry = source.Entries[0];
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Read(entry));
    }
}
