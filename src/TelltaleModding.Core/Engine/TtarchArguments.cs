namespace TelltaleModding.Core.Engine;

/// <summary>Builds ttarchext command-line arguments, one token per argument.</summary>
public static class TtarchArguments
{
    /// <summary>Arguments to extract or list an archive.</summary>
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

    /// <summary>Arguments to build an archive. ttarchext takes the output before the input here.</summary>
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
