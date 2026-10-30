using System.Buffers.Binary;
using System.Numerics;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Meshes;

/// <summary>Texture roles a compiled material declares for a texture (T3MaterialTextureType bits).</summary>
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

/// <summary>How a material's main pass is blended (the engine's T3BlendMode).</summary>
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

/// <summary>One texture a material binds: the property key, the texture, and its declared role.</summary>
/// <param name="Key">Symbol of the material property holding the texture.</param>
/// <param name="TextureSymbol">Symbol of the texture file name (0 when the property is unset).</param>
/// <param name="Types">Roles from the compiled material; <see cref="MaterialTextureTypes.None"/> when not compiled.</param>
/// <param name="Layout">0 = 2D, 1 = 3D, 2 = cube, 3 = 2D array (shared detail atlases).</param>
/// <param name="ShaderSlot">Texture slot in the compiled shader (-1 when not compiled).</param>
public readonly record struct MaterialTextureBinding(
    ulong Key, ulong TextureSymbol, MaterialTextureTypes Types, int Layout, int ShaderSlot);

/// <summary>
/// The engine data of a material embedded in a mesh (a PropertySet with a compiled
/// <c>T3MaterialData</c>): its parent material, texture properties, scalar/color
/// properties, and (from the compiled data) each texture's role and the main pass's
/// blend mode. Parsed from Definitive Series (v55) and Michonne / Tales from the
/// Borderlands era (v22-51) meshes. The values of a type it does not know are stepped over
/// when the types after them can be found (see <see cref="SkippedTypes"/>).
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
        // "int32" (0x7caceebcd26d075c) holds ints in Definitive Series materials and props.
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
        // A nested set: version, flags, then a block (whose size counts itself); version 1 has two.
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

        // Containers of any of those: a count, then the elements (a map's as key, value pairs),
        // and containers of containers of the plainest ones.
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
    /// How to step over one value of each type a property set may hold, by type symbol: the
    /// end of the value at an offset, or -1. A PropertySet gives no size per value or per
    /// type, so a type missing here can only be stepped over by <see cref="Resync"/>.
    /// (Declared after the name tables it is built from: static fields initialise in order.)
    /// </summary>
    private static readonly Dictionary<ulong, ValueLayout> Layouts = BuildLayouts();

    /// <summary>Symbol of the material type this one instances (e.g. <c>material_character_base.prop</c>).</summary>
    public IReadOnlyList<ulong> Parents { get; private init; } = Array.Empty<ulong>();

    /// <summary>Texture properties in stored order, with their compiled roles when known.</summary>
    public IReadOnlyList<MaterialTextureBinding> Textures { get; private init; } = Array.Empty<MaterialTextureBinding>();

    /// <summary>Float properties by key symbol.</summary>
    public IReadOnlyDictionary<ulong, float> Floats { get; private init; } = new Dictionary<ulong, float>();

    /// <summary>Color properties (RGBA) by key symbol.</summary>
    public IReadOnlyDictionary<ulong, Vector4> Colors { get; private init; } = new Dictionary<ulong, Vector4>();

    /// <summary>Bool properties by key symbol.</summary>
    public IReadOnlyDictionary<ulong, bool> Bools { get; private init; } = new Dictionary<ulong, bool>();

    /// <summary>Integer properties (<c>int</c>, <c>long</c>, <c>int32</c>, unsigned) by key symbol.</summary>
    public IReadOnlyDictionary<ulong, int> Ints { get; private init; } = new Dictionary<ulong, int>();

    /// <summary>
    /// Type symbols whose values could not be read and were stepped over (the properties of
    /// every other type still are; a run of unknown types is listed by its first); empty for a
    /// fully understood material.
    /// </summary>
    public IReadOnlyList<ulong> SkippedTypes { get; private init; } = Array.Empty<ulong>();

    /// <summary>
    /// Type symbol of the value parsing gave up at (it and every later type's properties are
    /// missing), or null when no type was left unread.
    /// </summary>
    public ulong? StoppedAtType { get; private init; }

    /// <summary>True when every type's values were read (none skipped) and they ended exactly at the end of the set.</summary>
    public bool IsComplete { get; private init; }

    /// <summary>True when the compiled material data (texture roles, passes) was read.</summary>
    public bool IsCompiled { get; private init; }

    /// <summary>
    /// Blend mode of the main pass: the material's "Material - Blend Mode" property when it
    /// sets one, otherwise the compiled data's when it names exactly one (older games
    /// compile a variant per blend mode, so without the property theirs stays null).
    /// </summary>
    public MaterialBlendMode? BlendMode { get; private init; }

    /// <summary>The material's own "Material - Blend Mode" property, or null when it does not set one.</summary>
    public MaterialBlendMode? BlendModeProperty { get; private init; }

    /// <summary>
    /// Every (pass type, blend mode) pair the compiled data lists; pass 0 is the main
    /// pass, 6 the shadow map.
    /// </summary>
    public IReadOnlyList<(int Pass, MaterialBlendMode Blend)> Passes { get; private init; } =
        Array.Empty<(int, MaterialBlendMode)>();

    /// <summary>True when the main pass blends the material over what is behind it.</summary>
    public bool IsBlended => BlendMode is MaterialBlendMode.Alpha or MaterialBlendMode.AlphaAlphaTest
        or MaterialBlendMode.Add or MaterialBlendMode.Multiply or MaterialBlendMode.InvMultiply
        or MaterialBlendMode.AlphaAdd or MaterialBlendMode.AlphaSubtract;

    /// <summary>True when the material only has passes other than the main one (e.g. shadow-only).</summary>
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

        // Find each type's values first, so that values of an unknown type can be stepped
        // over and a misread is caught before anything is decoded from the wrong place.
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
                // Planned but not decodable: keep what was read of it.
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
            // Older games compile every blend variant; only a single main-pass mode is telling.
            BlendMode = blendProperty
                ?? (passes.Where(p => p.Item1 == 0).Select(p => p.Item2).Distinct().ToList() is { Count: 1 } main
                    ? main[0] : null),
        };
    }

    /// <summary>
    /// How many further unknown types the remainder may hold when <see cref="Resync"/>
    /// checks a candidate start (each check is a full read to the end of the set).
    /// </summary>
    private const int ResyncDepth = 2;

    /// <summary>One type's values in a set: <see cref="End"/> is -1 when it could not be found.</summary>
    private readonly record struct TypeGroup(ulong Type, int Count, int Start, int End, bool Known);

    /// <summary>End of one value of <paramref name="type"/>, or -1 when its layout is unknown or it would overrun.</summary>
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
    /// Walks <paramref name="types"/> type groups (<c>type, count, { key, value }…</c>) from
    /// <paramref name="at"/>, stepping over a group of unknown layout when
    /// <paramref name="depth"/> allows a <see cref="Resync"/>. Records each group in
    /// <paramref name="groups"/> when given; true when they end exactly at <paramref name="end"/>.
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
            g += alsoUnknown;   // further unknown types between this one and where reading resumes
        }
        return at == end;
    }

    private static bool IsKnownType(ulong type) => Layouts.ContainsKey(type) || type == TypeMaterialData;

    /// <summary>
    /// Finds where reading can resume after a type of unknown layout: the first offset from
    /// <paramref name="from"/> holding a known type from which the remaining types read
    /// through to exactly <paramref name="end"/>, allowing up to <paramref name="depth"/>
    /// more unknown types (<paramref name="alsoUnknown"/>) before it. When every remaining
    /// type may be unknown they run to the end. Returns -1 when nothing fits.
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
    /// Reads a <c>T3MaterialData</c> value: names, domain, runtime properties, flags,
    /// version, then its compiled data (one per quality level; the first is used):
    /// parameters, textures, transforms, nested materials, pre-shaders, static
    /// parameters, texture parameters and passes, each a block-sized array.
    /// </summary>
    private static CompiledTextures? ReadMaterialData(byte[] d, ref int off, int setEnd)
    {
        int at = off;
        uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)); at += 4; return v; }
        int I32() => (int)U32();

        // Block-sized arrays: (block end, element count).
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
        // Domain (Definitive Series): present when the next value is a small enum rather
        // than the runtime-property array's block size.
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
            // Older games write a bare count, then an index before each entry, with no
            // block size: the rest of the set is taken as read.
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
                // name, texture name, name without extension, layout, [property type,]
                // texture types (a block-sized bitset), first param, param count, slot, nested.
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
    /// Graphic-black ink (Definitive Series "Ink Texture", and the ported seasons' single-channel
    /// "Detail Texture"): white marks ink. Lines at full value are always inked; lower values
    /// ink only as light falls off, down to solid black shadow.
    /// </summary>
    Shadow,

    /// <summary>Drawn line art (Tales from the Borderlands "Detail Texture", The Wolf Among Us ink lines): dark lines multiplied over the color.</summary>
    LineArt,
}

/// <summary>Which channels of a specular map carry what.</summary>
public enum SpecularMapLayout
{
    None,

    /// <summary>Color is the specular intensity (older games).</summary>
    Intensity,

    /// <summary>Blue is gloss and alpha the intensity; red marks metal (Season 4 materials).</summary>
    Packed,
}

/// <summary>
/// The shading values a viewer needs from a material: diffuse tint, specular color and
/// power, outline color, and how its specular map and ink texture are laid out.
/// </summary>
public sealed record MaterialShading
{
    /// <summary>Key symbol of the engine's diffuse color (a tint multiplied with the diffuse texture).</summary>
    public static readonly ulong DiffuseColorKey = TelltaleCrc64.HashSymbol("Material - Diffuse Color");
    public static readonly ulong SpecularColorKey = TelltaleCrc64.HashSymbol("Material - Specular Color");
    public static readonly ulong SpecularIntensityKey = TelltaleCrc64.HashSymbol("Material - Specular Intensity");
    public static readonly ulong SpecularPowerKey = TelltaleCrc64.HashSymbol("Material - Specular Power");
    public static readonly ulong GlossExponentKey = TelltaleCrc64.HashSymbol("Material - Gloss Exponent");
    public static readonly ulong OutlineColorKey = TelltaleCrc64.HashSymbol("Material - Outline Color");
    public static readonly ulong AlphaKey = TelltaleCrc64.HashSymbol("Material - Alpha");

    /// <summary>Linear RGB tint multiplied with the diffuse texture (white when the material sets none).</summary>
    public Vector3 DiffuseColor { get; init; } = Vector3.One;

    /// <summary>Specular color, or null when the material does not set one.</summary>
    public Vector3? SpecularColor { get; init; }

    /// <summary>Specular intensity, or null when unset.</summary>
    public float? SpecularIntensity { get; init; }

    /// <summary>Blinn-Phong exponent, or null when unset.</summary>
    public float? SpecularPower { get; init; }

    /// <summary>The material's toon outline color, or null when unset.</summary>
    public Vector4? OutlineColor { get; init; }

    public SpecularMapLayout SpecularLayout { get; init; }

    public InkMapKind InkKind { get; init; }

    /// <summary>True when the ink texture's value is in its alpha channel (8-bit alpha textures).</summary>
    public bool InkInAlpha { get; init; }

    /// <summary>
    /// How opaque an alpha-blended batch is drawn (1 = as its texture says), e.g. about 0.3 for
    /// tinted glasses lenses. <see cref="Scene.ModelAssembler"/> has already multiplied it into
    /// the batch texture's alpha, so a renderer only needs it to order or classify draws.
    /// </summary>
    public float Opacity { get; init; } = 1f;

    /// <summary>
    /// Linear color the batch's emissive texture is added in, unaffected by light (black when
    /// it has none or nothing makes it glow; see <see cref="Scene.AgentRenderSettings"/>).
    /// </summary>
    public Vector3 EmissiveColor { get; init; }

    /// <summary>
    /// True when the batch, if translucent, should show only its nearest surface: its depth is
    /// drawn before its color, as the engine does for "Render 3D Alpha" agents (holograms).
    /// </summary>
    public bool DrawAsOneLayer { get; init; }

    /// <summary>
    /// Light the surface gives off in its own color (linear), added whatever the scene's
    /// light: an agent's emission color, which makes a hologram as bright as the game shows
    /// it. Black for everything else.
    /// </summary>
    public Vector3 SelfIllumination { get; init; }

    /// <summary>Shading values with nothing set: white tint, no specular data, no maps.</summary>
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
