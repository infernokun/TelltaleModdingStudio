using System.Globalization;
using System.Reflection;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Export;

/// <summary>
/// Readable names for skeleton bones. Skeletons only store a hash of each name, so we
/// hash known names: the rig names below, the bundled list collected by RandomTBush
/// (<c>Data/BoneNames.txt</c>) and an optional user file.
/// </summary>
public static class BoneNames
{
    // Rig names shared by Telltale's skeletons. Face-rig code reads only these.
    private static readonly string[] Known =
    {
        "root", "pelvis", "spine1", "spine2", "spine3", "spine4", "neck", "neck2", "head", "jaw",
        "tongue", "tongue2", "chin", "browMiddle", "browUpper", "noseBridge", "noseUpper",
        "mouthLowerCenter", "mouthUpperCenter",
        "backpack1", "backpack2", "backpack3", "backpack4",
        "ankle", "armTwist1", "armTwist2", "armTwist3", "arm", "ball", "browCenter", "browInner", "browOuter",
        "cheekInner", "cheekLowerOuter", "cheekOuter", "cheekUpperInner", "cheekUpperOuter", "cheek",
        "chinOuter", "deltoid", "ear", "elbowHelper", "elbowTwist", "elbow",
        "eyeLidLower", "eyeLidUpper", "eye", "forehead", "hair1", "hair", "heel", "knee",
        "legTwist1", "legTwist2", "legTwist3", "leg", "lipUpperCorner", "lipUpper", "mouthCorner",
        "mouthLower", "mouthUpper", "nose", "shoulderPad", "shoulder", "temple",
        "thumb1", "thumb2", "thumb3", "toe", "wristTwist", "wrist",
    };

    // Names missing from the bundled list.
    private static readonly string[] Extra =
    {
        "vaughn_lens1", "vaughn_lens2", "vaughn_lens3",
    };

    private static readonly Lazy<Dictionary<ulong, string>> _standard = new(() =>
    {
        var map = new Dictionary<ulong, string>();
        foreach (var name in Known)
        {
            map.TryAdd(TelltaleCrc64.HashSymbol(name), name);
            map.TryAdd(TelltaleCrc64.HashSymbol(name + "_L"), name + "_L");
            map.TryAdd(TelltaleCrc64.HashSymbol(name + "_R"), name + "_R");
        }
        return map;
    });

    private static readonly object _gate = new();

    // Replaced, never mutated, so lookups need no lock.
    private static Dictionary<ulong, string>? _all;

    private static Dictionary<ulong, string> All
    {
        get
        {
            var all = Volatile.Read(ref _all);
            if (all is not null)
                return all;
            lock (_gate)
            {
                if (_all is null)
                {
                    var map = new Dictionary<ulong, string>(_standard.Value);
                    foreach (var name in Extra.Concat(BundledNames()))
                        map.TryAdd(TelltaleCrc64.HashSymbol(name), name);
                    Volatile.Write(ref _all, map);
                }
                return _all;
            }
        }
    }

    /// <summary>The bone's name when known, otherwise <c>bone_</c> and its symbol in hex.</summary>
    public static string For(ulong jointSymbol) =>
        TryGet(jointSymbol) ?? $"bone_{jointSymbol:X16}";

    /// <summary>The bone's name, or null when it is not a known name.</summary>
    public static string? TryGet(ulong jointSymbol) =>
        All.TryGetValue(jointSymbol, out var name) ? name : null;

    /// <summary>
    /// The bone's name if it's one of the shared rig names, or null. Used by code that
    /// reads meaning from names, so the bundled list doesn't affect it.
    /// </summary>
    public static string? TryGetStandard(ulong jointSymbol) =>
        _standard.Value.TryGetValue(jointSymbol, out var name) ? name : null;

    /// <summary>How many bone names are known.</summary>
    public static int Count => All.Count;

    /// <summary>Path of the user's own bone name file.</summary>
    public static string UserFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleModdingStudio", "BoneNames.txt");

    /// <summary>Adds the names in a bone name file. Returns how many bones gained a name.</summary>
    public static int LoadFile(string path) =>
        File.Exists(path) ? Add(Parse(File.ReadLines(path))) : 0;

    /// <summary>
    /// Adds bone names and returns how many changed. A plain name never replaces a known one,
    /// but an entry with a symbol always does.
    /// </summary>
    public static int Add(IEnumerable<(ulong? Symbol, string Name)> entries)
    {
        lock (_gate)
        {
            var map = new Dictionary<ulong, string>(All);
            int added = 0;
            foreach (var (symbol, name) in entries)
            {
                if (symbol is { } explicitSymbol)
                {
                    if (!map.TryGetValue(explicitSymbol, out var old) || old != name)
                    {
                        map[explicitSymbol] = name;
                        added++;
                    }
                }
                else if (map.TryAdd(TelltaleCrc64.HashSymbol(name), name))
                {
                    added++;
                }
            }
            Volatile.Write(ref _all, map);
            return added;
        }
    }

    /// <summary>
    /// Reads a bone name list. Each line is a name, or a hex joint symbol followed by the
    /// name to give it. Lines starting with <c>#</c> or <c>//</c> are skipped.
    /// </summary>
    public static IEnumerable<(ulong? Symbol, string Name)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            int split = line.IndexOfAny(new[] { ' ', '\t', '=', ',' });
            if (split > 0 && TryParseSymbol(line[..split], out ulong symbol))
            {
                var name = line[(split + 1)..].Trim(' ', '\t', '=', ',');
                if (name.Length > 0)
                    yield return (symbol, name);
                continue;
            }
            yield return (null, line);
        }
    }

    private static bool TryParseSymbol(string text, out ulong symbol)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        else if (text.StartsWith("bone_", StringComparison.OrdinalIgnoreCase))
            text = text[5..];
        symbol = 0;
        return text.Length == 16 &&
               ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out symbol);
    }

    private static IEnumerable<string> BundledNames()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("BoneNames.txt", StringComparison.OrdinalIgnoreCase));
        if (resource is null)
            yield break;
        using var reader = new StreamReader(asm.GetManifestResourceStream(resource)!);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0)
                yield return line;
        }
    }
}
