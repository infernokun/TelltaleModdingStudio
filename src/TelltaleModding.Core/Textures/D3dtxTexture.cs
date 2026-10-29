using System.Buffers.Binary;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Textures;

/// <summary>One mip/face region of a texture's pixel data.</summary>
/// <param name="FaceIndex">Cubemap face (0 for 2D textures).</param>
/// <param name="MipIndex">0 = largest mip.</param>
/// <param name="DataOffset">Offset into the file's pixel-data payload.</param>
public readonly record struct D3dtxRegion(
    int FaceIndex, int MipIndex, int MipCount, int DataSize, int Pitch, int DataOffset);

/// <summary>
/// A parsed Telltale .d3dtx texture (D3DTX version 4 for The Wolf Among Us and Walking Dead
/// Season Two, version 5 for Game of Thrones, version 7 for the Michonne / Tales from
/// the Borderlands era, and version 9 for Walking Dead: The Telltale Definitive Series)
/// with conversion to standard DDS. Handles both the usual meta-stream-wrapped form
/// (MSV5 or MSV6) and bare V7 payloads without the meta-stream header (as produced by
/// some extraction paths).
///
/// V7 field layout ported from Telltale-Modding-Group's texture tool, V9 additions
/// from TelltaleToolLib's T3Texture metaclass. V5 is V7 without the swizzle fields
/// and with an import specular power; V4 additionally lacks the import specular power,
/// layout, gamma and one shading float, and its region headers have no face index.
/// All verified against real files: every mip region's size must sum exactly to the
/// pixel payload.
/// </summary>
public sealed class D3dtxTexture
{
    public string Name { get; private init; } = "";
    public string ImportName { get; private init; } = "";
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int MipCount { get; private init; }
    public T3SurfaceFormat SurfaceFormat { get; private init; }
    public IReadOnlyList<D3dtxRegion> Regions { get; private init; } = Array.Empty<D3dtxRegion>();

    private byte[] _pixelData = Array.Empty<byte>();

    private D3dtxTexture() { }

    /// <summary>Short display summary, e.g. "2048×2048 BC1 · 10 mips".</summary>
    public string Describe() =>
        $"{Width}×{Height} {SurfaceFormat} · {MipCount} mip{(MipCount == 1 ? "" : "s")}";

    /// <summary>Attempts to parse a .d3dtx file's bytes.</summary>
    public static bool TryLoad(byte[] data, out D3dtxTexture? texture, out string? error)
    {
        texture = null;
        try
        {
            int payloadStart, payloadLength, pixelsStart, pixelsLength;

            if (MetaStreamHeader.TryParse(data, out var meta, out _))
            {
                payloadStart = meta!.DefaultSectionOffset;
                payloadLength = meta.DefaultSectionSize;
                pixelsStart = meta.AsyncSectionOffset;
                pixelsLength = meta.AsyncSectionSize;
            }
            else if (data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 7)
            {
                // Bare V7 payload with no meta-stream wrapper; pixel data follows the
                // fields; its start is only known after parsing, marked by -1 here.
                payloadStart = 0;
                payloadLength = data.Length;
                pixelsStart = -1;
                pixelsLength = 0;
            }
            else
            {
                error = "Not a d3dtx texture (no meta-stream header and no bare V7 payload).";
                return false;
            }

            texture = Parse(data, payloadStart, payloadLength, pixelsStart, pixelsLength);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            error = $"Malformed d3dtx: {ex.Message}";
            return false;
        }
    }

    private static D3dtxTexture Parse(
        byte[] file, int payloadStart, int payloadLength, int pixelsStart, int pixelsLength)
    {
        int off = payloadStart;
        int payloadEnd = payloadStart + payloadLength;
        uint U32()
        {
            if (off + 4 > payloadEnd) throw new InvalidDataException("payload truncated");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off)); off += 4; return v;
        }
        int I32() => (int)U32();
        string Str()
        {
            int len = I32();
            if (len < 0 || len > payloadEnd - off)
                throw new InvalidDataException("string length out of range");
            var s = System.Text.Encoding.ASCII.GetString(file, off, len);
            off += len;
            return s;
        }

        uint version = U32();
        if (version is not (4 or 5 or 7 or 9))
            throw new InvalidDataException(
                $"D3DTX version {version} is not supported (only 4, 5, 7 and 9).");
        bool v4 = version == 4;
        bool v5 = version == 5;
        bool v9 = version == 9;

        U32(); U32();       // mSamplerState block size + value
        U32(); U32();       // mPlatform block size + value
        U32();              // mName block size
        string name = Str();
        U32();              // mImportName block size
        string importName = Str();
        off += 4;           // mImportScale
        if (v5)
            off += 4;       // mImportSpecularPower

        // Telltale serializes bools as ASCII '0'/'1'.
        byte hasProps = file[off++];
        if (hasProps != (byte)'0')
            throw new InvalidDataException("texture carries tool properties (unsupported)");

        int mips = I32();
        int width = I32();
        int height = I32();
        if (v9)
            off += 4 * 2;   // mDepth, mArraySize
        var format = (T3SurfaceFormat)I32();
        if (v4 || v5)
        {
            // The MSV5 games have no swizzle; V4 also lacks layout, gamma and one of
            // the three shading floats.
            if (v5)
                off += 4 * 2;   // mTextureLayout, mSurfaceGamma
            off += 4 * 3;       // mResourceUsage, mType, mNormalMapFormat
            off += 4 * (v5 ? 3 : 2); // shading floats (specular gloss, HDR lightmap scale, toon cutoff)
            off += 4 * 2;       // mAlphaMode, mColorMode
            off += 4 * 4;       // mUVOffset, mUVScale
        }
        else
        {
            off += 4 * 2;   // mTextureLayout, mSurfaceGamma
            if (v9)
                off += 4;   // mSurfaceMultisample
            off += 4 * 3;   // mResourceUsage, mType, mSwizzleSize
            off += 4;       // mSwizzle
            off += 4 * 3;   // mSpecularGlossExponent, mHDRLightmapScale, mToonGradientCutoff
            off += 4 * 2;   // mAlphaMode, mColorMode
            off += 4 * 4;   // mUVOffset, mUVScale
        }

        if (v9)
        {
            int frameNamesStart = off;
            off = frameNamesStart + I32(); // mArrayFrameNames block (counts itself)
        }

        off += 4;           // toon regions array capacity
        int toonCount = I32();
        off += toonCount * 20;

        int regionCount = I32();
        int auxCount = I32();
        if (auxCount != 0)
            throw new InvalidDataException($"texture has {auxCount} aux data blocks (unsupported)");
        int totalDataSize = I32();

        var regions = new D3dtxRegion[regionCount];
        int dataOffset = 0;
        for (int i = 0; i < regionCount; i++)
        {
            int face = v4 ? 0 : I32();   // V4 regions carry no face index
            int mip = I32(), mipCount = I32(), size = I32(), pitch = I32();
            if (v9)
                I32();      // slice pitch
            regions[i] = new D3dtxRegion(face, mip, mipCount, size, pitch, dataOffset);
            dataOffset += size;
        }

        // Headerless form: pixel data follows the fields we just read.
        if (pixelsStart < 0)
        {
            pixelsStart = off;
            pixelsLength = file.Length - off;
        }

        if (dataOffset != totalDataSize || pixelsLength != totalDataSize)
            throw new InvalidDataException(
                $"pixel data size mismatch: regions say {dataOffset}, header says {totalDataSize}, payload is {pixelsLength}");

        return new D3dtxTexture
        {
            Name = name,
            ImportName = importName,
            Width = width,
            Height = height,
            MipCount = mips,
            SurfaceFormat = format,
            Regions = regions,
            _pixelData = file.AsSpan(pixelsStart, pixelsLength).ToArray(),
        };
    }

    // ---- DDS output -----------------------------------------------------

    private const uint DdsdCaps = 0x1, DdsdHeight = 0x2, DdsdWidth = 0x4, DdsdPitch = 0x8,
        DdsdPixelFormat = 0x1000, DdsdMipMapCount = 0x20000, DdsdLinearSize = 0x80000;
    private const uint DdpfAlpha = 0x2, DdpfFourCc = 0x4, DdpfRgb = 0x40;
    private const uint DdsCapsComplex = 0x8, DdsCapsTexture = 0x1000, DdsCapsMipMap = 0x400000;
    private const uint DdsCaps2Cubemap = 0xFE00; // cubemap + all six faces

    /// <summary>Converts to a standard DDS file, throwing for unsupported surface formats.</summary>
    public byte[] ToDds()
    {
        if (!TryToDds(out var dds, out var error))
            throw new NotSupportedException(error);
        return dds!;
    }

    /// <summary>
    /// Attempts conversion to DDS. The d3dtx stores mip regions smallest-first;
    /// DDS wants largest-first, so regions are re-ordered by (face, mip).
    /// </summary>
    public bool TryToDds(out byte[]? dds, out string? error)
    {
        dds = null;
        if (!TryGetDdsPixelFormat(out var fourCc, out var pfFlags, out var bitCount, out var masks, out var blockSize, out error))
            return false;

        int faceCount = Regions.Count == 0 ? 1 : Regions.Max(r => r.FaceIndex) + 1;

        uint flags = DdsdCaps | DdsdHeight | DdsdWidth | DdsdPixelFormat;
        uint pitchOrLinear;
        if (blockSize > 0)
        {
            flags |= DdsdLinearSize;
            pitchOrLinear = (uint)(Math.Max(1, (Width + 3) / 4) * Math.Max(1, (Height + 3) / 4) * blockSize);
        }
        else
        {
            flags |= DdsdPitch;
            pitchOrLinear = (uint)((Width * bitCount + 7) / 8);
        }
        if (MipCount > 1)
            flags |= DdsdMipMapCount;

        uint caps = DdsCapsTexture;
        if (MipCount > 1)
            caps |= DdsCapsMipMap | DdsCapsComplex;
        uint caps2 = 0;
        if (faceCount == 6)
        {
            caps2 = DdsCaps2Cubemap;
            caps |= DdsCapsComplex;
        }

        var result = new byte[128 + _pixelData.Length];
        void Put(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), value);

        Put(0, 0x20534444);            // "DDS "
        Put(4, 124);                   // header size
        Put(8, flags);
        Put(12, (uint)Height);
        Put(16, (uint)Width);
        Put(20, pitchOrLinear);
        Put(28, (uint)MipCount);
        Put(76, 32);                   // pixel format size
        Put(80, pfFlags);
        Put(84, fourCc);
        Put(88, bitCount);
        Put(92, masks.r); Put(96, masks.g); Put(100, masks.b); Put(104, masks.a);
        Put(108, caps);
        Put(112, caps2);

        // Emit regions face-major, mip 0 (largest) first, as DDS expects.
        int outPos = 128;
        foreach (var region in Regions.OrderBy(r => r.FaceIndex).ThenBy(r => r.MipIndex))
        {
            _pixelData.AsSpan(region.DataOffset, region.DataSize).CopyTo(result.AsSpan(outPos));
            outPos += region.DataSize;
        }

        dds = result;
        return true;
    }

    private bool TryGetDdsPixelFormat(
        out uint fourCc, out uint pfFlags, out uint bitCount,
        out (uint r, uint g, uint b, uint a) masks, out int blockSize, out string? error)
    {
        fourCc = 0; pfFlags = 0; bitCount = 0; masks = default; blockSize = 0; error = null;
        switch (SurfaceFormat)
        {
            case T3SurfaceFormat.BC1:
                pfFlags = DdpfFourCc; fourCc = FourCc("DXT1"); blockSize = 8; return true;
            case T3SurfaceFormat.BC2:
                pfFlags = DdpfFourCc; fourCc = FourCc("DXT3"); blockSize = 16; return true;
            case T3SurfaceFormat.BC3:
                pfFlags = DdpfFourCc; fourCc = FourCc("DXT5"); blockSize = 16; return true;
            case T3SurfaceFormat.BC4:
                pfFlags = DdpfFourCc; fourCc = FourCc("ATI1"); blockSize = 8; return true;
            case T3SurfaceFormat.BC5:
                pfFlags = DdpfFourCc; fourCc = FourCc("ATI2"); blockSize = 16; return true;
            case T3SurfaceFormat.A8:
                pfFlags = DdpfAlpha; bitCount = 8; masks = (0, 0, 0, 0xFF); return true;
            case T3SurfaceFormat.ARGB8:
                pfFlags = DdpfRgb | DdpfAlpha; bitCount = 32;
                masks = (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000); return true;
            case T3SurfaceFormat.RGBA32F:
                pfFlags = DdpfFourCc; fourCc = 116; return true; // D3DFMT_A32B32G32R32F
            default:
                error = $"No DDS mapping for Telltale surface format {SurfaceFormat} yet.";
                return false;
        }
    }

    private static uint FourCc(string s) =>
        (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
}
