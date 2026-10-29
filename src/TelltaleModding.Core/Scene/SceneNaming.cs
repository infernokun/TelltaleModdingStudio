using System.Text;

namespace TelltaleModding.Core.Scene;

/// <summary>Readable names for scene files and the episode archives they come from.</summary>
public static class SceneNaming
{
    /// <summary>True for location scenes (<c>adv_*.scene</c>); menus and interface scenes are not.</summary>
    public static bool IsLocation(string fileName) =>
        Path.GetFileName(fileName).StartsWith("adv_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A scene's display name: <c>adv_boardingSchoolExteriorDusk.scene</c> → "Boarding School
    /// Exterior Dusk", <c>adv_arenaGortysCoreSilo102.scene</c> → "Arena Gortys Core Silo 102".
    /// </summary>
    public static string DisplayName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith("adv_", StringComparison.OrdinalIgnoreCase))
            stem = stem[4..];
        return SplitWords(stem);
    }

    /// <summary>
    /// The episode an archive holds, from its name: <c>WDC_pc_WalkingDead401_data.ttarch2</c> →
    /// "Walking Dead 401", <c>Borderlands_pc_Borderlands101_data.ttarch2</c> → "Borderlands 101".
    /// </summary>
    public static string Episode(string archivePath)
    {
        var stem = Path.GetFileNameWithoutExtension(archivePath);
        int pc = stem.IndexOf("_pc_", StringComparison.OrdinalIgnoreCase);
        if (pc >= 0)
            stem = stem[(pc + 4)..];
        foreach (var suffix in new[] { "_data", "_txmesh", "_anichore", "_all", "_ms", "_uncompressed" })
        {
            int at = stem.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
                stem = stem[..at];
        }
        return SplitWords(stem);
    }

    /// <summary>Splits camel case, underscores and digit runs into title-cased words.</summary>
    public static string SplitWords(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        char previous = '\0';
        foreach (char c in text)
        {
            if (c is '_' or '-' or ' ')
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                previous = ' ';
                continue;
            }
            bool boundary = sb.Length > 0 && sb[^1] != ' ' &&
                            ((char.IsUpper(c) && char.IsLower(previous)) ||
                             (char.IsDigit(c) && char.IsLetter(previous)) ||
                             (char.IsLetter(c) && char.IsDigit(previous)));
            if (boundary)
                sb.Append(' ');
            sb.Append(sb.Length == 0 || sb[^1] == ' ' ? char.ToUpperInvariant(c) : c);
            previous = c;
        }
        return sb.ToString().Trim();
    }
}
