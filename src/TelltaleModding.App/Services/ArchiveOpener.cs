using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.App.Services;

/// <summary>Chooses how to open each archive of an installed game.</summary>
public static class ArchiveOpener
{
    /// <summary>
    /// Opens .ttarch2 archives with the native reader and older .ttarch archives with ttarchext.
    /// </summary>
    public static Func<string, IArchiveSource> For(GameInstall install)
    {
        var fallback = TtarchextArchiveSource.CreateOpener(install.Game.GameNum);
        return path => Ttarch2Archive.LooksLikeTtarch2(path)
            ? Ttarch2Archive.Open(path, install.Game.GameNum, install.RootFolder)
            : fallback(path);
    }
}
