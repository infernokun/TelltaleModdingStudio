using System.Buffers.Binary;

namespace TelltaleModding.Core.Audio;

/// <summary>Decoders for the uncompressed and ADPCM sample formats found in sound banks.</summary>
internal static class PcmDecoders
{
    /// <summary>Little-endian linear PCM (8-bit, 16-bit or 32-bit float) to 16-bit.</summary>
    public static short[] DecodeLinear(ReadOnlySpan<byte> data, int bytesPer, int channels, long frames, bool unsigned8 = true)
    {
        long n = Math.Min(frames * channels, data.Length / bytesPer);
        var output = new short[n];
        for (int i = 0; i < n; i++)
        {
            output[i] = bytesPer switch
            {
                1 => unsigned8 ? (short)((data[i] - 128) << 8) : (short)((sbyte)data[i] << 8),
                2 => BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]),
                _ => WavWriter.ToPcm16(BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..])),
            };
        }
        return output;
    }

    private static readonly int[] ImaSteps =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
        107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
        4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
        22385, 24623, 27086, 29794, 32767,
    };

    private static readonly int[] ImaIndexAdjust = { -1, -1, -1, -1, 2, 4, 6, 8 };

    /// <summary>
    /// Xbox-style IMA ADPCM: 36-byte blocks per channel, each a 4-byte header (initial
    /// sample and step index) followed by 64 nibbles; channels interleave in 4-byte words.
    /// </summary>
    public static short[] DecodeXboxIma(ReadOnlySpan<byte> data, int channels, long frames)
    {
        int blockAlign = 36 * channels;
        long blocks = data.Length / blockAlign;
        long total = frames > 0 ? Math.Min(frames, blocks * 64) : blocks * 64;
        var output = new short[total * channels];
        long frame = 0;
        for (long b = 0; b < blocks && frame < total; b++)
        {
            var block = data.Slice((int)(b * blockAlign), blockAlign);
            for (int c = 0; c < channels; c++)
            {
                int pred = BinaryPrimitives.ReadInt16LittleEndian(block[(c * 4)..]);
                int index = Math.Clamp((int)block[c * 4 + 2], 0, 88);
                for (int word = 0; word < 8; word++)
                {
                    int at = 4 * channels + (word * channels + c) * 4;
                    for (int k = 0; k < 8; k++)
                    {
                        int nibble = (block[at + k / 2] >> ((k & 1) * 4)) & 0xF;
                        int step = ImaSteps[index];
                        int diff = step >> 3;
                        if ((nibble & 1) != 0) diff += step >> 2;
                        if ((nibble & 2) != 0) diff += step >> 1;
                        if ((nibble & 4) != 0) diff += step;
                        pred = Math.Clamp((nibble & 8) != 0 ? pred - diff : pred + diff, short.MinValue, short.MaxValue);
                        index = Math.Clamp(index + ImaIndexAdjust[nibble & 7], 0, 88);
                        long f = frame + word * 8 + k;
                        if (f < total) output[f * channels + c] = (short)pred;
                    }
                }
            }
            frame += 64;
        }
        return output;
    }

    private static readonly int[,] FadpcmCoefs = { { 0, 0 }, { 60, 0 }, { 122, 60 }, { 115, 52 }, { 98, 55 }, { 0, 0 }, { 0, 0 }, { 0, 0 } };

    /// <summary>
    /// FMOD's own ADPCM: 0x8C-byte frames per channel (interleaved), each with packed
    /// coefficient/shift selectors, two history samples and 256 nibbles.
    /// </summary>
    public static short[] DecodeFadpcm(ReadOnlySpan<byte> data, int channels, long frames)
    {
        const int frameBytes = 0x8C, frameSamples = 256;
        long blocks = data.Length / (frameBytes * channels);
        long total = frames > 0 ? Math.Min(frames, blocks * frameSamples) : blocks * frameSamples;
        var output = new short[total * channels];
        for (long b = 0; b < blocks; b++)
        {
            for (int c = 0; c < channels; c++)
            {
                var f = data.Slice((int)((b * channels + c) * frameBytes), frameBytes);
                uint coefs = BinaryPrimitives.ReadUInt32LittleEndian(f);
                uint shifts = BinaryPrimitives.ReadUInt32LittleEndian(f[4..]);
                int hist1 = BinaryPrimitives.ReadInt16LittleEndian(f[8..]);
                int hist2 = BinaryPrimitives.ReadInt16LittleEndian(f[10..]);
                long sampleIndex = b * frameSamples;
                for (int i = 0; i < 8; i++)
                {
                    int ci = (int)((coefs >> (i * 4)) & 0xF) % 7;
                    int shift = 0x16 - (int)((shifts >> (i * 4)) & 0xF);
                    int c1 = FadpcmCoefs[ci, 0], c2 = FadpcmCoefs[ci, 1];
                    for (int j = 0; j < 4; j++)
                    {
                        uint nibbles = BinaryPrimitives.ReadUInt32LittleEndian(f[(0xC + 0x10 * i + 4 * j)..]);
                        for (int k = 0; k < 8; k++)
                        {
                            int s = (int)(((nibbles >> (k * 4)) & 0xF) << 28) >> shift;
                            s = (s - hist2 * c2 + hist1 * c1) >> 6;
                            s = Math.Clamp(s, short.MinValue, short.MaxValue);
                            if (sampleIndex < total) output[sampleIndex * channels + c] = (short)s;
                            sampleIndex++;
                            hist2 = hist1;
                            hist1 = s;
                        }
                    }
                }
            }
        }
        return output;
    }
}
