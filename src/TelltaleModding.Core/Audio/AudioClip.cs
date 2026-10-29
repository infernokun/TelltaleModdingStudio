namespace TelltaleModding.Core.Audio;

/// <summary>How a clip's samples are stored in the game file.</summary>
public enum AudioCodec { Pcm, Mp3, Vorbis, Adpcm, Other }

/// <summary>
/// One playable sound: a voice line, a sound effect, or one subsound of a bank.
/// <see cref="NativeFile"/> is a standalone file in the clip's own format (built
/// without decoding); <see cref="DecodePcm16"/> does the actual decode.
/// </summary>
public sealed class AudioClip
{
    private readonly Lazy<byte[]> _native;
    private readonly Func<byte[], short[]> _decode;

    /// <summary>Subsound name, or the file stem when the container has none.</summary>
    public string Name { get; }
    public AudioCodec Codec { get; }
    public int Channels { get; }
    public int SampleRate { get; }

    /// <summary>Length in sample frames (per channel) as recorded by the container; 0 when unknown.</summary>
    public long SampleCount { get; }

    public TimeSpan Duration =>
        SampleRate > 0 ? TimeSpan.FromSeconds((double)SampleCount / SampleRate) : TimeSpan.Zero;

    /// <summary>Extension of <see cref="NativeFile"/>: ".mp3", ".ogg" or ".wav".</summary>
    public string NativeExtension { get; }

    /// <summary>A standalone file in the native format, playable by ordinary players.</summary>
    public byte[] NativeFile => _native.Value;

    internal AudioClip(string name, AudioCodec codec, int channels, int sampleRate, long sampleCount,
        string nativeExtension, Func<byte[]> buildNative, Func<byte[], short[]> decode)
    {
        Name = name;
        Codec = codec;
        Channels = channels;
        SampleRate = sampleRate;
        SampleCount = sampleCount;
        NativeExtension = nativeExtension;
        _native = new Lazy<byte[]>(buildNative, LazyThreadSafetyMode.ExecutionAndPublication);
        _decode = decode;
    }

    /// <summary>
    /// Decodes the whole clip to interleaved 16-bit samples (<see cref="Channels"/> per frame).
    /// Throws <see cref="InvalidDataException"/> when the data cannot be decoded.
    /// </summary>
    public short[] DecodePcm16()
    {
        try
        {
            return _decode(NativeFile);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not decode {Codec} audio '{Name}': {ex.Message}", ex);
        }
    }

    /// <summary>Decodes the clip and wraps it in a 16-bit PCM RIFF/WAVE file.</summary>
    public byte[] ToWav() => WavWriter.Write(DecodePcm16(), Channels, SampleRate);

    public override string ToString() =>
        $"{Name} ({Codec}, {Channels} ch, {SampleRate} Hz, {Duration.TotalSeconds:0.00} s)";
}
