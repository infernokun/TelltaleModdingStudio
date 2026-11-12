namespace TelltaleModding.Core.Games;

/// <summary>The archive format a game uses, matching ttarchext's game table.</summary>
public enum ArchiveKind
{
    /// <summary>Older .ttarch archives.</summary>
    Ttarch,

    /// <summary>Newer .ttarch2 archives.</summary>
    Ttarch2,
}

/// <summary>One entry from ttarchext's game table.</summary>
/// <param name="GameNum">Index passed to ttarchext. It picks the Blowfish key.</param>
/// <param name="Name">Game or episode name.</param>
/// <param name="Kind">Archive format.</param>
/// <param name="OldMode">ttarchext's <c>old_mode</c> flag for this game, 0 for most.</param>
public sealed record GameEntry(int GameNum, string Name, ArchiveKind Kind, int OldMode);
