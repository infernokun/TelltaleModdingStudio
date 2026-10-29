using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.App.Services;

/// <summary>Chooses how to open each archive of an installed game.</summary>
public static class ArchiveOpener
{
    /// <summary>
    /// An archive opener for <paramref name="install"/>'s archives: the native reader
    /// for .ttarch2 (the game's root folder is searched for its Oodle DLL when needed),
    /// and the bundled ttarchext extractor for classic .ttarch archives.
    /// </summary>
    public static Func<string, IArchiveSource> For(GameInstall install)
    {
        var fallback = TtarchextArchiveSource.CreateOpener(install.Game.GameNum);
        return path => Ttarch2Archive.LooksLikeTtarch2(path)
            ? Ttarch2Archive.Open(path, install.Game.GameNum, install.RootFolder)
            : fallback(path);
    }
}
