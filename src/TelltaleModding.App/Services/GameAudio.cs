using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Audio;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.App.Services;

/// <summary>Reads and decodes the open game's sound files.</summary>
public sealed class GameAudio
{
    private readonly GameCatalog _catalog;
    private readonly Lazy<VorbisSetupLibrary> _setups;

    public GameAudio(GameCatalog catalog)
    {
        _catalog = catalog;
        // Vorbis setup headers come from the FMOD library the game ships.
        _setups = new Lazy<VorbisSetupLibrary>(() => VorbisSetupLibrary.FromFolder(catalog.Install.RootFolder));
    }

    /// <summary>The Vorbis setup headers from the game's FMOD library (read on first use).</summary>
    public VorbisSetupLibrary Setups => _setups.Value;

    /// <summary>Every clip in <paramref name="entry"/> (one for voice files, many for banks).</summary>
    public IReadOnlyList<AudioClip> LoadClips(ArchiveEntryRef entry)
    {
        var data = _catalog.Read(entry);
        if (!TelltaleAudio.TryLoad(data, entry.Name, _setups.Value, out var clips, out var error) || clips.Count == 0)
            throw new InvalidDataException(error ?? "No sounds in this file.");
        return clips;
    }

    /// <summary>The first clip in <paramref name="entry"/>.</summary>
    public AudioClip LoadClip(ArchiveEntryRef entry) => LoadClips(entry)[0];
}
