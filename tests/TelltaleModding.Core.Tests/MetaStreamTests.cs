using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

public class TelltaleCrc64Tests
{
    // Known-answer vectors taken from real MSV6 file headers.
    [Theory]
    [InlineData("D3DMesh", 0x98A19836CF4CCB7DUL)]
    [InlineData("T3Texture", 0x3E249E7E6F38CCE2UL)]
    [InlineData("Symbol", 0x004F023463D89FB0UL)]
    [InlineData("GFXPlatformAttributeParams", 0x1373B90F4631D050UL)]
    [InlineData("T3Texture::RegionStreamHeader", 0x584619F1AE300EEAUL)]
    public void HashSymbol_matches_real_file_hashes(string name, ulong expected) =>
        Assert.Equal(expected, TelltaleCrc64.HashSymbol(name));

    [Fact]
    public void HashSymbol_is_case_insensitive() =>
        Assert.Equal(TelltaleCrc64.HashSymbol("d3dmesh"), TelltaleCrc64.HashSymbol("D3DMesh"));
}

public class MetaStreamHeaderTests
{
    [Fact]
    public void Parses_real_mesh_file()
    {
        var data = File.ReadAllBytes(SampleData.MeshSample);
        var header = MetaStreamHeader.Parse(data);

        Assert.Equal(MetaStreamFormat.Msv6, header.Format);
        Assert.Equal(28, header.Classes.Count);
        Assert.Equal("D3DMesh", header.Classes[0].TypeName);
        Assert.True(header.ContainsClass("T3MeshData"));
        Assert.Equal(data.Length, header.TotalSize);
        Assert.True(header.AsyncSectionSize > 0, "mesh should carry GPU buffers in the async section");
    }

    [Fact]
    public void Parses_real_texture_file()
    {
        var data = File.ReadAllBytes(SampleData.TinyTextureSample);
        var header = MetaStreamHeader.Parse(data);

        Assert.Equal(MetaStreamFormat.Msv6, header.Format);
        Assert.Equal("T3Texture", header.Classes[0].TypeName);
        Assert.Equal(data.Length, header.TotalSize);
    }

    [Fact]
    public void Parses_tiny_mesh_file()
    {
        var data = File.ReadAllBytes(SampleData.TinyMeshSample);
        var header = MetaStreamHeader.Parse(data);
        Assert.Equal("D3DMesh", header.Classes[0].TypeName);
        Assert.Equal(data.Length, header.TotalSize);
    }

    [Fact]
    public void Header_only_parse_works_without_full_file()
    {
        var data = File.ReadAllBytes(SampleData.MeshSample);
        var ok = MetaStreamHeader.TryParse(
            data.AsSpan(0, 2048), out var header, out var error, isCompleteFile: false);

        Assert.True(ok, error);
        Assert.Equal(28, header!.Classes.Count);
    }

    [Fact]
    public void Describe_names_root_class_and_sections()
    {
        var header = MetaStreamHeader.Parse(File.ReadAllBytes(SampleData.MeshSample));
        var summary = header.Describe();
        Assert.Contains("MSV6", summary);
        Assert.Contains("root D3DMesh", summary);
        Assert.Contains("28 class versions", summary);
    }

    [Fact]
    public void Rejects_non_telltale_data()
    {
        Assert.False(MetaStreamHeader.TryParse("not a mesh file at all!!"u8, out _, out var error));
        Assert.Contains("magic", error);
    }

    [Fact]
    public void Reports_legacy_containers_distinctly()
    {
        Assert.False(MetaStreamHeader.TryParse("ERTM\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"u8, out _, out var error));
        Assert.Contains("Unsupported container", error);
    }

    [Fact]
    public void Rejects_size_mismatch()
    {
        var data = File.ReadAllBytes(SampleData.MeshSample);
        var truncated = data.AsSpan(0, data.Length - 100);
        Assert.False(MetaStreamHeader.TryParse(truncated, out _, out var error));
        Assert.Contains("disagree", error);
    }
}
