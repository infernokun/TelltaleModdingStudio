using TelltaleModding.Core.Games;

namespace TelltaleModding.Core.Tests;

public class GameDatabaseTests
{
    [Fact]
    public void LoadsAllSixtyEightGames()
    {
        Assert.Equal(68, GameDatabase.Games.Length);
    }

    [Fact]
    public void GameNumbersAreContiguousFromZero()
    {
        for (int i = 0; i < GameDatabase.Games.Length; i++)
            Assert.Equal(i, GameDatabase.Games[i].GameNum);
    }

    [Theory]
    [InlineData(0, "Wallace & Gromit: Episode 1: Fright of the Bumblebees", ArchiveKind.Ttarch)]
    [InlineData(24, "Tales of Monkey Island 101: Launch of the Screaming Narwhal", ArchiveKind.Ttarch)]
    [InlineData(54, "The Wolf Among Us", ArchiveKind.Ttarch2)]
    [InlineData(57, "Game of Thrones (all episodes)", ArchiveKind.Ttarch2)]
    [InlineData(67, "The Walking Dead: The Telltale Definitive Series", ArchiveKind.Ttarch2)]
    public void KnownIndicesMapToExpectedGames(int num, string name, ArchiveKind kind)
    {
        var game = GameDatabase.ByNumber(num);
        Assert.NotNull(game);
        Assert.Equal(name, game!.Name);
        Assert.Equal(kind, game.Kind);
    }

    [Fact]
    public void ByNumberReturnsNullOutOfRange()
    {
        Assert.Null(GameDatabase.ByNumber(-1));
        Assert.Null(GameDatabase.ByNumber(68));
    }

    [Fact]
    public void SearchIsCaseInsensitiveSubstring()
    {
        var results = GameDatabase.Search("wolf").ToList();
        Assert.Single(results);
        Assert.Equal(54, results[0].GameNum);
    }

    [Fact]
    public void Ttarch2GamesAreTheModernTitles()
    {
        // Everything from The Wolf Among Us (54) onward is ttarch2 except the two
        // Bone 2.0 re-releases (65, 66), which are classic .ttarch.
        var ttarch2 = GameDatabase.Games.Where(g => g.Kind == ArchiveKind.Ttarch2).Select(g => g.GameNum).ToHashSet();
        Assert.Contains(54, ttarch2);
        Assert.DoesNotContain(65, ttarch2);
        Assert.DoesNotContain(66, ttarch2);
    }
}
