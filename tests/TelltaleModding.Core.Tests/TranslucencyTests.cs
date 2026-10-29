using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>Opacity of alpha-blended Tales from the Borderlands batches: tinted lenses and clear eye shells.</summary>
public class TranslucencyTests
{
    /// <summary>Serves a mesh sample and gives every texture its materials bind a tiny stand-in.</summary>
    private sealed class SampleResolver : IAssetResolver
    {
        private readonly string _meshName;
        private readonly Dictionary<ulong, string> _textures = new();

        public SampleResolver(string meshName)
        {
            _meshName = meshName;
            Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample(meshName)), out var mesh, out var error), error);
            foreach (var (_, payload) in mesh!.MaterialResources)
                foreach (var binding in MaterialData.TryParse(payload)?.Textures ?? Array.Empty<MaterialTextureBinding>())
                    if (binding.TextureSymbol != 0)
                        _textures.TryAdd(binding.TextureSymbol, $"t{binding.TextureSymbol:x16}.d3dtx");
        }

        public byte[]? TryRead(string fileName) =>
            fileName == _meshName ? File.ReadAllBytes(SampleData.Sample(fileName))
            : fileName.EndsWith(".d3dtx", StringComparison.Ordinal) ? File.ReadAllBytes(SampleData.TinyTextureSample)
            : null;

        public string? TextureNameForSymbol(ulong symbol) => _textures.GetValueOrDefault(symbol);

        public IEnumerable<string> SkeletonNames => Array.Empty<string>();
    }

    private static AssembledModel Build(string meshName) =>
        new ModelAssembler(new SampleResolver(meshName)).Build(new[] { meshName });

    [SkippableFact]
    public void Vertex_alpha_carries_the_opacity_of_an_alpha_blended_batch()
    {
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("tftb_sk54_vaughn_glasses.d3dmesh")), out var mesh, out _));
        var colors = mesh!.GetVertexColors(0);
        Assert.NotNull(colors);
        var geometry = mesh.GetGeometry();
        Assert.Equal(geometry.Positions.Length, colors!.Length);
        var lens = geometry.Batches[1];
        Assert.All(geometry.Indices.Skip(lens.StartIndex).Take(lens.PrimitiveCount * 3),
            v => Assert.Equal(80 / 255f, colors[v].W, 0.002f));
    }

    [SkippableFact]
    public void Tinted_lens_is_drawn_see_through()
    {
        var model = Build("tftb_sk54_vaughn_glasses.d3dmesh");

        Assert.Equal(2, model.Geometry.Batches.Count);
        Assert.Equal(MaterialBlendMode.Alpha, model.BatchBlendModes[1]);
        Assert.Equal(80 / 255f, model.BatchMaterialParams[1]!.Opacity, 0.002f);
        Assert.Equal(1f, model.BatchMaterialParams[0]!.Opacity);
        // The opacity is in the lens texture's alpha (widened from the 4×4 stand-in so a
        // viewer counting translucent pixels sees it); the frame keeps the texture as is.
        var lens = model.BatchTextures[1]!;
        Assert.True(lens.Width >= 8 && lens.Height >= 8);
        Assert.All(Enumerable.Range(0, lens.Width * lens.Height), i => Assert.InRange(lens.Rgba[i * 4 + 3], 78, 82));
        Assert.Contains(model.Details, d => d.Contains("31% opaque"));
        Assert.NotSame(model.BatchTextures[0], lens);
    }

    [SkippableFact]
    public void Clear_shell_over_the_eyes_is_left_out()
    {
        // A cornea shell: alpha-blended with vertex alpha 0, it is invisible in the game; drawn
        // opaque it covered the iris in the eye texture's grey corner.
        var model = Build("tftb_sk54_hyperionSuit_eyes.d3dmesh");

        Assert.Single(model.Geometry.Batches);
        Assert.Equal(288, model.Geometry.Batches[0].PrimitiveCount);
        Assert.Contains(model.Details, d => d.Contains("hidden (fully transparent"));
    }
}
