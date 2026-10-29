namespace TelltaleModding.Core.Games;

/// <summary>
/// The on-disk archive family a game uses. Determined by Telltale's engine era
/// and mirrors the <c>EXT_TTARCH</c> / <c>EXT_TTARCH2</c> distinction inside
/// ttarchext's <c>gamekeys[]</c> table.
/// </summary>
public enum ArchiveKind
{
    /// <summary>Classic single-number-versioned archives (.ttarch), versions 1-9.</summary>
    Ttarch,

    /// <summary>Newer container-format archives (.ttarch2): ZCTT / NCTT / 3ATT and friends.</summary>
    Ttarch2,
}

/// <summary>
/// One entry from ttarchext's game table. The <see cref="GameNum"/> is the exact
/// index passed to ttarchext on the command line; it selects the Blowfish key.
/// </summary>
/// <param name="GameNum">Index passed to ttarchext (the <c>gamenum</c> argument).</param>
/// <param name="Name">Human-readable game/episode name.</param>
/// <param name="Kind">Archive family (.ttarch vs .ttarch2).</param>
/// <param name="OldMode">
/// ttarchext's <c>old_mode</c> flag for this game (0 = normal). A handful of very
/// old titles need the legacy code path; this is carried through for completeness.
/// </param>
public sealed record GameEntry(int GameNum, string Name, ArchiveKind Kind, int OldMode);
