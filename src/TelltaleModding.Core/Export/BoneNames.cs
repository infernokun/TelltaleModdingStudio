using System.Globalization;
using System.Reflection;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Export;

/// <summary>
/// Readable names for skeleton bones. Skeletons store only the CRC64 of each bone's
/// name, so names are recovered by hashing known Telltale bone names: the shared rig
/// names below, the bundled bone name list (<c>Data/BoneNames.txt</c>, about 19,500
/// names collected across Telltale's games, by RandomTBush) and an optional user file. A bone whose
/// name is still not known is named after its symbol.
/// </summary>
public static class BoneNames
{
    // Names confirmed against the hashes in shipped skeletons (Wolf Among Us through the
    // Definitive Series); the rigs share most of their naming. Face-rig code reads only
    // these (see TryGetStandard).
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

    // Names missing from the bundled list, confirmed against shipped skeletons
    // (sk54_felix uses Vaughn's glasses rig).
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
    /// The bone's name when it is one of the rig names Telltale's skeletons share
    /// (head, browInner_L and so on), or null. Code that reads meaning from a name (which bones
    /// are on the face) uses this, so names from the bundled list don't change it.
    /// </summary>
    public static string? TryGetStandard(ulong jointSymbol) =>
        _standard.Value.TryGetValue(jointSymbol, out var name) ? name : null;

    /// <summary>How many bone names are known.</summary>
    public static int Count => All.Count;

    /// <summary>
    /// Where the user's own bone names are read from: one name per line, or a joint
    /// symbol in hex and the name to show for it ("<c>E0E3EEDD9B630B88 shoeLace_L</c>").
    /// </summary>
    public static string UserFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TelltaleModdingStudio", "BoneNames.txt");

    /// <summary>
    /// Adds the names in a bone name file (see <see cref="Parse"/>). A missing file adds
    /// nothing. Returns how many bones gained a name.
    /// </summary>
    public static int LoadFile(string path) =>
        File.Exists(path) ? Add(Parse(File.ReadLines(path))) : 0;

    /// <summary>
    /// Adds bone names. A plain name is known for the bone it hashes to and never replaces
    /// a name already known; an entry with an explicit symbol names that bone whatever its
    /// name hashes to, replacing any earlier name. Returns how many bones gained or changed a name.
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
    /// Reads a bone name list: one entry per line, blank lines and lines starting with
    /// <c>#</c> or <c>//</c> skipped. An entry is a bone name, or a joint symbol (16 hex
    /// digits, optionally with a <c>0x</c> or <c>bone_</c> prefix as exports show it) followed by
    /// whitespace, <c>=</c> or <c>,</c> and the name to give that bone.
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
