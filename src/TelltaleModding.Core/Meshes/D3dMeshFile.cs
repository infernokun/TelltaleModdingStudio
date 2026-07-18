using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>Vertex attribute semantics (GFXPlatformVertexAttribute).</summary>
public enum GfxVertexAttribute
{
    Position = 0, Normal = 1, Tangent = 2, BlendWeight = 3,
    BlendIndex = 4, Color = 5, TexCoord = 6,
}

/// <summary>
/// Vertex element formats as used by the GoT / Tales from the Borderlands era
/// (values differ from later engine versions; sizes verified against real files).
/// </summary>
public enum GfxFormat
{
    F32x2 = 0x01,
    F32x3 = 0x02,
    SN16x2 = 0x12,
    SN16x4 = 0x14,
    U8x4 = 0x17,
    UN8x4 = 0x19,
}

/// <summary>Where one attribute lives inside the vertex buffers.</summary>
public readonly record struct GfxAttributeParams(
    GfxVertexAttribute Attribute, GfxFormat Format, int AttributeIndex, int BufferIndex, int BufferOffset);

/// <summary>One GPU buffer: element count, stride, and offset into the bulk payload.</summary>
public readonly record struct GfxBufferInfo(int Count, int Stride, int DataOffset);

/// <summary>One draw batch of LOD 0: an index range with its material.</summary>
public readonly record struct MeshBatch(
    int StartIndex, int PrimitiveCount, int MaterialIndex, int MinVertex, int MaxVertex);

/// <summary>
/// A parsed Telltale .d3dmesh (D3DMesh version 25 — the Game of Thrones /
/// Tales from the Borderlands tier). Layout ported from TelltaleToolLib's
/// serializer and verified byte-exact against a full game's meshes: every
/// block boundary is checked, and the vertex/index buffers must account for
/// the async section to the byte.
/// </summary>
public sealed class D3dMeshFile
{
    public string Name { get; private init; } = "";
    public int Version { get; private init; }
    public int VertexCount { get; private init; }
    public Vector3 BoundsMin { get; private init; }
    public Vector3 BoundsMax { get; private init; }

    /// <summary>LOD 0 draw batches.</summary>
    public IReadOnlyList<MeshBatch> Batches { get; private init; } = Array.Empty<MeshBatch>();

    /// <summary>Symbols (CRC64 of the texture file name, no extension) of referenced textures.</summary>
    public IReadOnlyList<ulong> TextureSymbols { get; private init; } = Array.Empty<ulong>();

    public IReadOnlyList<GfxAttributeParams> Attributes { get; private init; } = Array.Empty<GfxAttributeParams>();
    public GfxBufferInfo? IndexBuffer { get; private init; }
    public IReadOnlyList<GfxBufferInfo> VertexBuffers { get; private init; } = Array.Empty<GfxBufferInfo>();

    private byte[] _bufferData = Array.Empty<byte>();
    private readonly (Vector2 Scale, Vector2 Offset)[] _texCoordTransforms =
        { (Vector2.One, Vector2.Zero), (Vector2.One, Vector2.Zero),
          (Vector2.One, Vector2.Zero), (Vector2.One, Vector2.Zero) };

    private D3dMeshFile() { }

    public string Describe() =>
        $"{VertexCount:N0} verts · {Batches.Sum(b => b.PrimitiveCount):N0} tris · {Batches.Count} batch{(Batches.Count == 1 ? "" : "es")} · v{Version}";

    /// <summary>Attempts to parse a .d3dmesh file's bytes.</summary>
    public static bool TryLoad(byte[] data, out D3dMeshFile? mesh, out string? error)
    {
        mesh = null;
        if (!MetaStreamHeader.TryParse(data, out var meta, out error))
            return false;
        if (meta!.Classes.Count == 0 || meta.Classes[0].TypeName != "D3DMesh")
        {
            error = "Not a d3dmesh (header does not declare D3DMesh).";
            return false;
        }

        try
        {
            mesh = Parse(data, meta);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            error = $"Malformed d3dmesh: {ex.Message}";
            return false;
        }
    }

    private static D3dMeshFile Parse(byte[] file, MetaStreamHeader meta)
    {
        int off = meta.DefaultSectionOffset;
        int sectionEnd = meta.DefaultSectionOffset + meta.DefaultSectionSize;

        uint U32()
        {
            if (off + 4 > sectionEnd) throw new InvalidDataException("default section truncated");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off)); off += 4; return v;
        }
        int I32() => (int)U32();
        float F32() { var v = BitConverter.ToSingle(file, off); off += 4; return v; }
        ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(off)); off += 8; return v; }
        bool Bool()
        {
            byte b = file[off++];
            if (b is not ((byte)'0' or (byte)'1')) throw new InvalidDataException("bad bool byte");
            return b == (byte)'1';
        }
        // A block prefix counts itself; returns the block's absolute end offset.
        int Block() { int start = off; int size = I32(); return start + size; }
        void SkipBlock() { off = Block(); }
        void Expect(int expected, string what)
        {
            if (off != expected) throw new InvalidDataException($"{what}: at {off}, expected {expected}");
        }
        Vector3 Vec3() => new(F32(), F32(), F32());

        // ---- D3DMesh: name, version, tool props --------------------------
        int nameEnd = Block();
        int nameLen = I32();
        string name = System.Text.Encoding.ASCII.GetString(file, off, nameLen);
        off = nameEnd;
        int version = I32();
        if (Bool())
            throw new InvalidDataException("mesh carries tool properties — unsupported");
        if (version is < 22 or >= 52)
            throw new InvalidDataException($"D3DMesh version {version} is outside the supported tier (22-51).");

        // Internal resources (embedded material property sets) — skipped by block.
        int resourceCount = I32();
        for (int i = 0; i < resourceCount; i++)
        {
            U64(); U64();
            SkipBlock();
        }
        if (U32() != 0)
            SkipBlock(); // tool-only data

        // ---- T3MeshData --------------------------------------------------
        int meshEnd = Block();

        void Sphere() => SkipBlock();
        void Handle() => SkipBlock();
        void TexIndices()
        {
            int end = Block();
            uint i = U32();
            while ((i & 0x80000000) == 0) { U32(); i = U32(); }
            Expect(end, "T3MeshTextureIndices");
        }

        int ArrayHeader(out int end)
        {
            end = Block();
            return I32();
        }

        // mLODs
        var batches = new List<MeshBatch>();
        int lodVertexStateIndex = 0;
        int lodCount = ArrayHeader(out int lodsEnd);
        for (int l = 0; l < lodCount; l++)
        {
            int batchCount = ArrayHeader(out int batchesEnd);
            for (int b = 0; b < batchCount; b++)
            {
                off += 24; Sphere();          // bounding box + sphere
                U32();                        // batch usage flags
                int minVert = I32(), maxVert = I32();
                int startIndex = I32(), numPrims = I32();
                TexIndices();
                int materialIndex = I32();
                U32(); U32();                 // bone palette index, local transform index
                if (l == 0)
                    batches.Add(new MeshBatch(startIndex, numPrims, materialIndex, minVert, maxVert));
            }
            Expect(batchesEnd, "batches");
            SkipBlock();                      // vertex streams bit set
            off += 24; Sphere();              // LOD bounds
            int vstateIndex = I32();
            F32();                            // pixel size
            if (l == 0)
                lodVertexStateIndex = vstateIndex;
        }
        Expect(lodsEnd, "mLODs");

        // mTextures
        var textureSymbols = new List<ulong>();
        int texCount = ArrayHeader(out int texsEnd);
        for (int i = 0; i < texCount; i++)
        {
            U32(); Handle();
            textureSymbols.Add(U64());
            off += 24; Sphere(); F32(); F32();
        }
        Expect(texsEnd, "mTextures");

        // mMaterials
        int matCount = ArrayHeader(out int matsEnd);
        for (int i = 0; i < matCount; i++)
        {
            Handle(); U64(); off += 24; Sphere(); U32();
        }
        Expect(matsEnd, "mMaterials");

        // mMaterialOverrides
        int ovCount = ArrayHeader(out int ovsEnd);
        for (int i = 0; i < ovCount; i++) { Handle(); U32(); }
        Expect(ovsEnd, "mMaterialOverrides");

        // mBones / mLocalTransforms: arrays of palettes (outer count, then per
        // palette an inner count + entries) — verified against skinned meshes.
        void NestedArray(Action element)
        {
            int end = Block();
            int outer = I32();
            for (int o = 0; o < outer; o++)
            {
                int inner = I32();
                for (int i = 0; i < inner; i++)
                    element();
            }
            Expect(end, "nested array");
        }
        NestedArray(() => { U64(); off += 24; Sphere(); U32(); });   // bone: symbol, bounds, vert count
        NestedArray(() => { SkipBlock(); U32(); });                  // transform block, camera-facing type

        SkipBlock();                          // material requirements
        SkipBlock();                          // vertex streams bit set
        var boundsMin = Vec3();
        var boundsMax = Vec3();
        Sphere();                             // bounding sphere
        U32();                                // endian type
        U64();                                // property key base
        int vertexCount = I32();
        uint meshFlags = U32();

        var mesh = new D3dMeshFile
        {
            Name = name,
            Version = version,
            VertexCount = vertexCount,
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
            Batches = batches,
            TextureSymbols = textureSymbols,
        };

        // Sparse texcoord transforms
        int txCount = I32();
        for (int i = 0; i < txCount; i++)
        {
            int channel = I32();
            var scale = new Vector2(F32(), F32());
            var offset = new Vector2(F32(), F32());
            if (channel is >= 0 and < 4)
                mesh._texCoordTransforms[channel] = (scale, offset);
        }

        if ((meshFlags & 0x10) != 0)
            throw new InvalidDataException("CPU skinning data present — unsupported");

        // Vertex states: attribute layout + buffer descriptors.
        var attrs = new List<GfxAttributeParams>();
        var vbufs = new List<GfxBufferInfo>();
        GfxBufferInfo? ibuf = null;
        int dataOffset = 0;

        GfxBufferInfo ReadBuffer()
        {
            U32();                            // resource usage
            U32();                            // buffer format (index width comes from stride)
            U32();                            // buffer usage
            int count = I32(), stride = I32();
            var info = new GfxBufferInfo(count, stride, dataOffset);
            dataOffset += count * stride;
            return info;
        }

        int stateCount = I32();
        if (lodVertexStateIndex != 0 || stateCount < 1)
            throw new InvalidDataException($"unexpected vertex state layout (index {lodVertexStateIndex} of {stateCount})");
        for (int s = 0; s < stateCount; s++)
        {
            U32();                            // vertex count per instance
            int vbufCount = I32();
            int attrCount = I32();
            for (int a = 0; a < attrCount; a++)
            {
                var attr = new GfxAttributeParams(
                    (GfxVertexAttribute)I32(), (GfxFormat)I32(), I32(), I32(), I32());
                if (s == 0) attrs.Add(attr);
            }
            if (Bool())
            {
                var ib = ReadBuffer();
                if (s == 0) ibuf = ib;
            }
            for (int vb = 0; vb < vbufCount; vb++)
            {
                var buffer = ReadBuffer();
                if (s == 0) vbufs.Add(buffer);
            }
        }
        Expect(meshEnd, "T3MeshData");
        Expect(sectionEnd, "default section");

        if (dataOffset != meta.AsyncSectionSize)
            throw new InvalidDataException(
                $"buffer sizes disagree with bulk data: buffers say {dataOffset}, async section is {meta.AsyncSectionSize}");

        return new D3dMeshFile
        {
            Name = mesh.Name,
            Version = mesh.Version,
            VertexCount = mesh.VertexCount,
            BoundsMin = mesh.BoundsMin,
            BoundsMax = mesh.BoundsMax,
            Batches = mesh.Batches,
            TextureSymbols = mesh.TextureSymbols,
            Attributes = attrs,
            IndexBuffer = ibuf,
            VertexBuffers = vbufs,
            _bufferData = file.AsSpan(meta.AsyncSectionOffset, meta.AsyncSectionSize).ToArray(),
        }.CopyTransformsFrom(mesh);
    }

    private D3dMeshFile CopyTransformsFrom(D3dMeshFile other)
    {
        for (int i = 0; i < 4; i++)
            _texCoordTransforms[i] = other._texCoordTransforms[i];
        return this;
    }

    // ---- Geometry decoding ----------------------------------------------

    /// <summary>
    /// Decodes the packed vertex data into renderable arrays: float positions,
    /// unit normals, and UV channel 0 (with the mesh's texcoord transform applied
    /// and V flipped to top-left convention).
    /// </summary>
    public MeshGeometry GetGeometry()
    {
        if (IndexBuffer is not { } ib)
            throw new InvalidDataException("mesh has no index buffer");

        int[] indices = new int[ib.Count];
        for (int i = 0; i < ib.Count; i++)
        {
            indices[i] = ib.Stride switch
            {
                2 => BinaryPrimitives.ReadUInt16LittleEndian(_bufferData.AsSpan(ib.DataOffset + i * 2)),
                4 => BinaryPrimitives.ReadInt32LittleEndian(_bufferData.AsSpan(ib.DataOffset + i * 4)),
                _ => throw new InvalidDataException($"unsupported index stride {ib.Stride}"),
            };
        }

        int count = VertexBuffers.Count > 0 ? VertexBuffers[0].Count : 0;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];

        foreach (var attr in Attributes)
        {
            if (attr.AttributeIndex != 0)
                continue;
            var buffer = VertexBuffers[attr.BufferIndex];
            for (int i = 0; i < count; i++)
            {
                int at = buffer.DataOffset + i * buffer.Stride + attr.BufferOffset;
                switch (attr.Attribute)
                {
                    case GfxVertexAttribute.Position:
                        positions[i] = new Vector3(
                            BitConverter.ToSingle(_bufferData, at),
                            BitConverter.ToSingle(_bufferData, at + 4),
                            BitConverter.ToSingle(_bufferData, at + 8));
                        break;
                    case GfxVertexAttribute.Normal:
                        normals[i] = new Vector3(Sn16(at), Sn16(at + 2), Sn16(at + 4));
                        break;
                    case GfxVertexAttribute.TexCoord:
                        var (scale, offset) = _texCoordTransforms[0];
                        Vector2 raw = attr.Format switch
                        {
                            GfxFormat.SN16x2 => new Vector2(Sn16(at), Sn16(at + 2)),
                            GfxFormat.F32x2 => new Vector2(
                                BitConverter.ToSingle(_bufferData, at),
                                BitConverter.ToSingle(_bufferData, at + 4)),
                            _ => throw new InvalidDataException($"unsupported texcoord format {attr.Format}"),
                        };
                        var uv = raw * scale + offset;
                        uvs[i] = new Vector2(uv.X, 1f - uv.Y);
                        break;
                }
            }
        }

        return new MeshGeometry(positions, normals, uvs, indices, Batches, BoundsMin, BoundsMax);
    }

    private float Sn16(int at) =>
        Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(_bufferData.AsSpan(at)) / 32767f);
}

/// <summary>Decoded, renderable mesh data (LOD 0).</summary>
public sealed record MeshGeometry(
    Vector3[] Positions, Vector3[] Normals, Vector2[] TexCoords,
    int[] Indices, IReadOnlyList<MeshBatch> Batches,
    Vector3 BoundsMin, Vector3 BoundsMax);
