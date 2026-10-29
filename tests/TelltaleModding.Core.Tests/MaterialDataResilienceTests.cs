using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// Property types the material reader used to stop at (losing every later property), and its
/// stepping over of types it has no layout for.
/// </summary>
public class MaterialDataResilienceTests
{
    private static readonly ulong DiffuseTexture = TelltaleCrc64.HashSymbol("diffuse.d3dtx");
    private static readonly ulong AlphaKey = MaterialShading.AlphaKey;

    /// <summary>Writes a version-2 PropertySet: flags, one block of parents and type groups.</summary>
    private static byte[] PropertySet(params (string Type, Action<BinaryWriter>[] Values)[] groups)
    {
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            w.Write(1);                                          // parent count
            w.Write(TelltaleCrc64.HashSymbol("material_base.prop"));
            w.Write(groups.Length);
            foreach (var (type, values) in groups)
            {
                w.Write(type.StartsWith("0x") ? Convert.ToUInt64(type, 16) : TelltaleCrc64.HashSymbol(type));
                w.Write(values.Length);
                foreach (var value in values)
                    value(w);
            }
        }
        using var file = new MemoryStream();
        using var f = new BinaryWriter(file);
        f.Write(2u);                                             // version
        f.Write(0u);                                             // flags
        f.Write((int)body.Length + 4);                           // block size counts itself
        f.Write(body.ToArray());
        return file.ToArray();
    }

    private static Action<BinaryWriter> Value(string key, Action<BinaryWriter> write) => w =>
    {
        w.Write(TelltaleCrc64.HashSymbol(key));
        write(w);
    };

    private static (string, Action<BinaryWriter>[]) Texture() =>
        ("Handle<T3Texture>", new[] { Value("Material - Diffuse Texture", w => w.Write(DiffuseTexture)) });

    private static (string, Action<BinaryWriter>[]) Floats() =>
        ("float", new[] { Value("Material - Alpha", w => w.Write(0.5f)) });

    private static (string, Action<BinaryWriter>[]) Blend() =>
        ("BlendMode", new[] { Value("Material - Blend Mode", w => w.Write((int)MaterialBlendMode.Alpha)) });

    private static void AssertLaterPropertiesRead(MaterialData m)
    {
        Assert.Equal(DiffuseTexture, m.Textures.Single(t => t.Key == MaterialTextureLocator.DiffuseTextureKey).TextureSymbol);
        Assert.Equal(0.5f, m.Floats[AlphaKey]);
        Assert.Equal(MaterialBlendMode.Alpha, m.BlendModeProperty);
    }

    [SkippableFact]
    public void Type_7caceebcd26d075c_is_int32_and_no_longer_hides_the_rest_of_the_set()
    {
        Assert.Equal(0x7caceebcd26d075cUL, TelltaleCrc64.HashSymbol("int32"));
        // Definitive Series UI materials (ui_dialog_settingsMenu…) lead with an int32 group.
        var data = PropertySet(
            ("int32", new[] { Value("Material - Some Int", w => w.Write(3)) }),
            Floats(), Texture(), Blend());

        var m = MaterialData.TryParse(data)!;

        Assert.True(m.IsComplete);
        Assert.Empty(m.SkippedTypes);
        Assert.Null(m.StoppedAtType);
        Assert.Equal(3, m.Ints[TelltaleCrc64.HashSymbol("Material - Some Int")]);
        AssertLaterPropertiesRead(m);
    }

    [SkippableFact]
    public void Enlighten_precompute_params_are_two_floats()
    {
        // Most Definitive Series materials end with this type after their compiled data.
        var data = PropertySet(
            Texture(),
            ("T3MaterialEnlightenPrecomputeParams", new[] { Value("Enlighten", w => { w.Write(1f); w.Write(0f); }) }),
            Floats(), Blend());

        var m = MaterialData.TryParse(data)!;

        Assert.True(m.IsComplete);
        AssertLaterPropertiesRead(m);
    }

    [SkippableFact]
    public void Containers_enums_and_blocked_structs_are_sized()
    {
        var data = PropertySet(
            ("EnumT3MaterialNormalSpaceType", new[] { Value("Normal Space", w => w.Write(1)) }),
            ("String", new[] { Value("Name", w => { w.Write(5); w.Write("hello"u8.ToArray()); }) }),
            ("DCArray<Handle<D3DMesh>>", new[] { Value("Meshes", w => { w.Write(2); w.Write(1UL); w.Write(2UL); }) }),
            ("Map<String,String,less<String>>", new[] { Value("Map", w =>
            {
                w.Write(1);
                w.Write(1); w.Write((byte)'a');
                w.Write(2); w.Write("bc"u8.ToArray());
            }) }),
            ("AnimOrChore", new[] { Value("Idle", w => { w.Write(12); w.Write(7UL); w.Write(12); w.Write(0UL); }) }),
            Texture(), Floats(), Blend());

        var m = MaterialData.TryParse(data)!;

        Assert.True(m.IsComplete);
        AssertLaterPropertiesRead(m);
    }

    [SkippableTheory]
    [InlineData(0)]   // first
    [InlineData(1)]   // between known types
    [InlineData(3)]   // last
    public void Unknown_type_is_stepped_over_wherever_it_is(int position)
    {
        const string unknown = "0x0123456789abcdef";
        var groups = new List<(string, Action<BinaryWriter>[])> { Texture(), Floats(), Blend() };
        groups.Insert(position, (unknown, new[]
        {
            Value("A", w => { w.Write(12); w.Write(1f); w.Write(2f); w.Write(3f); }),
            Value("B", w => w.Write(new byte[7])),
        }));

        var m = MaterialData.TryParse(PropertySet(groups.ToArray()))!;

        Assert.False(m.IsComplete);
        Assert.Null(m.StoppedAtType);
        Assert.Equal(new[] { 0x0123456789abcdefUL }, m.SkippedTypes);
        AssertLaterPropertiesRead(m);
    }

    [SkippableFact]
    public void Two_unknown_types_in_a_row_are_both_stepped_over()
    {
        var data = PropertySet(
            Texture(),
            ("0x1111111111111111", new[] { Value("A", w => w.Write(new byte[5])) }),
            ("0x2222222222222222", new[] { Value("B", w => w.Write(new byte[9])) }),
            Floats(), Blend());

        var m = MaterialData.TryParse(data)!;

        // The second unknown type's header is never found, only stepped over with the first.
        Assert.Equal(new[] { 0x1111111111111111UL }, m.SkippedTypes);
        Assert.Null(m.StoppedAtType);
        AssertLaterPropertiesRead(m);
    }

    [SkippableFact]
    public void A_misread_set_still_returns_what_came_before_and_is_not_complete()
    {
        // The float group claims more values than it holds: nothing after it can be placed.
        var data = PropertySet(Texture(), ("float", new[] { Value("Material - Alpha", w => w.Write(0.5f)) }));
        data[^16] = 9; // the float group's count, now overrunning the block

        var m = MaterialData.TryParse(data)!;

        Assert.False(m.IsComplete);
        Assert.Equal(DiffuseTexture, m.Textures.Single().TextureSymbol);
    }

    [SkippableTheory]
    [InlineData("sk62_clementine400_head.d3dmesh")]
    [InlineData("sk62_clementine400_eyesMouth.d3dmesh")]
    [InlineData("sk55_sasha.d3dmesh")]
    [InlineData("skB4_banditMarauder_body.d3dmesh")]
    public void Sample_materials_are_read_completely(string meshName)
    {
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample(meshName)), out var mesh, out var error), error);

        Assert.NotEmpty(mesh!.MaterialResources);
        foreach (var (_, payload) in mesh.MaterialResources)
        {
            var m = MaterialData.TryParse(payload);
            Assert.NotNull(m);
            Assert.True(m!.IsComplete, $"stopped at {m.StoppedAtType:x16}, skipped {string.Join(",", m.SkippedTypes.Select(t => t.ToString("x16")))}");
        }
    }
}
