using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace TelltaleModding.App.Tests;

public class ShaderSourceTests
{
    // GLSL keywords reserved for future use (GLSL 3.30 and ESSL 3.00). Windows drivers let
    // most of them through as names, but Mesa on Linux rejects the shader, and the 3D view
    // stays blank. "packed" did exactly that.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "common", "partition", "active", "asm", "class", "union", "enum", "typedef", "template", "this",
        "packed", "resource", "goto", "inline", "noinline", "volatile", "public", "static", "extern",
        "external", "interface", "long", "short", "double", "half", "fixed", "unsigned", "superp",
        "input", "output", "hvec2", "hvec3", "hvec4", "dvec2", "dvec3", "dvec4", "fvec2", "fvec3",
        "fvec4", "sampler3DRect", "filter", "sizeof", "cast", "namespace", "using", "row_major",
    };

    public static IEnumerable<object[]> Shaders() =>
        typeof(App).Assembly.GetType("TelltaleModding.App.Views.MeshShaders")!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => new object[] { f.Name, (string)f.GetRawConstantValue()! });

    [Theory]
    [MemberData(nameof(Shaders))]
    public void Shaders_use_no_reserved_GLSL_words(string name, string source)
    {
        var code = Regex.Replace(source, @"//[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        var used = Regex.Matches(code, @"\b[A-Za-z_]\w*\b").Select(m => m.Value).Where(Reserved.Contains).Distinct();

        Assert.True(!used.Any(), $"{name} uses reserved GLSL words: {string.Join(", ", used)}");
    }

    private static string Source(string name) =>
        (string)typeof(App).Assembly.GetType("TelltaleModding.App.Views.MeshShaders")!.GetField(name)!.GetRawConstantValue()!;

    [Fact]
    public void Outlines_are_drawn_in_each_surface_s_own_color()
    {
        // The scene pass writes every surface's outline color (the material's, or the look's)
        // to a third target, the hull fills it too, and the edge pass draws lines in it.
        Assert.Contains("layout(location = 2) out vec4 outLine;", Source("SceneFragment"));
        Assert.Contains("outLine = vec4(uLineColor, alpha);", Source("SceneFragment"));
        Assert.Contains("layout(location = 2) out vec4 outLine;", Source("HullFragment"));
        Assert.Contains("texture(uLine, vUv).rgb", Source("PostFragment"));
        Assert.DoesNotContain("uOutlineColor", Source("PostFragment"));
    }
}
