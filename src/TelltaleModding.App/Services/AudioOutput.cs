using NAudio.Wave;

namespace TelltaleModding.App.Services;

/// <summary>Plays decoded 16-bit PCM, one clip at a time; callers poll <see cref="Position"/> and <see cref="IsPlaying"/>.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Starts <paramref name="pcm"/> (interleaved) from <paramref name="startAt"/>, replacing any clip playing.</summary>
    void Play(short[] pcm, int channels, int sampleRate, TimeSpan startAt);

    void Pause();
    void Resume();
    void Stop();
    void Seek(TimeSpan position);

    TimeSpan Position { get; }
    bool IsPlaying { get; }

    /// <summary>Output volume, 0-1.</summary>
    float Volume { get; set; }
}

/// <summary>Speaker output through the Windows wave-out API.</summary>
public sealed class WaveOutAudioOutput : IAudioOutput
{
    private WaveOutEvent? _device;
    private RawSourceWaveStream? _stream;

    public bool IsPlaying => _device?.PlaybackState == PlaybackState.Playing;

    private float _volume = 1f;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_device is not null)
                _device.Volume = _volume;
        }
    }

    public TimeSpan Position => _stream?.CurrentTime ?? TimeSpan.Zero;

    public void Play(short[] pcm, int channels, int sampleRate, TimeSpan startAt)
    {
        Stop();
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _stream = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(sampleRate, 16, channels));
        Seek(startAt);
        _device = new WaveOutEvent { DesiredLatency = 120, Volume = _volume };
        _device.Init(_stream);
        _device.Play();
    }

    public void Pause() => _device?.Pause();

    public void Resume() => _device?.Play();

    public void Seek(TimeSpan position)
    {
        if (_stream is null)
            return;
        long bytes = (long)(position.TotalSeconds * _stream.WaveFormat.AverageBytesPerSecond);
        bytes -= bytes % _stream.WaveFormat.BlockAlign;
        _stream.Position = Math.Clamp(bytes, 0, _stream.Length);
    }

    public void Stop()
    {
        _device?.Stop();
        _device?.Dispose();
        _device = null;
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose() => Stop();
}

/// <summary>Output that plays nothing (no sound device, or tests); time still advances.</summary>
public sealed class SilentAudioOutput : IAudioOutput
{
    private DateTime _startedAt;
    private TimeSpan _offset;
    private TimeSpan _length;
    private bool _playing;

    public bool IsPlaying => _playing && Position < _length;

    public float Volume { get; set; } = 1f;

    public TimeSpan Position => _playing ? Min(_offset + (DateTime.UtcNow - _startedAt), _length) : _offset;

    public void Play(short[] pcm, int channels, int sampleRate, TimeSpan startAt)
    {
        _length = TimeSpan.FromSeconds(pcm.Length / (double)Math.Max(1, channels * sampleRate));
        _offset = startAt;
        Resume();
    }

    public void Pause()
    {
        _offset = Position;
        _playing = false;
    }

    public void Resume()
    {
        _startedAt = DateTime.UtcNow;
        _playing = true;
    }

    public void Seek(TimeSpan position)
    {
        _offset = position;
        _startedAt = DateTime.UtcNow;
    }

    public void Stop()
    {
        _playing = false;
        _offset = TimeSpan.Zero;
    }

    public void Dispose() => Stop();

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>The app's one audio output, shared so starting a clip anywhere stops the last one.</summary>
public static class AudioOutputs
{
    private static IAudioOutput? _shared;

    /// <summary>The shared output: the sound card, or a silent stand-in when none is available.</summary>
    public static IAudioOutput Shared
    {
        get => _shared ??= OperatingSystem.IsWindows() && waveOutGetNumDevs() > 0 ? new WaveOutAudioOutput() : new SilentAudioOutput();
        set => _shared = value;
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern int waveOutGetNumDevs();
}
