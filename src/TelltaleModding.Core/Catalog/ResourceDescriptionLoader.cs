using System.Collections.Concurrent;
using TelltaleModding.Core.Engine;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// Reads the resource description files next to a game's archives. They're Blowfish-encrypted
/// with the game key, and ttarchext decrypts them when given one as its input file.
/// </summary>
public static class ResourceDescriptionLoader
{
    /// <summary>The description files in <paramref name="archivesFolder"/>, sorted by name.</summary>
    public static IReadOnlyList<string> FindDescriptionFiles(string archivesFolder)
    {
        try
        {
            return Directory.EnumerateFiles(archivesFolder)
                .Where(ResourceSetDescription.IsDescriptionFileName)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Decrypts and parses every description in <paramref name="archivesFolder"/>, skipping
    /// files that can't be decrypted or don't register a set.
    /// </summary>
    public static async Task<IReadOnlyList<ResourceSetDescription>> LoadAsync(
        string archivesFolder, int gameNum, string? enginePath = null, int maxParallelism = 8,
        CancellationToken cancellationToken = default)
    {
        var files = FindDescriptionFiles(archivesFolder);
        if (files.Count == 0)
            return Array.Empty<ResourceSetDescription>();

        TtarchRunner? runner = null;
        try
        {
            var path = enginePath ?? EngineLocator.TryLocate();
            if (path is not null && File.Exists(path) && EngineLocator.CanExecute(path))
                runner = new TtarchRunner(path);
        }
        catch (TtarchException) { }

        var tempRoot = Path.Combine(Path.GetTempPath(), "TelltaleModdingStudio", $"resdesc-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var results = new ConcurrentBag<ResourceSetDescription>();
        try
        {
            await Parallel.ForEachAsync(files,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallelism), CancellationToken = cancellationToken },
                async (file, ct) =>
                {
                    var data = await ReadDecryptedAsync(file, gameNum, runner, tempRoot, ct).ConfigureAwait(false);
                    if (data is not null && ResourceSetDescription.Parse(data, Path.GetFileName(file)) is { } set)
                        results.Add(set);
                }).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return results.OrderBy(s => s.SourceFile, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>True when the bytes are already readable Lua source or bytecode.</summary>
    public static bool IsPlainLua(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0x1B && data[1] == (byte)'L' && data[2] == (byte)'u' && data[3] == (byte)'a')
            return true;
        int n = Math.Min(data.Length, 64);
        if (n == 0) return false;
        for (int i = 0; i < n; i++)
        {
            byte b = data[i];
            if (b is not ((>= 0x20 and < 0x7F) or (byte)'\r' or (byte)'\n' or (byte)'\t'))
                return false;
        }
        return true;
    }

    private static async Task<byte[]?> ReadDecryptedAsync(
        string file, int gameNum, TtarchRunner? runner, string tempRoot, CancellationToken ct)
    {
        byte[] raw;
        try { raw = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false); }
        catch (IOException) { return null; }
        if (IsPlainLua(raw))
            return raw;
        if (runner is null)
            return null;

        var outDir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            await runner.RunAsync(new[] { "-o", gameNum.ToString(), file, outDir }, cancellationToken: ct).ConfigureAwait(false);
            var output = Directory.EnumerateFiles(outDir).FirstOrDefault();
            if (output is null) return null;
            var decrypted = await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false);
            return IsPlainLua(decrypted) ? decrypted : null;
        }
        catch (TtarchException)
        {
            return null;
        }
    }
}
