namespace TelltaleModding.Core.Meta;

/// <summary>Maps Telltale type-name hashes back to readable names.</summary>
public static class KnownTypeNames
{
    private static readonly Lazy<Dictionary<ulong, string>> _byHash = new(Build);

    /// <summary>Resolves a type-name hash, or null if the name is not known.</summary>
    public static string? Resolve(ulong hash) =>
        _byHash.Value.TryGetValue(hash, out var name) ? name : null;

    private static Dictionary<ulong, string> Build()
    {
        var map = new Dictionary<ulong, string>(Names.Length);
        foreach (var name in Names)
            map[TelltaleCrc64.HashSymbol(name)] = name;
        return map;
    }

    /// <summary>Known engine type names.</summary>
    private static readonly string[] Names =
    {
        // Seen in .d3dmesh headers.
        "D3DMesh", "Symbol", "PropertySet", "Flags", "Vector2", "Color",
        "T3MaterialData", "T3MaterialRuntimeProperty", "T3MaterialCompiledData",
        "T3MaterialParameter", "T3MaterialTexture", "T3MaterialTransform2D",
        "T3MaterialPreShader", "T3MaterialPassData", "T3MaterialRequirements",
        "T3MeshData", "T3MeshLOD", "T3MeshBatch", "BoundingBox", "Sphere",
        "T3MeshTexture", "T3MeshMaterial", "T3MeshTexCoordTransform",
        "GFXPlatformAttributeParams",

        // Material and prop property value types.
        "int32", "T3MaterialEnlightenPrecomputeParams", "EnumT3MaterialNormalSpaceType",
        "BlendMode", "EnumT3MaterialLightModelType",

        // Seen in .d3dtx headers.
        "T3Texture", "T3SamplerStateBlock", "EnumPlatformType",
        "RenderSwizzleParams", "T3Texture::RegionStreamHeader",

        // Chore resource types.
        "Animation", "Chore", "LanguageRes", "Procedural_LookAt", "PointOfInterestBlocking",
        "StyleIdleTransitionsRes", "ParticleInverseKinematics", "BlendCameraResource",
        "ProceduralEyes", "CameraSelect",

        // Other types from the same era.
        "Vector3", "Vector4", "Quaternion", "Transform", "String", "BinaryBuffer",
        "ToolProps", "LocationInfo", "T3MeshBoneEntry", "T3MeshLocalTransformEntry",
        "T3MeshEffectPreload", "T3MeshEffectPreloadEntry",
        "T3MeshEffectPreloadDynamicFeatures", "T3MeshPropertyEntry",
        "T3MeshTextureIndices", "T3MeshCPUSkinningData",
        "T3OcclusionMeshData", "T3OcclusionMeshBatch",
        "T3MaterialInstance", "T3MaterialNestedMaterial", "T3MaterialStaticParameter",
        "T3GFXBuffer", "T3GFXVertexState", "Skeleton", "Handle<T3Texture>",
        "Handle<PropertySet>", "Handle<Skeleton>", "Handle<D3DMesh>",
        "DCArray<T3MeshLOD>", "DCArray<T3MeshBatch>", "DCArray<T3MeshTexture>",
        "DCArray<T3MeshMaterial>", "DCArray<T3MeshBoneEntry>",
        "DCArray<T3MeshLocalTransformEntry>", "DCArray<unsigned short>",
        "DCArray<unsigned int>", "DCArray<int>", "DCArray<float>", "DCArray<Symbol>",
        "long", "int", "float", "bool", "double",
        "unsigned int", "unsigned long", "unsigned short", "unsigned char",
        "unsigned __int64", "__int64", "short", "char",
    };
}
