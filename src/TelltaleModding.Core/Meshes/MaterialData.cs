using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>Texture roles a compiled material declares for a texture.</summary>
[Flags]
public enum MaterialTextureTypes : uint
{
    None = 0,
    PrimaryColorMap = 1 << 0,
    ColorMap = 1 << 1,
    DetailColorMap = 1 << 2,
    SharpDetailMap = 1 << 3,
    NormalMap = 1 << 4,
    MiscMap = 1 << 5,
    Vector2Map = 1 << 6,
    NormalDxDyMap = 1 << 7,
    LinePatternMap = 1 << 8,
    LineVisibilityMap = 1 << 9,
    LineGenerationStyleMap = 1 << 10,
    PackedDetailMap = 1 << 11,
    SingleChannelDetailMap = 1 << 12,
    NormalXYMap = 1 << 13,
    CubeMap = 1 << 14,
    VolumeMap = 1 << 15,
    ObjectNormalMap = 1 << 16,
}

/// <summary>How a material's main pass is blended.</summary>
public enum MaterialBlendMode
{
    Normal = 0,
    Alpha = 1,
    AlphaAlphaTest = 2,
    AlphaTest = 3,
    InvAlphaTest = 4,
    Add = 5,
    Multiply = 6,
    InvMultiply = 7,
    AlphaAdd = 8,
    AlphaSubtract = 9,
    AlphaInvAlphaTest = 10,
}

/// <summary>One texture a material binds.</summary>
/// <param name="Key">Symbol of the material property holding the texture.</param>
/// <param name="TextureSymbol">Symbol of the texture file name, 0 when unset.</param>
/// <param name="Types">Roles from the compiled material, <see cref="MaterialTextureTypes.None"/> when not compiled.</param>
/// <param name="Layout">0 = 2D, 1 = 3D, 2 = cube, 3 = 2D array.</param>
/// <param name="ShaderSlot">Texture slot in the compiled shader, -1 when not compiled.</param>
public readonly record struct MaterialTextureBinding(
    ulong Key, ulong TextureSymbol, MaterialTextureTypes Types, int Layout, int ShaderSlot);

/// <summary>
/// A material embedded in a mesh: its parents, texture and value properties, and from the
/// compiled data each texture's role and the blend mode. Unknown types get skipped when
/// possible (see <see cref="SkippedTypes"/>).
/// </summary>
public sealed class MaterialData
{
    private static readonly ulong TypeFloat = TelltaleCrc64.HashSymbol("float");
    private static readonly ulong TypeBool = TelltaleCrc64.HashSymbol("bool");
    private static readonly ulong TypeColor = TelltaleCrc64.HashSymbol("Color");
    private static readonly ulong TypeTexture = TelltaleCrc64.HashSymbol("Handle<T3Texture>");
    private static readonly ulong TypeInt = TelltaleCrc64.HashSymbol("int");
    private static readonly ulong TypeLong = TelltaleCrc64.HashSymbol("long");
    private static readonly ulong TypeInt32 = TelltaleCrc64.HashSymbol("int32");
    private static readonly ulong TypeUInt = TelltaleCrc64.HashSymbol("unsigned int");
    private static readonly ulong TypeUInt32 = TelltaleCrc64.HashSymbol("uint32");
    private static readonly ulong TypeMaterialData = TelltaleCrc64.HashSymbol("T3MaterialData");
    private static readonly ulong TypeBlendMode = TelltaleCrc64.HashSymbol("BlendMode");

    /// <summary>Returns the end of the value at <paramref name="at"/>, or -1 when it would overrun <paramref name="end"/>.</summary>
    private delegate int ValueLayout(byte[] d, int at, int end);

    private static Dictionary<ulong, ValueLayout> BuildLayouts()
    {
        var layouts = new Dictionary<ulong, ValueLayout>();
        var elements = new List<(string Name, ValueLayout Layout)>();
        void Add(string name, ValueLayout layout)
        {
            layouts[TelltaleCrc64.HashSymbol(name)] = layout;
            elements.Add((name, layout));
        }
        void Fixed(int size, params string[] names)
        {
            ValueLayout layout = (_, at, end) => at + size <= end ? at + size : -1;
            foreach (var name in names)
                Add(name, layout);
        }

        // Intrinsics, enums, flags and handles are written bare.
        Fixed(1, "bool", "int8", "uint8", "char", "unsigned char", "signed char");
        Fixed(2, "int16", "uint16", "short", "unsigned short");
        // Definitive Series materials store ints as "int32".
        Fixed(4, "int", "long", "int32", "uint32", "unsigned int", "unsigned long", "__int32",
            "unsigned __int32", "float", "Flags", "FlagsT3LightEnvGroupSet", "BlendMode");
        Fixed(4, Enums);
        Fixed(8, "int64", "uint64", "__int64", "unsigned __int64", "double", "Symbol", "Vector2",
            "TRange<float>", "T3MaterialEnlightenPrecomputeParams");
        Fixed(8, HandleTargets.Select(t => $"Handle<{t}>").ToArray());
        Fixed(12, "Vector3", "Polar");
        Fixed(16, "Vector4", "Color", "Quaternion");
        // Two handles, each in its own block; two symbols in one block.
        Fixed(24, "AnimOrChore");
        Fixed(20, "SoundEventName<0>", "SoundEventName<1>", "SoundEventName<2>");
        Add("String", (d, at, end) =>
            at + 4 <= end && at + 4L + BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) is var next && next <= end
                ? (int)next : -1);
        // A nested set: version, flags, then a block. Version 1 has two blocks.
        Add("PropertySet", (d, at, end) =>
        {
            if (at + 12 > end)
                return -1;
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
            if (version is not (1 or 2))
                return -1;
            long next = at + 8L + BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 8));
            if (version == 1)
                next = next + 4 <= end ? next + BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)next)) : -1;
            return next > at && next <= end ? (int)next : -1;
        });

        // Containers of those: a count, then the elements. Simple types also nest one level.
        var keys = elements.Where(e => e.Name is "String" or "Symbol" or "int").ToList();
        var nestable = new List<(string Name, ValueLayout Layout)>();
        void AddContainers(string name, ValueLayout element, bool nest)
        {
            var array = Sequence(element, null);
            foreach (var container in new[] { $"DCArray<{name}>", $"Set<{name},less<{name}>>", $"List<{name}>" })
            {
                layouts[TelltaleCrc64.HashSymbol(container)] = array;
                if (nest)
                    nestable.Add((container, array));
            }
            foreach (var (keyName, key) in keys)
            {
                string map = $"Map<{keyName},{name},less<{keyName}>>";
                var layout = Sequence(key, element);
                layouts[TelltaleCrc64.HashSymbol(map)] = layout;
                if (nest)
                    nestable.Add((map, layout));
            }
        }
        foreach (var (name, element) in elements.ToList())
            AddContainers(name, element, nest: name is "String" or "Symbol" or "int" or "float" or "bool");
        foreach (var (name, element) in nestable.ToList())
            AddContainers(name, element, nest: false);
        return layouts;
    }

    private static ValueLayout Sequence(ValueLayout first, ValueLayout? second) => (d, at, end) =>
    {
        if (at + 4 > end)
            return -1;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));
        if (count > (uint)(end - at))
            return -1;
        at += 4;
        for (uint i = 0; i < count && at >= 0; i++)
        {
            at = first(d, at, end);
            if (second is not null && at >= 0)
                at = second(d, at, end);
        }
        return at;
    };

    private static readonly string[] HandleTargets =
    {
        "T3Texture", "PropertySet", "D3DMesh", "Skeleton", "Animation", "Chore", "Dlg", "Font",
        "SoundData", "ParticleProperties", "Scene", "LanguageDB", "LanguageResource", "PhonemeTable",
        "BlendGraph", "WalkBoxes", "Rules", "InputMapper", "SoundEventBankDummy", "SoundAmbience::AmbienceDefinition",
        "ParticleSprite", "LightProbeData", "TransitionMap", "DialogResource",
    };

    private static readonly string[] Enums =
    {
        "EnumT3MaterialLightModelType", "EnumT3MaterialNormalSpaceType", "EnumT3MaterialSwizzleType",
        "EnumT3MaterialLODFullyRough", "EnumT3DetailShadingType", "EnumT3NPRSpecularType",
        "EnumT3LightEnvGroup", "EnumT3LightEnvType", "EnumT3LightEnvMobility", "EnumT3LightEnvShadowType",
        "EnumT3LightEnvShadowQuality", "EnumT3LightEnvBakeOnStatic", "EnumT3LightEnvLODBehavior",
        "EnumT3LightEnvEnlightenBakeBehavior", "EnumHTextAlignmentType", "EnumVTextAlignmentType",
        "EnumTextOrientationType", "EnumRenderMaskTest", "EnumRenderMaskWrite", "EnumLightCellBlendMode",
        "EnumParticleGeometryType", "EnumParticleAffectorType", "EnumParticleSortMode",
        "EnumParticlePropDriver", "EnumParticlePropModifier", "EnumEmitterSpriteAnimationType",
        "EnumEmitterSpriteAnimationSelection", "EnumEmitterBoneSelection", "EnumEmitterColorType",
        "EnumEmitterConstraintType", "EnumEmitterParticleCountType", "EnumEmitterSpawnShape",
        "EnumEmitterTriggerEnable", "EnumEmittersEnableType", "EnumPlatformType", "EnumMeshDebugRenderType",
        "EnumTonemapType", "EnumDepthOfFieldType", "EnumDOFQualityLevel", "EnumBokehQualityLevel",
        "EnumBokehOcclusionType", "EnumGlowQualityLevel", "EnumRenderAntialiasType", "EnumRenderTAAJitterType",
        "EnumRenderTextureResolution", "EnumRenderLightmapUVGenerationType", "EnumHBAOPreset",
        "EnumHBAOQualityLevel", "EnumHBAOResolution", "EnumHBAOBlurQuality", "EnumHBAODeinterleaving",
        "EnumHBAOPerPixelNormals", "EnumHBAOParticipationType", "EnumAnimatedPathSegmentType",
        "EnumBlendGraphType", "EnumActiveDuring", "EnumEndRelativeTo", "EnumExtentsMode", "EnumIdleTransition",
        "EnumLookAtComputeStage", "EnumPlaybackMode", "EnumCompressorType", "EnumMode", "EnumOverrun",
        "NavCam::EnumMode", "PhysicsObject::EnumePhysicsCollisionType",
    };

    /// <summary>
    /// How to step over one value of each known type. PropertySets store no value sizes, so
    /// other types need <see cref="Resync"/>. Must stay below the name tables it's built from.
    /// </summary>
    private static readonly Dictionary<ulong, ValueLayout> Layouts = BuildLayouts();

    /// <summary>Symbols of the materials this one instances.</summary>
    public IReadOnlyList<ulong> Parents { get; private init; } = Array.Empty<ulong>();

    /// <summary>Texture properties in stored order, with their compiled roles when known.</summary>
    public IReadOnlyList<MaterialTextureBinding> Textures { get; private init; } = Array.Empty<MaterialTextureBinding>();

    /// <summary>Float properties by key symbol.</summary>
    public IReadOnlyDictionary<ulong, float> Floats { get; private init; } = new Dictionary<ulong, float>();

    /// <summary>Color properties (RGBA) by key symbol.</summary>
    public IReadOnlyDictionary<ulong, Vector4> Colors { get; private init; } = new Dictionary<ulong, Vector4>();

    /// <summary>Bool properties by key symbol.</summary>
    public IReadOnlyDictionary<ulong, bool> Bools { get; private init; } = new Dictionary<ulong, bool>();

    /// <summary>Integer properties by key symbol.</summary>
    public IReadOnlyDictionary<ulong, int> Ints { get; private init; } = new Dictionary<ulong, int>();

    /// <summary>Type symbols whose values couldn't be read and were skipped.</summary>
    public IReadOnlyList<ulong> SkippedTypes { get; private init; } = Array.Empty<ulong>();

    /// <summary>Type symbol parsing gave up at, or null when everything was read.</summary>
    public ulong? StoppedAtType { get; private init; }

    /// <summary>True when every type was read and nothing was skipped.</summary>
    public bool IsComplete { get; private init; }

    /// <summary>True when the compiled material data was read.</summary>
    public bool IsCompiled { get; private init; }

    /// <summary>
    /// Blend mode of the main pass: the "Material - Blend Mode" property when set, otherwise
    /// the compiled data's when it lists exactly one.
    /// </summary>
    public MaterialBlendMode? BlendMode { get; private init; }

    /// <summary>The material's own "Material - Blend Mode" property, or null when unset.</summary>
    public MaterialBlendMode? BlendModeProperty { get; private init; }

    /// <summary>Every (pass, blend mode) pair in the compiled data. Pass 0 is main, 6 is the shadow map.</summary>
    public IReadOnlyList<(int Pass, MaterialBlendMode Blend)> Passes { get; private init; } =
        Array.Empty<(int, MaterialBlendMode)>();

    /// <summary>True when the main pass blends the material over what is behind it.</summary>
    public bool IsBlended => BlendMode is MaterialBlendMode.Alpha or MaterialBlendMode.AlphaAlphaTest
        or MaterialBlendMode.Add or MaterialBlendMode.Multiply or MaterialBlendMode.InvMultiply
        or MaterialBlendMode.AlphaAdd or MaterialBlendMode.AlphaSubtract;

    /// <summary>True when the material has no main pass, like a shadow-only material.</summary>
    public bool HasNoMainPass => IsCompiled && Passes.Count > 0 && Passes.All(p => p.Pass != 0);

    /// <summary>Parses a mesh's embedded material payload, or returns null when it isn't one.</summary>
    public static MaterialData? TryParse(byte[] payload)
    {
        try
        {
            return Parse(payload);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    private static MaterialData? Parse(byte[] d)
    {
        int off = 0;
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off)); off += 4; return v; }
        int I32() => (int)U32();
        ulong U64() { var v = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(off)); off += 8; return v; }
        float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(off)); off += 4; return v; }

        uint version = U32();
        if (version is not (1 or 2))
            return null;
        U32();                                // flags
        int blockStart = off;
        int blockEnd = blockStart + I32();
        if (blockEnd > d.Length || blockEnd < off)
            return null;
        int parentCount = I32();
        if (parentCount is < 0 or > 16)
            return null;
        var parents = new List<ulong>();
        for (int i = 0; i < parentCount; i++)
            parents.Add(U64());
        if (version == 1)
        {
            // Version 1 closes the parents block and opens another for the properties.
            off = blockEnd;
            blockEnd = off + I32();
        }

        var textures = new List<(ulong Key, ulong Texture)>();
        var floats = new Dictionary<ulong, float>();
        var colors = new Dictionary<ulong, Vector4>();
        var bools = new Dictionary<ulong, bool>();
        var ints = new Dictionary<ulong, int>();
        var skipped = new List<ulong>();
        MaterialBlendMode? blendProperty = null;
        CompiledTextures? compiled = null;
        ulong? stoppedAt = null;

        // Locate every type's values first so unknown types can be skipped and misreads caught early.
        int typeCount = I32();
        var groups = new List<TypeGroup>();
        bool aligned = PlanGroups(d, off, typeCount, blockEnd, ResyncDepth, groups);
        foreach (var group in groups)
        {
            if (!group.Known)
            {
                if (group.End < 0)
                {
                    stoppedAt = group.Type;
                    break;
                }
                skipped.Add(group.Type);
                continue;
            }
            ulong type = group.Type;
            off = group.Start;
            try
            {
                for (int i = 0; i < group.Count; i++)
                {
                    ulong key = U64();
                    if (type == TypeTexture) textures.Add((key, U64()));
                    else if (type == TypeFloat) floats[key] = F32();
                    else if (type == TypeBool) bools[key] = d[off++] is (byte)'1' or 1;
                    else if (type == TypeColor) colors[key] = new Vector4(F32(), F32(), F32(), F32());
                    else if (type == TypeBlendMode)
                    {
                        int mode = I32();
                        if (Enum.IsDefined((MaterialBlendMode)mode))
                            blendProperty = (MaterialBlendMode)mode;
                    }
                    else if (type == TypeInt || type == TypeLong || type == TypeInt32 || type == TypeUInt || type == TypeUInt32)
                        ints[key] = I32();
                    else if (type == TypeMaterialData) compiled = ReadMaterialData(d, ref off, blockEnd);
                    else break;
                }
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
            {
                // Keep whatever was read.
            }
        }

        var bindings = textures.Select(tx =>
        {
            if (compiled?.ByKey.TryGetValue(tx.Key, out var c) == true)
                return new MaterialTextureBinding(tx.Key, tx.Texture, c.Types, c.Layout, c.Slot);
            return new MaterialTextureBinding(tx.Key, tx.Texture, MaterialTextureTypes.None, 0, -1);
        }).ToList();

        var passes = compiled?.Passes ?? new List<(int, MaterialBlendMode)>();
        return new MaterialData
        {
            Parents = parents,
            Textures = bindings,
            Floats = floats,
            Colors = colors,
            Bools = bools,
            Ints = ints,
            IsCompiled = compiled is not null,
            SkippedTypes = skipped,
            StoppedAtType = stoppedAt,
            IsComplete = aligned && skipped.Count == 0 && groups.Count == typeCount,
            Passes = passes,
            BlendModeProperty = blendProperty,
            // Older games compile every blend variant, so only trust a single main-pass mode.
            BlendMode = blendProperty
                ?? (passes.Where(p => p.Item1 == 0).Select(p => p.Item2).Distinct().ToList() is { Count: 1 } main
                    ? main[0] : null),
        };
    }

    /// <summary>How many more unknown types <see cref="Resync"/> allows after a candidate start.</summary>
    private const int ResyncDepth = 2;

    /// <summary>One type's values in a set. <see cref="End"/> is -1 when it couldn't be found.</summary>
    private readonly record struct TypeGroup(ulong Type, int Count, int Start, int End, bool Known);

    /// <summary>End of one value of <paramref name="type"/>, or -1 when unknown or out of range.</summary>
    private static int ValueEnd(ulong type, byte[] d, int at, int end)
    {
        if (Layouts.TryGetValue(type, out var layout))
            return layout(d, at, end);
        if (type != TypeMaterialData)
            return -1;
        try
        {
            ReadMaterialData(d, ref at, end);
            return at <= end ? at : -1;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Walks <paramref name="types"/> type groups from <paramref name="at"/>, skipping unknown ones
    /// with <see cref="Resync"/> while <paramref name="depth"/> allows. Adds each to
    /// <paramref name="groups"/> if given. True when they end exactly at <paramref name="end"/>.
    /// </summary>
    private static bool PlanGroups(byte[] d, int at, int types, int end, int depth, List<TypeGroup>? groups)
    {
        for (int g = 0; g < types; g++)
        {
            if (at + 12 > end)
                return false;
            ulong type = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(at));
            int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 8));
            at += 12;
            if (count < 0 || count > (end - at) / 8)
                return false;
            int start = at;
            for (int i = 0; i < count && at >= 0; i++)
                at = at + 8 <= end ? ValueEnd(type, d, at + 8, end) : -1;
            if (at >= 0)
            {
                groups?.Add(new TypeGroup(type, count, start, at, Known: true));
                continue;
            }
            int alsoUnknown = 0;
            at = depth > 0 ? Resync(d, start, types - g - 1, end, depth - 1, out alsoUnknown) : -1;
            groups?.Add(new TypeGroup(type, count, start, at, Known: false));
            if (at < 0)
                return false;
            g += alsoUnknown;   // unknown types skipped along with this one
        }
        return at == end;
    }

    private static bool IsKnownType(ulong type) => Layouts.ContainsKey(type) || type == TypeMaterialData;

    /// <summary>
    /// Finds where reading can resume after an unknown type: the first offset from
    /// <paramref name="from"/> where the remaining types read through to <paramref name="end"/>,
    /// allowing up to <paramref name="depth"/> more unknown types (<paramref name="alsoUnknown"/>).
    /// Returns -1 when nothing fits.
    /// </summary>
    private static int Resync(byte[] d, int from, int typesLeft, int end, int depth, out int alsoUnknown)
    {
        alsoUnknown = 0;
        for (int p = from; p + 12 <= end && typesLeft > 0; p++)
        {
            if (!IsKnownType(BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(p))))
                continue;
            for (int k = 0; k <= depth && k < typesLeft; k++)
                if (PlanGroups(d, p, typesLeft - k, end, depth - k, null))
                {
                    alsoUnknown = k;
                    return p;
                }
        }
        if (typesLeft > depth || from > end)
            return -1;
        alsoUnknown = typesLeft;
        return end;
    }

    private sealed class CompiledTextures
    {
        public Dictionary<ulong, (MaterialTextureTypes Types, int Layout, int Slot)> ByKey { get; } = new();
        public List<(int, MaterialBlendMode)> Passes { get; } = new();
    }

    /// <summary>
    /// Reads a <c>T3MaterialData</c> value and the first of its compiled data entries: texture
    /// roles and passes.
    /// </summary>
    private static CompiledTextures? ReadMaterialData(byte[] d, ref int off, int setEnd)
    {
        int at = off;
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)); at += 4; return v; }
        int I32() => (int)U32();

        // Block-sized array: (block end, count).
        (int End, int Count) Array()
        {
            int start = at;
            int end = start + I32();
            int count = I32();
            if (end > d.Length || end < at || count < 0)
                throw new InvalidDataException("bad material array");
            return (end, count);
        }

        at += 4 * 8;                          // material, runtime-properties, legacy texture/blend symbols
        // Definitive Series adds a domain enum here. Spot it by the next value not being the
        // runtime array's block size.
        int probe = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));
        int probeCount = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at + 4));
        bool hasDomain = !(probe == 8 + 16 * probeCount && probeCount >= 0);
        if (hasDomain)
            at += 4;
        var runtime = Array();
        at = runtime.End;
        U32();                                // flags
        U32();                                // version
        var result = new CompiledTextures();
        int compiledEnd;
        if (hasDomain)
        {
            var compiledArray = Array();
            compiledEnd = compiledArray.End;
            if (compiledArray.Count == 0)
            {
                off = compiledEnd;
                return result;
            }
        }
        else
        {
            // Older games write a bare count and an index, no block size, so assume it runs
            // to the end of the set.
            compiledEnd = setEnd;
            int count = I32();
            if (count <= 0)
            {
                off = at;
                return result;
            }
            U32();
        }

        var parameters = Array();
        at = parameters.End;
        var textures = Array();
        if (textures.Count > 0)
        {
            int stride = (textures.End - at) / textures.Count;
            for (int i = 0; i < textures.Count; i++)
            {
                int e = at + i * stride;
                ulong name = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(e));
                // Entry: name, texture name, bare name, layout, [property type,] types bitset,
                // first param, param count, slot, nested. Read the tail from the end.
                int tail = e + stride;
                int slot = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(tail - 8));
                uint types = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(tail - 20));
                int layout = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(e + 24));
                result.ByKey.TryAdd(name, ((MaterialTextureTypes)types, layout, slot));
            }
        }
        at = textures.End;
        for (int skip = 0; skip < 5; skip++)  // transforms, nested, pre-shaders, static params, texture params
            at = Array().End;
        var passes = Array();
        if (passes.Count > 0 && (passes.End - at) / passes.Count == 16)
            for (int i = 0; i < passes.Count; i++)
            {
                int e = at + i * 16;
                result.Passes.Add((BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(e)),
                    (MaterialBlendMode)BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(e + 4))));
            }
        off = compiledEnd;
        return result;
    }
}

/// <summary>How a batch's ink texture is applied.</summary>
public enum InkMapKind
{
    None,

    /// <summary>
    /// Black shadow ink where white marks ink. Full values always ink; lower values ink as light
    /// falls off.
    /// </summary>
    Shadow,

    /// <summary>Drawn line art, dark lines multiplied over the color.</summary>
    LineArt,
}

/// <summary>Which channels of a specular map carry what.</summary>
public enum SpecularMapLayout
{
    None,

    /// <summary>Color is the specular intensity (older games).</summary>
    Intensity,

    /// <summary>Blue is gloss, alpha the intensity, red marks metal.</summary>
    Packed,

    /// <summary>Color is the specular intensity and alpha the gloss; roughness is 1 - alpha.</summary>
    ColorGloss,
}

/// <summary>The shading values a viewer needs from a material.</summary>
public sealed record MaterialShading
{
    /// <summary>Key symbol of the diffuse color property.</summary>
    public static readonly ulong DiffuseColorKey = TelltaleCrc64.HashSymbol("Material - Diffuse Color");
    public static readonly ulong SpecularColorKey = TelltaleCrc64.HashSymbol("Material - Specular Color");
    public static readonly ulong SpecularIntensityKey = TelltaleCrc64.HashSymbol("Material - Specular Intensity");
    public static readonly ulong SpecularPowerKey = TelltaleCrc64.HashSymbol("Material - Specular Power");
    public static readonly ulong GlossExponentKey = TelltaleCrc64.HashSymbol("Material - Gloss Exponent");
    public static readonly ulong OutlineColorKey = TelltaleCrc64.HashSymbol("Material - Outline Color");
    public static readonly ulong AlphaKey = TelltaleCrc64.HashSymbol("Material - Alpha");

    /// <summary>Linear tint multiplied with the diffuse texture, white when unset.</summary>
    public Vector3 DiffuseColor { get; init; } = Vector3.One;

    /// <summary>Specular color, or null when unset.</summary>
    public Vector3? SpecularColor { get; init; }

    /// <summary>Specular intensity, or null when unset.</summary>
    public float? SpecularIntensity { get; init; }

    /// <summary>Blinn-Phong exponent, or null when unset.</summary>
    public float? SpecularPower { get; init; }

    /// <summary>The material's toon outline color, or null when unset.</summary>
    public Vector4? OutlineColor { get; init; }

    public SpecularMapLayout SpecularLayout { get; init; }

    public InkMapKind InkKind { get; init; }

    /// <summary>True when the ink texture's value is in its alpha channel.</summary>
    public bool InkInAlpha { get; init; }

    /// <summary>
    /// How opaque an alpha-blended batch is. <see cref="Scene.ModelAssembler"/> already multiplied
    /// it into the texture's alpha, so renderers only use it to sort or classify draws.
    /// </summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>
    /// Linear color the emissive texture is added in, black when it doesn't glow
    /// (see <see cref="Scene.AgentRenderSettings"/>).
    /// </summary>
    public Vector3 EmissiveColor { get; init; }

    /// <summary>
    /// True when a translucent batch should show only its nearest surface, like the engine's
    /// "Render 3D Alpha" agents.
    /// </summary>
    public bool DrawAsOneLayer { get; init; }

    /// <summary>Linear light the surface gives off regardless of scene lighting. Black for most things.</summary>
    public Vector3 SelfIllumination { get; init; }

    /// <summary>Shading values with nothing set.</summary>
    public static MaterialShading Default { get; } = new();

    /// <summary>Reads the shading values a parsed material sets.</summary>
    public static MaterialShading FromMaterial(MaterialData material)
    {
        static Vector3 Rgb(Vector4 c) => new(c.X, c.Y, c.Z);
        var diffuse = material.Colors.TryGetValue(DiffuseColorKey, out var d) && d.X >= 0 && d.Y >= 0 && d.Z >= 0
            ? Vector3.Min(Rgb(d), new Vector3(4f))
            : Vector3.One;
        float? power = material.Floats.TryGetValue(SpecularPowerKey, out var p) && p > 0 ? p
            : material.Floats.TryGetValue(GlossExponentKey, out var g) && g > 2 ? g : null;
        return new MaterialShading
        {
            DiffuseColor = diffuse,
            SpecularColor = material.Colors.TryGetValue(SpecularColorKey, out var s) ? Rgb(s) : null,
            SpecularIntensity = material.Floats.TryGetValue(SpecularIntensityKey, out var i) ? i : null,
            SpecularPower = power,
            OutlineColor = material.Colors.TryGetValue(OutlineColorKey, out var o) ? o : null,
        };
    }
}
