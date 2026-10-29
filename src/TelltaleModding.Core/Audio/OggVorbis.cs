using System.Buffers.Binary;

namespace TelltaleModding.Core.Audio;

/// <summary>Builds, inspects and decodes Ogg Vorbis streams.</summary>
internal static class OggVorbis
{
    /// <summary>The block sizes FMOD always encodes with (256 / 2048 samples).</summary>
    private const int ShortBlockExp = 8, LongBlockExp = 11;

    /// <summary>
    /// Wraps bare Vorbis audio packets into a complete Ogg Vorbis file, synthesizing the
    /// identification and comment headers and computing page granule positions.
    /// <paramref name="totalSamples"/> (if positive) trims the final page to the true length.
    /// </summary>
    public static byte[] Build(int channels, int sampleRate, byte[] setup, IReadOnlyList<ReadOnlyMemory<byte>> packets,
        long totalSamples, uint serial)
    {
        if (!VorbisSetupParser.TryParse(setup, 0, setup.Length, channels, out _, out var blockFlags))
            throw new InvalidDataException("The Vorbis setup header could not be parsed for this channel count.");

        var ident = new byte[30];
        ident[0] = 1;
        "vorbis"u8.CopyTo(ident.AsSpan(1));
        ident[11] = (byte)channels;
        BinaryPrimitives.WriteInt32LittleEndian(ident.AsSpan(12), sampleRate);
        ident[28] = (byte)(ShortBlockExp | (LongBlockExp << 4));
        ident[29] = 1;

        var comment = new byte[16];
        comment[0] = 3;
        "vorbis"u8.CopyTo(comment.AsSpan(1));
        // vendor length 0, comment count 0
        comment[15] = 1;

        var w = new OggPageWriter(serial);
        w.AddPacket(ident, 0);
        w.FlushPage(bos: true);
        w.AddPacket(comment, 0);
        w.AddPacket(setup, 0);
        w.FlushPage();

        int modeBits = VorbisSetupParser.ILog((uint)(blockFlags.Length - 1));
        int prevBlock = 0;
        long granule = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            var p = packets[i].Span;
            int block = 1 << ShortBlockExp;
            if (p.Length > 0)
            {
                int mode = (p[0] >> 1) & ((1 << modeBits) - 1);
                if (mode < blockFlags.Length && blockFlags[mode]) block = 1 << LongBlockExp;
            }
            if (prevBlock != 0) granule += prevBlock / 4 + block / 4;
            prevBlock = block;

            bool last = i == packets.Count - 1;
            long g = last && totalSamples > 0 && totalSamples < granule ? totalSamples : granule;
            w.AddPacket(packets[i], g);
            if (last) w.FlushPage(eos: true);
            else if (w.PendingBytes >= 4096) w.FlushPage();
        }
        if (packets.Count == 0) w.FlushPage(eos: true);
        return w.ToArray();
    }

    /// <summary>Reads channels, rate and length (last granule) from an Ogg Vorbis file without decoding it.</summary>
    public static bool TryReadInfo(byte[] ogg, out int channels, out int sampleRate, out long samples)
    {
        channels = 0; sampleRate = 0; samples = 0;
        if (ogg.Length < 58 || !ogg.AsSpan(0, 4).SequenceEqual("OggS"u8)) return false;
        int segs = ogg[26];
        int body = 27 + segs;
        if (body + 16 > ogg.Length || ogg[body] != 1 || !ogg.AsSpan(body + 1, 6).SequenceEqual("vorbis"u8)) return false;
        channels = ogg[body + 11];
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(ogg.AsSpan(body + 12));
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(14));

        // Last page of this stream carries the total length.
        for (int pos = ogg.Length - 27; pos >= 0; pos--)
        {
            if (ogg[pos] == 'O' && ogg.AsSpan(pos, 4).SequenceEqual("OggS"u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(pos + 14)) == serial)
            {
                long g = BinaryPrimitives.ReadInt64LittleEndian(ogg.AsSpan(pos + 6));
                if (g > 0) { samples = g; break; }
            }
        }
        return channels > 0 && sampleRate > 0;
    }

    /// <summary>Decodes an Ogg Vorbis file to interleaved 16-bit samples.</summary>
    public static short[] Decode(byte[] ogg)
    {
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg, writable: false), closeOnDispose: true);
        int channels = reader.Channels;
        long expected = reader.TotalSamples;
        var output = new List<short>(expected > 0 && expected < int.MaxValue / 4 ? (int)expected * channels : 1 << 16);
        var buf = new float[4096 * channels];
        int read;
        while ((read = reader.ReadSamples(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                output.Add(WavWriter.ToPcm16(buf[i]));
        }
        if (output.Count == 0)
            throw new InvalidDataException("The Vorbis stream contained no decodable audio.");
        return output.ToArray();
    }

    private sealed class OggPageWriter
    {
        private readonly MemoryStream _out = new();
        private readonly uint _serial;
        private readonly List<byte> _segments = new();
        private readonly MemoryStream _body = new();
        private uint _sequence;
        private long _granule = -1;
        private bool _continued;

        public OggPageWriter(uint serial) => _serial = serial;

        public int PendingBytes => (int)_body.Length;

        public void AddPacket(ReadOnlyMemory<byte> packet, long granuleAfter)
        {
            var span = packet.Span;
            int offset = 0;
            while (true)
            {
                int remaining = span.Length - offset;
                // Lace the packet; a page holds at most 255 segments.
                while (remaining >= 255 && _segments.Count < 255)
                {
                    _segments.Add(255);
                    _body.Write(span.Slice(offset, 255));
                    offset += 255;
                    remaining -= 255;
                }
                if (_segments.Count == 255)
                {
                    // Page is full in the middle of this packet.
                    WritePage(bos: false, eos: false, granule: -1, endsMidPacket: true);
                    continue;
                }
                _segments.Add((byte)remaining);
                _body.Write(span.Slice(offset, remaining));
                _granule = granuleAfter;
                break;
            }
        }

        public void FlushPage(bool bos = false, bool eos = false)
        {
            if (_segments.Count == 0 && !eos) return;
            WritePage(bos, eos, _granule, endsMidPacket: false);
        }

        private void WritePage(bool bos, bool eos, long granule, bool endsMidPacket)
        {
            var header = new byte[27 + _segments.Count];
            "OggS"u8.CopyTo(header);
            header[4] = 0;
            header[5] = (byte)((_continued ? 1 : 0) | (bos ? 2 : 0) | (eos ? 4 : 0));
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(6), granule);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), _serial);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), _sequence++);
            header[26] = (byte)_segments.Count;
            for (int i = 0; i < _segments.Count; i++) header[27 + i] = _segments[i];

            var body = _body.ToArray();
            uint crc = AudioCrc.OggUpdate(AudioCrc.Ogg(header), body);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), crc);
            _out.Write(header);
            _out.Write(body);

            _segments.Clear();
            _body.SetLength(0);
            _continued = endsMidPacket;
            _granule = -1;
        }

        public byte[] ToArray() => _out.ToArray();
    }
}
