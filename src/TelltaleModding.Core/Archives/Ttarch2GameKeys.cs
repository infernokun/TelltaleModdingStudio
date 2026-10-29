namespace TelltaleModding.Core.Archives;

/// <summary>
/// Blowfish keys for the .ttarch2 games, keyed by ttarchext game index (the same
/// numbering as <c>Data/games.json</c>, which was generated from ttarchext's
/// <c>gamekeys[]</c> table). Only the .ttarch2-era entries are ported; classic
/// .ttarch games are not read in-process.
/// </summary>
internal static class Ttarch2GameKeys
{
    // Hex of the raw key bytes, exactly as they appear in ttarchext.c.
    private static readonly Dictionary<int, string> HexKeys = new()
    {
        [54] = "85CA8FA089DF6473A2B2D6C69ECAC688995A73D89FE19BE296519F9B9ACECD99D276627FA7C49BD8DAE7A99C6778B3D1B1C89964A0A285", // The Wolf Among Us
        [55] = "96CA999F8DDA9A87D7CDD9966295AAB8D59596E5A4B99BD0C9539F8590CDCD9FC8B39993C6C49D9EA5A4CFCDA39DBBDDACA78B94D4A46F", // The Walking Dead: Season 2
        [56] = "81D89F9889DE9FA4E0D0E895629595C6DB8E92E9A9D6A4D3D8529F857BDBD398C4B79EB0CFC7AC9DA5A4BADBA996B7E1B1C49497E3A36F", // Tales from the Borderlands
        [57] = "86CA9A9973D287ABE4DBE3C9A5968387B08B9ADC8CDB8AD7D790DDBAAC9D9164A6A69FB4B0C98DD4E7E3E6D1AA63829F8CC49398BFD893", // Game of Thrones
        [58] = "8CD29B9987DE94A9E69DA5947FCEC1BCCC9C8EDDB1A6669FB28ADDBA9CDEC29AD376627FAECCA7D1D8E6D9D2AB63829F92CC9498D3E4A0", // Minecraft: Story Mode
        [59] = "96CA999F8DDA9A87D7CDD9B1639583AECA9698E0ABDC7AD4C685BC86699CB895CBB09BBDC8A79ECDD9C1A99C6789B3DBB0CC949AB4D7A0", // The Walking Dead: Michonne
        [60] = "81CAA1A185DA6473A2AED6D89FC6C188995A6FD8B1E297DD96519F979AE0CE95CD76627FA3C4ADD9D6E2A99C6774B3E3B2C49464A0A281", // Batman: The Telltale Series
        [61] = "96CA999F8DDA9A87D7CDD9976295AAB8D59596E5A4B99BD0C9549F8590CDCD9FC8B39993C6C49D9FA5A4CFCDA39DBBDDACA78B94D4A56F", // The Walking Dead: A New Frontier
        [62] = "86DE8EA688D594B1E59DA59479DAB4C9CD938EE5B0A6669FAC96D0C79DD5C2A2D276627FA8D89ADED9DDD9DAAA63829F8CD887A5D4DBA0", // Marvel's Guardians of the Galaxy
        [63] = "8CD29B9987DE94A9E69EA5947FCEC1BCCC9C8EDDB1A7669FB28ADDBA9CDEC29AD377627FAECCA7D1D8E6D9D2AB64829F92CC9498D3E4A0", // Minecraft: Story Mode - Season Two
        [64] = "81CAA1A185DA6573A2AED6D89FC6C189995A6FD8B1E297DD97519F979AE0CE95CD77627FA3C4ADD9D6E2AA9C6774B3E3B2C49465A0A281", // Batman: The Enemy Within
        [67] = "96CA999F8DDA9A87D7CDD9BB93D1BEC0D79171DC9ED98DD0D18CD8C3A0B0C695C39C93BBCCCCA7D3B9D9D9D08E93BEDAAED18D77D5D3A3", // The Walking Dead: The Telltale Definitive Series
    };

    // Keyed ciphers are built once per game and shared: decryption never mutates them.
    private static readonly Dictionary<int, TtarchBlowfish> Ciphers = new();

    /// <summary>Returns the keyed cipher for <paramref name="gameNum"/>, or null when it has no .ttarch2 key.</summary>
    public static TtarchBlowfish? TryGetCipher(int gameNum)
    {
        if (!HexKeys.TryGetValue(gameNum, out var hex))
            return null;
        lock (Ciphers)
        {
            if (!Ciphers.TryGetValue(gameNum, out var cipher))
                Ciphers[gameNum] = cipher = new TtarchBlowfish(Convert.FromHexString(hex));
            return cipher;
        }
    }
}
