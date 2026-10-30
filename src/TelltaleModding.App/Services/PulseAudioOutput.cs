using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TelltaleModding.App.Services;

/// <summary>
/// Speaker output on Linux through PulseAudio's simple API (<c>libpulse-simple</c>), which
/// PipeWire also provides. A thread per clip writes the samples in short chunks, so pause,
/// seek and volume take effect within a chunk or two.
/// </summary>
public sealed class PulseAudioOutput : IAudioOutput
{
    private const string Library = "libpulse-simple.so.0";
    private const int SampleS16Le = 3;
    private const int StreamPlayback = 1;
    private const int ChunkMs = 20;
    private const int BufferMs = 120;

    private Thread? _thread;
    private short[] _pcm = Array.Empty<short>();
    private int _channels = 1;
    private int _rate = 1;
    private volatile bool _stop;
    private volatile bool _paused;
    private volatile bool _finished = true;
    private long _seekFrame = -1; // set by Seek, taken by the writer thread
    private long _heardFrame;     // the frame playing now, kept by the writer thread
    private float _volume = 1f;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsPlaying => _thread is not null && !_paused && !_finished;

    public TimeSpan Position => TimeSpan.FromSeconds(Interlocked.Read(ref _heardFrame) / (double)_rate);

    private long Frames => _pcm.Length / _channels;

    /// <summary>True when a PulseAudio or PipeWire server accepts a playback stream.</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            var spec = new SampleSpec { Format = SampleS16Le, Rate = 44100, Channels = 2 };
            var attr = BufferAttr.ServerDefaults;
            var s = pa_simple_new(null, "Telltale Modding Studio", StreamPlayback, null, "Probe", ref spec, IntPtr.Zero, ref attr, out _);
            if (s == IntPtr.Zero)
                return false;
            pa_simple_free(s);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Play(short[] pcm, int channels, int sampleRate, TimeSpan startAt)
    {
        Stop();
        _pcm = pcm;
        _channels = Math.Max(1, channels);
        _rate = Math.Max(1, sampleRate);
        Start(ClampFrame(startAt));
    }

    public void Pause() => _paused = true;

    public void Resume()
    {
        if (_pcm.Length == 0)
            return;
        _paused = false;
        // A clip that played to its end starts again from wherever it was sought to.
        if (_finished)
            Start(Interlocked.Read(ref _heardFrame));
    }

    public void Seek(TimeSpan position)
    {
        if (_pcm.Length == 0)
            return;
        long frame = ClampFrame(position);
        Interlocked.Exchange(ref _heardFrame, frame);
        Interlocked.Exchange(ref _seekFrame, frame);
    }

    public void Stop()
    {
        EndThread();
        _pcm = Array.Empty<short>();
        _paused = false;
        Interlocked.Exchange(ref _heardFrame, 0);
        Interlocked.Exchange(ref _seekFrame, -1);
    }

    public void Dispose() => Stop();

    private long ClampFrame(TimeSpan at) => Math.Clamp((long)(at.TotalSeconds * _rate), 0, Frames);

    private void Start(long frame)
    {
        EndThread();
        Interlocked.Exchange(ref _heardFrame, frame);
        Interlocked.Exchange(ref _seekFrame, -1);
        _stop = false;
        _finished = false;
        var thread = new Thread(() => Run(frame)) { IsBackground = true, Name = "PulseAudio output" };
        _thread = thread;
        thread.Start();
    }

    private void EndThread()
    {
        var thread = _thread;
        if (thread is null)
            return;
        _stop = true;
        thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _finished = true;
    }

    private void Run(long frame)
    {
        int channels = _channels, rate = _rate;
        int frameBytes = 2 * channels;
        var spec = new SampleSpec { Format = SampleS16Le, Rate = (uint)rate, Channels = (byte)channels };
        var attr = BufferAttr.ServerDefaults;
        attr.TLength = (uint)(rate * BufferMs / 1000 * frameBytes);
        var s = IntPtr.Zero;
        try
        {
            s = pa_simple_new(null, "Telltale Modding Studio", StreamPlayback, null, "Preview", ref spec, IntPtr.Zero, ref attr, out _);
            if (s == IntPtr.Zero)
                return;

            int chunkFrames = Math.Max(1, rate * ChunkMs / 1000);
            var bytes = new byte[chunkFrames * frameBytes];
            long frames = Frames;
            while (!_stop)
            {
                long seek = Interlocked.Exchange(ref _seekFrame, -1);
                if (seek >= 0)
                {
                    pa_simple_flush(s, out _);
                    frame = seek;
                }
                if (_paused)
                {
                    // Drop what the server still holds, and carry on from what was heard.
                    pa_simple_flush(s, out _);
                    frame = Interlocked.Read(ref _heardFrame);
                    while (_paused && !_stop && Interlocked.Read(ref _seekFrame) < 0)
                        Thread.Sleep(10);
                    continue;
                }
                if (frame >= frames)
                {
                    pa_simple_drain(s, out _);
                    if (_stop || _paused || Interlocked.Read(ref _seekFrame) >= 0)
                        continue;
                    Interlocked.Exchange(ref _heardFrame, frames);
                    return;
                }

                int n = (int)Math.Min(chunkFrames, frames - frame);
                Fill(bytes, frame, n, channels);
                if (pa_simple_write(s, bytes, (nuint)(n * frameBytes), out _) < 0)
                    return;
                frame += n;
                ulong latencyUs = pa_simple_get_latency(s, out _);
                long heard = frame - (long)(latencyUs * (ulong)rate / 1_000_000);
                if (Interlocked.Read(ref _seekFrame) < 0)
                    Interlocked.Exchange(ref _heardFrame, Math.Clamp(heard, 0, frames));
            }
            pa_simple_flush(s, out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No PulseAudio library after all: the clip just doesn't sound.
        }
        finally
        {
            if (s != IntPtr.Zero)
                pa_simple_free(s);
            _finished = true;
        }
    }

    private void Fill(byte[] bytes, long frame, int frames, int channels)
    {
        float volume = _volume;
        int start = (int)(frame * channels), count = frames * channels;
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(_pcm[start + i] * volume));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint Prebuf;
        public uint MinReq;
        public uint FragSize;

        // (uint)-1 asks the server for its default in each field.
        public static BufferAttr ServerDefaults => new()
        {
            MaxLength = uint.MaxValue, TLength = uint.MaxValue, Prebuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = uint.MaxValue,
        };
    }

    [DllImport(Library)]
    private static extern IntPtr pa_simple_new(string? server, string name, int dir, string? dev, string streamName,
        ref SampleSpec spec, IntPtr channelMap, ref BufferAttr attr, out int error);

    [DllImport(Library)]
    private static extern int pa_simple_write(IntPtr s, byte[] data, nuint bytes, out int error);

    [DllImport(Library)]
    private static extern int pa_simple_drain(IntPtr s, out int error);

    [DllImport(Library)]
    private static extern int pa_simple_flush(IntPtr s, out int error);

    [DllImport(Library)]
    private static extern ulong pa_simple_get_latency(IntPtr s, out int error);

    [DllImport(Library)]
    private static extern void pa_simple_free(IntPtr s);
}
