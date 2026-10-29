using System.Buffers.Binary;
using System.IO.Compression;
using TelltaleModding.Core.Meta;
using TelltaleModding.Core.Scene;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Tests;

public class LipSyncTests
{
    [SkippableFact]
    public void Reads_the_phoneme_keys_of_a_voice_line()
    {
        var keys = PhonemeTrack.Read(File.ReadAllBytes(SampleData.Sample("lipsync_tftb_362321480.anm")));
        Assert.Equal(16, keys.Count);
        var first = keys[0];
        Assert.Equal(TelltaleCrc64.HashSymbol("AA"), first.Phoneme);
        Assert.Equal(0.5333f, first.Time, 1e-3f);
        Assert.Equal(0.1333f, first.FadeIn, 1e-3f);
        Assert.Equal(0.7636f, first.Contribution, 1e-3f);
        Assert.Equal(TelltaleCrc64.HashSymbol("NN"), keys[1].Phoneme);
        Assert.True(keys.Zip(keys.Skip(1)).All(p => p.First.Time <= p.Second.Time));
        Assert.All(keys, k => Assert.Contains(k.Phoneme, PhonemePoses.Phonemes.Select(TelltaleCrc64.HashSymbol)));
    }

    [SkippableFact]
    public void Other_clips_have_no_phoneme_keys()
    {
        Assert.Empty(PhonemeTrack.Read(File.ReadAllBytes(SampleData.Sample("sk54_bigbyCrouch_weightShiftAShort.anm"))));
        Assert.Empty(PhonemeTrack.Read(new byte[] { 1, 2, 3 }));
    }

    [SkippableFact]
    public void Key_weight_eases_in_holds_and_eases_out()
    {
        var key = new PhonemeKey(1f, 0, FadeIn: 0.2f, Hold: 0.4f, FadeOut: 0.2f, Contribution: 0.5f);
        Assert.Equal(0f, key.WeightAt(0.9f));
        Assert.Equal(0.25f, key.WeightAt(1.1f), 1e-3f);
        Assert.Equal(0.5f, key.WeightAt(1.4f), 1e-3f);
        Assert.Equal(0.25f, key.WeightAt(1.7f), 1e-3f);
        Assert.Equal(0f, key.WeightAt(1.81f));
    }

    [SkippableFact]
    public void Finds_phoneme_pose_clips_and_mood_variants()
    {
        var files = new[] { "rhys_phoneme_AA.anm", "rhys_phoneme_EE.anm", "rhys_phoneme_AA_smugA.anm", "rhys_phonemes_smugA.anm", "x.anm" };
        Assert.Equal(new[] { "rhys" }, PhonemePoses.Prefixes(files));
        var clips = PhonemePoses.ClipNames("rhys", "smugA", files.Contains);
        Assert.Equal("rhys_phoneme_AA_smugA.anm", clips[TelltaleCrc64.HashSymbol("AA")]);
        Assert.Equal("rhys_phoneme_EE.anm", clips[TelltaleCrc64.HashSymbol("EE")]);
        Assert.Equal(2, clips.Count);
    }

    [SkippableFact]
    public void Png_encodes_pixels_losslessly()
    {
        var rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 11);
        var png = PngWriter.Encode(3, 2, rgba);

        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

        // Inflate IDAT and strip the per-row filter bytes.
        int at = 8;
        var idat = new MemoryStream();
        while (at < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            if (type == "IDAT")
                idat.Write(png, at + 8, length);
            at += 12 + length;
        }
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.CopyTo(raw);
        var rows = raw.ToArray();
        Assert.Equal(2 * (1 + 12), rows.Length);
        Assert.Equal(rgba[..12], rows[1..13]);
        Assert.Equal(rgba[12..], rows[14..26]);
    }
}
