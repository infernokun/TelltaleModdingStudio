namespace TelltaleModding.Core.Audio;

/// <summary>
/// Finds MPEG audio frames in a sound bank's sample data and joins them into a
/// plain .mp3 stream, dropping the alignment padding the banks put between frames.
/// </summary>
internal static class MpegFrames
{
    private static readonly int[,] BitratesV1 =
    {
        { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 }, // layer I
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },    // layer II
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },     // layer III
    };

    private static readonly int[,] BitratesV2 =
    {
        { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },
    };

    private static readonly int[] RatesV1 = { 44100, 48000, 32000 };

    internal readonly record struct FrameHeader(int Version, int Layer, int SampleRate, int Channels, int Length, int SamplesPerFrame)
    {
        /// <summary>Bits that must stay constant across a stream: version, layer, rate.</summary>
        public int Signature => Version * 1000000 + Layer * 100000 + SampleRate;
    }

    /// <summary>Parses the 4-byte frame header at <paramref name="p"/>; Version is 1, 2 or 25 (for 2.5).</summary>
    internal static bool TryParseHeader(ReadOnlySpan<byte> p, out FrameHeader header)
    {
        header = default;
        if (p.Length < 4 || p[0] != 0xFF || (p[1] & 0xE0) != 0xE0) return false;
        int verBits = (p[1] >> 3) & 3, layerBits = (p[1] >> 1) & 3;
        int brIdx = p[2] >> 4, srIdx = (p[2] >> 2) & 3, pad = (p[2] >> 1) & 1;
        if (verBits == 1 || layerBits == 0 || brIdx == 0 || brIdx == 15 || srIdx == 3) return false;

        int version = verBits == 3 ? 1 : verBits == 2 ? 2 : 25;
        int layer = 4 - layerBits;
        int rate = RatesV1[srIdx] / (version == 1 ? 1 : version == 2 ? 2 : 4);
        int kbps = (version == 1 ? BitratesV1 : BitratesV2)[layer - 1, brIdx];
        int bitrate = kbps * 1000;
        int length, spf;
        if (layer == 1) { length = (12 * bitrate / rate + pad) * 4; spf = 384; }
        else if (layer == 2) { length = 144 * bitrate / rate + pad; spf = 1152; }
        else
        {
            spf = version == 1 ? 1152 : 576;
            length = (version == 1 ? 144 : 72) * bitrate / rate + pad;
        }
        int channels = ((p[3] >> 6) & 3) == 3 ? 1 : 2;
        header = new FrameHeader(version, layer, rate, channels, length, spf);
        return length > 4;
    }

    /// <summary>
    /// Copies every frame found in <paramref name="data"/> into a contiguous stream.
    /// Bytes between frames that are not a frame start (padding) are skipped.
    /// </summary>
    internal static byte[] Extract(ReadOnlySpan<byte> data, out FrameHeader first, out int frameCount)
    {
        using var ms = new MemoryStream(data.Length);
        first = default;
        frameCount = 0;
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            if (TryParseHeader(data[pos..], out var h)
                && (frameCount == 0 || h.Signature == first.Signature)
                && pos + h.Length <= data.Length)
            {
                if (frameCount == 0) first = h;
                ms.Write(data.Slice(pos, h.Length));
                frameCount++;
                pos += h.Length;
            }
            else
            {
                pos++;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Decodes an .mp3 stream to interleaved 16-bit samples.</summary>
    internal static short[] Decode(byte[] mp3, int maxFrames)
    {
        using var file = new NLayer.MpegFile(new MemoryStream(mp3, writable: false));
        int channels = file.Channels;
        var output = new List<short>(maxFrames > 0 ? maxFrames * channels : 1 << 16);
        var buf = new float[4096 * channels];
        int read;
        while ((read = file.ReadSamples(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                output.Add(WavWriter.ToPcm16(buf[i]));
        }
        if (output.Count == 0)
            throw new InvalidDataException("The MPEG stream contained no decodable frames.");
        return output.ToArray();
    }
}
