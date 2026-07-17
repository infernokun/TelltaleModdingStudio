namespace TelltaleModding.Core.Engine;

/// <summary>
/// Builds ttarchext command-line arguments as discrete tokens. Kept separate from
/// process execution so the exact argument vector can be unit-tested without a
/// Windows host. Every path is a distinct token, so the runtime quotes them
/// correctly even when they contain spaces.
/// </summary>
public static class TtarchArguments
{
    /// <summary>
    /// Builds arguments for extracting (or, with <see cref="ExtractOptions.ListOnly"/>,
    /// listing) an archive: <c>[options] &lt;gamenum&gt; &lt;archive&gt; &lt;outputDir&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> Extract(
        int gameNum, string archivePath, string outputDir, ExtractOptions? options = null)
    {
        options ??= new ExtractOptions();
        var args = new List<string>();

        if (options.ListOnly) args.Add("-l");
        if (options.Overwrite) args.Add("-o");
        if (options.MetaExtract) args.Add("-m");
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            args.Add("-f");
            args.Add(options.Filter!);
        }
        if (options.VersionOverride is int v)
        {
            args.Add("-V");
            args.Add(v.ToString());
        }

        args.Add(gameNum.ToString());
        args.Add(archivePath);
        args.Add(outputDir);
        return args;
    }

    /// <summary>
    /// Builds arguments for rebuilding an archive:
    /// <c>-b -V &lt;ver&gt; [options] &lt;gamenum&gt; &lt;outputArchive&gt; &lt;inputFolder&gt;</c>.
    /// Note the positional order for build is output-then-input, matching ttarchext.
    /// </summary>
    public static IReadOnlyList<string> Build(
        int gameNum, string outputArchive, string inputFolder, BuildOptions? options = null)
    {
        options ??= new BuildOptions();
        var args = new List<string> { "-b", "-V", options.Version.ToString() };

        if (options.Overwrite) args.Add("-o");
        if (options.CompatibilityMode) args.Add("-x");

        args.Add(gameNum.ToString());
        args.Add(outputArchive);
        args.Add(inputFolder);
        return args;
    }
}
