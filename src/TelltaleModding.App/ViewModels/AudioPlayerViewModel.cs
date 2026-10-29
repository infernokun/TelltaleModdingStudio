using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Audio;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// Transport for one audio clip: play/pause, stop and a seekable timeline. Players
/// share the app's output, so starting one stops whichever was playing.
/// </summary>
public sealed partial class AudioPlayerViewModel : ViewModelBase
{
    private static AudioPlayerViewModel? _active;

    private readonly IAudioOutput _output;
    private readonly DispatcherTimer _timer;
    private short[]? _pcm;
    private bool _updatingPosition;
    private int _loadGeneration;

    public AudioPlayerViewModel() : this(null) { }

    public AudioPlayerViewModel(IAudioOutput? output)
    {
        _output = output ?? AudioOutputs.Shared;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Tick());
    }

    /// <summary>Raised when the clip plays through to its end.</summary>
    public event Action? Ended;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StopCommand))]
    private AudioClip? _clip;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    /// <summary>Clip length in seconds.</summary>
    [ObservableProperty]
    private double _duration;

    /// <summary>Playback position in seconds; setting it seeks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _position;

    public string TimeText => $"{Format(Position)} / {Format(Duration)}";

    public string FormatText => Clip is null ? "" :
        $"{Clip.Codec} · {Clip.SampleRate / 1000.0:0.#} kHz · {(Clip.Channels == 1 ? "mono" : Clip.Channels == 2 ? "stereo" : $"{Clip.Channels} ch")}";

    partial void OnClipChanged(AudioClip? value)
    {
        _pcm = null;
        Duration = value?.Duration.TotalSeconds ?? 0;
        SetPosition(0);
        OnPropertyChanged(nameof(FormatText));
        OnPropertyChanged(nameof(TimeText));
    }

    partial void OnPositionChanged(double value)
    {
        if (!_updatingPosition && _active == this && _pcm is not null)
            _output.Seek(TimeSpan.FromSeconds(value));
    }

    /// <summary>
    /// Loads a clip (decoding off the UI thread) and optionally starts it. A later call
    /// supersedes an earlier one still decoding.
    /// </summary>
    public async Task LoadAsync(Func<AudioClip> load, bool autoPlay)
    {
        int generation = ++_loadGeneration;
        StopPlayback();
        Error = null;
        IsLoading = true;
        try
        {
            var (clip, pcm) = await Task.Run(() =>
            {
                var c = load();
                return (c, autoPlay ? c.DecodePcm16() : null);
            });
            if (generation != _loadGeneration)
                return;
            Clip = clip;
            _pcm = pcm;
            if (autoPlay)
                Start();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            if (generation != _loadGeneration)
                return;
            Clip = null;
            Error = ex.Message;
        }
        finally
        {
            if (generation == _loadGeneration)
                IsLoading = false;
        }
    }

    /// <summary>Forgets the clip and stops playback.</summary>
    public void Unload()
    {
        _loadGeneration++;
        StopPlayback();
        Clip = null;
        Error = null;
        IsLoading = false;
    }

    private bool HasClip() => Clip is not null;

    [RelayCommand(CanExecute = nameof(HasClip))]
    private async Task PlayPauseAsync()
    {
        if (Clip is null)
            return;
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (_active == this && _pcm is not null && Position < Duration - 0.05)
        {
            _output.Resume();
            IsPlaying = true;
            _timer.Start();
            return;
        }
        if (_pcm is null)
        {
            var clip = Clip;
            IsLoading = true;
            try
            {
                _pcm = await Task.Run(clip.DecodePcm16);
            }
            catch (InvalidDataException ex)
            {
                Error = ex.Message;
                return;
            }
            finally
            {
                IsLoading = false;
            }
            if (clip != Clip)
                return;
        }
        Start(Position < Duration - 0.05 ? Position : 0);
    }

    /// <summary>Pauses playback if it is running; Play resumes from the same point.</summary>
    public void Pause()
    {
        if (!IsPlaying)
            return;
        _output.Pause();
        IsPlaying = false;
        _timer.Stop();
    }

    [RelayCommand(CanExecute = nameof(HasClip))]
    private void Stop()
    {
        StopPlayback();
        SetPosition(0);
    }

    private void Start(double from = 0)
    {
        if (Clip is null || _pcm is null)
            return;
        if (_active is { } other && other != this)
            other.StopPlayback();
        _active = this;
        try
        {
            _output.Play(_pcm, Clip.Channels, Clip.SampleRate, TimeSpan.FromSeconds(from));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            Error = $"Could not play: {ex.Message}";
            return;
        }
        SetPosition(from);
        IsPlaying = true;
        _timer.Start();
    }

    private void StopPlayback()
    {
        _timer.Stop();
        if (_active == this)
        {
            _output.Stop();
            _active = null;
        }
        IsPlaying = false;
    }

    private void Tick()
    {
        if (_active != this)
        {
            _timer.Stop();
            IsPlaying = false;
            return;
        }
        SetPosition(Math.Min(_output.Position.TotalSeconds, Duration));
        if (!_output.IsPlaying && IsPlaying)
        {
            _timer.Stop();
            IsPlaying = false;
            SetPosition(Duration);
            Ended?.Invoke();
        }
    }

    private void SetPosition(double seconds)
    {
        _updatingPosition = true;
        Position = seconds;
        _updatingPosition = false;
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.TotalMinutes >= 1 ? t.ToString(@"m\:ss") : t.ToString(@"s\.f") + "s";
    }
}
