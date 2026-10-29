using System.Buffers.Binary;

namespace TelltaleModding.Core.Audio;

/// <summary>
/// Entry point for game audio: recognizes the containers Telltale games ship sounds in
/// (FSB5 and FSB4 sound banks named .wav, FMOD Studio .bank files, plain Ogg Vorbis,
/// and meta-wrapped .aud streams) and turns them into playable <see cref="AudioClip"/>s.
/// </summary>
public static class TelltaleAudio
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".ogg", ".aud", ".bank", ".fsb",
    };

    /// <summary>True for entry names whose extension may hold audio this reader understands.</summary>
    public static bool IsAudioFile(string name) => Extensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// Parses an audio entry. <paramref name="setups"/> supplies Vorbis setup headers for
    /// FSB5 Vorbis banks and may be <see cref="VorbisSetupLibrary.Empty"/> otherwise.
    /// Returns false with a readable <paramref name="error"/> when nothing could be loaded.
    /// </summary>
    public static bool TryLoad(byte[] data, string name, VorbisSetupLibrary setups,
        out IReadOnlyList<AudioClip> clips, out string? error)
    {
        clips = Array.Empty<AudioClip>();
        error = null;
        setups ??= VorbisSetupLibrary.Empty;
        string stem = Path.GetFileNameWithoutExtension(name);
        if (data is null || data.Length < 16)
        {
            error = "The file is too small to contain audio.";
            return false;
        }

        try
        {
            var span = data.AsSpan();
            if (FsbReader.IsFsb5(span))
                return Finish(FsbReader.TryReadFsb5(data, 0, stem, setups, out var list, out error), list, out clips);
            if (FsbReader.IsFsb4(span))
                return Finish(FsbReader.TryReadFsb4(data, stem, out var list, out error), list, out clips);
            if (span[..4].SequenceEqual("OggS"u8))
                return TryLoadOgg(data, stem, out clips, out error);
            if (span[..4].SequenceEqual("RIFF"u8) && span.Length >= 12)
            {
                if (span[8..12].SequenceEqual("WAVE"u8))
                    return TryLoadRiffWave(data, stem, out clips, out error);
                int fsb = FsbReader.FindEmbeddedFsb5(data);
                if (fsb >= 0)
                    return Finish(FsbReader.TryReadFsb5(data, fsb, stem, setups, out var list, out error), list, out clips);
                error = "This .bank file has no embedded sounds (it holds only event metadata).";
                return false;
            }

            // Meta-wrapped streams: look for a known container a little way in.
            int window = Math.Min(data.Length, 8192);
            int ogg = span[..window].IndexOf("OggS"u8);
            if (ogg > 0)
                return TryLoadOgg(data.AsSpan(ogg).ToArray(), stem, out clips, out error);
            int fsb5 = span[..window].IndexOf("FSB5"u8);
            if (fsb5 > 0)
                return Finish(FsbReader.TryReadFsb5(data, fsb5, stem, setups, out var list, out error), list, out clips);

            error = "Unrecognized audio format.";
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or EndOfStreamException)
        {
            error = "The audio file is damaged or truncated: " + ex.Message;
            clips = Array.Empty<AudioClip>();
            return false;
        }
    }

    private static bool Finish(bool ok, List<AudioClip> list, out IReadOnlyList<AudioClip> clips)
    {
        clips = ok ? list : Array.Empty<AudioClip>();
        return ok;
    }

    private static bool TryLoadOgg(byte[] ogg, string stem, out IReadOnlyList<AudioClip> clips, out string? error)
    {
        clips = Array.Empty<AudioClip>();
        if (!OggVorbis.TryReadInfo(ogg, out int channels, out int rate, out long samples))
        {
            error = "The Ogg stream is not Vorbis audio.";
            return false;
        }
        error = null;
        clips = new[]
        {
            new AudioClip(stem, AudioCodec.Vorbis, channels, rate, samples, ".ogg", () => ogg, OggVorbis.Decode),
        };
        return true;
    }

    private static bool TryLoadRiffWave(byte[] data, string stem, out IReadOnlyList<AudioClip> clips, out string? error)
    {
        clips = Array.Empty<AudioClip>();
        error = null;
        int pos = 12, format = 0, channels = 0, rate = 0, bits = 0, dataOff = -1, dataLen = 0;
        while (pos + 8 <= data.Length)
        {
            var id = data.AsSpan(pos, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4));
            int body = pos + 8;
            if (len < 0) break;
            if (id.SequenceEqual("fmt "u8) && body + 16 <= data.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataOff = body;
                dataLen = Math.Min(len, data.Length - body);
                break;
            }
            pos = body + len + (len & 1);
        }
        if (dataOff < 0 || channels <= 0 || rate <= 0 || format != 1 || bits is not (8 or 16))
        {
            error = "Only 8/16-bit PCM RIFF WAVE files are supported.";
            return false;
        }
        int bytesPer = bits / 8;
        long frames = dataLen / (bytesPer * channels);
        var raw = new ReadOnlyMemory<byte>(data, dataOff, dataLen);
        clips = new[]
        {
            new AudioClip(stem, AudioCodec.Pcm, channels, rate, frames, ".wav", () => data,
                _ => PcmDecoders.DecodeLinear(raw.Span, bytesPer, channels, frames)),
        };
        return true;
    }
}
