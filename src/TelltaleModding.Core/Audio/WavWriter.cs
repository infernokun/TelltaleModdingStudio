using System.Buffers.Binary;

namespace TelltaleModding.Core.Audio;

/// <summary>Writes interleaved 16-bit samples as a canonical PCM RIFF/WAVE file.</summary>
public static class WavWriter
{
    public static byte[] Write(ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        int dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataBytes);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(s[(44 + i * 2)..], samples[i]);
        return wav;
    }

    /// <summary>Converts float samples in [-1, 1] to 16-bit with clipping.</summary>
    internal static short ToPcm16(float f)
    {
        float v = f * 32767f;
        if (v >= 32767f) return short.MaxValue;
        if (v <= -32768f) return short.MinValue;
        return (short)MathF.Round(v);
    }
}
