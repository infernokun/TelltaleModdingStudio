using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Catalog;

/// <summary>
/// Turns a game's file index into <see cref="CharacterEntry"/> records.
/// <para>
/// Primary source: character agent props. Every family-prefixed <c>.prop</c> (e.g.
/// <c>sk62_clementine400.prop</c>, <c>sk54_rhys_hacking.prop</c>) that names a
/// "Skeleton File" and meshes defines one character: its parts are its "D3D Mesh List"
/// (plus "D3D Mesh"), and its default outfit is its top-level "Mesh X - Visible" values.
/// </para>
/// <para>
/// Fallback: a family-prefixed skeleton that no such prop uses becomes a character whose
/// parts are the meshes named <c>&lt;skeleton&gt;</c> or <c>&lt;skeleton&gt;_*</c>, with
/// defaults from <see cref="HeuristicDefaults"/>. Unprefixed skeletons (<c>obj_</c>,
/// <c>adv_</c>, <c>ui_</c>, <c>fx_</c>: props, foliage, UI) are never characters.
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

    /// <summary>
    /// The <c>.prop</c> files worth reading to find characters: family-prefixed props
    /// that aren't material props (<c>*_m.prop</c>) and are small.
    /// </summary>
    public static IEnumerable<ArchiveEntryRef> SelectCharacterPropCandidates(IEnumerable<ArchiveEntryRef> entries) =>
        entries.Where(e =>
            e.Name.EndsWith(".prop", StringComparison.OrdinalIgnoreCase) &&
            e.Size <= MaxPropSize &&
            CharacterNaming.FamilyPrefix(e.Name) is not null &&
            !Path.GetFileNameWithoutExtension(e.Name).EndsWith("_m", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the character list.
    /// </summary>
    /// <param name="entries">Every file the game uses (one winner per name).</param>
    /// <param name="props">Parsed character-prop candidates, keyed by prop file name.</param>
    /// <returns>Characters sorted by DisplayName, then Variant (null first), then Id.</returns>
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
                // A legacy single mesh next to a mesh list is usually the old combined body;
                // only show it by default when it is the character's only geometry.
                bool? on = prop.IsMeshVisible(singleMesh.Name) ?? (parts.Count == 0 ? true : false);
                parts.Add((singleMesh, on));
            }
            if (parts.Count == 0) continue;

            drafts.Add((id, skeleton, parts, true));
            usedSkeletons.Add(skeleton.Name);
        }

        // Skeletons nobody's prop uses: group meshes by name.
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
            // Meshes borrowed from a sibling agent ("clementine105_headHands" on Clementine 100)
            // read better without the sibling's name, unless that would clash with another part.
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

        // Same name and variant in two skeleton families (TFTB's sk54_/sk55_ hyperionSuit):
        // tell them apart by family.
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
    /// Some game data turns every head and hairstyle off and lets a script pick them at spawn
    /// (Game of Thrones' sk61_bannermanForrester), leaving eyes and mouth floating over the
    /// neck. Characters whose defaults come from game data get the plainest head and hairstyle.
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
    /// When face parts are on but no head or hair (and no whole-body part that may carry a
    /// head), turns on the plainest head and hairstyle. A head built into the body mesh with
    /// its hair on (sk62_ladyKingsLandingLow, whose headCap is a scalp piece) is left alone.
    /// Returns whether it did.
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
    /// Picks a default outfit when the game data doesn't say: per body slot (Head, Hair,
    /// Upper/Lower body, Hands, Feet) the plainest part: fewest damage/alternate words
    /// (bloody, wet, no…, bandage, hat…), then the shortest name; the plainest Face part of each
    /// kind (eyes, teeth…);
    /// no Accessories; Other parts only when nothing else is on (a whole-body mesh).
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
                    // Faces are often split (eyes, teeth, tongue): the plainest of each kind.
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
            // Nothing in a body slot: show the plainest "Other" part (typically the whole mesh).
            var others = Enumerable.Range(0, parts.Count).Where(i => parts[i].Slot != CharacterSlots.Accessory).ToList();
            if (others.Count == 0) others = Enumerable.Range(0, parts.Count).ToList();
            if (others.Count > 0) on[others.MinBy(Score)] = true;
        }
        return on;
    }

    /// <summary>
    /// Variants that wear their base character's body but leave one of the base's default
    /// slots empty borrow the base's default part for it. Tales from the Borderlands'
    /// <c>sk54_rhys_hacking</c> lists Rhys's body without the temple-port piece that fills a
    /// hole in it (the scene attaches one); shown alone the viewer looks through the hole.
    /// Slots the variant fills itself (another hairstyle, no hat) are left alone.
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
    /// Generic agents (the Definitive Series' <c>zombieAverage</c> family) mark a whole pool of
    /// alternates visible (five heads, four hairstyles, a dozen arms) and a script picks
    /// one of each when the agent spawns; worn together they overlap into a tangle. When
    /// some body slot has three or more parts on for the same side, keep the plainest part
    /// per slot and side (see <see cref="HeuristicDefaults"/> for "plainest"). Ordinary
    /// characters, whose split parts (left/right arms, body plus jacket) are fewer, are left
    /// as the game data says.
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

    /// <summary>"Right", "Left" or "" from a part name's words (armRightFull, legL, handLeftA).</summary>
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

    /// <summary>Orders "2" before "10" and "200" before "203"; nulls first.</summary>
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
