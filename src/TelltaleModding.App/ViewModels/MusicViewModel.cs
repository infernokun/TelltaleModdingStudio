using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Audio;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The game's soundtrack as albums: the loose music tracks of each episode
/// (<c>mus_*.ogg</c> / <c>mus_*.wav</c>) and each music bank (<c>101 Music.bank</c>).
/// Plays through the shared player of the Dialogue workspace.
/// </summary>
public sealed partial class MusicViewModel : ViewModelBase
{
    private readonly AudioPlayerViewModel _player;
    private GameAudio? _audio;
    private int _albumGeneration;
    private List<MusicAlbumViewModel> _allAlbums = new();
    private string? _episode;

    public MusicViewModel(AudioPlayerViewModel player) => _player = player;

    public ObservableCollection<MusicAlbumViewModel> Albums { get; } = new();
    public ObservableCollection<MusicTrackViewModel> Tracks { get; } = new();

    [ObservableProperty]
    private MusicAlbumViewModel? _selectedAlbum;

    [ObservableProperty]
    private MusicTrackViewModel? _selectedTrack;

    [ObservableProperty]
    private MusicTrackViewModel? _nowPlaying;

    [ObservableProperty]
    private bool _isLoadingAlbum;

    [ObservableProperty]
    private string _summary = "";

    public void Load(GameCatalog catalog, GameAudio audio)
    {
        _audio = audio;
        Albums.Clear();
        Tracks.Clear();
        NowPlaying = null;
        _allAlbums = new List<MusicAlbumViewModel>();

        var loose = catalog.Entries
            .Where(e => IsMusicTrack(e.Name))
            .GroupBy(e => DialogueIndex.EpisodeOf(e.ArchivePath))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in loose)
            _allAlbums.Add(new MusicAlbumViewModel(group.Key, group.Key,
                group.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(), null));
        foreach (var bank in catalog.Entries.Where(e => IsMusicBank(e.Name)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            _allAlbums.Add(new MusicAlbumViewModel(Path.GetFileNameWithoutExtension(bank.Name),
                DialogueIndex.EpisodeOf(bank.ArchivePath), Array.Empty<ArchiveEntryRef>(), bank));
        FillAlbums();
    }

    /// <summary>The episodes that have music.</summary>
    public IEnumerable<string> AlbumEpisodes => _allAlbums.Select(a => a.Episode);

    /// <summary>Lists only the albums of <paramref name="episode"/> (all when null).</summary>
    public void SetEpisode(string? episode)
    {
        if (string.Equals(_episode, episode, StringComparison.OrdinalIgnoreCase))
            return;
        _episode = episode;
        FillAlbums();
    }

    private void FillAlbums()
    {
        var keep = SelectedAlbum;
        Albums.Clear();
        foreach (var album in _allAlbums)
            if (_episode is null || album.Episode.Equals(_episode, StringComparison.OrdinalIgnoreCase))
                Albums.Add(album);
        Summary = Albums.Count > 0 ? $"{Albums.Count} albums"
            : _episode is null ? "No music found in this game." : $"No music in {_episode}.";
        if (keep is not null && Albums.Contains(keep))
            SelectedAlbum = keep;
        else
        {
            SelectedAlbum = Albums.FirstOrDefault();
            if (SelectedAlbum is null)
                Tracks.Clear();
        }
    }

    public void Clear()
    {
        _audio = null;
        _allAlbums.Clear();
        Albums.Clear();
        Tracks.Clear();
        NowPlaying = null;
        Summary = "";
    }

    private static bool IsMusicTrack(string name) =>
        (name.StartsWith("mus_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("music_", StringComparison.OrdinalIgnoreCase)) &&
        (name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));

    private static bool IsMusicBank(string name) =>
        name.EndsWith(".bank", StringComparison.OrdinalIgnoreCase) && name.Contains("music", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedAlbumChanged(MusicAlbumViewModel? value)
    {
        if (value is not null)
            _ = ShowAlbumAsync(value);
    }

    private async Task ShowAlbumAsync(MusicAlbumViewModel album)
    {
        int generation = ++_albumGeneration;
        Tracks.Clear();
        if (album.Bank is null)
        {
            foreach (var entry in album.Entries)
                Tracks.Add(new MusicTrackViewModel(Title(entry.Name), entry, null));
            Summary = $"{Tracks.Count} tracks";
            return;
        }

        // Banks hold many sounds; list them once (the bank is read in full).
        if (album.Clips is null && _audio is { } audio)
        {
            IsLoadingAlbum = true;
            Summary = $"Reading {album.Name} ({AssetsViewModel.FormatSize(album.Bank.Size)})…";
            try
            {
                album.Clips = await Task.Run(() => audio.LoadClips(album.Bank));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                Summary = $"Could not read {album.Name}: {ex.Message}";
                return;
            }
            finally
            {
                IsLoadingAlbum = false;
            }
        }
        if (generation != _albumGeneration || album.Clips is null)
            return;
        foreach (var clip in album.Clips)
            Tracks.Add(new MusicTrackViewModel(Title(clip.Name), null, clip));
        Summary = $"{Tracks.Count} tracks";
    }

    partial void OnSelectedTrackChanged(MusicTrackViewModel? value)
    {
        if (value is not null && value != NowPlaying)
            _ = PlayAsync(value);
    }

    private async Task PlayAsync(MusicTrackViewModel track)
    {
        if (_audio is not { } audio)
            return;
        if (NowPlaying is { } previous)
            previous.IsCurrent = false;
        NowPlaying = track;
        track.IsCurrent = true;
        await _player.LoadAsync(() => track.Clip ?? audio.LoadClip(track.Entry!), autoPlay: true);
    }

    /// <summary>Moves to the previous/next track and plays it.</summary>
    public void Step(int direction)
    {
        if (Tracks.Count == 0)
            return;
        int i = NowPlaying is null ? -1 : Tracks.IndexOf(NowPlaying);
        int j = i + direction;
        if (j >= 0 && j < Tracks.Count)
            SelectedTrack = Tracks[j];
    }

    /// <summary>A file name for the playing track.</summary>
    public string SuggestedFileName => NowPlaying is null ? "track" : SafeName(NowPlaying.Title);

    // "mus_101m_chesapeake.ogg" → "101m chesapeake"; bank names lose their GUIDs.
    private static string Title(string name)
    {
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(name), @"\s*\{[0-9a-fA-F-]+\}", "");
        stem = Regex.Replace(stem, "^(mus|music)_", "", RegexOptions.IgnoreCase);
        return stem.Replace('_', ' ').Trim();
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "track" : name;
    }
}

/// <summary>An episode's loose music or one music bank.</summary>
public sealed class MusicAlbumViewModel
{
    public MusicAlbumViewModel(string name, string episode, IReadOnlyList<ArchiveEntryRef> entries, ArchiveEntryRef? bank)
    {
        Name = name;
        Episode = episode;
        Entries = entries;
        Bank = bank;
    }

    public string Name { get; }

    /// <summary>The episode label of the archive the music ships in.</summary>
    public string Episode { get; }

    public IReadOnlyList<ArchiveEntryRef> Entries { get; }
    public ArchiveEntryRef? Bank { get; }
    public IReadOnlyList<AudioClip>? Clips { get; set; }
    public string Detail => Bank is null ? $"{Entries.Count} tracks" : $"Sound bank · {AssetsViewModel.FormatSize(Bank.Size)}";
}

/// <summary>One track: a music file, or a sound in a bank.</summary>
public sealed partial class MusicTrackViewModel : ViewModelBase
{
    public MusicTrackViewModel(string title, ArchiveEntryRef? entry, AudioClip? clip)
    {
        Title = title;
        Entry = entry;
        Clip = clip;
    }

    public string Title { get; }
    public ArchiveEntryRef? Entry { get; }
    public AudioClip? Clip { get; }
    public string Detail => Clip is { } c ? $"{c.Duration:m\\:ss} · {c.Codec}" : AssetsViewModel.FormatSize(Entry?.Size ?? 0);

    [ObservableProperty]
    private bool _isCurrent;
}
