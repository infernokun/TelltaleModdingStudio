using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// Turns a game's file index into <see cref="CharacterEntry"/> records.
/// <para>
/// Characters come from family-prefixed agent props that name a skeleton and meshes.
/// A prefixed skeleton no prop uses becomes a character made of the meshes named after it.
/// Unprefixed skeletons are never characters.
/// </para>
/// </summary>
public static class CharacterGrouping
{
    /// <summary>Character props are small; anything bigger is not an agent description.</summary>
    public const long MaxPropSize = 1024 * 1024;

    private static readonly string[] Penalised =
    {
        "damage", "damaged", "bloody", "blood", "wet", "dead", "bitten", "bite", "missing", "no", "ripped", "rip", "cut",
        "bandage", "bandaged", "alpha", "low", "lod", "shadow", "dirty", "burnt", "burned", "zombie", "wound", "wounds",
        "torn", "broken", "hurt", "alt", "injured", "scar", "scars", "slashed", "amputated", "stump", "glow", "on", "off",
        "open", "hood", "hooded", "backpack", "bag", "hat", "nohat", "hatless", "sleeves", "rolled", "dirt", "grime",
    };

    /// <summary>The <c>.prop</c> files worth reading to find characters.</summary>
    public static IEnumerable<ArchiveEntryRef> SelectCharacterPropCandidates(IEnumerable<ArchiveEntryRef> entries) =>
        entries.Where(e =>
            e.Name.EndsWith(".prop", StringComparison.OrdinalIgnoreCase) &&
            e.Size <= MaxPropSize &&
            CharacterNaming.FamilyPrefix(e.Name) is not null &&
            !Path.GetFileNameWithoutExtension(e.Name).EndsWith("_m", StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the character list.</summary>
    /// <param name="entries">Every file the game uses, one winner per name.</param>
    /// <param name="props">Parsed character-prop candidates, keyed by prop file name.</param>
    /// <returns>Characters sorted by name, then variant, then id.</returns>
    public static IReadOnlyList<CharacterEntry> Build(
        IReadOnlyList<ArchiveEntryRef> entries, IReadOnlyDictionary<string, CharacterProp> props)
    {
        var bySymbol = new Dictionary<ulong, ArchiveEntryRef>();
        var byName = new Dictionary<string, ArchiveEntryRef>(StringComparer.OrdinalIgnoreCase);
        var meshes = new List<ArchiveEntryRef>();
        var skeletons = new List<ArchiveEntryRef>();
        foreach (var e in entries)
        {
            bool isMesh = e.Name.EndsWith(".d3dmesh", StringComparison.OrdinalIgnoreCase);
            bool isSkeleton = e.Name.EndsWith(".skl", StringComparison.OrdinalIgnoreCase);
            if (!isMesh && !isSkeleton) continue;
            byName.TryAdd(e.Name, e);
            bySymbol.TryAdd(TelltaleCrc64.HashSymbol(e.Name), e);
            bySymbol.TryAdd(TelltaleCrc64.HashSymbol(Path.GetFileNameWithoutExtension(e.Name)), e);
            if (isMesh) meshes.Add(e);
            else if (CharacterNaming.FamilyPrefix(e.Name) is not null) skeletons.Add(e);
        }

        ArchiveEntryRef? Resolve(ulong symbol, string extension) =>
            bySymbol.TryGetValue(symbol, out var e) && e.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? e : null;

        var drafts = new List<(string Id, ArchiveEntryRef Skeleton, List<(ArchiveEntryRef Mesh, bool? On)> Parts, bool FromProp)>();
        var usedSkeletons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (propName, prop) in props.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!prop.DescribesCharacter) continue;
            var id = Path.GetFileNameWithoutExtension(propName);
            var skeleton = Resolve(prop.SkeletonSymbol!.Value, ".skl") ?? byName.GetValueOrDefault(id + ".skl");
            if (skeleton is null) continue;

            var parts = new List<(ArchiveEntryRef, bool?)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in prop.MeshListSymbols)
                if (Resolve(symbol, ".d3dmesh") is { } mesh && seen.Add(mesh.Name))
                    parts.Add((mesh, prop.IsMeshVisible(mesh.Name) ?? true));
            if (prop.MeshSymbol is { } single && Resolve(single, ".d3dmesh") is { } singleMesh && seen.Add(singleMesh.Name))
            {
                // A single mesh next to a mesh list is usually the old combined body, so only
                // turn it on when it's the only geometry.
                bool? on = prop.IsMeshVisible(singleMesh.Name) ?? (parts.Count == 0 ? true : false);
                parts.Add((singleMesh, on));
            }
            if (parts.Count == 0) continue;

            drafts.Add((id, skeleton, parts, true));
            usedSkeletons.Add(skeleton.Name);
        }

        // Skeletons no prop uses: group meshes by name.
        var meshesByStem = meshes.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var propIds = new HashSet<string>(drafts.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var skeleton in skeletons.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(skeleton.Name);
            if (usedSkeletons.Contains(skeleton.Name) || propIds.Contains(stem)) continue;
            var parts = meshesByStem
                .Where(m =>
                {
                    var ms = Path.GetFileNameWithoutExtension(m.Name);
                    return ms.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                           (ms.Length > stem.Length + 1 && ms.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase));
                })
                .Select(m => (m, (bool?)null))
                .ToList();
            if (parts.Count == 0) continue;
            drafts.Add((stem, skeleton, parts, false));
        }

        var names = CharacterNaming.ComputeNames(drafts.Select(d => d.Id));
        var characters = new List<CharacterEntry>(drafts.Count);
        foreach (var (id, skeleton, rawParts, fromProp) in drafts)
        {
            var skeletonStem = Path.GetFileNameWithoutExtension(skeleton.Name);
            var partNames = rawParts
                .Select(p => CharacterNaming.PartName(Path.GetFileNameWithoutExtension(p.Mesh.Name), id, skeletonStem))
                .ToList();
            // Drop a sibling agent's name from borrowed meshes, unless that clashes with another part.
            var personWord = CharacterNaming.SplitWords(CharacterNaming.StripFamily(id)).FirstOrDefault() ?? id;
            for (int i = 0; i < partNames.Count; i++)
            {
                int cut = partNames[i].IndexOf('_');
                if (cut <= 0 || cut == partNames[i].Length - 1 ||
                    !partNames[i].StartsWith(personWord, StringComparison.OrdinalIgnoreCase)) continue;
                var shorter = partNames[i][(cut + 1)..];
                if (!partNames.Where((n, j) => j != i).Any(n => n.Equals(shorter, StringComparison.OrdinalIgnoreCase)))
                    partNames[i] = shorter;
            }
            var named = rawParts.Select((p, i) =>
                (p.Mesh, PartName: partNames[i], Slot: CharacterNaming.ClassifySlot(partNames[i]), p.On)).ToList();

            bool useProp = fromProp && named.Any(p => p.On == true);
            var defaults = useProp
                ? named.Select(p => p.On == true).ToArray()
                : HeuristicDefaults(named.Select(p => (p.PartName, p.Slot)).ToList());

            var parts = named.Select((p, i) => new CharacterPart(p.Mesh, p.PartName, p.Slot, defaults[i])).ToArray();
            var (displayName, variant) = names[id];
            characters.Add(new CharacterEntry(id, displayName, variant, skeleton, parts) { DefaultsFromGameData = useProp });
        }

        // Same name and variant in two skeleton families: tell them apart by family.
        foreach (var clash in characters
                     .GroupBy(c => (c.DisplayName.ToLowerInvariant(), c.Variant?.ToLowerInvariant()))
                     .Where(g => g.Count() > 1)
                     .ToList())
        {
            foreach (var c in clash)
            {
                var family = CharacterNaming.FamilyPrefix(c.Id)?.TrimEnd('_');
                if (family is null) continue;
                int i = characters.IndexOf(c);
                characters[i] = c with { Variant = c.Variant is null ? family : $"{c.Variant} {family}" };
            }
        }

        return characters
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Variant is null ? 0 : 1)
            .ThenBy(c => c.Variant, NaturalComparer.Instance)
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Turns on the plainest head and hairstyle when the game data leaves them all off for a
    /// script to pick, which would leave the eyes and mouth floating.
    /// </summary>
    public static CharacterEntry PickScriptedHead(CharacterEntry character)
    {
        if (!character.DefaultsFromGameData)
            return character;
        var on = character.Parts.Select(p => p.DefaultOn).ToArray();
        if (!PickHeadIfScripted(character.Parts.Select(p => (p.PartName, p.Slot)).ToList(), on))
            return character;
        return character with { Parts = character.Parts.Select((p, i) => p with { DefaultOn = on[i] }).ToArray() };
    }

    /// <summary>
    /// Turns on the plainest head and hairstyle when face parts are on but no head, hair or
    /// whole-body part is. Returns whether it did.
    /// </summary>
    internal static bool PickHeadIfScripted(IReadOnlyList<(string PartName, string Slot)> parts, bool[] on)
    {
        bool AnyOn(string slot) => Enumerable.Range(0, parts.Count).Any(i => on[i] && parts[i].Slot == slot);
        if (!parts.Any(p => p.Slot == CharacterSlots.Head) || AnyOn(CharacterSlots.Head) || AnyOn(CharacterSlots.Hair) ||
            !AnyOn(CharacterSlots.Face) || AnyOn(CharacterSlots.Other))
            return false;
        foreach (var slot in new[] { CharacterSlots.Head, CharacterSlots.Hair })
        {
            var inSlot = Enumerable.Range(0, parts.Count).Where(i => parts[i].Slot == slot).ToList();
            if (inSlot.Count == 0)
                continue;
            var picked = HeuristicDefaults(inSlot.Select(i => parts[i]).ToList());
            for (int k = 0; k < inSlot.Count; k++)
                on[inSlot[k]] |= picked[k];
        }
        return true;
    }

    /// <summary>
    /// Picks a default outfit when the game data doesn't say: the plainest part per body slot,
    /// the plainest face part of each kind, no accessories, and "Other" only when nothing else is on.
    /// Plainest means fewest damage or alternate words, then the shortest name.
    /// </summary>
    public static bool[] HeuristicDefaults(IReadOnlyList<(string PartName, string Slot)> parts)
    {
        var on = new bool[parts.Count];
        int Score(int i) =>
            CharacterNaming.SplitWords(parts[i].PartName).Count(w => Penalised.Contains(w.ToLowerInvariant())) * 1000 +
            parts[i].PartName.Length;

        foreach (var group in Enumerable.Range(0, parts.Count).GroupBy(i => parts[i].Slot))
        {
            switch (group.Key)
            {
                case CharacterSlots.Accessory:
                case CharacterSlots.Other:
                    break;
                case CharacterSlots.Face:
                    // Faces are often split into eyes, teeth and so on: take one of each kind.
                    foreach (var kind in group.GroupBy(i => (CharacterNaming.SplitWords(parts[i].PartName).FirstOrDefault() ?? "").ToLowerInvariant()))
                        on[kind.MinBy(Score)] = true;
                    break;
                default:
                    on[group.MinBy(Score)] = true;
                    break;
            }
        }

        if (!on.Any(x => x))
        {
            // Nothing in a body slot, so show the plainest "Other" part, usually the whole mesh.
            var others = Enumerable.Range(0, parts.Count).Where(i => parts[i].Slot != CharacterSlots.Accessory).ToList();
            if (others.Count == 0) others = Enumerable.Range(0, parts.Count).ToList();
            if (others.Count > 0) on[others.MinBy(Score)] = true;
        }
        return on;
    }

    /// <summary>
    /// Variants that wear their base character's body but leave one of its default slots empty
    /// borrow the base's part for that slot, so the body doesn't show a hole the scene would fill.
    /// </summary>
    public static IReadOnlyList<CharacterEntry> InheritBaseParts(IReadOnlyList<CharacterEntry> characters)
    {
        var bases = characters
            .Where(c => c.Variant is null && c.Skeleton is not null && c.Parts.Any(p => p.DefaultOn))
            .GroupBy(c => (c.DisplayName.ToLowerInvariant(), c.Skeleton!.Name.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.First());
        var result = new CharacterEntry[characters.Count];
        for (int i = 0; i < characters.Count; i++)
        {
            var character = result[i] = characters[i];
            if (character.Variant is null || character.Skeleton is null ||
                !bases.TryGetValue((character.DisplayName.ToLowerInvariant(), character.Skeleton.Name.ToLowerInvariant()), out var baseCharacter))
                continue;
            var own = character.Parts.Select(p => p.Mesh.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool sameBody = baseCharacter.Parts.Any(p => p.DefaultOn && p.Slot is "Upper body" or "Lower body" && own.Contains(p.Mesh.Name));
            if (!sameBody)
                continue;
            var slots = character.Parts.Select(p => p.Slot).ToHashSet(StringComparer.Ordinal);
            var borrowed = baseCharacter.Parts
                .Where(p => p.DefaultOn && !slots.Contains(p.Slot) && !own.Contains(p.Mesh.Name))
                .ToList();
            if (borrowed.Count > 0)
                result[i] = character with { Parts = character.Parts.Concat(borrowed).ToList() };
        }
        return result;
    }

    /// <summary>
    /// Generic agents mark a whole pool of alternates visible and a script picks one at spawn.
    /// When a body slot has three or more parts on for the same side, keep only the plainest
    /// per slot and side.
    /// </summary>
    public static CharacterEntry ThinRandomPools(CharacterEntry character)
    {
        if (!character.DefaultsFromGameData)
            return character;
        var on = character.Parts.Select((p, i) => (Part: p, Index: i)).Where(x => x.Part.DefaultOn).ToList();
        var groups = on
            .Where(x => x.Part.Slot is not (CharacterSlots.Accessory or CharacterSlots.Other or CharacterSlots.Face))
            .GroupBy(x => (x.Part.Slot, Side(x.Part.PartName)))
            .ToList();
        if (!groups.Any(g => g.Count() >= 3))
            return character;

        var keep = new HashSet<int>(on.Select(x => x.Index));
        foreach (var group in groups.Where(g => g.Count() >= 2))
        {
            var best = group.MinBy(x => Plainness(x.Part.PartName));
            foreach (var x in group)
                if (x.Index != best.Index)
                    keep.Remove(x.Index);
        }
        var parts = character.Parts.Select((p, i) => p.DefaultOn && !keep.Contains(i) ? p with { DefaultOn = false } : p).ToArray();
        return character with { Parts = parts };
    }

    /// <summary>"Right", "Left" or "" from a part name's words.</summary>
    private static string Side(string partName)
    {
        foreach (var word in CharacterNaming.SplitWords(partName).Skip(1))
        {
            if (word.Equals("Right", StringComparison.OrdinalIgnoreCase) || word.Equals("R", StringComparison.Ordinal))
                return "Right";
            if (word.Equals("Left", StringComparison.OrdinalIgnoreCase) || word.Equals("L", StringComparison.Ordinal))
                return "Left";
        }
        return "";
    }

    private static int Plainness(string partName) =>
        CharacterNaming.SplitWords(partName).Count(w => Penalised.Contains(w.ToLowerInvariant()) || PoolPenalised.Contains(w.ToLowerInvariant())) * 1000 +
        partName.Length;

    /// <summary>Damage words of generic pools, on top of <see cref="Penalised"/>.</summary>
    private static readonly string[] PoolPenalised = { "stump", "fracture", "chop", "eaten", "jaw", "unique", "missing" };

    /// <summary>Compares numbers in strings by value, with nulls first.</summary>
    private sealed class NaturalComparer : IComparer<string?>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null) return x is null ? (y is null ? 0 : -1) : 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0');
                    var b = y[sj..j].TrimStart('0');
                    int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
