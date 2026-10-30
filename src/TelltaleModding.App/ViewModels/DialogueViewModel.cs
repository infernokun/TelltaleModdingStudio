using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Dialogue;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// Every spoken line in the open game: browse by episode and speaker, search the
/// text, listen, and export the voice files with their transcripts.
/// </summary>
public sealed partial class DialogueViewModel : ViewModelBase
{
    private const int MaxResults = 3000;
    private const string AllEpisodes = "All episodes";
    private const string AllSpeakers = "All speakers";

    private GameCatalog? _catalog;
    private GameAudio? _audio;
    private DialogueIndex? _index;
    private CancellationTokenSource? _filterCts;
    private CancellationTokenSource? _debounceCts;
    private int _loadGeneration;
    private bool _suppressFilter;

    public DialogueViewModel() : this(null) { }

    public DialogueViewModel(Services.IAudioOutput? output)
    {
        Player = new AudioPlayerViewModel(output);
        Player.Ended += OnClipEnded;
        Music = new MusicViewModel(Player);
        Conversations.PlayLineRequested += entry => _ = PlayAsync(new DialogueLineViewModel(entry), autoPlay: true);
        Conversations.EpisodeFilterCleared += () =>
        {
            _suppressFilter = true;
            SelectedEpisode = AllEpisodes;
            _suppressFilter = false;
            Music.SetEpisode(null);
            QueueFilter();
        };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioPlayerViewModel.Clip))
            {
                ExportAudioCommand.NotifyCanExecuteChanged();
                ExportWavCommand.NotifyCanExecuteChanged();
            }
        };
        // The speaker's mouth follows the recording.
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioPlayerViewModel.Position) && IsSpeakerVisible)
                Speaker.Render(Player.Position);
        };
    }

    /// <summary>Each scene's conversations as trees, with a click-through mode.</summary>
    public ConversationViewModel Conversations { get; } = new();

    /// <summary>The soundtrack, played through the same player.</summary>
    public MusicViewModel Music { get; }

    /// <summary>Lines, conversations or music.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinesMode), nameof(IsConversationsMode), nameof(IsMusicMode), nameof(IsSpeakerVisible))]
    private DialogueMode _mode = DialogueMode.Lines;

    public bool IsLinesMode { get => Mode == DialogueMode.Lines; set { if (value) Mode = DialogueMode.Lines; } }
    public bool IsConversationsMode { get => Mode == DialogueMode.Conversations; set { if (value) Mode = DialogueMode.Conversations; } }
    public bool IsMusicMode { get => Mode == DialogueMode.Music; set { if (value) Mode = DialogueMode.Music; } }

    /// <summary>The speaker panel shows for spoken lines, not music.</summary>
    public bool IsSpeakerVisible => ShowSpeaker && !IsMusicMode;

    /// <summary>The speaking character, lip-synced to the line.</summary>
    public SpeakerViewModel Speaker { get; } = new();

    [ObservableProperty]
    private bool _showSpeaker = true;

    /// <summary>Plays each line's body language from the game's chore on the speaker.</summary>
    [ObservableProperty]
    private bool _playPerformance = true;

    partial void OnPlayPerformanceChanged(bool value)
    {
        Speaker.PlayPerformance = value;
        if (ShowSpeaker && NowPlaying is { } line)
            _ = Speaker.ShowAsync(line.Entry);
    }

    partial void OnShowSpeakerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSpeakerVisible));
        if (value && NowPlaying is { } line)
            _ = Speaker.ShowAsync(line.Entry);
    }

    /// <summary>Set by the shell so pickers can be shown.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>How the speaker's viewport shades (shared by the workspaces; set by the shell).</summary>
    public ViewportLookViewModel Look { get; set; } = new();

    /// <summary>Raised with a short status message for the status bar.</summary>
    public event Action<string>? StatusMessage;

    public AudioPlayerViewModel Player { get; }

    /// <summary>The open game's dialogue lines, once read (null while indexing).</summary>
    public DialogueIndex? Index => _index;

    [ObservableProperty]
    private bool _hasSource;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private string _indexStatus = "";

    /// <summary>The subtitle language to pick when a game has it (from Settings).</summary>
    public string PreferredLanguage { get; set; } = "english";

    /// <summary>
    /// Starts indexing <paramref name="catalog"/>'s dialogue in the background.
    /// <paramref name="models"/> builds the speakers (the game's shared model assembler).
    /// </summary>
    public void Load(GameCatalog catalog, ModelAssembler? models = null)
    {
        _catalog = catalog;
        _audio = new GameAudio(catalog);
        Speaker.Load(catalog, models);
        Music.Load(catalog, _audio);
        HasSource = true;
        // Clear the selection while the list is refilled so the picker shows the new one.
        _suppressFilter = true;
        Query = "";
        SelectedSpeaker = null;
        SelectedLanguage = "";
        Languages.Clear();
        foreach (var language in DialogueIndex.LanguagesOf(catalog.Entries))
            Languages.Add(language);
        SelectedLanguage = Languages.Contains(PreferredLanguage) ? PreferredLanguage
            : Languages.Contains("english") ? "english" : Languages.FirstOrDefault() ?? "english";
        _suppressFilter = false;
        _ = BuildIndexAsync();
    }

    public void Clear()
    {
        _loadGeneration++;
        _catalog = null;
        _audio = null;
        _index = null;
        HasSource = false;
        Player.Unload();
        Speaker.Clear();
        Music.Clear();
        Conversations.Clear();
        Lines.Clear();
        Speakers.Clear();
        Episodes.Clear();
        Languages.Clear();
        Summary = "";
    }

    /// <summary>Filters to one speaker's lines, matched loosely to a character's name.</summary>
    public void ShowLinesOf(string characterName)
    {
        var words = characterName.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var compact = string.Concat(words).Replace("'", "");
        var match = Speakers.FirstOrDefault(s => s.Name.Equals(compact, StringComparison.OrdinalIgnoreCase))
                    ?? Speakers.FirstOrDefault(s => words.Length > 0 && s.Name.Equals(words[0], StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            SelectedSpeaker = match;
        else
            Query = words.FirstOrDefault() ?? "";
    }

    private async Task BuildIndexAsync()
    {
        if (_catalog is null)
            return;
        int generation = ++_loadGeneration;
        var catalog = _catalog;
        var language = SelectedLanguage;
        IsIndexing = true;
        IndexStatus = "Reading dialogue…";
        Lines.Clear();
        var progress = new Progress<(int Done, int Total)>(p => IndexStatus = $"Reading dialogue… {p.Done:N0}/{p.Total:N0} files");
        try
        {
            var index = await Task.Run(() => DialogueIndex.Build(catalog, language, progress));
            if (generation != _loadGeneration)
                return;
            _index = index;
            if (_catalog is { } openCatalog)
                Conversations.Load(openCatalog, index);
            _suppressFilter = true;
            var previousSpeaker = SelectedSpeaker?.Name;
            // Episodes with lines, conversations or music, in name order.
            var previousEpisode = SelectedEpisode;
            Episodes.Clear();
            Episodes.Add(AllEpisodes);
            foreach (var episode in index.Lines.Select(l => l.Episode)
                         .Concat(Conversations.SceneEpisodes).Concat(Music.AlbumEpisodes)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
                Episodes.Add(episode);
            SelectedEpisode = Episodes.FirstOrDefault(e => e == previousEpisode) ?? AllEpisodes;
            Conversations.SetEpisode(EpisodeFilter);
            Music.SetEpisode(EpisodeFilter);
            Speakers.Clear();
            foreach (var (speaker, count) in index.Speakers)
                Speakers.Add(new SpeakerOption(speaker, count));
            SelectedSpeaker = Speakers.FirstOrDefault(s => s.Name == previousSpeaker);
            _suppressFilter = false;
            int voiced = index.Lines.Count(l => l.HasAudio);
            IndexStatus = $"{index.Lines.Count:N0} lines · {voiced:N0} voiced · {index.Speakers.Count:N0} speakers";
            await FilterAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            IndexStatus = $"Could not read dialogue: {ex.Message}";
        }
        finally
        {
            if (generation == _loadGeneration)
                IsIndexing = false;
        }
    }

    // ---- Filters ----------------------------------------------------------

    public ObservableCollection<string> Languages { get; } = new();
    public ObservableCollection<string> Episodes { get; } = new();
    public ObservableCollection<SpeakerOption> Speakers { get; } = new();
    public ObservableCollection<DialogueLineViewModel> Lines { get; } = new();

    [ObservableProperty]
    private string _selectedLanguage = "english";

    [ObservableProperty]
    private string? _selectedEpisode;

    [ObservableProperty]
    private SpeakerOption? _selectedSpeaker;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private bool _voicedOnly = true;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _autoPlayNext = true;

    public bool HasSpeakerFilter => SelectedSpeaker is not null;

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!_suppressFilter && HasSource && !string.IsNullOrEmpty(value))
            _ = BuildIndexAsync();
    }

    /// <summary>The picked episode, or null for all.</summary>
    private string? EpisodeFilter => SelectedEpisode is null or AllEpisodes ? null : SelectedEpisode;

    partial void OnSelectedEpisodeChanged(string? value)
    {
        if (_suppressFilter)
            return;
        Conversations.SetEpisode(EpisodeFilter);
        Music.SetEpisode(EpisodeFilter);
        QueueFilter();
    }

    partial void OnSelectedSpeakerChanged(SpeakerOption? value)
    {
        OnPropertyChanged(nameof(HasSpeakerFilter));
        QueueFilter();
    }

    partial void OnQueryChanged(string value) => QueueFilter(debounce: true);

    partial void OnVoicedOnlyChanged(bool value) => QueueFilter();

    [RelayCommand]
    private void ClearSpeaker() => SelectedSpeaker = null;

    private async void QueueFilter(bool debounce = false)
    {
        if (_suppressFilter || _index is null)
            return;
        _debounceCts?.Cancel();
        if (debounce)
        {
            var cts = _debounceCts = new CancellationTokenSource();
            try
            {
                await Task.Delay(200, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        await FilterAsync();
    }

    private async Task FilterAsync()
    {
        if (_index is null)
            return;
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();
        var index = _index;
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var episode = SelectedEpisode is null or AllEpisodes ? null : SelectedEpisode;
        var speaker = SelectedSpeaker?.Name;
        bool voicedOnly = VoicedOnly;

        List<DialogueEntry> hits;
        int total;
        try
        {
            (hits, total) = await Task.Run(() =>
            {
                var found = new List<DialogueEntry>();
                int count = 0;
                foreach (var line in index.Lines)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    if (voicedOnly && !line.HasAudio)
                        continue;
                    if (episode is not null && line.Episode != episode)
                        continue;
                    if (speaker is not null && !line.Line.Speaker.Equals(speaker, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (terms.Length > 0 && !terms.All(t => Matches(line, t)))
                        continue;
                    if (++count <= MaxResults)
                        found.Add(line);
                }
                return (found, count);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts != _filterCts)
            return;

        Lines.Clear();
        foreach (var hit in hits)
            Lines.Add(new DialogueLineViewModel(hit));
        Summary = total == 0
            ? "No lines match."
            : total > MaxResults
                ? $"Showing {MaxResults:N0} of {total:N0} lines. Narrow it down."
                : $"{total:N0} line{(total == 1 ? "" : "s")}";
        ExportVisibleCommand.NotifyCanExecuteChanged();
    }

    private static bool Matches(DialogueEntry line, string term) =>
        line.Line.RawText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        line.Line.Speaker.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        line.Scene.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        line.Line.Id.ToString() == term;

    // ---- Playback ---------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayLineCommand))]
    private DialogueLineViewModel? _selectedLine;

    [ObservableProperty]
    private DialogueLineViewModel? _nowPlaying;

    // Picking a line plays it, like a playlist.
    partial void OnSelectedLineChanged(DialogueLineViewModel? value)
    {
        if (value is not null && value != NowPlaying)
            _ = PlayAsync(value, autoPlay: true);
    }

    [RelayCommand(CanExecute = nameof(HasPlayableSelection))]
    private Task PlayLineAsync(DialogueLineViewModel? line)
    {
        line ??= SelectedLine;
        return line is null ? Task.CompletedTask : PlayAsync(line, autoPlay: true);
    }

    private bool HasPlayableSelection(DialogueLineViewModel? line) => (line ?? SelectedLine)?.HasAudio == true;

    private async Task PlayAsync(DialogueLineViewModel line, bool autoPlay)
    {
        if (_audio is null)
            return;
        if (NowPlaying is { } previous)
            previous.IsCurrent = false;
        NowPlaying = line;
        line.IsCurrent = true;
        if (ShowSpeaker)
            _ = Speaker.ShowAsync(line.Entry);
        if (line.Entry.Audio is not { } audio)
        {
            Player.Unload();
            Player.Error = "This line has no recording in the game.";
            return;
        }
        var source = _audio;
        await Player.LoadAsync(() => source.LoadClip(audio), autoPlay);
    }

    private void OnClipEnded()
    {
        if (IsMusicMode)
        {
            if (AutoPlayNext)
                Music.Step(+1);
            return;
        }
        if (IsConversationsMode)
        {
            Conversations.OnLineFinished();
            return;
        }
        if (!AutoPlayNext || NowPlaying is null)
            return;
        int i = Lines.IndexOf(NowPlaying);
        var next = Lines.Skip(i + 1).FirstOrDefault(l => l.HasAudio);
        if (next is not null)
            SelectedLine = next;
    }

    /// <summary>Opens the selected line in its scene's conversation tree.</summary>
    [RelayCommand]
    private async Task ShowInConversationAsync()
    {
        if (SelectedLine is not { } line)
            return;
        Mode = DialogueMode.Conversations;
        if (!await Conversations.ShowLineAsync(line.Entry))
            StatusMessage?.Invoke("This line isn't part of a conversation tree.");
    }

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Next() => Step(+1);

    private void Step(int direction)
    {
        if (IsMusicMode)
        {
            Music.Step(direction);
            return;
        }
        if (Lines.Count == 0)
            return;
        int i = NowPlaying is null ? -1 : Lines.IndexOf(NowPlaying);
        for (int j = i + direction; j >= 0 && j < Lines.Count; j += direction)
        {
            if (!Lines[j].HasAudio)
                continue;
            SelectedLine = Lines[j];
            return;
        }
    }

    // ---- Export -----------------------------------------------------------

    private bool CanExportSelected() => Player.Clip is not null;

    [RelayCommand(CanExecute = nameof(CanExportSelected))]
    private Task ExportAudioAsync() => ExportOneAsync(wav: false);

    [RelayCommand(CanExecute = nameof(CanExportSelected))]
    private Task ExportWavAsync() => ExportOneAsync(wav: true);

    private async Task ExportOneAsync(bool wav)
    {
        if (Dialogs is null || Player.Clip is not { } clip)
            return;
        var name = IsMusicMode ? Music.SuggestedFileName : NowPlaying?.SuggestedFileName ?? clip.Name;
        try
        {
            var ext = wav ? ".wav" : clip.NativeExtension;
            var path = await Dialogs.SaveFileAsync(IsMusicMode ? "Export track" : "Export voice line", name + ext,
                (ext.TrimStart('.').ToUpperInvariant() + " audio", new[] { "*" + ext }));
            if (path is null)
                return;
            await Task.Run(() => File.WriteAllBytes(path, wav ? clip.ToWav() : clip.NativeFile));
            StatusMessage?.Invoke($"Exported {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusMessage?.Invoke($"Could not export: {ex.Message}");
        }
    }

    private bool HasVisibleLines() => Lines.Any(l => l.HasAudio) && _audio is not null;

    /// <summary>Exports every voiced line in the list plus a transcript (<c>transcript.tsv</c>).</summary>
    [RelayCommand(CanExecute = nameof(HasVisibleLines))]
    private async Task ExportVisibleAsync()
    {
        if (Dialogs is null || _audio is null)
            return;
        var lines = Lines.Where(l => l.HasAudio).ToList();
        var folder = await Dialogs.OpenFolderAsync($"Export {lines.Count:N0} voice lines to…");
        if (folder is null)
            return;
        var source = _audio;
        var progress = new Progress<int>(n => StatusMessage?.Invoke($"Exporting voice lines… {n:N0}/{lines.Count:N0}"));
        int failed = await Task.Run(() =>
        {
            int done = 0, errors = 0;
            var transcript = new StringBuilder("file\tid\tspeaker\ttext\tepisode\tscene\n");
            foreach (var line in lines)
            {
                try
                {
                    var clip = source.LoadClip(line.Entry.Audio!);
                    var file = line.SuggestedFileName + clip.NativeExtension;
                    File.WriteAllBytes(Path.Combine(folder, file), clip.NativeFile);
                    transcript.Append(file).Append('\t').Append(line.Entry.Line.Id).Append('\t')
                        .Append(line.Speaker).Append('\t').Append(line.Text.Replace('\t', ' ').Replace('\n', ' ')).Append('\t')
                        .Append(line.Entry.Episode).Append('\t').Append(line.Entry.Scene).Append('\n');
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    errors++;
                }
                if (++done % 20 == 0)
                    ((IProgress<int>)progress).Report(done);
            }
            File.WriteAllText(Path.Combine(folder, "transcript.tsv"), transcript.ToString());
            return errors;
        });
        StatusMessage?.Invoke($"Exported {lines.Count - failed:N0} voice lines to {folder}" +
                              (failed > 0 ? $" ({failed} failed)" : ""));
    }
}

/// <summary>What the Dialogue workspace lists.</summary>
public enum DialogueMode { Lines, Conversations, Music }

/// <summary>A speaker in the filter list.</summary>
public sealed record SpeakerOption(string Name, int Lines)
{
    public string Label => $"{Display} · {Lines:N0}";

    /// <summary>The name in title case (<c>RHYS</c> → <c>Rhys</c>).</summary>
    public string Display => DialogueLineViewModel.TitleCase(Name);
}

/// <summary>One line in the dialogue list.</summary>
public sealed partial class DialogueLineViewModel : ViewModelBase
{
    public DialogueLineViewModel(DialogueEntry entry)
    {
        Entry = entry;
        Text = entry.Line.Text.Length > 0 ? entry.Line.Text : "(no text)";
        Directions = string.Join(" · ", entry.Line.Directions);
    }

    public DialogueEntry Entry { get; }
    public string Speaker => Entry.Line.Speaker.Length > 0 ? TitleCase(Entry.Line.Speaker) : "";
    public string Text { get; }
    public string Directions { get; }
    public bool HasDirections => Directions.Length > 0;
    public bool HasAudio => Entry.HasAudio;
    public string Where => $"{Entry.Episode} · {Entry.Scene}";

    /// <summary>Highlights the line being played.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>A file name like <c>Rhys - 362321480 - Vasquez screwed me out of</c>.</summary>
    public string SuggestedFileName
    {
        get
        {
            var words = string.Join(' ', Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(7));
            var name = $"{Speaker} - {Entry.Line.Id} - {words}";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.TrimEnd('.', ' ');
        }
    }

    internal static string TitleCase(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
}
