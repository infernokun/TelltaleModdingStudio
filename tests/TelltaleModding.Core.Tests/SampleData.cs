namespace TelltaleModding.Core.Tests;

/// <summary>Locates the repository's <c>samples/</c> and <c>engine/</c> folders at test time.</summary>
internal static class SampleData
{
    private static readonly Lazy<string> _root = new(FindRepoRoot);

    public static string RepoRoot => _root.Value;
    public static string SamplesDir => Path.Combine(RepoRoot, "samples");
    public static string EngineExe => Path.Combine(RepoRoot, "engine", "ttarchext", "ttarchext.exe");

    public static string Sample(string fileName) => Path.Combine(SamplesDir, fileName);

    public static string GameOfThrones => Sample("GameOfThrones_pc_GameOfThrones102_data.ttarch2");
    public static string TestArchive => Sample("test.ttarch2");

    // Real extracted assets (MSV6 era) used by the meta-stream/format tests.
    public static string MeshSample => Sample("adv_arenaEntrance_meshesA.d3dmesh");
    public static string TinyMeshSample => Sample("obj_screenCUILayer1.d3dmesh");
    public static string TinyTextureSample => Sample("color_EEE.d3dtx");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "samples")) &&
                Directory.Exists(Path.Combine(dir, "engine")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repository root (looked for 'samples' and 'engine' folders).");
    }
}
