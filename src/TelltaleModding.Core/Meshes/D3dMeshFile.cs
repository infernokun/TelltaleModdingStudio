using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>Vertex attribute semantics.</summary>
public enum GfxVertexAttribute
{
    Position = 0, Normal = 1, Tangent = 2, BlendWeight = 3,
    BlendIndex = 4, Color = 5, TexCoord = 6,
}

/// <summary>Vertex element formats for mesh versions 22-51. Later versions renumbered them.</summary>
public enum GfxFormat
{
    F32x2 = 0x01,
    F32x3 = 0x02,
    SN16x2 = 0x12,
    SN16x4 = 0x14,
    U8x4 = 0x17,
    UN8x4 = 0x19,
}

/// <summary>Vertex element formats for mesh version 55.</summary>
public enum GfxPlatformFormat
{
    F32x2 = 0x02,
    F32x3 = 0x03,
    UN16x2 = 0x18,
    SN16x2 = 0x17,
    UN16x4 = 0x1A,
    U8x4 = 0x20,
    SN8x4 = 0x25,
    UN8x4 = 0x26,
    UN10x3_UN2 = 0x29,
}

/// <summary>
/// Vertex component slots of a legacy MSV5 mesh, in table order. Slot 7 is the tangent with
/// handedness in its fourth element; slot 8 is a smoothed normal.
/// </summary>
public enum LegacyVertexSemantic
{
    Position = 0, TexCoord = 1, Normal = 2, BlendWeight = 3, BlendIndex = 4, Color = 5,
    SoftwareSkinning = 6, Tangent = 7, SmoothNormal = 8,
    ShadowMapTexCoord = 9, DetailMapTexCoord = 10, ScaledTexCoord = 11, LightmapTexCoord = 12,
}

/// <summary>Element types of a legacy vertex component.</summary>
public enum LegacyVertexType
{
    None = 0, Float = 1, SN8 = 2, UN8 = 3, SN16 = 4, UN16 = 5,

    /// <summary>Unnormalized bytes (blend indices).</summary>
    U8 = 8,

    Half = 11,
}

/// <summary>One component of a legacy mesh's vertex layout.</summary>
/// <param name="BufferIndex">Which of the mesh's two vertex buffers holds it.</param>
/// <param name="Offset">Byte offset inside the buffer's vertex.</param>
/// <param name="Count">Number of elements.</param>
public readonly record struct LegacyVertexComponent(
    LegacyVertexSemantic Semantic, int BufferIndex, int Offset, int Count, LegacyVertexType Type);

/// <summary>Where one attribute lives inside the vertex buffers.</summary>
public readonly record struct GfxAttributeParams(
    GfxVertexAttribute Attribute, GfxFormat Format, int AttributeIndex, int BufferIndex, int BufferOffset);

/// <summary>Where one attribute lives inside the vertex buffers, for v55.</summary>
public readonly record struct GfxPlatformAttributeParams(
    GfxVertexAttribute Attribute, GfxPlatformFormat Format, int AttributeIndex, int BufferIndex, int BufferOffset);

/// <summary>One GPU buffer: element count, stride, and offset into the bulk payload.</summary>
public readonly record struct GfxBufferInfo(int Count, int Stride, int DataOffset);

/// <summary>One draw batch of LOD 0.</summary>
/// <param name="DiffuseTextureIndex">Index into the mesh's texture list, -1 when untextured.</param>
/// <param name="BonePaletteIndex">Index into the mesh's bone palettes, -1 for static batches.</param>
public readonly record struct MeshBatch(
    int StartIndex, int PrimitiveCount, int MaterialIndex, int MinVertex, int MaxVertex,
    int DiffuseTextureIndex, int BonePaletteIndex);

/// <summary>
/// A parsed Telltale .d3dmesh: the legacy MSV5 layout, versions 22-51, and version 55.
/// Every block boundary is checked and the buffers must match the bulk data exactly.
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

    /// <summary>Symbols of referenced textures.</summary>
    public IReadOnlyList<ulong> TextureSymbols { get; private init; } = Array.Empty<ulong>();

    /// <summary>Material handle symbols, indexed by batch MaterialIndex.</summary>
    public IReadOnlyList<ulong> MaterialHandles { get; private init; } = Array.Empty<ulong>();

    /// <summary>Bone symbols this mesh is skinned to, empty for static meshes.</summary>
    public IReadOnlyList<ulong> BoneSymbols { get; private init; } = Array.Empty<ulong>();

    /// <summary>Bone palettes; batch <see cref="MeshBatch.BonePaletteIndex"/> selects one.</summary>
    public IReadOnlyList<IReadOnlyList<ulong>> BonePalettes { get; private init; } =
        Array.Empty<IReadOnlyList<ulong>>();

    /// <summary>Embedded material property sets: name symbol and raw payload.</summary>
    public IReadOnlyList<(ulong NameSymbol, byte[] Payload)> MaterialResources { get; private init; } =
        Array.Empty<(ulong, byte[])>();

    /// <summary>Vertex layout for versions 22-51. See <see cref="PlatformAttributes"/> for v55.</summary>
    public IReadOnlyList<GfxAttributeParams> Attributes { get; private init; } = Array.Empty<GfxAttributeParams>();

    /// <summary>Vertex layout for version 55. See <see cref="Attributes"/> for older meshes.</summary>
    public IReadOnlyList<GfxPlatformAttributeParams> PlatformAttributes { get; private init; } =
        Array.Empty<GfxPlatformAttributeParams>();

    /// <summary>
    /// Vertex layout of a legacy MSV5 mesh. Newer meshes use <see cref="Attributes"/> or
    /// <see cref="PlatformAttributes"/>.
    /// </summary>
    public IReadOnlyList<LegacyVertexComponent> LegacyComponents { get; private init; } =
        Array.Empty<LegacyVertexComponent>();

    /// <summary>Per-channel UV scales of a legacy mesh, empty for newer meshes.</summary>
    public IReadOnlyList<Vector2> LegacyUvScales { get; private init; } = Array.Empty<Vector2>();

    public GfxBufferInfo? IndexBuffer { get; private init; }
    public IReadOnlyList<GfxBufferInfo> VertexBuffers { get; private init; } = Array.Empty<GfxBufferInfo>();

    private byte[] _bufferData = Array.Empty<byte>();
    // v55 positions are UN16-quantized: pos = raw.xyz * scale + raw.w * wScale + offset.
    private Vector3 _positionScale = Vector3.One, _positionWScale, _positionOffset;
    // v55 batches add a base vertex to every index in their range.
    private int[] _batchBaseIndices = Array.Empty<int>();
    private readonly (Vector2 Scale, Vector2 Offset)[] _texCoordTransforms =
        { (Vector2.One, Vector2.Zero), (Vector2.One, Vector2.Zero),
          (Vector2.One, Vector2.Zero), (Vector2.One, Vector2.Zero) };

    private D3dMeshFile() { }

    /// <summary>Bytes of vertex and index data held.</summary>
    internal long DataSize => _bufferData.LongLength;

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
        if (version < 22 && meta.Format == MetaStreamFormat.Msv5)
            return ParseLegacy(file, meta, off, name, version);
        if (Bool())
            throw new InvalidDataException("mesh carries tool properties (unsupported)");
        if (version == 55)
            return ParseDefinitive(file, meta, off, name, version);
        if (version is < 22 or >= 52)
            throw new InvalidDataException($"D3DMesh version {version} is outside the supported tiers (MSV5 legacy, 22-51, 55).");

        // Internal resources. Keep embedded material property sets for texture lookup.
        ulong propertySetHash = TelltaleCrc64.HashSymbol("PropertySet");
        var materialResources = new List<(ulong NameSymbol, byte[] Payload)>();
        int resourceCount = I32();
        for (int i = 0; i < resourceCount; i++)
        {
            ulong resourceName = U64();
            ulong resourceType = U64();
            int resourceEnd = Block();
            if (resourceType == propertySetHash)
                materialResources.Add((resourceName, file[off..resourceEnd]));
            off = resourceEnd;
        }
        if (U32() != 0)
            SkipBlock(); // tool-only data

        // ---- T3MeshData --------------------------------------------------
        int meshEnd = Block();

        void Sphere() => SkipBlock();
        void Handle() => SkipBlock();
        // (slot, value) pairs ending at a slot with the high bit set. Slot 0 is the diffuse.
        int TexIndices()
        {
            int end = Block();
            int diffuse = -1;
            uint slot = U32();
            while ((slot & 0x80000000) == 0)
            {
                int value = I32();
                if (slot == 0)
                    diffuse = value;
                slot = U32();
            }
            Expect(end, "T3MeshTextureIndices");
            return diffuse;
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
                int diffuseIndex = TexIndices();
                int materialIndex = I32();
                int bonePalette = I32();
                U32();                        // local transform index
                if (l == 0)
                    batches.Add(new MeshBatch(startIndex, numPrims, materialIndex, minVert, maxVert, diffuseIndex, bonePalette));
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

        // mMaterials (the handle block carries the material's name symbol)
        var materialHandles = new List<ulong>();
        int matCount = ArrayHeader(out int matsEnd);
        for (int i = 0; i < matCount; i++)
        {
            int handleEnd = Block();
            materialHandles.Add(handleEnd - off >= 8 ? U64() : 0);
            off = handleEnd;
            U64(); off += 24; Sphere(); U32();
        }
        Expect(matsEnd, "mMaterials");

        // mMaterialOverrides
        int ovCount = ArrayHeader(out int ovsEnd);
        for (int i = 0; i < ovCount; i++) { Handle(); U32(); }
        Expect(ovsEnd, "mMaterialOverrides");

        // mBones and mLocalTransforms: an outer count, then per palette an inner count and entries.
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
        // Batches pick a palette; vertex blend indices index into it.
        var bonePalettes = new List<IReadOnlyList<ulong>>();
        {
            int end = Block();
            int outerCount = I32();
            for (int o = 0; o < outerCount; o++)
            {
                int innerCount = I32();
                var palette = new ulong[innerCount];
                for (int i = 0; i < innerCount; i++)
                {
                    palette[i] = U64();
                    off += 24; Sphere(); U32();
                }
                bonePalettes.Add(palette);
            }
            Expect(end, "bone palettes");
        }
        var boneSymbols = bonePalettes.SelectMany(p => p).Distinct().ToList();
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
            MaterialHandles = materialHandles,
            MaterialResources = materialResources,
            BoneSymbols = boneSymbols,
            BonePalettes = bonePalettes,
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
            throw new InvalidDataException("CPU skinning data present (unsupported)");

        // Vertex states: attribute layout and buffer descriptors.
        var attrs = new List<GfxAttributeParams>();
        var vbufs = new List<GfxBufferInfo>();
        GfxBufferInfo? ibuf = null;
        int dataOffset = 0;

        GfxBufferInfo ReadBuffer()
        {
            U32();                            // resource usage
            U32();                            // buffer format (stride gives index width)
            U32();                            // buffer usage
            int count = I32(), stride = I32();
            var info = new GfxBufferInfo(count, stride, dataOffset);
            dataOffset += count * stride;
            return info;
        }

        int stateCount = I32();
        // A mesh with no vertex states has no geometry.
        if (stateCount > 0 && (lodVertexStateIndex < 0 || lodVertexStateIndex >= stateCount))
            throw new InvalidDataException($"unexpected vertex state layout (index {lodVertexStateIndex} of {stateCount})");
        for (int s = 0; s < stateCount; s++)
        {
            // Only LOD 0's state is decoded, but the others still take up bulk data.
            bool used = s == lodVertexStateIndex;
            U32();                            // vertex count per instance
            int vbufCount = I32();
            int attrCount = I32();
            for (int a = 0; a < attrCount; a++)
            {
                var attr = new GfxAttributeParams(
                    (GfxVertexAttribute)I32(), (GfxFormat)I32(), I32(), I32(), I32());
                if (used) attrs.Add(attr);
            }
            if (Bool())
            {
                var ib = ReadBuffer();
                if (used) ibuf = ib;
            }
            for (int vb = 0; vb < vbufCount; vb++)
            {
                var buffer = ReadBuffer();
                if (used) vbufs.Add(buffer);
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
            MaterialHandles = mesh.MaterialHandles,
            MaterialResources = mesh.MaterialResources,
            BoneSymbols = mesh.BoneSymbols,
            BonePalettes = mesh.BonePalettes,
            Attributes = attrs,
            IndexBuffer = ibuf,
            VertexBuffers = vbufs,
            _bufferData = file.AsSpan(meta.AsyncSectionOffset, meta.AsyncSectionSize).ToArray(),
        }.CopyTransformsFrom(mesh);
    }

    /// <summary>
    /// Parses the rest of a version 55 mesh after the tool-props flag. Compared to 22-51, batches
    /// gain a base index and lose the bone palette, LODs carry their own bone list, mesh bones are
    /// a flat list, and vertex states list their index buffers.
    /// </summary>
    private static D3dMeshFile ParseDefinitive(byte[] file, MetaStreamHeader meta, int off, string name, int version)
    {
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
        int Block() { int start = off; int size = I32(); return start + size; }
        void SkipBlock() { off = Block(); }
        void Expect(int expected, string what)
        {
            if (off != expected) throw new InvalidDataException($"{what}: at {off}, expected {expected}");
        }
        Vector3 Vec3() => new(F32(), F32(), F32());
        void Sphere() => SkipBlock();
        void Handle() => SkipBlock();
        int ArrayHeader(out int end)
        {
            end = Block();
            return I32();
        }

        // ---- D3DMesh: lightmap scale, lightmap texcoord version, LOD param CRC
        F32(); I32(); U64();

        // Internal resources. Keep embedded material property sets for texture lookup.
        ulong propertySetHash = TelltaleCrc64.HashSymbol("PropertySet");
        var materialResources = new List<(ulong NameSymbol, byte[] Payload)>();
        int resourceCount = I32();
        for (int i = 0; i < resourceCount; i++)
        {
            ulong resourceName = U64();
            ulong resourceType = U64();
            int resourceEnd = Block();
            if (resourceType == propertySetHash)
                materialResources.Add((resourceName, file[off..resourceEnd]));
            off = resourceEnd;
        }
        if (U32() != 0)
            SkipBlock(); // tool-only data
        if (Bool())
            SkipBlock(); // occlusion mesh data

        // ---- T3MeshData --------------------------------------------------
        int meshEnd = Block();

        // (slot, value) pairs ending at a slot with the high bit set.
        int TexIndices()
        {
            int end = Block();
            int diffuse = -1;
            uint slot = U32();
            while ((slot & 0x80000000) == 0)
            {
                int value = I32();
                if (slot == 0)
                    diffuse = value;
                slot = U32();
            }
            Expect(end, "T3MeshTextureIndices");
            return diffuse;
        }

        // mLODs: each has default and shadow batches. Only LOD 0's default batches are used.
        var batches = new List<MeshBatch>();
        var baseIndices = new List<int>();
        var lodBones = new List<ulong>();
        int lodVertexStateIndex = 0;
        int lodCount = ArrayHeader(out int lodsEnd);
        for (int l = 0; l < lodCount; l++)
        {
            for (int kind = 0; kind < 2; kind++)
            {
                int batchCount = ArrayHeader(out int batchesEnd);
                for (int b = 0; b < batchCount; b++)
                {
                    off += 24; Sphere();      // bounding box + sphere
                    U32();                    // batch usage flags
                    int minVert = I32(), maxVert = I32();
                    int baseIndex = I32();
                    int startIndex = I32(), numPrims = I32();
                    I32();                    // index count
                    int diffuseIndex = TexIndices();
                    int materialIndex = I32();
                    I32();                    // adjacency start index
                    if (l == 0 && kind == 0)
                    {
                        batches.Add(new MeshBatch(startIndex, numPrims, materialIndex, minVert, maxVert, diffuseIndex, 0));
                        baseIndices.Add(baseIndex);
                    }
                }
                Expect(batchesEnd, "batches");
            }
            SkipBlock();                      // vertex streams bit set
            off += 24; Sphere();              // LOD bounds
            U32();                            // LOD flags
            int vstateIndex = I32();
            off += 6 * 4;                     // primitives, batches, vertex start/count, atlas width/height
            F32(); F32();                     // pixel size, distance
            int boneCount = ArrayHeader(out int bonesEnd);
            for (int i = 0; i < boneCount; i++)
            {
                ulong bone = U64();
                if (l == 0)
                    lodBones.Add(bone);
            }
            Expect(bonesEnd, "LOD bones");
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

        // mMaterials (the handle block carries the material's name symbol)
        var materialHandles = new List<ulong>();
        int matCount = ArrayHeader(out int matsEnd);
        for (int i = 0; i < matCount; i++)
        {
            int handleEnd = Block();
            materialHandles.Add(handleEnd - off >= 8 ? U64() : 0);
            off = handleEnd;
            U64(); U64();                     // base material name, legacy render texture property
            off += 24; Sphere(); U32();
        }
        Expect(matsEnd, "mMaterials");

        // mMaterialOverrides
        int ovCount = ArrayHeader(out int ovsEnd);
        for (int i = 0; i < ovCount; i++) { Handle(); U32(); }
        Expect(ovsEnd, "mMaterialOverrides");

        // mBones
        var boneSymbols = new List<ulong>();
        int boneEntryCount = ArrayHeader(out int boneEntriesEnd);
        for (int i = 0; i < boneEntryCount; i++)
        {
            boneSymbols.Add(U64());
            off += 24; Sphere(); U32();
        }
        Expect(boneEntriesEnd, "mBones");

        // mLocalTransforms
        int ltCount = ArrayHeader(out int ltsEnd);
        for (int i = 0; i < ltCount; i++) { SkipBlock(); U32(); }
        Expect(ltsEnd, "mLocalTransforms");

        SkipBlock();                          // material requirements
        SkipBlock();                          // vertex streams bit set
        var boundsMin = Vec3();
        var boundsMax = Vec3();
        Sphere();                             // bounding sphere
        U32();                                // endian type
        var positionScale = Vec3();
        var positionWScale = Vec3();
        var positionOffset = Vec3();
        F32();                                // lightmap texel area per surface area
        U64();                                // property key base
        int vertexCount = I32();
        uint meshFlags = U32();
        SkipBlock();                          // mesh effect preloads

        var texCoordTransforms = new (Vector2 Scale, Vector2 Offset)[]
            { (Vector2.One, Vector2.Zero), (Vector2.One, Vector2.Zero),
              (Vector2.One, Vector2.Zero), (Vector2.One, Vector2.Zero) };
        int txCount = I32();
        for (int i = 0; i < txCount; i++)
        {
            int channel = I32();
            var scale = new Vector2(F32(), F32());
            var offset = new Vector2(F32(), F32());
            if (channel is >= 0 and < 4)
                texCoordTransforms[channel] = (scale, offset);
        }

        if ((meshFlags & 0x10) != 0)
            throw new InvalidDataException("CPU skinning data present (unsupported)");

        // Vertex states: attributes, index buffers, vertex buffers. Bulk data is in the same order.
        var attrs = new List<GfxPlatformAttributeParams>();
        var vbufs = new List<GfxBufferInfo>();
        GfxBufferInfo? ibuf = null;
        int dataOffset = 0;

        GfxBufferInfo ReadBuffer()
        {
            U32();                            // resource usage
            U32();                            // buffer format (stride gives index width)
            U32();                            // buffer usage
            int count = I32(), stride = I32();
            var info = new GfxBufferInfo(count, stride, dataOffset);
            dataOffset += count * stride;
            return info;
        }

        int stateCount = I32();
        // A mesh with no vertex states has no geometry.
        if (stateCount > 0 && (lodVertexStateIndex < 0 || lodVertexStateIndex >= stateCount))
            throw new InvalidDataException($"unexpected vertex state layout (index {lodVertexStateIndex} of {stateCount})");
        for (int s = 0; s < stateCount; s++)
        {
            bool used = s == lodVertexStateIndex;
            U32();                            // vertex count per instance
            int ibufCount = I32();
            int vbufCount = I32();
            int attrCount = I32();
            for (int a = 0; a < attrCount; a++)
            {
                var attr = new GfxPlatformAttributeParams(
                    (GfxVertexAttribute)I32(), (GfxPlatformFormat)I32(), I32(), I32(), I32());
                if (used) attrs.Add(attr);
            }
            // Buffer 0 matches the default batches. Buffer 1 is a shadow-only copy.
            for (int ib = 0; ib < ibufCount; ib++)
            {
                var buffer = ReadBuffer();
                if (used && ib == 0)
                    ibuf = buffer;
            }
            for (int vb = 0; vb < vbufCount; vb++)
            {
                var buffer = ReadBuffer();
                if (used) vbufs.Add(buffer);
            }
        }
        Expect(meshEnd, "T3MeshData");
        Expect(sectionEnd, "default section");

        if (dataOffset != meta.AsyncSectionSize)
            throw new InvalidDataException(
                $"buffer sizes disagree with bulk data: buffers say {dataOffset}, async section is {meta.AsyncSectionSize}");

        var mesh = new D3dMeshFile
        {
            Name = name,
            Version = version,
            VertexCount = vertexCount,
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
            Batches = batches,
            TextureSymbols = textureSymbols,
            MaterialHandles = materialHandles,
            MaterialResources = materialResources,
            BoneSymbols = boneSymbols,
            BonePalettes = new[] { lodBones },
            PlatformAttributes = attrs,
            IndexBuffer = ibuf,
            VertexBuffers = vbufs,
            _bufferData = file.AsSpan(meta.AsyncSectionOffset, meta.AsyncSectionSize).ToArray(),
            _positionScale = positionScale,
            _positionWScale = positionWScale,
            _positionOffset = positionOffset,
            _batchBaseIndices = baseIndices.ToArray(),
        };
        for (int i = 0; i < 4; i++)
            mesh._texCoordTransforms[i] = texCoordTransforms[i];
        return mesh;
    }

    /// <summary>
    /// Parses the rest of a legacy MSV5 mesh after the version. There are no material resources,
    /// and the index buffer and two vertex buffers sit inline in the default section.
    /// </summary>
    private static D3dMeshFile ParseLegacy(byte[] file, MetaStreamHeader meta, int off, string name, int version)
    {
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
        int Block() { int start = off; int size = I32(); return start + size; }
        void SkipBlock() { off = Block(); }
        void Expect(int expected, string what)
        {
            if (off != expected) throw new InvalidDataException($"{what}: at {off}, expected {expected}");
        }
        Vector3 Vec3() => new(F32(), F32(), F32());
        void Sphere() => SkipBlock();
        int ArrayHeader(out int end)
        {
            end = Block();
            return I32();
        }

        uint meshFlags = U32();
        var boundsMin = Vec3();
        var boundsMax = Vec3();
        Sphere();

        // mTriangleSets: the draw batches. mTxIndex has one index per texture slot (0 diffuse,
        // 2 normal map) into that slot's list in mTextures. Slot count varies by game.
        var batches = new List<MeshBatch>();
        var batchSlots = new List<int[]>();
        int setCount = ArrayHeader(out int setsEnd);
        for (int i = 0; i < setCount; i++)
        {
            uint setFlags = U32();
            if ((setFlags & LegacyTriangleStripsFlag) != 0)
                throw new InvalidDataException("triangle-strip batches (unsupported)");
            int bonePalette = I32();
            I32(); I32();                     // local transform palette + index
            int minVert = I32(), maxVert = I32();
            int startIndex = I32(), numPrims = I32();
            off += 24; Sphere();              // bounding box + sphere
            int slotsEnd = Block();
            var slots = new int[(slotsEnd - off) / 4];
            for (int s = 0; s < slots.Length; s++)
                slots[s] = I32();
            Expect(slotsEnd, "texture slot indices");
            SkipBlock();                      // triangle strips
            I32();                            // total index count
            // Shading constants, skipped.
            off += LegacyTriangleSetShadingSize;
            batches.Add(new MeshBatch(startIndex, numPrims, -1, minVert, maxVert, -1, bonePalette));
            batchSlots.Add(slots);
        }
        Expect(setsEnd, "triangle sets");

        SkipBlock();                          // vertex animations

        // mBonePalettes: batches pick a palette; vertex blend indices index into it.
        var bonePalettes = new List<IReadOnlyList<ulong>>();
        {
            int outerCount = ArrayHeader(out int end);
            for (int o = 0; o < outerCount; o++)
            {
                int innerCount = I32();
                var palette = new ulong[innerCount];
                for (int i = 0; i < innerCount; i++)
                {
                    palette[i] = U64();
                    off += 24; Sphere(); I32();   // bounds, vertex count
                }
                bonePalettes.Add(palette);
            }
            Expect(end, "bone palettes");
        }
        SkipBlock();                          // skinning data
        SkipBlock();                          // bone data
        SkipBlock();                          // local transform palettes
        I32();                                // triangle strip state
        I32();                                // animated vertex count

        // mTextures: one list per texture slot. Each entry ends with one or two texel densities
        // depending on the game, so try both and keep the one that fits the block.
        int texturesStart = off;
        int texturesEnd = Block();
        List<List<ulong>>? ReadTextures(int densities)
        {
            off = texturesStart + 4;
            var lists = new List<List<ulong>>();
            while (off < texturesEnd)
            {
                int count = I32();
                if (count < 0 || count > (texturesEnd - off) / 40)
                    return null;
                var list = new List<ulong>(count);
                for (int i = 0; i < count; i++)
                {
                    int handleEnd = Block();
                    if (handleEnd <= off || handleEnd > texturesEnd)
                        return null;
                    list.Add(handleEnd - off >= 8 ? U64() : 0);
                    off = handleEnd;
                    U32(); U64();             // flags, name symbol
                    off += 24;                // bounding box
                    int sphereEnd = Block();
                    if (sphereEnd <= off || sphereEnd > texturesEnd)
                        return null;
                    off = sphereEnd + densities * 4;
                }
                lists.Add(list);
            }
            return off == texturesEnd ? lists : null;
        }
        var slotTextures = ReadTextures(2) ?? ReadTextures(1)
            ?? throw new InvalidDataException("texture lists do not match their block");

        // UV scales: 4 or more depending on the game. Count them by finding where the two tool
        // blocks and the tool-props flag line up.
        bool ToolTailAt(int at)
        {
            for (int b = 0; b < 2; b++)
            {
                if (at + 4 > sectionEnd) return false;
                int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
                if (size < 8 || size > sectionEnd - at) return false;
                at += size;
            }
            return at < sectionEnd && file[at] is (byte)'0' or (byte)'1';
        }
        int uvScales = 4;
        while (uvScales < 6 && !ToolTailAt(off + uvScales * 8))
            uvScales++;
        var legacyUvScales = new Vector2[uvScales];
        for (int s = 0; s < uvScales; s++)
            legacyUvScales[s] = new Vector2(F32(), F32());
        SkipBlock();                          // tool animated vertex entries
        SkipBlock();                          // tool animated vertex groups
        if (Bool())
            throw new InvalidDataException("mesh carries tool properties (unsupported)");

        // The diffuse slot comes first, so a batch's diffuse index is its slot 0 index.
        var textureSymbols = slotTextures.SelectMany(l => l).ToList();
        int diffuseCount = slotTextures.Count > 0 ? slotTextures[0].Count : 0;
        for (int b = 0; b < batches.Count; b++)
        {
            int diffuse = batchSlots[b].Length > 0 ? batchSlots[b][0] : -1;
            if (diffuse >= diffuseCount)
                throw new InvalidDataException($"batch {b} diffuse index {diffuse} out of range");
            batches[b] = batches[b] with { DiffuseTextureIndex = diffuse };
        }

        // Inline buffers: index buffer, then two vertex buffers, each a header and data.
        int bulkStart = off;
        int indexFormat = I32();
        int indexCount = I32();
        U32(); U32();                         // flags, usage
        GfxBufferInfo? ibuf = null;
        if ((meshFlags & LegacyHasIndexBufferFlag) != 0)
        {
            int stride = indexFormat switch
            {
                LegacyIndexFormat16 => 2,
                LegacyIndexFormat32 => 4,
                _ => throw new InvalidDataException($"unknown index format {indexFormat}"),
            };
            ibuf = new GfxBufferInfo(indexCount, stride, off - bulkStart);
            off += indexCount * stride;
        }

        bool IsComponentTable(int at)
        {
            if (at + 4 > sectionEnd) return false;
            int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
            return size >= 4 + 12 && size <= 4 + 12 * 32 && (size - 4) % 12 == 0;
        }
        var vbufs = new List<GfxBufferInfo>();
        var components = new List<LegacyVertexComponent>();
        // Game of Thrones always writes both buffers; The Wolf Among Us only the first.
        for (int vb = 0; vb < 2 && off < sectionEnd; vb++)
        {
            int vertexCount = I32();
            int vertexSize = I32();
            U32(); U32();                     // flags, usage
            // The Wolf Among Us has one more header field before the component table.
            if (!IsComponentTable(off))
                U32();
            int componentsEnd = Block();
            for (int c = 0; off < componentsEnd; c++)
            {
                int componentOffset = I32(), componentCount = I32(), componentType = I32();
                if (componentType != 0)
                    components.Add(new LegacyVertexComponent(
                        (LegacyVertexSemantic)c, vb, componentOffset, componentCount, (LegacyVertexType)componentType));
            }
            Expect(componentsEnd, "vertex components");
            if (vertexCount < 0 || vertexSize < 0 || (long)vertexCount * vertexSize > sectionEnd - off)
                throw new InvalidDataException("vertex buffer exceeds the section");
            vbufs.Add(new GfxBufferInfo(vertexCount, vertexSize, off - bulkStart));
            off += vertexCount * vertexSize;
        }
        Expect(sectionEnd, "default section");
        if (meta.AsyncSectionSize != 0)
            throw new InvalidDataException($"unexpected bulk section ({meta.AsyncSectionSize} bytes) in a legacy mesh");
        if (vbufs.Count == 0)
            throw new InvalidDataException("mesh has no vertex buffer");
        if (vbufs.Count > 1 && vbufs[1].Count != 0 && vbufs[1].Count != vbufs[0].Count)
            throw new InvalidDataException($"vertex buffers disagree on vertex count ({vbufs[0].Count} vs {vbufs[1].Count})");

        return new D3dMeshFile
        {
            Name = name,
            Version = version,
            VertexCount = vbufs[0].Count,
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
            Batches = batches,
            TextureSymbols = textureSymbols,
            BoneSymbols = bonePalettes.SelectMany(p => p).Distinct().ToList(),
            BonePalettes = bonePalettes,
            LegacyComponents = components,
            LegacyUvScales = legacyUvScales,
            IndexBuffer = ibuf,
            VertexBuffers = vbufs,
            _bufferData = file.AsSpan(bulkStart, sectionEnd - bulkStart).ToArray(),
        };
    }

    private const uint LegacyHasIndexBufferFlag = 0x1;
    private const uint LegacyTriangleStripsFlag = 0x10000;
    private const int LegacyIndexFormat16 = 101, LegacyIndexFormat32 = 102; // D3DFMT_INDEX16 / INDEX32
    // 3 floats, 4 colors, 5 floats, 7 ints/floats, 1 color.
    private const int LegacyTriangleSetShadingSize = 3 * 4 + 4 * 16 + 5 * 4 + 7 * 4 + 16;

    private D3dMeshFile CopyTransformsFrom(D3dMeshFile other)
    {
        for (int i = 0; i < 4; i++)
            _texCoordTransforms[i] = other._texCoordTransforms[i];
        return this;
    }

    // ---- Geometry decoding ----------------------------------------------

    /// <summary>
    /// The vertices' texture coordinates of set <paramref name="set"/>, matching
    /// <see cref="GetGeometry"/>, or null when the mesh has no such set.
    /// </summary>
    public Vector2[]? GetTexCoords(int set)
    {
        if (VertexBuffers.Count == 0 || set < 0)
            return null;
        if (LegacyComponents.Count > 0)
            return GetLegacyTexCoords(set);
        if (PlatformAttributes.Count == 0)
            return GetMidTexCoords(set);
        var attr = PlatformAttributes.FirstOrDefault(a => a.Attribute == GfxVertexAttribute.TexCoord && a.AttributeIndex == set);
        if (attr.Attribute != GfxVertexAttribute.TexCoord || attr.AttributeIndex != set ||
            attr.BufferIndex < 0 || attr.BufferIndex >= VertexBuffers.Count)
            return null;
        var (scale, offset) = set < _texCoordTransforms.Length ? _texCoordTransforms[set] : (Vector2.One, Vector2.Zero);
        var buffer = VertexBuffers[attr.BufferIndex];
        int count = VertexBuffers[0].Count;
        var uvs = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int at = buffer.DataOffset + i * buffer.Stride + attr.BufferOffset;
            Vector2 raw = attr.Format switch
            {
                GfxPlatformFormat.F32x2 => new Vector2(BitConverter.ToSingle(_bufferData, at), BitConverter.ToSingle(_bufferData, at + 4)),
                GfxPlatformFormat.SN16x2 => new Vector2(Sn16(at), Sn16(at + 2)),
                GfxPlatformFormat.UN16x2 => new Vector2(Un16(at), Un16(at + 2)),
                _ => throw new InvalidDataException($"unsupported texcoord format {attr.Format}"),
            };
            uvs[i] = raw * scale + offset;
        }
        return uvs;
    }

    /// <summary>
    /// The vertices' RGBA colors of set <paramref name="set"/>, matching <see cref="GetGeometry"/>,
    /// or null when there's no such set or the mesh is legacy.
    /// </summary>
    public Vector4[]? GetVertexColors(int set)
    {
        if (VertexBuffers.Count == 0 || set < 0 || LegacyComponents.Count > 0)
            return null;
        int bufferIndex, bufferOffset;
        if (PlatformAttributes.Count > 0)
        {
            var attr = PlatformAttributes.FirstOrDefault(a => a.Attribute == GfxVertexAttribute.Color && a.AttributeIndex == set);
            if (attr.Attribute != GfxVertexAttribute.Color || attr.AttributeIndex != set || attr.Format != GfxPlatformFormat.UN8x4)
                return null;
            (bufferIndex, bufferOffset) = (attr.BufferIndex, attr.BufferOffset);
        }
        else
        {
            var attr = Attributes.FirstOrDefault(a => a.Attribute == GfxVertexAttribute.Color && a.AttributeIndex == set);
            if (attr.Attribute != GfxVertexAttribute.Color || attr.AttributeIndex != set || attr.Format != GfxFormat.UN8x4)
                return null;
            (bufferIndex, bufferOffset) = (attr.BufferIndex, attr.BufferOffset);
        }
        if (bufferIndex < 0 || bufferIndex >= VertexBuffers.Count)
            return null;
        var buffer = VertexBuffers[bufferIndex];
        int count = VertexBuffers[0].Count;
        if (buffer.Count < count || buffer.DataOffset + (long)(count - 1) * buffer.Stride + bufferOffset + 4 > _bufferData.Length)
            return null;
        var colors = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            int at = buffer.DataOffset + i * buffer.Stride + bufferOffset;
            colors[i] = new Vector4(_bufferData[at], _bufferData[at + 1], _bufferData[at + 2], _bufferData[at + 3]) / 255f;
        }
        return colors;
    }

    // Legacy meshes: set 0 is the color UVs, set 1 the detail map, each with its own scale.
    private Vector2[]? GetLegacyTexCoords(int set)
    {
        var semantic = set switch
        {
            0 => LegacyVertexSemantic.TexCoord,
            1 => LegacyVertexSemantic.DetailMapTexCoord,
            _ => (LegacyVertexSemantic?)null,
        };
        var component = LegacyComponents.FirstOrDefault(c => c.Semantic == semantic);
        if (semantic is null || component.Semantic != semantic || component.BufferIndex >= VertexBuffers.Count)
            return null;
        var buffer = VertexBuffers[component.BufferIndex];
        int count = VertexCount;
        if (buffer.Count != count)
            return null;
        var scale = set < LegacyUvScales.Count ? LegacyUvScales[set] : Vector2.One;
        var uvs = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int at = buffer.DataOffset + i * buffer.Stride + component.Offset;
            uvs[i] = new Vector2(LegacyElement(component.Type, at, 0), LegacyElement(component.Type, at, 1)) * scale;
        }
        return uvs;
    }

    // Versions 22-51.
    private Vector2[]? GetMidTexCoords(int set)
    {
        var attr = Attributes.FirstOrDefault(a => a.Attribute == GfxVertexAttribute.TexCoord && a.AttributeIndex == set);
        if (attr.Attribute != GfxVertexAttribute.TexCoord || attr.AttributeIndex != set ||
            attr.BufferIndex < 0 || attr.BufferIndex >= VertexBuffers.Count)
            return null;
        var (scale, offset) = set < _texCoordTransforms.Length ? _texCoordTransforms[set] : (Vector2.One, Vector2.Zero);
        var buffer = VertexBuffers[attr.BufferIndex];
        int count = VertexBuffers[0].Count;
        var uvs = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int at = buffer.DataOffset + i * buffer.Stride + attr.BufferOffset;
            Vector2 raw = attr.Format switch
            {
                GfxFormat.SN16x2 => new Vector2(Sn16(at), Sn16(at + 2)),
                GfxFormat.F32x2 => new Vector2(BitConverter.ToSingle(_bufferData, at), BitConverter.ToSingle(_bufferData, at + 4)),
                _ => throw new InvalidDataException($"unsupported texcoord format {attr.Format}"),
            };
            uvs[i] = raw * scale + offset;
        }
        return uvs;
    }

    /// <summary>
    /// Decodes the vertex data into positions, normals and UV set 0. UVs stay in D3D convention
    /// with v=0 at the top.
    /// </summary>
    public MeshGeometry GetGeometry()
    {
        if (IndexBuffer is null && VertexBuffers.Count == 0 && LegacyComponents.Count == 0)
            return new MeshGeometry(Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(),
                Array.Empty<Vector4>(), Array.Empty<int>(), Array.Empty<MeshBatch>(), BoundsMin, BoundsMax);
        if (PlatformAttributes.Count > 0)
            return GetDefinitiveGeometry();
        if (LegacyComponents.Count > 0)
            return GetLegacyGeometry();

        int[] indices = ReadIndices();

        int count = VertexBuffers.Count > 0 ? VertexBuffers[0].Count : 0;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        var tangents = new Vector4[count];
        var blendWeights = new Vector4[count];
        var blendIndices = new (byte A, byte B, byte C, byte D)[count];
        bool hasWeights = false, hasIndices = false;

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
                    case GfxVertexAttribute.Tangent:
                        tangents[i] = new Vector4(Sn16(at), Sn16(at + 2), Sn16(at + 4), Sn16(at + 6));
                        break;
                    case GfxVertexAttribute.BlendWeight:
                        hasWeights = true;
                        blendWeights[i] = new Vector4(Sn16(at), Sn16(at + 2), Sn16(at + 4), Sn16(at + 6));
                        break;
                    case GfxVertexAttribute.BlendIndex:
                        // Stored premultiplied by 3 for D3D9 constant-row addressing.
                        hasIndices = true;
                        blendIndices[i] = (
                            (byte)(_bufferData[at] / 3), (byte)(_bufferData[at + 1] / 3),
                            (byte)(_bufferData[at + 2] / 3), (byte)(_bufferData[at + 3] / 3));
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
                        uvs[i] = raw * scale + offset;
                        break;
                }
            }
        }

        // Rigid parts store only blend indices, so each vertex follows its first bone.
        if (hasIndices && !hasWeights)
        {
            Array.Fill(blendWeights, new Vector4(1, 0, 0, 0));
            hasWeights = true;
        }

        bool skinned = hasWeights && hasIndices;
        return new MeshGeometry(
            positions, normals, uvs, tangents, indices, Batches, BoundsMin, BoundsMax,
            skinned ? blendWeights : null, skinned ? blendIndices : null, BonePalettes);
    }

    private int[] ReadIndices()
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
        return indices;
    }

    /// <summary>
    /// v55 geometry. Blend indices point straight into the LOD's bone list (bone palette 0).
    /// Blend weights are sorted largest first, each stored as a fraction of its max share:
    /// the second (up to 1/2) uses 12 bits (the top 2 bits over the first 10), the third and
    /// fourth (up to 1/3 and 1/4) 10 bits each, and the first is the remainder.
    /// </summary>
    private MeshGeometry GetDefinitiveGeometry()
    {
        int[] indices = ReadIndices();
        for (int b = 0; b < Batches.Count; b++)
        {
            int baseIndex = _batchBaseIndices[b];
            if (baseIndex == 0)
                continue;
            var batch = Batches[b];
            int end = Math.Min(indices.Length, batch.StartIndex + batch.PrimitiveCount * 3);
            for (int i = batch.StartIndex; i < end; i++)
                indices[i] += baseIndex;
        }

        int count = VertexBuffers.Count > 0 ? VertexBuffers[0].Count : 0;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        var tangents = new Vector4[count];
        var blendWeights = new Vector4[count];
        var blendIndices = new (byte A, byte B, byte C, byte D)[count];
        bool hasWeights = false, hasIndices = false;

        foreach (var attr in PlatformAttributes)
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
                        positions[i] = attr.Format switch
                        {
                            GfxPlatformFormat.UN16x4 =>
                                new Vector3(Un16(at), Un16(at + 2), Un16(at + 4)) * _positionScale +
                                Un16(at + 6) * _positionWScale + _positionOffset,
                            GfxPlatformFormat.F32x3 => new Vector3(
                                BitConverter.ToSingle(_bufferData, at),
                                BitConverter.ToSingle(_bufferData, at + 4),
                                BitConverter.ToSingle(_bufferData, at + 8)),
                            // Three 10-bit fractions of the bounds plus a 2-bit W step.
                            GfxPlatformFormat.UN10x3_UN2 => Un10x3Position(
                                BinaryPrimitives.ReadUInt32LittleEndian(_bufferData.AsSpan(at))),
                            _ => throw new InvalidDataException($"unsupported position format {attr.Format}"),
                        };
                        break;
                    case GfxVertexAttribute.Normal when attr.Format == GfxPlatformFormat.SN8x4:
                        var n = new Vector3(Sn8(at), Sn8(at + 1), Sn8(at + 2));
                        normals[i] = n == Vector3.Zero ? n : Vector3.Normalize(n);
                        break;
                    case GfxVertexAttribute.Tangent when attr.Format == GfxPlatformFormat.SN8x4:
                        var t = new Vector3(Sn8(at), Sn8(at + 1), Sn8(at + 2));
                        tangents[i] = new Vector4(
                            t == Vector3.Zero ? t : Vector3.Normalize(t),
                            (sbyte)_bufferData[at + 3] < 0 ? -1f : 1f);
                        break;
                    case GfxVertexAttribute.BlendWeight when attr.Format == GfxPlatformFormat.UN10x3_UN2:
                        hasWeights = true;
                        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(_bufferData.AsSpan(at));
                        float w1 = ((packed >> 30) << 10 | (packed & 0x3FF)) / 4095f / 2f;
                        float w2 = ((packed >> 10) & 0x3FF) / 1023f / 3f;
                        float w3 = ((packed >> 20) & 0x3FF) / 1023f / 4f;
                        blendWeights[i] = new Vector4(Math.Max(0f, 1f - w1 - w2 - w3), w1, w2, w3);
                        break;
                    case GfxVertexAttribute.BlendIndex when attr.Format == GfxPlatformFormat.U8x4:
                        hasIndices = true;
                        blendIndices[i] = (_bufferData[at], _bufferData[at + 1], _bufferData[at + 2], _bufferData[at + 3]);
                        break;
                    case GfxVertexAttribute.TexCoord:
                        var (scale, offset) = _texCoordTransforms[0];
                        Vector2 raw = attr.Format switch
                        {
                            GfxPlatformFormat.F32x2 => new Vector2(
                                BitConverter.ToSingle(_bufferData, at),
                                BitConverter.ToSingle(_bufferData, at + 4)),
                            GfxPlatformFormat.SN16x2 => new Vector2(Sn16(at), Sn16(at + 2)),
                            GfxPlatformFormat.UN16x2 => new Vector2(Un16(at), Un16(at + 2)),
                            _ => throw new InvalidDataException($"unsupported texcoord format {attr.Format}"),
                        };
                        uvs[i] = raw * scale + offset;
                        break;
                }
            }
        }

        // Rigid parts store only blend indices, so each vertex follows its first bone.
        if (hasIndices && !hasWeights)
        {
            Array.Fill(blendWeights, new Vector4(1, 0, 0, 0));
            hasWeights = true;
        }

        bool skinned = hasWeights && hasIndices;
        return new MeshGeometry(
            positions, normals, uvs, tangents, indices, Batches, BoundsMin, BoundsMax,
            skinned ? blendWeights : null, skinned ? blendIndices : null, BonePalettes);
    }

    /// <summary>Legacy MSV5 geometry, read through each component's buffer, offset and type.</summary>
    private MeshGeometry GetLegacyGeometry()
    {
        int[] indices = ReadIndices();
        var diffuseUvScale = LegacyUvScales.Count > 0 ? LegacyUvScales[0] : Vector2.One;

        int count = VertexCount;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uvs = new Vector2[count];
        var tangents = new Vector4[count];
        var blendWeights = new Vector4[count];
        var blendIndices = new (byte A, byte B, byte C, byte D)[count];
        bool hasWeights = false, hasIndices = false;

        foreach (var component in LegacyComponents)
        {
            var buffer = VertexBuffers[component.BufferIndex];
            if (buffer.Count != count)
                continue;
            if (component.Offset + component.Count * LegacyElementSize(component.Type) > buffer.Stride)
                throw new InvalidDataException($"{component.Semantic} component overruns its {buffer.Stride}-byte vertex");
            var type = component.Type;
            for (int i = 0; i < count; i++)
            {
                int at = buffer.DataOffset + i * buffer.Stride + component.Offset;
                float E(int index) => index < component.Count ? LegacyElement(type, at, index) : 0f;
                switch (component.Semantic)
                {
                    case LegacyVertexSemantic.Position:
                        positions[i] = new Vector3(E(0), E(1), E(2));
                        break;
                    case LegacyVertexSemantic.Normal:
                        var n = new Vector3(E(0), E(1), E(2));
                        normals[i] = n == Vector3.Zero ? n : Vector3.Normalize(n);
                        break;
                    case LegacyVertexSemantic.Tangent:
                        var t = new Vector3(E(0), E(1), E(2));
                        tangents[i] = new Vector4(t == Vector3.Zero ? t : Vector3.Normalize(t), E(3) < 0 ? -1f : 1f);
                        break;
                    case LegacyVertexSemantic.TexCoord:
                        // UVs are normalized, so scale them back with the diffuse UV scale.
                        uvs[i] = new Vector2(E(0), E(1)) * diffuseUvScale;
                        break;
                    case LegacyVertexSemantic.BlendWeight:
                        hasWeights = true;
                        // With three stored weights, the fourth is implied.
                        var w = new Vector4(E(0), E(1), E(2), E(3));
                        if (component.Count == 3)
                            w.W = Math.Max(0f, 1f - w.X - w.Y - w.Z);
                        blendWeights[i] = w;
                        break;
                    case LegacyVertexSemantic.BlendIndex when type == LegacyVertexType.U8:
                        // Premultiplied by 3, same as newer meshes.
                        hasIndices = true;
                        blendIndices[i] = ((byte)(E(0) / 3), (byte)(E(1) / 3), (byte)(E(2) / 3), (byte)(E(3) / 3));
                        break;
                }
            }
        }

        // Rigid parts store only blend indices, so each vertex follows its first bone.
        if (hasIndices && !hasWeights)
        {
            Array.Fill(blendWeights, new Vector4(1, 0, 0, 0));
            hasWeights = true;
        }

        bool skinned = hasWeights && hasIndices;
        return new MeshGeometry(
            positions, normals, uvs, tangents, indices, Batches, BoundsMin, BoundsMax,
            skinned ? blendWeights : null, skinned ? blendIndices : null, BonePalettes);
    }

    private static int LegacyElementSize(LegacyVertexType type) => type switch
    {
        LegacyVertexType.Float => 4,
        LegacyVertexType.SN16 or LegacyVertexType.UN16 or LegacyVertexType.Half => 2,
        LegacyVertexType.SN8 or LegacyVertexType.UN8 or LegacyVertexType.U8 => 1,
        _ => throw new InvalidDataException($"unknown vertex component type {(int)type}"),
    };

    /// <summary>Element <paramref name="index"/> of a legacy component, as a float.</summary>
    private float LegacyElement(LegacyVertexType type, int at, int index) => type switch
    {
        LegacyVertexType.Float => BitConverter.ToSingle(_bufferData, at + index * 4),
        LegacyVertexType.SN16 => Sn16(at + index * 2),
        LegacyVertexType.UN16 => Un16(at + index * 2),
        LegacyVertexType.Half => (float)BitConverter.ToHalf(_bufferData, at + index * 2),
        LegacyVertexType.SN8 => Sn8(at + index),
        LegacyVertexType.UN8 => _bufferData[at + index] / 255f,
        LegacyVertexType.U8 => _bufferData[at + index],
        _ => throw new InvalidDataException($"unknown vertex component type {(int)type}"),
    };

    private float Sn16(int at) =>
        Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(_bufferData.AsSpan(at)) / 32767f);

    private Vector3 Un10x3Position(uint p) =>
        new Vector3((p & 0x3FF) / 1023f, ((p >> 10) & 0x3FF) / 1023f, ((p >> 20) & 0x3FF) / 1023f) * _positionScale +
        (p >> 30) / 3f * _positionWScale + _positionOffset;

    private float Un16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(_bufferData.AsSpan(at)) / 65535f;

    private float Sn8(int at) => Math.Max(-1f, (sbyte)_bufferData[at] / 127f);
}

/// <summary>Decoded LOD 0 mesh data. Tangent W holds handedness.</summary>
public sealed record MeshGeometry(
    Vector3[] Positions, Vector3[] Normals, Vector2[] TexCoords, Vector4[] Tangents,
    int[] Indices, IReadOnlyList<MeshBatch> Batches,
    Vector3 BoundsMin, Vector3 BoundsMax,
    Vector4[]? BlendWeights = null, (byte A, byte B, byte C, byte D)[]? BlendIndices = null,
    IReadOnlyList<IReadOnlyList<ulong>>? BonePalettes = null)
{
    public bool IsSkinned => BlendWeights is not null && BlendIndices is not null;

    /// <summary>
    /// The bone palette each vertex uses (-1 for none), taken from the batch whose triangles
    /// draw it. Batch vertex ranges can overlap, so ranges are only a fallback.
    /// </summary>
    public int[] VertexPaletteIndices()
    {
        var result = new int[Positions.Length];
        Array.Fill(result, -1);
        int paletteCount = BonePalettes?.Count ?? 0;
        foreach (var batch in Batches)
        {
            if (batch.BonePaletteIndex < 0 || batch.BonePaletteIndex >= paletteCount)
                continue;
            int hi = Math.Min(Positions.Length - 1, batch.MaxVertex);
            for (int v = Math.Max(0, batch.MinVertex); v <= hi; v++)
                result[v] = batch.BonePaletteIndex;
        }

        foreach (var batch in Batches)
        {
            if (batch.BonePaletteIndex >= paletteCount)
                continue;
            int end = Math.Min(Indices.Length, batch.StartIndex + batch.PrimitiveCount * 3);
            for (int i = Math.Max(0, batch.StartIndex); i < end; i++)
            {
                if ((uint)Indices[i] < (uint)result.Length)
                    result[Indices[i]] = batch.BonePaletteIndex;
            }
        }
        return result;
    }

    /// <summary>A copy of this unskinned mesh with every vertex bound to one bone.</summary>
    public MeshGeometry BindRigid(ulong boneSymbol)
    {
        var weights = new Vector4[Positions.Length];
        Array.Fill(weights, new Vector4(1, 0, 0, 0));
        return this with
        {
            BlendWeights = weights,
            BlendIndices = new (byte, byte, byte, byte)[Positions.Length],
            BonePalettes = new IReadOnlyList<ulong>[] { new[] { boneSymbol } },
            Batches = Batches.Select(b => b with { BonePaletteIndex = 0 }).ToList(),
        };
    }

    /// <summary>
    /// Merges several meshes into one. Unskinned parts stay at rest unless bound with
    /// <see cref="BindRigid"/> first.
    /// </summary>
    public static MeshGeometry Combine(IReadOnlyList<MeshGeometry> parts)
    {
        if (parts.Count == 1)
            return parts[0];
        if (parts.Count == 0)
            throw new ArgumentException("nothing to combine", nameof(parts));

        bool skinned = parts.Any(p => p.IsSkinned && p.BonePalettes is not null);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tangents = new List<Vector4>();
        var indices = new List<int>();
        var batches = new List<MeshBatch>();
        var weights = new List<Vector4>();
        var blendIndices = new List<(byte, byte, byte, byte)>();
        var palettes = new List<IReadOnlyList<ulong>>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var part in parts)
        {
            int vertexBase = positions.Count, indexBase = indices.Count, paletteBase = palettes.Count;
            positions.AddRange(part.Positions);
            normals.AddRange(part.Normals);
            uvs.AddRange(part.TexCoords);
            tangents.AddRange(part.Tangents);
            indices.AddRange(part.Indices.Select(i => i + vertexBase));
            bool partSkinned = part.IsSkinned && part.BonePalettes is not null;
            foreach (var b in part.Batches)
                batches.Add(b with
                {
                    StartIndex = b.StartIndex + indexBase,
                    MinVertex = b.MinVertex + vertexBase,
                    MaxVertex = b.MaxVertex + vertexBase,
                    BonePaletteIndex = skinned && !partSkinned ? -1
                        : b.BonePaletteIndex < 0 ? b.BonePaletteIndex : b.BonePaletteIndex + paletteBase,
                });
            if (skinned)
            {
                if (partSkinned)
                {
                    weights.AddRange(part.BlendWeights!);
                    blendIndices.AddRange(part.BlendIndices!);
                }
                else
                {
                    weights.AddRange(new Vector4[part.Positions.Length]);
                    blendIndices.AddRange(new (byte, byte, byte, byte)[part.Positions.Length]);
                }
            }
            if (part.BonePalettes is not null && (!skinned || partSkinned))
                palettes.AddRange(part.BonePalettes);
            min = Vector3.Min(min, part.BoundsMin);
            max = Vector3.Max(max, part.BoundsMax);
        }

        return new MeshGeometry(
            positions.ToArray(), normals.ToArray(), uvs.ToArray(), tangents.ToArray(),
            indices.ToArray(), batches, min, max,
            skinned ? weights.ToArray() : null, skinned ? blendIndices.ToArray() : null, palettes);
    }
}
