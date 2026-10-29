using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Export;

/// <summary>
/// Readable names for skeleton bones. Skeletons store only the CRC64 of each bone's
/// name, so names are recovered by hashing known Telltale rig bone names; a bone whose
/// name is not known is named after its symbol.
/// </summary>
public static class BoneNames
{
    // Names confirmed against the hashes in shipped skeletons (Wolf Among Us through the
    // Definitive Series); the rigs share most of their naming.
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

    private static readonly Lazy<Dictionary<ulong, string>> _bySymbol = new(() =>
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

    /// <summary>The bone's name when known, otherwise <c>bone_</c> and its symbol in hex.</summary>
    public static string For(ulong jointSymbol) =>
        TryGet(jointSymbol) ?? $"bone_{jointSymbol:X16}";

    /// <summary>The bone's name, or null when it is not a known name.</summary>
    public static string? TryGet(ulong jointSymbol) =>
        _bySymbol.Value.TryGetValue(jointSymbol, out var name) ? name : null;
}
