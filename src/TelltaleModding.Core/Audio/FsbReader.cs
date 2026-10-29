using System.Buffers.Binary;
using System.Text;

namespace TelltaleModding.Core.Audio;

/// <summary>
/// Reads FMOD sound banks: FSB5 (used by later games, alone or inside .bank files)
/// and FSB4 (used by earlier games). Each subsound becomes an <see cref="AudioClip"/>.
/// </summary>
internal static class FsbReader
{
    private const int Fsb5Pcm8 = 1, Fsb5Pcm16 = 2, Fsb5PcmFloat = 5, Fsb5ImaAdpcm = 7,
        Fsb5Mpeg = 11, Fsb5Vorbis = 15, Fsb5Fadpcm = 16;

    private static readonly string[] Fsb5CodecNames =
    {
        "none", "PCM8", "PCM16", "PCM24", "PCM32", "PCM float", "GameCube ADPCM", "IMA ADPCM", "VAG",
        "HEVAG", "XMA", "MPEG", "CELT", "ATRAC9", "XWMA", "Vorbis", "FMOD ADPCM", "Opus",
    };

    private static readonly int[] Fsb5Rates = { 4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000 };

    public static bool IsFsb5(ReadOnlySpan<byte> d) => d.Length >= 0x3C && d[..4].SequenceEqual("FSB5"u8);
    public static bool IsFsb4(ReadOnlySpan<byte> d) => d.Length >= 0x30 && d[..4].SequenceEqual("FSB4"u8);

    /// <summary>Finds an FSB5 image embedded in an FMOD Studio .bank (RIFF) file. Returns -1 if none.</summary>
    public static int FindEmbeddedFsb5(byte[] data)
    {
        var span = data.AsSpan();
        int pos = 0;
        while (true)
        {
            int i = span[pos..].IndexOf("FSB5"u8);
            if (i < 0) return -1;
            int at = pos + i;
            if (TryReadFsb5Header(span[at..], out _)) return at;
            pos = at + 1;
        }
    }

    private readonly record struct Fsb5Header(int Version, int Count, int SampleHeaderSize, int NameTableSize, int DataSize, int Codec, int HeaderSize)
    {
        public int Total => HeaderSize + SampleHeaderSize + NameTableSize + DataSize;
    }

    private static bool TryReadFsb5Header(ReadOnlySpan<byte> d, out Fsb5Header h)
    {
        h = default;
        if (!IsFsb5(d)) return false;
        int version = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
        int shSize = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        int ntSize = BinaryPrimitives.ReadInt32LittleEndian(d[16..]);
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(d[20..]);
        int codec = BinaryPrimitives.ReadInt32LittleEndian(d[24..]);
        int headerSize = version == 0 ? 0x40 : 0x3C;
        if (version is < 0 or > 1 || count <= 0 || shSize < count * 8 || ntSize < 0 || dataSize < 0) return false;
        h = new Fsb5Header(version, count, shSize, ntSize, dataSize, codec, headerSize);
        // Allow a truncated data section (some banks pad), but the tables must fit.
        return (long)headerSize + shSize + ntSize <= d.Length;
    }

    private sealed record Fsb5Sample(int Index, int Channels, int Rate, long Samples, int Offset, uint VorbisCrc);

    public static bool TryReadFsb5(byte[] file, int start, string fallbackName, VorbisSetupLibrary setups,
        out List<AudioClip> clips, out string? error)
    {
        clips = new List<AudioClip>();
        error = null;
        var d = file.AsSpan(start);
        if (!TryReadFsb5Header(d, out var h))
        {
            error = "Not a valid FSB5 sound bank.";
            return false;
        }

        // Sample headers.
        var samples = new List<Fsb5Sample>(h.Count);
        int p = h.HeaderSize;
        int shEnd = h.HeaderSize + h.SampleHeaderSize;
        for (int i = 0; i < h.Count; i++)
        {
            if (p + 8 > shEnd) { error = "FSB5 sample headers are truncated."; return false; }
            ulong mode = BinaryPrimitives.ReadUInt64LittleEndian(d[p..]);
            p += 8;
            bool hasExtra = (mode & 1) != 0;
            int rateIdx = (int)((mode >> 1) & 0xF);
            int channels = ((mode >> 5) & 3) switch { 0 => 1, 1 => 2, 2 => 6, _ => 8 };
            int offset = (int)(((mode >> 7) & 0x7FFFFFF) << 5);
            long count = (long)((mode >> 34) & 0x3FFFFFFF);
            int rate = rateIdx < Fsb5Rates.Length ? Fsb5Rates[rateIdx] : 44100;
            uint crc = 0;
            while (hasExtra)
            {
                if (p + 4 > shEnd) { error = "FSB5 sample headers are truncated."; return false; }
                uint chunk = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
                hasExtra = (chunk & 1) != 0;
                int size = (int)((chunk >> 1) & 0xFFFFFF);
                int type = (int)(chunk >> 25);
                int body = p + 4;
                if (body + size > shEnd) { error = "FSB5 sample headers are truncated."; return false; }
                switch (type)
                {
                    case 1 when size >= 1: channels = d[body]; break;
                    case 2 when size >= 4: rate = BinaryPrimitives.ReadInt32LittleEndian(d[body..]); break;
                    case 11 when size >= 4: crc = BinaryPrimitives.ReadUInt32LittleEndian(d[body..]); break;
                }
                p = body + size;
            }
            samples.Add(new Fsb5Sample(i, channels, rate, count, offset, crc));
        }

        // Names.
        var names = new string[h.Count];
        int nt = shEnd;
        if (h.NameTableSize >= 4 * h.Count)
        {
            for (int i = 0; i < h.Count; i++)
            {
                int rel = BinaryPrimitives.ReadInt32LittleEndian(d[(nt + 4 * i)..]);
                int at = nt + rel;
                if (rel < 0 || at >= nt + h.NameTableSize) continue;
                int len = d[at..(nt + h.NameTableSize)].IndexOf((byte)0);
                if (len < 0) len = nt + h.NameTableSize - at;
                names[i] = Encoding.UTF8.GetString(d.Slice(at, len));
            }
        }

        int dataStart = start + shEnd + h.NameTableSize;
        int dataEnd = (int)Math.Min(file.Length, (long)dataStart + h.DataSize);
        var failures = new List<string>();
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            int from = dataStart + s.Offset;
            int to = i + 1 < samples.Count ? dataStart + samples[i + 1].Offset : dataEnd;
            to = Math.Min(to, dataEnd);
            string name = !string.IsNullOrEmpty(names[i]) ? names[i]
                : h.Count == 1 ? fallbackName : $"{fallbackName}_{i:D3}";
            if (from < dataStart || from > to)
            {
                failures.Add($"{name}: sample data lies outside the bank");
                continue;
            }
            var stream = new ReadOnlyMemory<byte>(file, from, to - from);
            if (TryMakeFsb5Clip(h.Codec, s, name, stream, setups, out var clip, out var why))
                clips.Add(clip!);
            else
                failures.Add(why!);
        }

        if (clips.Count == 0)
        {
            error = failures.Count > 0 ? failures[0] : "The sound bank contains no sounds.";
            if (failures.Count > 1) error += $" (and {failures.Count - 1} more)";
            return false;
        }
        return true;
    }

    private static bool TryMakeFsb5Clip(int codec, Fsb5Sample s, string name, ReadOnlyMemory<byte> stream,
        VorbisSetupLibrary setups, out AudioClip? clip, out string? error)
    {
        clip = null;
        error = null;
        switch (codec)
        {
            case Fsb5Mpeg:
                return TryMakeMpegClip(name, s.Channels, s.Rate, s.Samples, stream, out clip, out error);

            case Fsb5Vorbis:
            {
                if (!setups.TryGet(s.VorbisCrc, out var setup))
                {
                    error = setups.Count == 0
                        ? $"{name}: Vorbis audio needs the game's FMOD DLL (fmod*.dll in the game folder) to rebuild its setup header, and none was found."
                        : $"{name}: Vorbis setup header 0x{s.VorbisCrc:X8} was not found in the game's FMOD DLLs.";
                    return false;
                }
                if (!VorbisSetupParser.TryParse(setup, 0, setup.Length, s.Channels, out _, out _))
                {
                    error = $"{name}: the Vorbis setup header does not match a {s.Channels}-channel stream.";
                    return false;
                }
                var packets = SplitFsbVorbisPackets(stream);
                uint serial = (uint)name.GetHashCode() | 1;
                clip = new AudioClip(name, AudioCodec.Vorbis, s.Channels, s.Rate, s.Samples, ".ogg",
                    () => OggVorbis.Build(s.Channels, s.Rate, setup, packets, s.Samples, serial),
                    OggVorbis.Decode);
                return true;
            }

            case Fsb5Pcm16:
            case Fsb5Pcm8:
            case Fsb5PcmFloat:
            {
                int bytesPer = codec == Fsb5Pcm16 ? 2 : codec == Fsb5Pcm8 ? 1 : 4;
                long frames = s.Samples > 0 ? Math.Min(s.Samples, stream.Length / (bytesPer * s.Channels)) : stream.Length / (bytesPer * s.Channels);
                clip = PcmClip(name, AudioCodec.Pcm, s.Channels, s.Rate, frames,
                    () => PcmDecoders.DecodeLinear(stream.Span, bytesPer, s.Channels, frames));
                return true;
            }

            case Fsb5Fadpcm:
            {
                long frames = s.Samples;
                clip = PcmClip(name, AudioCodec.Adpcm, s.Channels, s.Rate, frames,
                    () => PcmDecoders.DecodeFadpcm(stream.Span, s.Channels, frames));
                return true;
            }

            case Fsb5ImaAdpcm:
            {
                long frames = s.Samples;
                clip = PcmClip(name, AudioCodec.Adpcm, s.Channels, s.Rate, frames,
                    () => PcmDecoders.DecodeXboxIma(stream.Span, s.Channels, frames));
                return true;
            }

            default:
                string codecName = codec >= 0 && codec < Fsb5CodecNames.Length ? Fsb5CodecNames[codec] : $"codec {codec}";
                error = $"{name}: {codecName} audio is not supported.";
                return false;
        }
    }

    /// <summary>A clip whose native form is a decoded WAV (decoded once, on first use).</summary>
    private static AudioClip PcmClip(string name, AudioCodec codec, int channels, int rate, long frames, Func<short[]> decode)
    {
        var pcm = new Lazy<short[]>(decode);
        return new AudioClip(name, codec, channels, rate, frames, ".wav",
            () => WavWriter.Write(pcm.Value, channels, rate),
            _ => pcm.Value);
    }

    private static bool TryMakeMpegClip(string name, int channels, int rate, long samples, ReadOnlyMemory<byte> stream,
        out AudioClip? clip, out string? error)
    {
        clip = null;
        error = null;
        if (channels > 2)
        {
            error = $"{name}: multichannel ({channels}) MPEG audio is not supported.";
            return false;
        }
        var mp3 = MpegFrames.Extract(stream.Span, out var first, out int frames);
        if (frames == 0)
        {
            error = $"{name}: no MPEG frames were found in the sample data.";
            return false;
        }
        if (rate <= 0) rate = first.SampleRate;
        long count = samples > 0 ? samples : (long)frames * first.SamplesPerFrame;
        int maxFrames = (int)Math.Min(int.MaxValue / 4, (long)frames * first.SamplesPerFrame);
        clip = new AudioClip(name, AudioCodec.Mp3, first.Channels, first.SampleRate, count, ".mp3",
            () => mp3, data => MpegFrames.Decode(data, maxFrames));
        return true;
    }

    /// <summary>FSB5 Vorbis streams store each packet as a u16 length followed by the packet bytes.</summary>
    internal static List<ReadOnlyMemory<byte>> SplitFsbVorbisPackets(ReadOnlyMemory<byte> stream)
    {
        var list = new List<ReadOnlyMemory<byte>>();
        var s = stream.Span;
        int pos = 0;
        while (pos + 2 <= s.Length)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(s[pos..]);
            if (size == 0 || pos + 2 + size > s.Length) break;
            list.Add(stream.Slice(pos + 2, size));
            pos += 2 + size;
        }
        return list;
    }

    // ---- FSB4 ----

    private const uint Fsb4Mode8Bits = 0x08, Fsb4ModeUnsigned = 0x80, Fsb4ModeMpeg = 0x200,
        Fsb4ModeImaAdpcm = 0x400000, Fsb4ModeVag = 0x800000, Fsb4ModeXma = 0x1000000,
        Fsb4ModeGcAdpcm = 0x2000000, Fsb4ModeOgg = 0x8000000, Fsb4ModeMpegLayer3 = 0x10000000,
        Fsb4ModeMpegLayer2 = 0x40000;

    private const uint Fsb4HeaderBasicHeaders = 0x02;

    public static bool TryReadFsb4(byte[] file, string fallbackName, out List<AudioClip> clips, out string? error)
    {
        clips = new List<AudioClip>();
        error = null;
        var d = file.AsSpan();
        if (!IsFsb4(d)) { error = "Not a valid FSB4 sound bank."; return false; }
        int count = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        int shSize = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(d[12..]);
        uint headerMode = BinaryPrimitives.ReadUInt32LittleEndian(d[20..]);
        const int headerSize = 0x30;
        if (count <= 0 || shSize <= 0 || headerSize + (long)shSize > d.Length)
        {
            error = "The FSB4 header is invalid.";
            return false;
        }

        int p = headerSize;
        int dataPos = headerSize + shSize;
        int dataEnd = (int)Math.Min(d.Length, (long)dataPos + dataSize);
        bool basic = (headerMode & Fsb4HeaderBasicHeaders) != 0;
        var failures = new List<string>();
        // Basic headers reuse the first full header's format and only store lengths.
        uint mode = 0; int rate = 0, channels = 0;
        for (int i = 0; i < count; i++)
        {
            long samples;
            int compressed;
            string name;
            if (i == 0 || !basic)
            {
                if (p + 0x40 > headerSize + shSize) { error = "FSB4 sample headers are truncated."; return false; }
                int size = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]);
                var rawName = d.Slice(p + 2, 30);
                int nul = rawName.IndexOf((byte)0);
                name = Encoding.ASCII.GetString(nul < 0 ? rawName : rawName[..nul]);
                samples = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 32)..]);
                compressed = BinaryPrimitives.ReadInt32LittleEndian(d[(p + 36)..]);
                mode = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 48)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(d[(p + 52)..]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(d[(p + 62)..]);
                p += size > 0 ? size : 0x50;
            }
            else
            {
                if (p + 8 > headerSize + shSize) { error = "FSB4 sample headers are truncated."; return false; }
                samples = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
                compressed = BinaryPrimitives.ReadInt32LittleEndian(d[(p + 4)..]);
                name = "";
                p += 8;
            }
            if (string.IsNullOrEmpty(name)) name = count == 1 ? fallbackName : $"{fallbackName}_{i:D3}";
            if (channels <= 0) channels = 1;

            int from = dataPos, to = (int)Math.Min(dataEnd, (long)dataPos + compressed);
            // Sample data is aligned to 32 bytes between entries.
            dataPos = (int)Math.Min(int.MaxValue, ((long)dataPos + compressed + 31) & ~31L);
            if (to <= from) { failures.Add($"{name}: sample data lies outside the bank"); continue; }
            var stream = new ReadOnlyMemory<byte>(file, from, to - from);

            if ((mode & (Fsb4ModeMpeg | Fsb4ModeMpegLayer3 | Fsb4ModeMpegLayer2)) != 0)
            {
                if (TryMakeMpegClip(name, channels, rate, samples, stream, out var clip, out var why)) clips.Add(clip!);
                else failures.Add(why!);
            }
            else if ((mode & Fsb4ModeImaAdpcm) != 0)
            {
                int ch = channels;
                long frames = samples;
                clips.Add(PcmClip(name, AudioCodec.Adpcm, ch, rate, frames,
                    () => PcmDecoders.DecodeXboxIma(stream.Span, ch, frames)));
            }
            else if ((mode & (Fsb4ModeVag | Fsb4ModeXma | Fsb4ModeGcAdpcm | Fsb4ModeOgg)) != 0)
            {
                failures.Add($"{name}: this FSB4 codec (mode 0x{mode:X8}) is not supported.");
            }
            else
            {
                int bytesPer = (mode & Fsb4Mode8Bits) != 0 ? 1 : 2;
                int ch = channels;
                bool unsigned8 = (mode & Fsb4ModeUnsigned) != 0;
                long frames = Math.Min(samples, stream.Length / (bytesPer * ch));
                clips.Add(PcmClip(name, AudioCodec.Pcm, ch, rate, frames,
                    () => PcmDecoders.DecodeLinear(stream.Span, bytesPer, ch, frames, unsigned8)));
            }
        }

        if (clips.Count == 0)
        {
            error = failures.Count > 0 ? failures[0] : "The sound bank contains no sounds.";
            return false;
        }
        return true;
    }
}
