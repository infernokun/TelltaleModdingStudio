using System.Text;
using System.Text.RegularExpressions;

namespace TelltaleModding.Core.Catalog;

/// <summary>The slot names <see cref="CharacterPart.Slot"/> uses.</summary>
public static class CharacterSlots
{
    public const string Head = "Head";
    public const string Face = "Face";
    public const string Hair = "Hair";
    public const string UpperBody = "Upper body";
    public const string LowerBody = "Lower body";
    public const string Hands = "Hands";
    public const string Feet = "Feet";
    public const string Accessory = "Accessory";
    public const string Other = "Other";

    /// <summary>All slots in display order.</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { Head, Face, Hair, UpperBody, LowerBody, Hands, Feet, Accessory, Other };
}

/// <summary>
/// Name rules for characters: Telltale's skeleton-family prefixes (<c>sk62_</c>), turning
/// ids like <c>sk54_rhysFuture</c> into "Rhys" / "Future", splitting part names into
/// words and classifying them into <see cref="CharacterSlots"/>.
/// </summary>
public static class CharacterNaming
{
    private static readonly Regex Family = new(@"^(sk[0-9a-z]{2})_", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EmbeddedNumber = new(@"^(.*?[A-Za-z])(\d{3,})(.*)$", RegexOptions.Compiled);

    private static readonly string[] Acronyms = { "AI", "UI", "NPC", "TV" };

    /// <summary>
    /// The skeleton-family prefix including its underscore (<c>"sk62_"</c>), lowercased,
    /// or null when the name has none. Every Telltale character asset (skeleton, meshes,
    /// props, most animations) carries its family's prefix.
    /// </summary>
    public static string? FamilyPrefix(string name)
    {
        var m = Family.Match(name);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>The name without its family prefix (<c>sk62_clementine400</c> → <c>clementine400</c>).</summary>
    public static string StripFamily(string name)
    {
        var m = Family.Match(name);
        return m.Success ? name[m.Length..] : name;
    }

    /// <summary>
    /// Splits camelCase / snake_case / digit runs into words, keeping acronyms together:
    /// <c>handsomeJackAI</c> → handsome, Jack, AI; <c>bodyUpperBBackpack</c> → body, Upper, B, Backpack.
    /// </summary>
    public static IReadOnlyList<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '_' or ' ' or '-' or '.')
            {
                Flush();
                continue;
            }
            if (current.Length > 0)
            {
                char prev = current[^1];
                bool boundary =
                    (char.IsUpper(c) && (char.IsLower(prev) || char.IsDigit(prev))) ||
                    (char.IsDigit(c) != char.IsDigit(prev) && !IsNameDigit(text, i, current.Length)) ||
                    // "BBackpack": an upper followed by upper+lower starts a new word.
                    (char.IsUpper(c) && char.IsUpper(prev) && i + 1 < text.Length && char.IsLower(text[i + 1]));
                if (boundary) Flush();
            }
            current.Append(c);
        }
        Flush();
        return words;
    }

    /// <summary>
    /// A single digit ending a short word is part of the name ("Zer0"), not a number to split
    /// off ("Phase 2" keeps splitting because "Phase" is longer).
    /// </summary>
    private static bool IsNameDigit(string text, int i, int wordLength) =>
        char.IsDigit(text[i]) && wordLength <= 3 && (i + 1 >= text.Length || !char.IsDigit(text[i + 1])) &&
        !(i + 1 < text.Length && char.IsLower(text[i + 1]));

    /// <summary>Human-readable form: words split and capitalised (<c>rhys_hacking</c> → "Rhys Hacking").</summary>
    public static string Prettify(string text)
    {
        var words = SplitWords(text).Select(w =>
        {
            var acronym = Acronyms.FirstOrDefault(a => a.Equals(w, StringComparison.OrdinalIgnoreCase));
            if (acronym is not null && w.Any(char.IsUpper)) return acronym;
            return char.ToUpperInvariant(w[0]) + w[1..];
        });
        return string.Join(' ', words);
    }

    /// <summary>
    /// Computes each id's (DisplayName, Variant). An id whose core (id minus family prefix)
    /// extends another id's core at a word boundary is a variant of it, the shortest such
    /// core is the person (<c>rhysFuture</c>, <c>rhys_hacking</c> → "Rhys"). A run of three
    /// or more digits starts a variant too (<c>clementine400</c> → "Clementine" / "400").
    /// </summary>
    public static IReadOnlyDictionary<string, (string DisplayName, string? Variant)> ComputeNames(IEnumerable<string> ids)
    {
        var cores = ids.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, StripFamily, StringComparer.OrdinalIgnoreCase);
        // Roots are other ids' cores plus their names without a number ("clementine" from
        // "clementine400"), so "clementineCredits" groups with Clementine too.
        var coreSet = cores.Values
            .Concat(cores.Values.Select(c => EmbeddedNumber.Match(c)).Where(m => m.Success).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var memo = new Dictionary<string, (string Name, string? Variant)>(StringComparer.OrdinalIgnoreCase);

        (string Name, string? Variant) Split(string core, int depth)
        {
            if (memo.TryGetValue(core, out var cached)) return cached;
            (string, string?) result;
            var root = depth < 8
                ? coreSet.Where(b => b.Length < core.Length && core.StartsWith(b, StringComparison.OrdinalIgnoreCase) &&
                                     (IsWordBoundary(core, b.Length) ||
                                      // all-lowercase ids ("bigbywolfout"): accept a longer root and suffix
                                      (core.All(ch => !char.IsUpper(ch)) && b.Length >= 4 && core.Length - b.Length >= 3)))
                         .OrderBy(b => b.Length).FirstOrDefault()
                : null;
            if (root is not null)
            {
                var (rootName, rootVariant) = Split(root, depth + 1);
                var rest = core[root.Length..].TrimStart('_', '-', ' ');
                result = (rootName, Join(rootVariant, rest.Length > 0 ? Prettify(rest) : null));
            }
            else
            {
                var m = EmbeddedNumber.Match(core);
                result = m.Success
                    ? (Prettify(m.Groups[1].Value), Join(m.Groups[2].Value, m.Groups[3].Value.Trim('_').Length > 0 ? Prettify(m.Groups[3].Value) : null))
                    : (Prettify(core), null);
            }
            memo[core] = result;
            return result;
        }

        return cores.ToDictionary(kv => kv.Key, kv => Split(kv.Value, 0), StringComparer.OrdinalIgnoreCase);

        static string? Join(string? a, string? b) =>
            string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " " + b;
    }

    /// <summary>
    /// True when position <paramref name="at"/> of <paramref name="text"/> starts a new word:
    /// end of text, a separator, a digit after a letter, or an upper-case letter after a lower-case one.
    /// </summary>
    public static bool IsWordBoundary(string text, int at)
    {
        if (at <= 0 || at >= text.Length) return true;
        char c = text[at], prev = text[at - 1];
        if (c is '_' or '-' or ' ' or '.') return true;
        if (char.IsDigit(c) != char.IsDigit(prev)) return true;
        return char.IsUpper(c) && !char.IsUpper(prev);
    }

    /// <summary>
    /// A mesh's part name relative to its character: <c>sk62_clementine400_hairCap</c> →
    /// <c>hairCap</c>. Falls back to the skeleton's name, then to the name minus its family
    /// prefix; a mesh that is the whole character (<c>sk55_sasha</c>) keeps its core name.
    /// </summary>
    public static string PartName(string meshStem, string characterId, string? skeletonStem)
    {
        foreach (var owner in new[] { characterId, skeletonStem })
        {
            if (string.IsNullOrEmpty(owner) || !meshStem.StartsWith(owner, StringComparison.OrdinalIgnoreCase) ||
                meshStem.Length == owner.Length || !IsWordBoundary(meshStem, owner.Length))
                continue;
            var rest = meshStem[owner.Length..].TrimStart('_', '-');
            if (rest.Length > 0) return rest;
        }
        return StripFamily(meshStem);
    }

    private static readonly string[] HeadWords = { "head", "skull", "scalp", "neck" };
    private static readonly string[] FaceWords =
        { "face", "eye", "eyes", "eyeball", "eyelash", "eyelashes", "lash", "lashes", "brow", "brows", "eyebrow", "eyebrows",
          "mouth", "teeth", "tooth", "tongue", "beard", "mustache", "moustache", "stubble", "lips", "jaw" };
    private static readonly string[] HairWords = { "hair", "hairline", "haircut", "ponytail", "pony", "pigtails", "bun", "braid", "fur" };
    private static readonly string[] UpperWords =
        { "upper", "torso", "chest", "shirt", "jacket", "coat", "vest", "top", "sweater", "hoodie", "dress", "arm", "arms", "sleeve", "sleeves", "shoulder", "shoulders" };
    private static readonly string[] LowerWords =
        { "lower", "pants", "legs", "leg", "skirt", "shorts", "jeans", "trousers", "waist", "hips" };
    private static readonly string[] HandWords = { "hand", "hands", "glove", "gloves", "finger", "fingers", "fist", "fists" };
    private static readonly string[] FeetWords = { "feet", "foot", "shoe", "shoes", "boot", "boots", "sneaker", "sneakers", "sock", "socks" };
    private static readonly string[] AccessoryWords =
        { "accessory", "accessories", "glasses", "sunglasses", "goggles", "hat", "cap", "helmet", "hood", "mask", "backpack", "bag",
          "satchel", "holster", "belt", "watch", "necklace", "bracelet", "earring", "earrings", "jewelry", "scarf", "bandana",
          "badge", "gun", "knife", "sheath", "strap", "pouch", "quiver", "weapon", "phone", "headphones", "tie", "bandage",
          "cast", "sling", "patch", "lens", "port", "cigarette", "pipe", "collar", "pin", "button", "hanky", "wrap", "bow",
          "harness", "walkie", "talkie", "radio", "rifle", "pistol", "machete", "axe", "sword", "shield", "armor", "armour" };

    /// <summary>
    /// Classifies a part name (<see cref="PartName"/>) into one of <see cref="CharacterSlots"/>.
    /// The first word decides when it names a body region (<c>hairCap</c> → Hair,
    /// <c>bodyUpperBBackpack</c> → Upper body); otherwise any accessory word wins, then
    /// any region word. A plain <c>body</c> counts as Upper body (the torso-plus mesh).
    /// </summary>
    public static string ClassifySlot(string partName)
    {
        var words = SplitWords(partName).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Count == 0) return CharacterSlots.Other;

        string? Region(string w) =>
            HairWords.Contains(w) ? CharacterSlots.Hair :
            FaceWords.Contains(w) ? CharacterSlots.Face :
            HeadWords.Contains(w) ? CharacterSlots.Head :
            HandWords.Contains(w) ? CharacterSlots.Hands :
            FeetWords.Contains(w) ? CharacterSlots.Feet :
            LowerWords.Contains(w) ? CharacterSlots.LowerBody :
            UpperWords.Contains(w) ? CharacterSlots.UpperBody : null;

        var first = words[0];
        if (first is "body" or "outfit" or "clothes" or "clothing")
        {
            if (words.Skip(1).Contains("lower") || words.Skip(1).Any(LowerWords.Contains)) return CharacterSlots.LowerBody;
            return CharacterSlots.UpperBody;
        }
        if (first is "accessory" or "accessories") return CharacterSlots.Accessory;
        if (Region(first) is { } firstRegion) return firstRegion;
        if (words.Any(AccessoryWords.Contains)) return CharacterSlots.Accessory;
        foreach (var w in words)
        {
            if (Region(w) is { } region) return region;
            if (w is "body") return CharacterSlots.UpperBody;
        }
        return CharacterSlots.Other;
    }
}
