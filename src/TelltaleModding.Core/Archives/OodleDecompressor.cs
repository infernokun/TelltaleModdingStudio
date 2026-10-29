using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TelltaleModding.Core.Archives;

/// <summary>
/// Binds <c>OodleLZ_Decompress</c> from a game's own <c>oo2core_*_win64.dll</c>.
/// Oodle is proprietary, so nothing is bundled: the archive reader looks for the
/// DLL next to the game. Each DLL is loaded once per process and never unloaded.
/// </summary>
internal sealed class OodleDecompressor
{
    // intptr_t OodleLZ_Decompress(const void* comp, intptr_t compLen, void* raw, intptr_t rawLen,
    //     int fuzzSafe, int checkCRC, int verbosity, void* decBufBase, intptr_t decBufSize,
    //     void* fpCallback, void* callbackUserData, void* decoderMemory, intptr_t decoderMemorySize,
    //     int threadPhase), the same 14 arguments ttarchext passes.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint DecompressFn(
        [In] byte[] comp, nint compLen, [Out] byte[] raw, nint rawLen,
        int fuzzSafe, int checkCrc, int verbosity, nint decBufBase, nint decBufSize,
        nint callback, nint callbackUserData, nint decoderMemory, nint decoderMemorySize, int threadPhase);

    private const int ThreadPhaseAll = 3;

    private static readonly Regex DllName = new(@"^oo2core_(\d+)_win64\.dll$", RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, OodleDecompressor> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private readonly DecompressFn _decompress;

    private OodleDecompressor(string dllPath, DecompressFn decompress)
    {
        DllPath = dllPath;
        _decompress = decompress;
    }

    /// <summary>Full path of the oo2core DLL in use.</summary>
    public string DllPath { get; }

    /// <summary>
    /// Finds and loads an oo2core DLL. Looks in <paramref name="searchDir"/>, its
    /// parent and its immediate subfolders, then beside the archive and in the
    /// archive folder's parent (a game's install root usually holds the DLL while
    /// the archives sit one level down). The newest oo2core version found wins.
    /// </summary>
    /// <exception cref="FileNotFoundException">No oo2core DLL could be found.</exception>
    public static OodleDecompressor Locate(string? searchDir, string archivePath)
    {
        var dll = FindDll(searchDir, archivePath) ?? throw new FileNotFoundException(
            $"'{Path.GetFileName(archivePath)}' is Oodle-compressed, but no oo2core_*_win64.dll was found " +
            $"(searched {(searchDir is null ? "" : $"'{searchDir}', ")}beside the archive and their parent/child folders). " +
            "Point the reader at the game's install folder, which ships the DLL.");

        lock (Loaded)
        {
            if (Loaded.TryGetValue(dll, out var existing))
                return existing;

            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(dll);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                throw new InvalidOperationException($"Could not load the Oodle library '{dll}': {ex.Message}", ex);
            }

            if (!NativeLibrary.TryGetExport(handle, "OodleLZ_Decompress", out var fn))
                throw new InvalidOperationException($"'{dll}' does not export OodleLZ_Decompress.");

            var loaded = new OodleDecompressor(dll, Marshal.GetDelegateForFunctionPointer<DecompressFn>(fn));
            Loaded[dll] = loaded;
            return loaded;
        }
    }

    /// <summary>
    /// Decompresses <paramref name="compLength"/> bytes of <paramref name="comp"/> into
    /// <paramref name="raw"/> (at most <paramref name="rawLength"/> bytes) and returns
    /// the decoded length, or 0 when Oodle rejects the data.
    /// </summary>
    public int Decompress(byte[] comp, int compLength, byte[] raw, int rawLength) =>
        (int)_decompress(comp, compLength, raw, rawLength, 0, 0, 0, 0, 0, 0, 0, 0, 0, ThreadPhaseAll);

    private static string? FindDll(string? searchDir, string archivePath)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(searchDir))
        {
            AddWithNeighbours(searchDir, children: true);
        }
        var archiveDir = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        if (archiveDir is not null)
            AddWithNeighbours(archiveDir, children: false);

        foreach (var dir in dirs)
        {
            string[] found;
            try
            {
                found = Directory.GetFiles(dir, "oo2core_*_win64.dll");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            var best = found
                .Select(path => (path, m: DllName.Match(Path.GetFileName(path))))
                .Where(c => c.m.Success)
                .OrderByDescending(c => int.Parse(c.m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Select(c => c.path)
                .FirstOrDefault();
            if (best is not null)
                return Path.GetFullPath(best);
        }
        return null;

        void AddWithNeighbours(string dir, bool children)
        {
            if (!Directory.Exists(dir))
                return;
            dir = Path.GetFullPath(dir);
            Add(dir);
            Add(Path.GetDirectoryName(dir));
            if (!children)
                return;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                    Add(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable folder just means fewer places to look.
            }
        }

        void Add(string? dir)
        {
            if (dir is not null && !dirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
                dirs.Add(dir);
        }
    }
}
