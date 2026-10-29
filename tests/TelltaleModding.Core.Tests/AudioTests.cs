using System.Buffers.Binary;
using TelltaleModding.Core.Audio;

namespace TelltaleModding.Core.Tests;

/// <summary>A fact that only runs when The Walking Dead Definitive Series is installed (its FMOD DLL supplies Vorbis setups).</summary>
public sealed class WdcInstallFactAttribute : SkippableFactAttribute
{
    public const string Folder = @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";

    public WdcInstallFactAttribute()
    {
        if (!Directory.Exists(Folder))
            Skip = "The Walking Dead Definitive Series is not installed on this host.";
    }
}

public class AudioTests
{
    private static byte[] Sample(string name) => File.ReadAllBytes(SampleData.Sample(name));

    private static AudioClip LoadSingle(string file, VorbisSetupLibrary? setups = null)
    {
        Assert.True(TelltaleAudio.TryLoad(Sample(file), file, setups ?? VorbisSetupLibrary.Empty, out var clips, out var error), error);
        return Assert.Single(clips);
    }

    private static double Rms(short[] pcm) => Math.Sqrt(pcm.Average(s => (double)s * s));

    [SkippableTheory]
    [InlineData("voice.wav", true)]
    [InlineData("music.OGG", true)]
    [InlineData("Master Bank.bank", true)]
    [InlineData("mesh.d3dmesh", false)]
    [InlineData("readme.txt", false)]
    public void IsAudioFile_RecognizesAudioExtensions(string name, bool expected) =>
        Assert.Equal(expected, TelltaleAudio.IsAudioFile(name));

    [SkippableTheory]
    [InlineData("tftb_361128592.wav", 1, 44100, 105984)]
    [InlineData("tftb_362135553.wav", 2, 44100, 182016)]
    [InlineData("got_365359694.wav", 1, 44100, 240768)]
    public void Fsb5Mpeg_ExtractsPlayableMp3(string file, int channels, int rate, long samples)
    {
        var clip = LoadSingle(file);

        Assert.Equal(AudioCodec.Mp3, clip.Codec);
        Assert.Equal(".mp3", clip.NativeExtension);
        Assert.Equal(channels, clip.Channels);
        Assert.Equal(rate, clip.SampleRate);
        Assert.Equal(samples, clip.SampleCount);
        Assert.Equal(samples / (double)rate, clip.Duration.TotalSeconds, 3);
        Assert.Equal(Path.GetFileNameWithoutExtension(file).Split('_')[1], clip.Name);
        AssertContiguousMpegFrames(clip.NativeFile);

        var pcm = clip.DecodePcm16();
        Assert.Equal(samples * channels, pcm.Length);
        Assert.True(Rms(pcm) > 200, "decoded voice line should not be silent");
    }

    [SkippableFact]
    public void Fsb4Mpeg_StripsFramePadding()
    {
        var clip = LoadSingle("wau_357250533.wav");

        Assert.Equal(AudioCodec.Mp3, clip.Codec);
        Assert.Equal(1, clip.Channels);
        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(72576, clip.SampleCount);
        AssertContiguousMpegFrames(clip.NativeFile);

        var pcm = clip.DecodePcm16();
        Assert.Equal(72576, pcm.Length);
        Assert.True(Rms(pcm) > 200);
    }

    private static void AssertContiguousMpegFrames(byte[] mp3)
    {
        int pos = 0, frames = 0;
        while (pos < mp3.Length)
        {
            Assert.True(MpegFrames.TryParseHeader(mp3.AsSpan(pos), out var h), $"no frame header at {pos}");
            pos += h.Length;
            frames++;
        }
        Assert.Equal(mp3.Length, pos);
        Assert.True(frames > 10);
    }

    [SkippableFact]
    public void Fsb5Vorbis_WithoutSetups_FailsMentioningTheFmodDll()
    {
        Assert.False(TelltaleAudio.TryLoad(Sample("wdc_396743171.wav"), "396743171.wav", VorbisSetupLibrary.Empty, out var clips, out var error));
        Assert.Empty(clips);
        Assert.Contains("FMOD DLL", error);
    }

    [SkippableFact]
    public void Fsb5Vorbis_WithUnrelatedSetups_ReportsTheMissingHeader()
    {
        var lib = VorbisSetupLibrary.FromPackets(new[] { "\u0005vorbis-not-a-real-setup"u8.ToArray() });
        Assert.Equal(1, lib.Count);

        Assert.False(TelltaleAudio.TryLoad(Sample("wdc_396743171.wav"), "396743171.wav", lib, out _, out var error));
        Assert.Contains("0x08474B3B", error);
    }

    [WdcInstallFact]
    public void Fsb5Vorbis_RebuildsOggFromGameDll()
    {
        var lib = VorbisSetupLibrary.FromFolder(WdcInstallFactAttribute.Folder);
        Assert.True(lib.Count > 30, $"expected the DLL's setup table, got {lib.Count}");
        Assert.Same(lib, VorbisSetupLibrary.FromFolder(WdcInstallFactAttribute.Folder));

        var clip = LoadSingle("wdc_396743171.wav", lib);
        Assert.Equal(AudioCodec.Vorbis, clip.Codec);
        Assert.Equal(".ogg", clip.NativeExtension);
        Assert.Equal(1, clip.Channels);
        Assert.Equal(44100, clip.SampleRate);
        Assert.Equal(91567, clip.SampleCount);

        var ogg = clip.NativeFile;
        AssertValidOggPages(ogg);

        var pcm = clip.DecodePcm16();
        Assert.Equal(91567, pcm.Length);
        Assert.True(Rms(pcm) > 200);

        // The rebuilt file reads back as a plain Ogg Vorbis entry with the same length.
        Assert.True(TelltaleAudio.TryLoad(ogg, "rebuilt.ogg", VorbisSetupLibrary.Empty, out var again, out var error), error);
        var plain = Assert.Single(again);
        Assert.Equal(91567, plain.SampleCount);
        Assert.Same(ogg, plain.NativeFile);
    }

    private static void AssertValidOggPages(byte[] ogg)
    {
        int pos = 0, pages = 0;
        while (pos < ogg.Length)
        {
            Assert.True(ogg.AsSpan(pos, 4).SequenceEqual("OggS"u8), $"no page at {pos}");
            int segs = ogg[pos + 26];
            int len = 27 + segs;
            for (int i = 0; i < segs; i++) len += ogg[pos + 27 + i];
            var page = ogg.AsSpan(pos, len).ToArray();
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22));
            page.AsSpan(22, 4).Clear();
            Assert.Equal(stored, AudioCrc.Ogg(page));
            pos += len;
            pages++;
        }
        Assert.True(pages >= 3);
    }

    [SkippableFact]
    public void Bank_FindsEmbeddedFsb5()
    {
        // An FMOD Studio bank is a RIFF file with the FSB5 inside a sound chunk.
        var fsb = Sample("got_365359694.wav");
        var bank = new byte[64 + fsb.Length];
        "RIFF"u8.CopyTo(bank);
        BinaryPrimitives.WriteInt32LittleEndian(bank.AsSpan(4), bank.Length - 8);
        "FEV FMT "u8.CopyTo(bank.AsSpan(8));
        "SND "u8.CopyTo(bank.AsSpan(56));
        BinaryPrimitives.WriteInt32LittleEndian(bank.AsSpan(60), fsb.Length);
        fsb.CopyTo(bank, 64);

        Assert.True(TelltaleAudio.TryLoad(bank, "Test.bank", VorbisSetupLibrary.Empty, out var clips, out var error), error);
        var clip = Assert.Single(clips);
        Assert.Equal("365359694", clip.Name);
        Assert.Equal(AudioCodec.Mp3, clip.Codec);
    }

    [SkippableFact]
    public void Bank_WithoutSounds_ReportsMetadataOnly()
    {
        var bank = new byte[64];
        "RIFF"u8.CopyTo(bank);
        "FEV FMT "u8.CopyTo(bank.AsSpan(8));
        Assert.False(TelltaleAudio.TryLoad(bank, "Master Bank.bank", VorbisSetupLibrary.Empty, out _, out var error));
        Assert.Contains("no embedded sounds", error);
    }

    [SkippableFact]
    public void Garbage_IsRejected()
    {
        Assert.False(TelltaleAudio.TryLoad(new byte[100], "x.wav", VorbisSetupLibrary.Empty, out var clips, out var error));
        Assert.Empty(clips);
        Assert.NotNull(error);
    }

    [SkippableFact]
    public void ToWav_WritesPcm16Riff()
    {
        var clip = LoadSingle("tftb_362135553.wav");
        var wav = clip.ToWav();

        Assert.True(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.True(wav.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(182016 * 2 * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(44 + 182016 * 4, wav.Length);

        // A plain PCM WAVE entry loads back unchanged.
        Assert.True(TelltaleAudio.TryLoad(wav, "x.wav", VorbisSetupLibrary.Empty, out var again, out var error), error);
        var pcm = Assert.Single(again);
        Assert.Equal(AudioCodec.Pcm, pcm.Codec);
        Assert.Equal(182016, pcm.SampleCount);
        Assert.Equal(clip.DecodePcm16(), pcm.DecodePcm16());
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetupLibrary_HarvestsPlainAndPatchedTableRecords(bool is64)
    {
        var setup = new byte[57];
        "\u0005vorbis"u8.CopyTo(setup);
        for (int i = 7; i < setup.Length; i++) setup[i] = (byte)(i * 37);
        var patch = new byte[] { 0xAA, 0xBB, 0xCC };
        var patched = (byte[])setup.Clone();
        patch.CopyTo(patched, 50);

        var dll = BuildFakePe(is64, setup, patch, AudioCrc.Crc32(setup), AudioCrc.Crc32(patched));
        var lib = VorbisSetupLibrary.FromImages(dll);

        Assert.Equal(2, lib.Count);
        Assert.True(lib.Contains(AudioCrc.Crc32(setup)));
        Assert.True(lib.Contains(AudioCrc.Crc32(patched)));
    }

    [SkippableFact]
    public void SetupLibrary_IgnoresRecordsWithWrongCrc()
    {
        var setup = new byte[40];
        "\u0005vorbis"u8.CopyTo(setup);
        var dll = BuildFakePe(false, setup, new byte[] { 1 }, AudioCrc.Crc32(setup), 0xDEADBEEF);
        var lib = VorbisSetupLibrary.FromImages(dll);
        Assert.Equal(1, lib.Count);
        Assert.False(lib.Contains(0xDEADBEEF));
    }

    [SkippableFact]
    public void SetupLibrary_MissingFolderIsEmpty()
    {
        Assert.Equal(0, VorbisSetupLibrary.FromFolder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Count);
        Assert.Equal(0, VorbisSetupLibrary.Empty.Count);
    }

    /// <summary>
    /// A minimal PE image with one section holding a setup packet, a patch, and a
    /// two-record setup table (one plain record, one patched record).
    /// </summary>
    private static byte[] BuildFakePe(bool is64, byte[] setup, byte[] patch, uint setupCrc, uint patchedCrc)
    {
        const int peOffset = 0x40, rawOffset = 0x200, sectionVa = 0x1000;
        ulong imageBase = is64 ? 0x180000000UL : 0x10000000UL;
        int optSize = is64 ? 0xF0 : 0xE0;
        int ptr = is64 ? 8 : 4;
        var section = new byte[0x200];
        setup.CopyTo(section, 0);
        patch.CopyTo(section, 0x80);

        ulong Va(int sectionOffset) => imageBase + sectionVa + (ulong)sectionOffset;
        void WritePtr(int at, ulong value)
        {
            if (is64) BinaryPrimitives.WriteUInt64LittleEndian(section.AsSpan(at), value);
            else BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(at), (uint)value);
        }

        int rec = 0x100, recSize = ptr * 2 + 16;
        WritePtr(rec, Va(0));
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(rec + ptr), setup.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(rec + ptr + 4), setupCrc);
        rec += recSize;
        WritePtr(rec, Va(0x80));
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(rec + ptr), setup.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(rec + ptr + 4), patchedCrc);
        WritePtr(rec + ptr + 8, Va(0));
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(rec + ptr * 2 + 8), 50);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(rec + ptr * 2 + 12), patch.Length);

        var image = new byte[rawOffset + section.Length];
        var s = image.AsSpan();
        s[0] = (byte)'M'; s[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(s[0x3C..], peOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s[peOffset..], 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(peOffset + 4)..], (ushort)(is64 ? 0x8664 : 0x14C));
        BinaryPrimitives.WriteUInt16LittleEndian(s[(peOffset + 6)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[(peOffset + 20)..], (ushort)optSize);
        int opt = peOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(s[opt..], (ushort)(is64 ? 0x20B : 0x10B));
        if (is64) BinaryPrimitives.WriteUInt64LittleEndian(s[(opt + 24)..], imageBase);
        else BinaryPrimitives.WriteUInt32LittleEndian(s[(opt + 28)..], (uint)imageBase);
        int sh = opt + optSize;
        ".rdata"u8.CopyTo(s[sh..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[(sh + 8)..], section.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[(sh + 12)..], sectionVa);
        BinaryPrimitives.WriteInt32LittleEndian(s[(sh + 16)..], section.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[(sh + 20)..], rawOffset);
        section.CopyTo(image, rawOffset);
        return image;
    }
}
