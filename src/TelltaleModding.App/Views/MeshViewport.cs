using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.App.Views;

/// <summary>
/// Orbit-camera 3D preview of a decoded mesh. Left-drag rotates, right- or
/// middle-drag pans, wheel zooms, double-click resets the view. Shades in linear light
/// under a <see cref="LightRig"/> with specular highlights and tone-mapped output
/// (<see cref="RenderMode.Studio"/>), or in the game's stylised look (ink outlines,
/// banded light, ink shadows) per <see cref="Style"/> (<see cref="RenderMode.GameLook"/>).
/// </summary>
public class MeshViewport : OpenGlControlBase
{
    // GL constants (the subset we use).
    private const int GL_ARRAY_BUFFER = 0x8892, GL_ELEMENT_ARRAY_BUFFER = 0x8893,
        GL_STATIC_DRAW = 0x88E4, GL_FLOAT = 0x1406, GL_TRIANGLES = 0x0004,
        GL_UNSIGNED_INT = 0x1405, GL_DEPTH_TEST = 0x0B71, GL_TEXTURE_2D = 0x0DE1,
        GL_RGBA = 0x1908, GL_RGBA8 = 0x8058, GL_UNSIGNED_BYTE = 0x1401, GL_VERTEX_SHADER = 0x8B31,
        GL_FRAGMENT_SHADER = 0x8B30, GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x0100,
        GL_LINEAR = 0x2601, GL_NEAREST = 0x2600, GL_LINEAR_MIPMAP_LINEAR = 0x2703,
        GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800,
        GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_CLAMP_TO_EDGE = 0x812F,
        GL_TEXTURE_MAX_ANISOTROPY = 0x84FE, GL_EXTENSIONS = 0x1F03,
        GL_TEXTURE0 = 0x84C0, GL_BLEND = 0x0BE2, GL_SRC_ALPHA = 0x0302, GL_ONE_MINUS_SRC_ALPHA = 0x0303,
        GL_FRAMEBUFFER = 0x8D40, GL_COLOR_ATTACHMENT0 = 0x8CE0, GL_COLOR_ATTACHMENT1 = 0x8CE1, GL_COLOR_ATTACHMENT2 = 0x8CE2,
        GL_DEPTH_ATTACHMENT = 0x8D00, GL_FRAMEBUFFER_COMPLETE = 0x8CD5, GL_COLOR = 0x1800,
        GL_DEPTH_COMPONENT = 0x1902, GL_DEPTH_COMPONENT24 = 0x81A6, GL_UNSIGNED_INT_DEPTH = 0x1405,
        GL_FRAMEBUFFER_BINDING = 0x8CA6, GL_CULL_FACE = 0x0B44, GL_FRONT = 0x0404, GL_BACK = 0x0405,
        GL_LESS = 0x0201, GL_LEQUAL = 0x0203;


    private const int VertexFloats = 16; // pos3 + normal3 + uv2 + tangent4 + lightmap uv2 + ink uv2
    private const int SkinFloats = 8;    // bone indices 4 + weights 4 (a second buffer)
    private const int GL_RGBA32F = 0x8814, BoneTextureUnit = 5, EmissiveTextureUnit = 6,
        GL_DYNAMIC_DRAW = 0x88E8, GL_LINES = 0x0001, GL_POINTS = 0x0000, GL_PROGRAM_POINT_SIZE = 0x8642;
    private const int MaxLights = 8;

    // Theme surface color, the background behind the model.
    private Vector3 _background = new(0.082f, 0.094f, 0.118f);

    private float[]? _pendingVertices;
    private int[]? _pendingIndices;
    private IReadOnlyList<DdsImage?>? _pendingBatchTextures;
    private IReadOnlyList<DdsImage?>? _pendingBatchNormalMaps;
    private IReadOnlyList<DdsImage?>? _pendingBatchSpecular;
    private IReadOnlyList<DdsImage?>? _pendingBatchInk;
    private IReadOnlyList<DdsImage?>? _pendingBatchEmissive;
    private IReadOnlyList<MaterialShading?>? _pendingBatchParams;
    private IReadOnlyList<MaterialBlendMode?>? _pendingBatchBlendModes;
    private Vector3[] _batchCenters = Array.Empty<Vector3>();
    private IReadOnlyList<DdsImage?>? _pendingBatchLightmaps;
    private float _lightmapScale = 1f;
    private IReadOnlyList<MeshBatch>? _pendingBatches;
    private bool[]? _pendingClockwise;
    private bool[] _batchClockwise = Array.Empty<bool>();
    private bool _meshDirty, _textureDirty;

    private GlExtras? _x;
    private SceneProgram? _scene;
    private HullProgram? _hull;
    private PostProgram? _post;
    private int _vao, _vbo, _ebo, _postVao, _skinVbo, _boneTex;

    // Skinning: per-vertex bones and weights (uploaded once per model) and the per-frame
    // skinning matrices (a float texture, one row of four texels per bone).
    private float[]? _pendingSkin;
    private bool _skinDirty;
    private float[]? _boneData;
    private int _boneCount;
    private bool _bonesDirty;
    private bool _gpuSkinning = true;   // false when float textures are unavailable: skin on the CPU
    private int[]? _cpuBones;
    private System.Numerics.Vector4[]? _cpuWeights;
    private System.Numerics.Vector3[]? _restPositions, _restNormals;
    private int _indexCount;
    private readonly List<int> _textures = new();          // GL ids shared by all slot arrays
    private readonly List<bool> _textureHasAlpha = new();  // parallel to _textures
    private BatchDraw[] _draws = Array.Empty<BatchDraw>();
    private MeshBatch[] _batches = Array.Empty<MeshBatch>();
    private bool _glReady;
    private float _maxAnisotropy;

    // Offscreen targets: color, view-space normal and depth, read back by the post pass.
    private int _fbo, _colorTex, _normalTex, _lineTex, _depthTex, _targetWidth, _targetHeight;
    private bool _targetsFailed;

    private Vector3 _center, _homeCenter;
    private float _radius = 1f;
    private float _yaw = 0.8f, _pitch = 0.35f, _zoom = 2.4f;
    /// <summary>Vertical field of view in degrees.</summary>
    public float FieldOfView { get; set; } = 45f;

    /// <summary>Orbit and look speed multiplier.</summary>
    public float OrbitSpeed { get; set; } = 1f;

    /// <summary>Scroll dolly speed multiplier.</summary>
    public float DollySpeed { get; set; } = 1f;

    /// <summary>Vertical mouse movement orbits and looks the other way.</summary>
    public bool InvertLook { get; set; }

    /// <summary>Fly speed in model sizes per second (the wheel adjusts it while flying).</summary>
    public float FlySpeed
    {
        get => _flySpeed;
        set => _flySpeed = Math.Clamp(value, 0.05f, 40f);
    }

    /// <summary>The color behind the model.</summary>
    public Vector3 BackgroundColor
    {
        get => _background;
        set
        {
            _background = value;
            RequestNextFrameRendering();
        }
    }

    public MeshViewport()
    {
        // Keyboard focus for the fly keys and F.
        Focusable = true;
    }

    private Point _lastPointer;
    private bool _rotating, _panning, _flying;

    // Skeleton overlay: bind-pose joint positions and parents, posed by the skin matrices.
    private Vector3[]? _skeletonBind;
    private int[]? _skeletonParents;
    private Matrix4x4[]? _lastSkin;
    private bool _showSkeleton;
    private int _lineProgram, _lineMvp, _lineColor, _linePointSize, _lineVao, _lineVbo;

    // Fly mode (right mouse held): mouse-look from the camera, WASD/QE to move.
    private readonly HashSet<Key> _flyKeys = new();
    private DateTime _lastFlyTick;
    private float _flySpeed = 1f;   // model radii per second

    private RenderMode _renderMode = RenderMode.Studio;
    private GameLookStyle _style = GameLookStyle.Default;
    private LightRig _rig = LightRig.Studio;

    /// <summary>Studio (neutral lighting) or the game's stylised look.</summary>
    public RenderMode RenderMode
    {
        get => _renderMode;
        set
        {
            _renderMode = value;
            RequestNextFrameRendering();
        }
    }

    /// <summary>The game's stylised look, used in <see cref="RenderMode.GameLook"/> and (with full graphic black) <see cref="RenderMode.GraphicBlack"/>.</summary>
    public GameLookStyle Style
    {
        get => _style;
        set
        {
            _style = value ?? GameLookStyle.Default;
            _graphicBlackStyle = _style.WithGraphicBlack();
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// How far graphic black reaches in <see cref="RenderMode.GraphicBlack"/>, scaling its
    /// threshold: 0 turns it off, 1 is full graphic black, 2 twice as far.
    /// </summary>
    public float GraphicBlackIntensity
    {
        get => _graphicBlackIntensity;
        set
        {
            _graphicBlackIntensity = Math.Clamp(value, 0f, 2f);
            RequestNextFrameRendering();
        }
    }

    /// <summary>
    /// How far the game's own graphic black reaches in <see cref="RenderMode.GameLook"/>, scaling
    /// its threshold: 0 turns it off, 1 is the game's default, 2 twice as far.
    /// </summary>
    public float EngineDefaultIntensity
    {
        get => _engineDefaultIntensity;
        set
        {
            _engineDefaultIntensity = Math.Clamp(value, 0f, 2f);
            RequestNextFrameRendering();
        }
    }

    private GameLookStyle _graphicBlackStyle = GameLookStyle.Default.WithGraphicBlack();
    private float _graphicBlackIntensity = 1f;
    private float _engineDefaultIntensity = 1f;

    // The style the current mode draws with.
    private GameLookStyle ActiveStyle => _renderMode switch
    {
        RenderMode.GameLook => _style,
        RenderMode.GraphicBlack => _graphicBlackStyle,
        _ => StudioStyle,
    };

    /// <summary>The lights the model is shown under; null restores the default studio rig.</summary>
    public void SetLighting(LightRig? rig)
    {
        _rig = rig ?? LightRig.Studio;
        RequestNextFrameRendering();
    }

    /// <summary>The rig in use.</summary>
    public LightRig Lighting => _rig;

    /// <summary>
    /// Prepares skinning for the loaded mesh: each vertex's four skeleton bone indices
    /// (-1 = none, see <see cref="MeshSkinner.ResolveVertexBones"/>) and blend weights.
    /// Frames then come from <see cref="UpdateSkinMatrices"/>. Loading a new mesh clears it.
    /// </summary>
    public void SetSkinning(IReadOnlyList<int>? vertexBones, IReadOnlyList<System.Numerics.Vector4>? weights)
    {
        int count = _restPositions?.Length ?? 0;
        if (vertexBones is null || weights is null || vertexBones.Count != count * 4 || weights.Count != count)
        {
            ResetSkinning();
            return;
        }
        var data = new float[count * SkinFloats];
        for (int i = 0; i < count; i++)
        {
            int at = i * SkinFloats;
            for (int k = 0; k < 4; k++)
                data[at + k] = vertexBones[i * 4 + k];
            var w = weights[i];
            data[at + 4] = w.X; data[at + 5] = w.Y; data[at + 6] = w.Z; data[at + 7] = w.W;
        }
        _pendingSkin = data;
        _cpuBones = vertexBones.ToArray();
        _cpuWeights = weights.ToArray();
        _skinDirty = true;
        _boneData = null;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Poses the skinned mesh with per-bone skinning matrices (inverse bind × posed world,
    /// see <see cref="MeshSkinner.SkinMatrices"/>); null returns it to its rest pose. The
    /// blend runs in the vertex shader, so only the matrices cross to the GPU each frame.
    /// </summary>
    public void UpdateSkinMatrices(System.Numerics.Matrix4x4[]? skin)
    {
        _lastSkin = skin;
        if (_showSkeleton)
            RequestNextFrameRendering();
        if (_pendingSkin is null)
            return;
        if (skin is null)
        {
            _boneData = null;
            if (!_gpuSkinning && _restPositions is not null && _restNormals is not null)
                UpdateDeformedVertices(_restPositions, _restNormals);
            RequestNextFrameRendering();
            return;
        }
        if (!_gpuSkinning)
        {
            SkinOnCpu(skin);
            return;
        }
        var data = _boneData is { } existing && existing.Length == skin.Length * 16 ? existing : new float[skin.Length * 16];
        for (int b = 0; b < skin.Length; b++)
        {
            var m = skin[b];
            int at = b * 16;
            data[at] = m.M11; data[at + 1] = m.M12; data[at + 2] = m.M13; data[at + 3] = m.M14;
            data[at + 4] = m.M21; data[at + 5] = m.M22; data[at + 6] = m.M23; data[at + 7] = m.M24;
            data[at + 8] = m.M31; data[at + 9] = m.M32; data[at + 10] = m.M33; data[at + 11] = m.M34;
            data[at + 12] = m.M41; data[at + 13] = m.M42; data[at + 14] = m.M43; data[at + 15] = m.M44;
        }
        _boneData = data;
        _boneCount = skin.Length;
        _bonesDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// The skeleton to overlay: each bone's bind-pose world position and parent index (-1 =
    /// root). Its joints follow the same skinning matrices as the mesh. Null removes it.
    /// </summary>
    public void SetSkeleton(IReadOnlyList<Vector3>? bindPositions, IReadOnlyList<int>? parents)
    {
        _skeletonBind = bindPositions?.ToArray();
        _skeletonParents = parents?.ToArray();
        _lastSkin = null;
        RequestNextFrameRendering();
    }

    /// <summary>Draws the skeleton through the model (lines between joints, dots at joints).</summary>
    public bool ShowSkeleton
    {
        get => _showSkeleton;
        set
        {
            _showSkeleton = value;
            RequestNextFrameRendering();
        }
    }

    /// <summary>True while the rest pose is being deformed by skinning matrices on the GPU.</summary>
    private bool SkinActive => _gpuSkinning && _pendingSkin is not null && _boneData is not null && _boneTex != 0;

    private void ResetSkinning()
    {
        _pendingSkin = null;
        _cpuBones = null;
        _cpuWeights = null;
        _boneData = null;
        _skinDirty = true;
    }

    // Fallback when the GPU path is unavailable: the same blend as the shader, on the CPU.
    private void SkinOnCpu(System.Numerics.Matrix4x4[] skin)
    {
        if (_cpuBones is not { } bones || _cpuWeights is not { } weights || _restPositions is not { } rest || _restNormals is not { } restN)
            return;
        var positions = new System.Numerics.Vector3[rest.Length];
        var normals = new System.Numerics.Vector3[rest.Length];
        for (int v = 0; v < rest.Length; v++)
        {
            var w = weights[v];
            float total = w.X + w.Y + w.Z + w.W;
            if (total < 1e-4f || bones[v * 4] < 0)
            {
                positions[v] = rest[v];
                normals[v] = restN[v];
                continue;
            }
            System.Numerics.Vector3 p = default, n = default;
            for (int k = 0; k < 4; k++)
            {
                float wk = k switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };
                int bone = bones[v * 4 + k];
                if (wk <= 0f || bone < 0 || bone >= skin.Length)
                    continue;
                p += System.Numerics.Vector3.Transform(rest[v], skin[bone]) * wk;
                n += System.Numerics.Vector3.TransformNormal(restN[v], skin[bone]) * wk;
            }
            positions[v] = p / total;
            normals[v] = n == default ? restN[v] : System.Numerics.Vector3.Normalize(n);
        }
        UpdateDeformedVertices(positions, normals);
    }

    /// <summary>
    /// Replaces vertex positions/normals in place (animation playback). UVs and
    /// tangents keep their loaded values; the whole buffer re-uploads next frame.
    /// </summary>
    public void UpdateDeformedVertices(System.Numerics.Vector3[] positions, System.Numerics.Vector3[] normals)
    {
        var v = _pendingVertices;
        if (v is null || positions.Length * VertexFloats != v.Length)
            return;
        for (int i = 0; i < positions.Length; i++)
        {
            int at = i * VertexFloats;
            v[at] = positions[i].X; v[at + 1] = positions[i].Y; v[at + 2] = positions[i].Z;
            v[at + 3] = normals[i].X; v[at + 4] = normals[i].Y; v[at + 5] = normals[i].Z;
        }
        _meshDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Loads an assembled model with everything its materials provide: color, normal,
    /// specular and ink textures and shading values. See <see cref="SetMesh"/>.
    /// </summary>
    public void SetModel(AssembledModel model, bool keepCamera = false)
    {
        SetMesh(model.Geometry, model.BatchTextures, model.BatchNormalMaps, keepCamera);
        _pendingBatchSpecular = model.BatchSpecularMaps;
        _pendingBatchInk = model.BatchInkTextures;
        _pendingBatchEmissive = model.BatchEmissiveMaps;
        _pendingBatchParams = model.BatchMaterialParams;
        _pendingBatchBlendModes = model.BatchBlendModes;
        if (model.InkTexCoords is { } ink && _pendingVertices is { } v && ink.Length * VertexFloats == v.Length)
        {
            for (int i = 0; i < ink.Length; i++)
            {
                v[i * VertexFloats + 14] = ink[i].X;
                v[i * VertexFloats + 15] = ink[i].Y;
            }
        }
    }

    /// <summary>
    /// Adds baked lightmaps to the current mesh: <paramref name="uv2"/> holds one lightmap UV
    /// per vertex and <paramref name="batchLightmaps"/> one atlas per batch (null where a batch
    /// has none). A lightmapped batch is lit by <c>sample × scale × intensity</c> plus the
    /// rig's lights that <see cref="LightSource.AffectsLightmapped"/>, without ambient.
    /// Lightmaps apply under a scene's rig, not the camera-following studio rig. Null
    /// arguments clear them; <see cref="SetMesh"/> clears them too.
    /// </summary>
    public void SetLightmaps(IReadOnlyList<Vector2>? uv2, IReadOnlyList<DdsImage?>? batchLightmaps, float scale, float intensity)
    {
        var v = _pendingVertices;
        if (v is null)
            return;
        int count = v.Length / VertexFloats;
        bool usable = uv2 is not null && batchLightmaps is not null && uv2.Count == count;
        for (int i = 0; i < count; i++)
        {
            int at = i * VertexFloats + 12;
            v[at] = usable ? uv2![i].X : 0f;
            v[at + 1] = usable ? uv2![i].Y : 0f;
        }
        _pendingBatchLightmaps = usable ? batchLightmaps : null;
        _lightmapScale = scale * intensity;
        _meshDirty = _textureDirty = true;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Loads a decoded mesh into the viewport. The per-batch lists hold one entry
    /// per batch of the geometry (null where nothing matched). With
    /// <paramref name="keepCamera"/> the view stays where the user left it (e.g. when
    /// an outfit part is toggled); otherwise it frames the new mesh.
    /// </summary>
    public void SetMesh(MeshGeometry geometry, IReadOnlyList<DdsImage?> batchTextures,
        IReadOnlyList<DdsImage?>? batchNormalMaps = null, bool keepCamera = false)
    {
        keepCamera &= _hasMesh;
        _hasMesh = true;
        var v = new float[geometry.Positions.Length * VertexFloats];
        for (int i = 0; i < geometry.Positions.Length; i++)
        {
            int at = i * VertexFloats;
            v[at] = geometry.Positions[i].X;
            v[at + 1] = geometry.Positions[i].Y;
            v[at + 2] = geometry.Positions[i].Z;
            v[at + 3] = geometry.Normals[i].X;
            v[at + 4] = geometry.Normals[i].Y;
            v[at + 5] = geometry.Normals[i].Z;
            v[at + 6] = geometry.TexCoords[i].X;
            v[at + 7] = geometry.TexCoords[i].Y;
            v[at + 8] = geometry.Tangents[i].X;
            v[at + 9] = geometry.Tangents[i].Y;
            v[at + 10] = geometry.Tangents[i].Z;
            v[at + 11] = geometry.Tangents[i].W;
            v[at + 14] = geometry.TexCoords[i].X;
            v[at + 15] = geometry.TexCoords[i].Y;
        }

        _pendingVertices = v;
        _restPositions = geometry.Positions;
        _restNormals = geometry.Normals;
        ResetSkinning();
        _skeletonBind = null;
        _skeletonParents = null;
        _lastSkin = null;
        _batchVisible = null;
        _pendingIndices = geometry.Indices;
        _pendingBatches = geometry.Batches;
        _pendingClockwise = ClockwiseBatches(geometry);
        _pendingBatchTextures = batchTextures;
        _pendingBatchNormalMaps = batchNormalMaps;
        _pendingBatchSpecular = null;
        _pendingBatchInk = null;
        _pendingBatchEmissive = null;
        _pendingBatchParams = null;
        _pendingBatchLightmaps = null;
        _meshDirty = _textureDirty = true;

        _boundsMin = geometry.BoundsMin;
        _boundsMax = geometry.BoundsMax;
        _homeCenter = (geometry.BoundsMin + geometry.BoundsMax) / 2f;
        float radius = Math.Max(0.01f, (geometry.BoundsMax - geometry.BoundsMin).Length() / 2f);
        if (keepCamera)
        {
            // Keep the same eye distance even though the bounds (and so the radius) changed.
            _zoom = Math.Clamp(_zoom * _radius / radius, MinZoom, 20f);
            _radius = radius;
        }
        else
        {
            _radius = radius;
            ResetView();
        }
        RequestNextFrameRendering();
    }

    private bool _hasMesh;
    private Vector3 _boundsMin, _boundsMax;
    private bool[]? _batchVisible;

    /// <summary>
    /// Closest the camera may come, as a fraction of the model's bounding radius (default 0.08).
    /// Lower it for large models, such as whole scenes, that are explored from inside.
    /// </summary>
    public float MinZoom { get; set; } = 0.08f;

    /// <summary>Near clip distance as a fraction of the bounding radius (default 0.01).</summary>
    public float NearPlaneFactor { get; set; } = 0.01f;

    /// <summary>
    /// Shows or hides individual batches of the current mesh without re-uploading it (null
    /// shows all). Entries past the list's end are shown. <see cref="SetMesh"/> resets it.
    /// </summary>
    public void SetBatchVisibility(IReadOnlyList<bool>? visible)
    {
        _batchVisible = visible?.ToArray();
        RequestNextFrameRendering();
    }

    /// <summary>Frames the current mesh from the default angle.</summary>
    public void ResetView()
    {
        _center = _homeCenter;
        _yaw = 0.8f; _pitch = 0.35f; _zoom = 2.4f;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Looks straight at the top of the model (a standing character's face) from the
    /// front, close enough to inspect eyes and lashes. Zoom is relative to the whole
    /// model, so the close-up is expressed as a small zoom factor.
    /// </summary>
    public void FocusFace() => FocusFace(1f);

    /// <summary>
    /// Frames a face whose top of head is at <paramref name="headTop"/> on a body
    /// <paramref name="height"/> tall (a posed character may stand away from its rest-pose
    /// bounds); <paramref name="distance"/> as in <see cref="FocusFace(float)"/>.
    /// </summary>
    public void FocusFaceAt(Vector3 headTop, float height, float distance)
    {
        _center = new Vector3(headTop.X, headTop.Y - height * (0.075f + 0.02f * (distance - 1f)), headTop.Z);
        _yaw = 0f; _pitch = 0.05f;
        _zoom = Math.Max(0.12f, height * 0.16f / _radius) * distance;
        RequestNextFrameRendering();
    }

    /// <param name="distance">How far out to stand: 1 is an eyes-and-lashes close-up; larger values
    /// back off toward head and shoulders, aiming a little lower.</param>
    public void FocusFace(float distance)
    {
        float height = _boundsMax.Y - _boundsMin.Y;
        _center = new Vector3((_boundsMin.X + _boundsMax.X) / 2f, _boundsMax.Y - height * (0.075f + 0.02f * (distance - 1f)),
            (_boundsMin.Z + _boundsMax.Z) / 2f);
        _yaw = 0f; _pitch = 0.05f;
        _zoom = Math.Max(0.12f, height * 0.16f / _radius) * distance;
        RequestNextFrameRendering();
    }

    /// <summary>Removes the mesh; the viewport shows its background only.</summary>
    public void Clear()
    {
        _restPositions = null;
        _restNormals = null;
        ResetSkinning();
        _pendingVertices = Array.Empty<float>();
        _pendingIndices = Array.Empty<int>();
        _pendingBatches = Array.Empty<MeshBatch>();
        _pendingBatchTextures = null;
        _pendingBatchNormalMaps = null;
        _pendingBatchSpecular = null;
        _pendingBatchInk = null;
        _pendingBatchEmissive = null;
        _pendingBatchParams = null;
        _pendingBatchLightmaps = null;
        _meshDirty = _textureDirty = true;
        _hasMesh = false;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// For each batch, true when its triangles wind clockwise around their vertex normals
    /// (meshes differ), judged by a majority of sampled triangles.
    /// </summary>
    private static bool[] ClockwiseBatches(MeshGeometry geometry)
    {
        var result = new bool[geometry.Batches.Count];
        var p = geometry.Positions;
        var n = geometry.Normals;
        var idx = geometry.Indices;
        for (int b = 0; b < result.Length; b++)
        {
            var batch = geometry.Batches[b];
            int step = Math.Max(1, batch.PrimitiveCount / 256), votes = 0;
            for (int t = 0; t < batch.PrimitiveCount; t += step)
            {
                int at = batch.StartIndex + t * 3;
                if (at + 2 >= idx.Length)
                    break;
                int i0 = idx[at], i1 = idx[at + 1], i2 = idx[at + 2];
                if ((uint)i0 >= p.Length || (uint)i1 >= p.Length || (uint)i2 >= p.Length)
                    continue;
                var face = Vector3.Cross(p[i1] - p[i0], p[i2] - p[i0]);
                float d = Vector3.Dot(face, n[i0] + n[i1] + n[i2]);
                votes += d > 0 ? 1 : d < 0 ? -1 : 0;
            }
            result[b] = votes < 0;
        }
        return result;
    }

    /// <summary>True when any pixel is meaningfully transparent (not just BC noise).</summary>
    private static bool HasMeaningfulAlpha(DdsImage image)
    {
        int transparent = 0;
        for (int i = 3; i < image.Rgba.Length; i += 4)
        {
            if (image.Rgba[i] < 240 && ++transparent > 16)
                return true;
        }
        return false;
    }

    // ---- GL setup ---------------------------------------------------------

    private string ShaderHeader =>
        GlVersion.Type == GlProfileType.OpenGL
            ? "#version 330 core\n"
            : "#version 300 es\nprecision highp float;\nprecision highp int;\n";

    private int Compile(GlInterface gl, string vertex, string fragment)
    {
        int program = gl.CreateProgram();
        int vs = gl.CreateShader(GL_VERTEX_SHADER);
        var vsError = gl.CompileShaderAndGetError(vs, ShaderHeader + vertex);
        int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        var fsError = gl.CompileShaderAndGetError(fs, ShaderHeader + fragment);
        if (vsError is not null || fsError is not null)
            throw new InvalidOperationException($"shader compile failed: {vsError ?? fsError}");
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        // Fixed attribute slots, so the scene and hull programs share one vertex array.
        string[] attributes = { "aPos", "aNormal", "aUv", "aTangent", "aUv2", "aUv3", "aBones", "aWeights" };
        for (int i = 0; i < attributes.Length; i++)
            gl.BindAttribLocationString(program, i, attributes[i]);
        var linkError = gl.LinkProgramAndGetError(program);
        if (linkError is not null)
            throw new InvalidOperationException($"shader link failed: {linkError}");
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return program;
    }

    // Avalonia drops an exception thrown here without logging it and leaves the view
    // blank, so it goes to the crash log first.
    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            InitGl(gl);
        }
        catch (Exception ex)
        {
            CrashLog.Write("3D view setup", ex);
            throw;
        }
    }

    private void InitGl(GlInterface gl)
    {
        _x = new GlExtras(gl);
        _scene = new SceneProgram(gl, Compile(gl, MeshShaders.SceneVertex, MeshShaders.SceneFragment));
        try
        {
            _hull = new HullProgram(gl, Compile(gl, MeshShaders.HullVertex, MeshShaders.HullFragment));
        }
        catch (InvalidOperationException)
        {
            _hull = null; // no hull outlines
        }
        try
        {
            if (_x.HasFramebuffers)
                _post = new PostProgram(gl, Compile(gl, MeshShaders.PostVertex, MeshShaders.PostFragment));
        }
        catch (InvalidOperationException)
        {
            _post = null; // no post pass: draw straight to the window, without outlines
        }

        try
        {
            if (gl.GetString(GL_EXTENSIONS) is { } ext && ext.Contains("texture_filter_anisotropic"))
                _maxAnisotropy = 8f;
        }
        catch (Exception ex) when (ex is OpenGlException or InvalidOperationException)
        {
        }

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        _skinVbo = gl.GenBuffer();
        try
        {
            _lineProgram = Compile(gl, MeshShaders.LineVertex, MeshShaders.LineFragment);
            _lineMvp = gl.GetUniformLocationString(_lineProgram, "uMvp");
            _lineColor = gl.GetUniformLocationString(_lineProgram, "uColor");
            _linePointSize = gl.GetUniformLocationString(_lineProgram, "uPointSize");
            _lineVao = gl.GenVertexArray();
            _lineVbo = gl.GenBuffer();
        }
        catch (InvalidOperationException)
        {
            _lineProgram = 0; // no skeleton overlay
        }
        _postVao = gl.GenVertexArray();
        _glReady = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _glReady = false;
        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_ebo);
        gl.DeleteBuffer(_skinVbo);
        if (_lineProgram != 0)
        {
            gl.DeleteProgram(_lineProgram);
            gl.DeleteBuffer(_lineVbo);
            gl.DeleteVertexArray(_lineVao);
        }
        if (_boneTex != 0)
            gl.DeleteTexture(_boneTex);
        _boneTex = 0;
        gl.DeleteVertexArray(_vao);
        gl.DeleteVertexArray(_postVao);
        DeleteTextures(gl);
        DeleteTargets(gl);
        if (_scene is not null)
            gl.DeleteProgram(_scene.Id);
        if (_post is not null)
            gl.DeleteProgram(_post.Id);
        if (_hull is not null)
            gl.DeleteProgram(_hull.Id);
    }

    private void DeleteTextures(GlInterface gl)
    {
        foreach (var id in _textures)
            gl.DeleteTexture(id);
        _textures.Clear();
        _textureHasAlpha.Clear();
    }

    private unsafe void DeleteTargets(GlInterface gl)
    {
        if (_fbo != 0 && _x is not null)
        {
            int fbo = _fbo;
            _x.DeleteFramebuffers(1, &fbo);
        }
        foreach (var t in new[] { _colorTex, _normalTex, _lineTex, _depthTex })
            if (t != 0)
                gl.DeleteTexture(t);
        _fbo = _colorTex = _normalTex = _lineTex = _depthTex = 0;
        _targetWidth = _targetHeight = 0;
    }

    /// <summary>(Re)creates the offscreen targets at the given size; false when unsupported.</summary>
    private unsafe bool EnsureTargets(GlInterface gl, int width, int height)
    {
        if (_post is null || _x is null || _targetsFailed || width <= 0 || height <= 0)
            return false;
        if (_fbo != 0 && width == _targetWidth && height == _targetHeight)
            return true;
        DeleteTargets(gl);

        int Target(int internalFormat, int format, int type, int filter)
        {
            int id = gl.GenTexture();
            gl.BindTexture(GL_TEXTURE_2D, id);
            gl.TexImage2D(GL_TEXTURE_2D, 0, internalFormat, width, height, 0, format, type, IntPtr.Zero);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, filter);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, filter);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
            return id;
        }
        _colorTex = Target(GL_RGBA8, GL_RGBA, GL_UNSIGNED_BYTE, GL_NEAREST);
        _normalTex = Target(GL_RGBA8, GL_RGBA, GL_UNSIGNED_BYTE, GL_NEAREST);
        _lineTex = Target(GL_RGBA8, GL_RGBA, GL_UNSIGNED_BYTE, GL_NEAREST);
        _depthTex = Target(GL_DEPTH_COMPONENT24, GL_DEPTH_COMPONENT, GL_UNSIGNED_INT_DEPTH, GL_NEAREST);

        int fbo;
        _x.GenFramebuffers(1, &fbo);
        _fbo = fbo;
        _x.BindFramebuffer(GL_FRAMEBUFFER, _fbo);
        _x.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _colorTex, 0);
        _x.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT1, GL_TEXTURE_2D, _normalTex, 0);
        _x.FramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT2, GL_TEXTURE_2D, _lineTex, 0);
        _x.FramebufferTexture2D(GL_FRAMEBUFFER, GL_DEPTH_ATTACHMENT, GL_TEXTURE_2D, _depthTex, 0);
        bool complete = _x.CheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE;
        if (!complete)
        {
            DeleteTargets(gl);
            _targetsFailed = true;
            return false;
        }
        _targetWidth = width;
        _targetHeight = height;
        return true;
    }

    // ---- Rendering ----------------------------------------------------------

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        AdvanceFlight();
        double scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)(Bounds.Width * scaling));
        int height = Math.Max(1, (int)(Bounds.Height * scaling));

        if (!_glReady || _scene is null || _x is null)
        {
            gl.ClearColor(_background.X, _background.Y, _background.Z, 1f);
            gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            return;
        }

        UploadPending(gl);

        bool offscreen = _indexCount > 0 && EnsureTargets(gl, width, height);
        _x.BindFramebuffer(GL_FRAMEBUFFER, offscreen ? _fbo : fb);
        gl.Viewport(0, 0, width, height);
        if (offscreen)
        {
            int* buffers = stackalloc int[] { GL_COLOR_ATTACHMENT0, GL_COLOR_ATTACHMENT1, GL_COLOR_ATTACHMENT2 };
            _x.DrawBuffers(3, buffers);
        }
        gl.ClearColor(_background.X, _background.Y, _background.Z, 1f);
        gl.ClearDepth(1f);
        _x.DepthMask(1);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        if (offscreen)
        {
            // The normal target is "empty" (alpha 0) where nothing is drawn.
            float* empty = stackalloc float[] { 0.5f, 0.5f, 1f, 0f };
            _x.ClearBufferfv(GL_COLOR, 1, empty);
        }
        if (_indexCount == 0)
            return;

        var camera = Camera(width, height);
        DrawScene(gl, camera);

        if (offscreen)
        {
            _x.BindFramebuffer(GL_FRAMEBUFFER, fb);
            gl.Viewport(0, 0, width, height);
            DrawPost(gl, camera, width, height, (float)scaling);
        }
        if (_showSkeleton)
            DrawSkeleton(gl, camera, (float)scaling);
    }

    // Bones as lines to their parents and dots at the joints, over everything.
    private unsafe void DrawSkeleton(GlInterface gl, CameraState camera, float scaling)
    {
        if (_lineProgram == 0 || _skeletonBind is not { } bind || _skeletonParents is not { } parents)
            return;
        var skin = _lastSkin is { } s && s.Length == bind.Length ? s : null;
        var joints = new Vector3[bind.Length];
        for (int i = 0; i < bind.Length; i++)
            joints[i] = skin is null ? bind[i] : Vector3.Transform(bind[i], skin[i]);

        var lines = new List<float>(bind.Length * 6);
        for (int i = 0; i < joints.Length; i++)
        {
            int parent = i < parents.Length ? parents[i] : -1;
            if (parent < 0 || parent >= joints.Length)
                continue;
            lines.Add(joints[parent].X); lines.Add(joints[parent].Y); lines.Add(joints[parent].Z);
            lines.Add(joints[i].X); lines.Add(joints[i].Y); lines.Add(joints[i].Z);
        }
        var points = new float[joints.Length * 3];
        for (int i = 0; i < joints.Length; i++)
        {
            points[i * 3] = joints[i].X; points[i * 3 + 1] = joints[i].Y; points[i * 3 + 2] = joints[i].Z;
        }

        var x = _x!;
        gl.UseProgram(_lineProgram);
        gl.BindVertexArray(_lineVao);
        gl.Disable(GL_DEPTH_TEST);
        gl.Enable(GL_BLEND);
        x.BlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        gl.Enable(GL_PROGRAM_POINT_SIZE);
        while (gl.GetError() != 0) { }   // the point-size switch is desktop-only
        var mvp = camera.View * camera.Projection;
        gl.UniformMatrix4fv(_lineMvp, 1, false, &mvp);
        gl.BindBuffer(GL_ARRAY_BUFFER, _lineVbo);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 3 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(0);

        void Draw(float[] data, int mode, float r, float g, float b, float a, float pointSize)
        {
            if (data.Length == 0)
                return;
            fixed (float* p = data)
                gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(data.Length * sizeof(float)), new IntPtr(p), GL_DYNAMIC_DRAW);
            x.Uniform4f(_lineColor, r, g, b, a);
            gl.Uniform1f(_linePointSize, pointSize);
            x.DrawArrays(mode, 0, data.Length / 3);
        }
        Draw(lines.ToArray(), GL_LINES, 1f, 0.78f, 0.25f, 0.95f, 1f);
        Draw(points, GL_POINTS, 1f, 0.33f, 0.36f, 1f, 5f * scaling);

        gl.Disable(GL_BLEND);
        gl.Enable(GL_DEPTH_TEST);
        gl.BindVertexArray(0);
    }

    private readonly record struct CameraState(Matrix4x4 View, Matrix4x4 Projection, Vector3 Eye,
        Vector3 Right, Vector3 Up, Vector3 Back, float Near, float Far, float Aspect);

    private CameraState Camera(int width, int height)
    {
        float aspect = width / (float)Math.Max(1, height);
        float distance = _radius * _zoom;
        var eye = _center + new Vector3(
            distance * MathF.Cos(_pitch) * MathF.Sin(_yaw),
            distance * MathF.Sin(_pitch),
            distance * MathF.Cos(_pitch) * MathF.Cos(_yaw));
        var view = Matrix4x4.CreateLookAt(eye, _center, Vector3.UnitY);
        float near = _radius * NearPlaneFactor, far = _radius * 50f;
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView * MathF.PI / 180f, aspect, near, far);
        var back = Vector3.Normalize(eye - _center);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, back));
        var up = Vector3.Cross(back, right);
        return new CameraState(view, proj, eye, right, up, back, near, far, aspect);
    }

    private unsafe void UploadPending(GlInterface gl)
    {
        var program = _scene!;
        gl.UseProgram(program.Id);
        gl.BindVertexArray(_vao);

        if (_meshDirty && _pendingVertices is not null && _pendingIndices is not null)
        {
            _meshDirty = false;
            _indexCount = _pendingIndices.Length;

            gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
            fixed (float* p = _pendingVertices)
                gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(_pendingVertices.Length * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);
            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _ebo);
            fixed (int* p = _pendingIndices)
                gl.BufferData(GL_ELEMENT_ARRAY_BUFFER, new IntPtr(_pendingIndices.Length * sizeof(int)), new IntPtr(p), GL_STATIC_DRAW);

            int stride = VertexFloats * sizeof(float);
            void Attribute(string name, int size, int offset)
            {
                int location = gl.GetAttribLocationString(program.Id, name);
                if (location < 0)
                    return;
                gl.VertexAttribPointer(location, size, GL_FLOAT, 0, stride, new IntPtr(offset));
                gl.EnableVertexAttribArray(location);
            }
            Attribute("aPos", 3, 0);
            Attribute("aNormal", 3, 12);
            Attribute("aUv", 2, 24);
            Attribute("aTangent", 4, 32);
            Attribute("aUv2", 2, 48);
            Attribute("aUv3", 2, 56);
            _skinDirty = true;
        }

        if (_skinDirty && _pendingVertices is not null)
        {
            _skinDirty = false;
            // Always sized to the mesh (zeros when unskinned) so the attribute never reads past the buffer.
            var skinData = _pendingSkin ?? new float[_pendingVertices.Length / VertexFloats * SkinFloats];
            gl.BindBuffer(GL_ARRAY_BUFFER, _skinVbo);
            fixed (float* p = skinData)
                gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(Math.Max(1, skinData.Length) * sizeof(float)), new IntPtr(p), GL_STATIC_DRAW);
            int skinStride = SkinFloats * sizeof(float);
            gl.VertexAttribPointer(6, 4, GL_FLOAT, 0, skinStride, IntPtr.Zero);
            gl.EnableVertexAttribArray(6);
            gl.VertexAttribPointer(7, 4, GL_FLOAT, 0, skinStride, new IntPtr(4 * sizeof(float)));
            gl.EnableVertexAttribArray(7);
        }

        if (_bonesDirty && _boneData is { } bones && _gpuSkinning)
        {
            _bonesDirty = false;
            if (_boneTex == 0)
                _boneTex = gl.GenTexture();
            gl.ActiveTexture(GL_TEXTURE0 + BoneTextureUnit);
            gl.BindTexture(GL_TEXTURE_2D, _boneTex);
            while (gl.GetError() != 0) { }
            fixed (float* p = bones)
                gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA32F, 4, _boneCount, 0, GL_RGBA, GL_FLOAT, new IntPtr(p));
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
            gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
            gl.ActiveTexture(GL_TEXTURE0);
            if (gl.GetError() != 0)
            {
                // No float textures here: skin on the CPU from now on.
                _gpuSkinning = false;
                gl.DeleteTexture(_boneTex);
                _boneTex = 0;
            }
        }

        if (!_textureDirty)
            return;
        _textureDirty = false;
        DeleteTextures(gl);
        _batches = _pendingBatches?.ToArray() ?? Array.Empty<MeshBatch>();
        _batchCenters = new Vector3[_batches.Length];
        if (_restPositions is { } rest)
        {
            for (int i = 0; i < _batches.Length; i++)
            {
                int lo = Math.Clamp(_batches[i].MinVertex, 0, rest.Length - 1);
                int hi = Math.Clamp(_batches[i].MaxVertex, lo, rest.Length - 1);
                var min = rest[lo];
                var max = rest[lo];
                for (int v = lo; v <= hi; v++)
                {
                    min = Vector3.Min(min, rest[v]);
                    max = Vector3.Max(max, rest[v]);
                }
                _batchCenters[i] = (min + max) / 2f;
            }
        }
        _batchClockwise = _pendingClockwise is { } cw && cw.Length == _batches.Length ? cw : new bool[_batches.Length];
        _draws = new BatchDraw[_batches.Length];

        // Upload each distinct image once; batches sharing an image share a slot.
        var slotByImage = new Dictionary<DdsImage, int>();
        int SlotFor(DdsImage? image, bool lightmap = false)
        {
            if (image is null)
                return -1;
            if (!slotByImage.TryGetValue(image, out int slot))
            {
                int id = gl.GenTexture();
                gl.BindTexture(GL_TEXTURE_2D, id);
                fixed (byte* p = image.Rgba)
                    gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, image.Width, image.Height, 0, GL_RGBA, GL_UNSIGNED_BYTE, new IntPtr(p));
                if (lightmap)
                {
                    // Atlases pack many charts: no mips (they would bleed across charts).
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
                }
                else
                {
                    _x!.GenerateMipmap(GL_TEXTURE_2D);
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR_MIPMAP_LINEAR);
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                    if (_maxAnisotropy > 1f)
                        _x.TexParameterf(GL_TEXTURE_2D, GL_TEXTURE_MAX_ANISOTROPY, _maxAnisotropy);
                }
                slot = _textures.Count;
                _textures.Add(id);
                _textureHasAlpha.Add(HasMeaningfulAlpha(image));
                slotByImage[image] = slot;
            }
            return slot;
        }
        static T? At<T>(IReadOnlyList<T?>? list, int i) where T : class =>
            list is not null && i < list.Count ? list[i] : null;

        for (int b = 0; b < _batches.Length; b++)
        {
            var normal = At(_pendingBatchNormalMaps, b);
            var specular = At(_pendingBatchSpecular, b);
            var ink = At(_pendingBatchInk, b);
            var shading = At(_pendingBatchParams, b) ?? MaterialShading.Default;
            int tex = SlotFor(At(_pendingBatchTextures, b));
            var layout = specular is null ? SpecularMapLayout.None : shading.SpecularLayout == SpecularMapLayout.None
                ? SpecularMapLayout.Intensity : shading.SpecularLayout;
            // A highlight only where the material asks for one (an intensity or a map): many
            // materials inherit a specular power their shader never uses (Tales from the
            // Borderlands' character shader has no specular term), and meshes without material
            // data (The Wolf Among Us, Game of Thrones) read as glossy with any default sheen.
            float intensity = shading.SpecularIntensity ?? (specular is not null ? 1f : 0f);
            // Translucent: the texture has real alpha, the material is see-through glass, or
            // its blend mode says so (the opacity is already baked into the texture's alpha).
            var blend = _pendingBatchBlendModes is { } modes && b < modes.Count ? modes[b] : null;
            // Batches drawn as one layer (hologram agents) are depth-sorted by a depth pass instead.
            bool glass = shading.Opacity < 0.999f && !shading.DrawAsOneLayer;
            bool blended = blend is MaterialBlendMode.Alpha or MaterialBlendMode.AlphaAlphaTest or MaterialBlendMode.AlphaAdd;
            _draws[b] = new BatchDraw
            {
                Texture = tex,
                HasAlpha = tex >= 0 && (_textureHasAlpha[tex] || glass || blended),
                Glass = glass && tex >= 0,
                NormalMap = SlotFor(normal),
                // BC3 normal maps keep X in blue and Y in alpha; BC5 in red and green.
                NormalKind = normal is null ? -1 : normal.Format switch
                {
                    DdsPixelFormat.Bc5 => 1,
                    DdsPixelFormat.Bc3 or DdsPixelFormat.Bc2 => 0,
                    _ => 2,
                },
                Specular = SlotFor(specular),
                SpecularLayout = (int)layout,
                Ink = SlotFor(ink),
                InkKind = ink is null ? 0 : (int)shading.InkKind,
                InkInAlpha = shading.InkInAlpha,
                Tint = shading.DiffuseColor,
                SpecularColor = (shading.SpecularColor ?? Vector3.One) * intensity,
                SpecularPower = Math.Clamp(shading.SpecularPower ?? 24f, 1f, 512f),
                Lightmap = SlotFor(At(_pendingBatchLightmaps, b), lightmap: true),
                Emissive = SlotFor(At(_pendingBatchEmissive, b)),
                EmissiveColor = shading.EmissiveColor,
                SelfLit = shading.SelfIllumination,
                OneLayer = shading.DrawAsOneLayer,
                Outline = shading.OutlineColor is { } line ? new Vector3(line.X, line.Y, line.Z) : null,
            };
        }
    }

    private struct BatchDraw
    {
        public int Texture, NormalMap, Specular, Ink, Lightmap, Emissive;
        public bool HasAlpha, InkInAlpha;

        /// <summary>Translucent but showing only its nearest surface: its depth is drawn before its color.</summary>
        public bool OneLayer;

        /// <summary>See-through (opacity below 1): drawn last, back to front, without depth writes.</summary>
        public bool Glass;
        public int NormalKind, SpecularLayout, InkKind;
        public Vector3 Tint, SpecularColor, EmissiveColor, SelfLit;
        public float SpecularPower;

        /// <summary>The material's own outline color (Handsome Jack's hologram is cyan); null draws the look's.</summary>
        public Vector3? Outline;
    }

    private static readonly BatchDraw Untextured = new()
    {
        Texture = -1, NormalMap = -1, Specular = -1, Ink = -1, Lightmap = -1, Emissive = -1, NormalKind = -1,
        Tint = Vector3.One, SpecularColor = new Vector3(0.12f), SpecularPower = 24f,
    };

    private static Vector3 SrgbToLinear(Vector3 c) =>
        new(MathF.Pow(Math.Max(0, c.X), 2.2f), MathF.Pow(Math.Max(0, c.Y), 2.2f), MathF.Pow(Math.Max(0, c.Z), 2.2f));

    private unsafe void DrawScene(GlInterface gl, CameraState camera)
    {
        var p = _scene!;
        var x = _x!;
        gl.UseProgram(p.Id);
        gl.BindVertexArray(_vao);
        gl.Enable(GL_DEPTH_TEST);

        var view = camera.View;
        var mvp = camera.View * camera.Projection;
        gl.UniformMatrix4fv(p.Mvp, 1, false, &mvp);
        gl.UniformMatrix4fv(p.View, 1, false, &view);
        BindSkinning(gl, p.Skinned, p.Bones);
        x.Uniform3f(p.Eye, camera.Eye.X, camera.Eye.Y, camera.Eye.Z);
        x.Uniform1i(p.Tex, 0);
        x.Uniform1i(p.Nmap, 1);
        x.Uniform1i(p.Spec, 2);
        x.Uniform1i(p.InkTex, 3);
        x.Uniform1i(p.Lightmap, 4);
        x.Uniform1i(p.Emissive, EmissiveTextureUnit);
        // Baked lighting belongs to the scene's own lights, not the studio rig.
        _lightmapsOn = !_rig.FollowCamera;
        gl.Uniform1f(p.LightmapScale, _lightmapScale);

        // Lights, in world space; camera-following rigs are given in view space.
        var rig = _rig;
        int count = Math.Min(MaxLights, rig.Lights.Count);
        float* pos = stackalloc float[MaxLights * 4];
        float* dir = stackalloc float[MaxLights * 4];
        float* col = stackalloc float[MaxLights * 4];
        float* cone = stackalloc float[MaxLights * 4];
        float maxLuma = 1e-4f;
        for (int i = 0; i < count; i++)
        {
            var l = rig.Lights[i];
            maxLuma = Math.Max(maxLuma, Luma(l.Color) * l.Intensity);
        }
        for (int i = 0; i < count; i++)
        {
            var l = rig.Lights[i];
            Vector3 lp = l.Position, ld = l.Direction;
            if (rig.FollowCamera)
            {
                lp = camera.Eye + camera.Right * l.Position.X + camera.Up * l.Position.Y + camera.Back * l.Position.Z;
                ld = camera.Right * l.Direction.X + camera.Up * l.Direction.Y + camera.Back * l.Direction.Z;
            }
            ld = ld.LengthSquared() > 1e-8f ? Vector3.Normalize(ld) : -Vector3.UnitY;
            var c = l.Color * l.Intensity;
            pos[i * 4] = lp.X; pos[i * 4 + 1] = lp.Y; pos[i * 4 + 2] = lp.Z; pos[i * 4 + 3] = (int)l.Kind;
            dir[i * 4] = ld.X; dir[i * 4 + 1] = ld.Y; dir[i * 4 + 2] = ld.Z; dir[i * 4 + 3] = l.Range;
            col[i * 4] = c.X; col[i * 4 + 1] = c.Y; col[i * 4 + 2] = c.Z; col[i * 4 + 3] = l.AffectsLightmapped ? 1f : 0f;
            float outer = l.OuterConeDegrees > 0 ? l.OuterConeDegrees : 45f;
            float inner = Math.Min(l.InnerConeDegrees > 0 ? l.InnerConeDegrees : outer * 0.8f, outer - 0.01f);
            // Cone angles are full angles: the cosine of half of each.
            cone[i * 4] = MathF.Cos(inner * MathF.PI / 360f);
            cone[i * 4 + 1] = MathF.Cos(outer * MathF.PI / 360f);
            cone[i * 4 + 2] = Luma(c) / maxLuma;
            cone[i * 4 + 3] = 0;
        }
        x.Uniform1i(p.LightCount, count);
        x.Uniform4fv(p.LightPos, MaxLights, pos);
        x.Uniform4fv(p.LightDir, MaxLights, dir);
        x.Uniform4fv(p.LightColor, MaxLights, col);
        x.Uniform4fv(p.LightCone, MaxLights, cone);
        x.Uniform3f(p.AmbientSky, rig.AmbientSky.X, rig.AmbientSky.Y, rig.AmbientSky.Z);
        x.Uniform3f(p.AmbientGround, rig.AmbientGround.X, rig.AmbientGround.Y, rig.AmbientGround.Z);

        // The look: Studio is the neutral style; ink and line art show in both, as they are
        // part of the materials.
        bool game = _renderMode != RenderMode.Studio;
        var s = ActiveStyle;
        gl.Uniform1f(p.Exposure, rig.Exposure * s.Exposure);
        float* bands = stackalloc float[4] { 2f, 2f, 2f, 2f };
        float* levels = stackalloc float[4];
        int bandCount = Math.Min(3, s.BandThresholds.Count);
        for (int i = 0; i < bandCount; i++)
            bands[i] = s.BandThresholds[i];
        for (int i = 0; i <= bandCount; i++)
            levels[i] = s.BandLevels is { } given && i < given.Count ? given[i] : bandCount == 0 ? 1f : i / (float)bandCount;
        x.Uniform4fv(p.Bands, 1, bands);
        x.Uniform4fv(p.BandLevels, 1, levels);
        x.Uniform1i(p.BandCount, bandCount);
        x.Uniform3f(p.BandShape, s.BandSoftness, s.BandSmoothness, Math.Clamp(s.NormalMapInfluence, 0f, 1f));
        // The toon model lights characters with one key; a scene's own lights all count.
        x.Uniform2f(p.LightModel, Math.Max(0f, s.Wrap), s.ToonKeyLightOnly && rig.FollowCamera ? 1f : 0f);
        x.Uniform3f(p.ShadowTint, s.ShadowTint.X, s.ShadowTint.Y, s.ShadowTint.Z);
        x.Uniform4f(p.Rim, s.RimStrength, s.RimThreshold, s.SpecularThreshold, s.SpecularStrength);
        // A scene's rig is tested for graphic black as the game does (all light, ambient
        // included, faded in with distance); the studio rig, whose ambient and wrapped fills
        // stand in for missing shadows, by its key light alone.
        bool studio = rig.FollowCamera;
        float intensity = _renderMode == RenderMode.GraphicBlack ? _graphicBlackIntensity : _engineDefaultIntensity;
        float graphicBlack = intensity > 0f ? s.InkStrength : 0f;
        x.Uniform4f(p.InkParams, graphicBlack, (studio ? s.InkThreshold : s.InkSceneThreshold) * intensity,
            studio ? s.InkSoftness : s.InkSceneSoftness, s.InkLineStrength);
        gl.Uniform1f(p.InkAmbient, studio ? 0f : 1f);
        gl.Uniform1f(p.InkKeyWrap, s.InkKeyWrap);
        var inkColor = SrgbToLinear(s.InkColor);
        x.Uniform3f(p.InkColor, inkColor.X, inkColor.Y, inkColor.Z);
        x.Uniform3f(p.InkFade, studio ? s.InkFadeNear : s.InkSceneFadeNear, studio ? s.InkFadeFar : s.InkSceneFadeFar, s.LineArtStrength);
        x.Uniform3f(p.Grade, s.Saturation, s.Contrast, 0f);
        _styleLine = s.OutlineColor;

        x.BlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        gl.Enable(GL_BLEND);

        if (_batches.Length == 0)
        {
            Bind(gl, p, Untextured);
            gl.DrawElements(GL_TRIANGLES, _indexCount, GL_UNSIGNED_INT, IntPtr.Zero);
        }
        else
        {
            // Opaque batches first, then alpha cut-outs (hair, lashes, irises) so they blend over
            // what is behind them, then glass last: back to front without writing depth, so
            // eyes and lashes behind a lens always show through it.
            bool Shown(int b) => !(_batchVisible is { } shown && b < shown.Length && !shown[b]);
            void DrawBatch(int b)
            {
                Bind(gl, p, _draws[b]);
                gl.DrawElements(GL_TRIANGLES, _batches[b].PrimitiveCount * 3, GL_UNSIGNED_INT,
                    new IntPtr(_batches[b].StartIndex * sizeof(int)));
            }
            for (int b = 0; b < _batches.Length; b++)
                if (Shown(b) && !_draws[b].HasAlpha)
                    DrawBatch(b);
            // Translucent batches drawn as one layer (the engine's "Render 3D Alpha", e.g. a
            // hologram) lay down their depth first, so only their nearest surface blends in.
            var layered = Enumerable.Range(0, _batches.Length).Where(b => Shown(b) && _draws[b].HasAlpha && _draws[b].OneLayer).ToList();
            if (layered.Count > 0)
            {
                x.ColorMask(0, 0, 0, 0);
                foreach (int b in layered)
                    DrawBatch(b);
                x.ColorMask(1, 1, 1, 1);
                x.DepthFunc(GL_LEQUAL);
            }
            for (int b = 0; b < _batches.Length; b++)
                if (Shown(b) && _draws[b].HasAlpha && !_draws[b].Glass)
                    DrawBatch(b);
            x.DepthFunc(GL_LESS);
            var glassBatches = Enumerable.Range(0, _batches.Length).Where(b => Shown(b) && _draws[b].Glass).ToList();
            if (glassBatches.Count > 0)
            {
                var eye = camera.Eye;
                glassBatches.Sort((a, c) => Vector3.DistanceSquared(BatchCenter(c), eye).CompareTo(Vector3.DistanceSquared(BatchCenter(a), eye)));
                x.DepthMask(0);
                foreach (int b in glassBatches)
                    DrawBatch(b);
                x.DepthMask(1);
            }
        }
        gl.Disable(GL_BLEND);

        if (game && s.HullOutline && _hull is not null)
            DrawHull(gl, camera);
        gl.BindVertexArray(0);
    }

    /// <summary>The neutral look of <see cref="RenderMode.Studio"/>: softly wrapped light, smooth highlights, no ink shadows.</summary>
    private static readonly GameLookStyle StudioStyle = new()
    {
        Name = "Studio",
        OutlineWidth = 0f,
        Wrap = 0.15f,
        SpecularStrength = 1f,
    };

    /// <summary>The inverted-hull outline: opaque batches again, back faces only, pushed out along the normals.</summary>
    private unsafe void DrawHull(GlInterface gl, CameraState camera)
    {
        var h = _hull!;
        var x = _x!;
        var s = ActiveStyle;
        gl.UseProgram(h.Id);
        var view = camera.View;
        var mvp = camera.View * camera.Projection;
        gl.UniformMatrix4fv(h.Mvp, 1, false, &mvp);
        gl.UniformMatrix4fv(h.View, 1, false, &view);
        BindSkinning(gl, h.Skinned, h.Bones);
        x.Uniform4f(h.Params, s.HullWidth, s.HullZRange.X, Math.Max(s.HullZRange.X, s.HullZRange.Y), camera.Aspect);
        var color = s.OutlineColor;
        x.Uniform3f(h.Color, color.X, color.Y, color.Z);
        gl.Enable(GL_CULL_FACE);
        x.CullFace(GL_FRONT);
        if (_batches.Length == 0)
            gl.DrawElements(GL_TRIANGLES, _indexCount, GL_UNSIGNED_INT, IntPtr.Zero);
        var current = color;
        for (int b = 0; b < _batches.Length; b++)
        {
            // Cut-out cards (hair, lashes) would outline their quads.
            if (_batchVisible is { } shown && b < shown.Length && !shown[b] || _draws[b].HasAlpha)
                continue;
            var line = _draws[b].Outline ?? color;
            if (line != current)
            {
                x.Uniform3f(h.Color, line.X, line.Y, line.Z);
                current = line;
            }
            // Keep the far side of the shell whichever way the batch is wound.
            x.CullFace(_batchClockwise[b] ? GL_BACK : GL_FRONT);
            gl.DrawElements(GL_TRIANGLES, _batches[b].PrimitiveCount * 3, GL_UNSIGNED_INT,
                new IntPtr(_batches[b].StartIndex * sizeof(int)));
        }
        gl.Disable(GL_CULL_FACE);
    }

    private unsafe void BindSkinning(GlInterface gl, int skinnedUniform, int bonesUniform)
    {
        var x = _x!;
        bool on = SkinActive;
        x.Uniform1i(skinnedUniform, on ? 1 : 0);
        x.Uniform1i(bonesUniform, BoneTextureUnit);
        if (!on)
            return;
        gl.ActiveTexture(GL_TEXTURE0 + BoneTextureUnit);
        gl.BindTexture(GL_TEXTURE_2D, _boneTex);
        gl.ActiveTexture(GL_TEXTURE0);
    }

    private static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private unsafe void Bind(GlInterface gl, SceneProgram p, BatchDraw d)
    {
        var x = _x!;
        void Texture(int unit, int slot)
        {
            if (slot < 0)
                return;
            gl.ActiveTexture(GL_TEXTURE0 + unit);
            gl.BindTexture(GL_TEXTURE_2D, _textures[slot]);
        }
        Texture(0, d.Texture);
        Texture(1, d.NormalMap);
        Texture(2, d.Specular);
        Texture(3, d.Ink);
        Texture(EmissiveTextureUnit, d.Emissive);
        bool lightmapped = _lightmapsOn && d.Lightmap >= 0;
        if (lightmapped)
            Texture(4, d.Lightmap);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.Uniform1f(p.HasTex, d.Texture >= 0 ? 1f : 0f);
        x.Uniform1i(p.NmapKind, d.NormalMap >= 0 ? d.NormalKind : -1);
        x.Uniform1i(p.SpecLayout, d.Specular >= 0 ? d.SpecularLayout : 0);
        x.Uniform1i(p.InkKind, d.Ink >= 0 ? d.InkKind : 0);
        gl.Uniform1f(p.InkInAlpha, d.InkInAlpha ? 1f : 0f);
        x.Uniform3f(p.Tint, d.Tint.X, d.Tint.Y, d.Tint.Z);
        x.Uniform3f(p.SpecColor, d.SpecularColor.X, d.SpecularColor.Y, d.SpecularColor.Z);
        var glow = d.Emissive >= 0 ? d.EmissiveColor : Vector3.Zero;
        x.Uniform3f(p.EmissiveColor, glow.X, glow.Y, glow.Z);
        x.Uniform3f(p.SelfLit, d.SelfLit.X, d.SelfLit.Y, d.SelfLit.Z);
        gl.Uniform1f(p.SpecPower, d.SpecularPower);
        gl.Uniform1f(p.HasLightmap, lightmapped ? 1f : 0f);
        var line = d.Outline ?? _styleLine;
        x.Uniform3f(p.LineColor, line.X, line.Y, line.Z);
    }

    private bool _lightmapsOn;

    // The look's outline color, for batches whose material sets none.
    private Vector3 _styleLine;

    // A batch's rest-pose centre, for sorting glass back to front.
    private Vector3 BatchCenter(int b) => b < _batchCenters.Length ? _batchCenters[b] : _center;

    private unsafe void DrawPost(GlInterface gl, CameraState camera, int width, int height, float scaling)
    {
        var p = _post!;
        var x = _x!;
        gl.Disable(GL_DEPTH_TEST);
        gl.UseProgram(p.Id);
        gl.BindVertexArray(_postVao);
        gl.ActiveTexture(GL_TEXTURE0);
        gl.BindTexture(GL_TEXTURE_2D, _colorTex);
        gl.ActiveTexture(GL_TEXTURE0 + 1);
        gl.BindTexture(GL_TEXTURE_2D, _normalTex);
        gl.ActiveTexture(GL_TEXTURE0 + 2);
        gl.BindTexture(GL_TEXTURE_2D, _depthTex);
        gl.ActiveTexture(GL_TEXTURE0 + 3);
        gl.BindTexture(GL_TEXTURE_2D, _lineTex);
        gl.ActiveTexture(GL_TEXTURE0);
        x.Uniform1i(p.Color, 0);
        x.Uniform1i(p.Normal, 1);
        x.Uniform1i(p.Depth, 2);
        x.Uniform1i(p.Line, 3);

        var s = ActiveStyle;
        bool edges = _renderMode != RenderMode.Studio && s.OutlineWidth > 0 && s.OutlineOpacity > 0;
        x.Uniform2f(p.Texel, 1f / width, 1f / height);
        x.Uniform2f(p.Planes, camera.Near, camera.Far);
        x.Uniform4f(p.Outline, edges ? s.OutlineWidth * scaling : 0f, s.DepthThreshold, s.NormalThreshold, s.CreaseWidthScale);
        gl.Uniform1f(p.OutlineOpacity, s.OutlineOpacity);
        x.Uniform2f(p.Falloff, s.OutlineFalloffDistance * _radius, s.OutlineMinScale);
        x.DrawArrays(GL_TRIANGLES, 0, 3);
        gl.BindVertexArray(0);
    }

    // ---- Programs and extra entry points -------------------------------------

    private sealed class SceneProgram
    {
        public int Id, Mvp, View, Eye, Tex, Nmap, Spec, InkTex, HasTex, NmapKind, SpecLayout, InkKind, InkInAlpha,
            Tint, SpecColor, SpecPower, LightCount, LightPos, LightDir, LightColor, LightCone, AmbientSky, AmbientGround,
            Exposure, Bands, BandLevels, BandCount, BandShape, LightModel, ShadowTint, Rim, InkParams, InkColor, InkFade, Grade,
            Lightmap, HasLightmap, LightmapScale, InkAmbient, InkKeyWrap, Skinned, Bones, Emissive, EmissiveColor, SelfLit,
            LineColor;

        public SceneProgram(GlInterface gl, int id)
        {
            Id = id;
            int U(string name) => gl.GetUniformLocationString(id, name);
            Mvp = U("uMvp"); View = U("uView"); Eye = U("uEye");
            Tex = U("uTex"); Nmap = U("uNmap"); Spec = U("uSpec"); InkTex = U("uInkTex");
            HasTex = U("uHasTex"); NmapKind = U("uNmapKind"); SpecLayout = U("uSpecLayout");
            InkKind = U("uInkKind"); InkInAlpha = U("uInkInAlpha");
            Tint = U("uTint"); SpecColor = U("uSpecColor"); SpecPower = U("uSpecPower");
            LightCount = U("uLightCount"); LightPos = U("uLightPos"); LightDir = U("uLightDir");
            LightColor = U("uLightColor"); LightCone = U("uLightCone");
            AmbientSky = U("uAmbientSky"); AmbientGround = U("uAmbientGround");
            Exposure = U("uExposure"); Bands = U("uBands"); BandLevels = U("uBandLevels"); BandCount = U("uBandCount");
            LightModel = U("uLightModel"); InkAmbient = U("uInkAmbient"); InkKeyWrap = U("uInkKeyWrap");
            BandShape = U("uBandShape"); ShadowTint = U("uShadowTint"); Rim = U("uRim");
            InkParams = U("uInk"); InkColor = U("uInkColor"); InkFade = U("uInkFade"); Grade = U("uGrade");
            Lightmap = U("uLightmap"); HasLightmap = U("uHasLightmap"); LightmapScale = U("uLightmapScale");
            Skinned = U("uSkinned"); Bones = U("uBones");
            Emissive = U("uEmissive"); EmissiveColor = U("uEmissiveColor"); SelfLit = U("uSelfLit");
            LineColor = U("uLineColor");
        }
    }

    private sealed class HullProgram
    {
        public int Id, Mvp, View, Params, Color, Skinned, Bones;

        public HullProgram(GlInterface gl, int id)
        {
            Id = id;
            int U(string name) => gl.GetUniformLocationString(id, name);
            Mvp = U("uMvp"); View = U("uView"); Params = U("uHull"); Color = U("uColor");
            Skinned = U("uSkinned"); Bones = U("uBones");
        }
    }

    private sealed class PostProgram
    {
        public int Id, Color, Normal, Depth, Line, Texel, Planes, Outline, OutlineOpacity, Falloff;

        public PostProgram(GlInterface gl, int id)
        {
            Id = id;
            int U(string name) => gl.GetUniformLocationString(id, name);
            Color = U("uColor"); Normal = U("uNormal"); Depth = U("uDepth"); Line = U("uLine"); Texel = U("uTexel");
            Planes = U("uPlanes"); Outline = U("uOutline"); OutlineOpacity = U("uOutlineOpacity"); Falloff = U("uFalloff");
        }
    }

    /// <summary>GL entry points <see cref="GlInterface"/> does not wrap, fetched by name.</summary>
    private sealed unsafe class GlExtras
    {
        public readonly delegate* unmanaged[Stdcall]<int, int, void> Uniform1i;
        public readonly delegate* unmanaged[Stdcall]<int, float, float, void> Uniform2f;
        public readonly delegate* unmanaged[Stdcall]<int, float, float, float, void> Uniform3f;
        public readonly delegate* unmanaged[Stdcall]<int, float, float, float, float, void> Uniform4f;
        public readonly delegate* unmanaged[Stdcall]<int, int, float*, void> Uniform4fv;
        public readonly delegate* unmanaged[Stdcall]<int, int, void> BlendFunc;
        public readonly delegate* unmanaged[Stdcall]<int, void> CullFace;
        public readonly delegate* unmanaged[Stdcall]<int, void> GenerateMipmap;
        public readonly delegate* unmanaged[Stdcall]<int, int, float, void> TexParameterf;
        public readonly delegate* unmanaged[Stdcall]<byte, void> DepthMask;
        public readonly delegate* unmanaged[Stdcall]<int, void> DepthFunc;
        public readonly delegate* unmanaged[Stdcall]<byte, byte, byte, byte, void> ColorMask;
        public readonly delegate* unmanaged[Stdcall]<int, int, int, void> DrawArrays;
        public readonly delegate* unmanaged[Stdcall]<int, int*, void> GenFramebuffers;
        public readonly delegate* unmanaged[Stdcall]<int, int*, void> DeleteFramebuffers;
        public readonly delegate* unmanaged[Stdcall]<int, int, void> BindFramebuffer;
        public readonly delegate* unmanaged[Stdcall]<int, int, int, int, int, void> FramebufferTexture2D;
        public readonly delegate* unmanaged[Stdcall]<int, int> CheckFramebufferStatus;
        public readonly delegate* unmanaged[Stdcall]<int, int*, void> DrawBuffers;
        public readonly delegate* unmanaged[Stdcall]<int, int, float*, void> ClearBufferfv;

        public GlExtras(GlInterface gl)
        {
            void* P(string name)
            {
                var ptr = gl.GetProcAddress(name);
                if (ptr == IntPtr.Zero)
                    throw new InvalidOperationException($"OpenGL entry point {name} is missing");
                return (void*)ptr;
            }
            void* Optional(string name) => (void*)gl.GetProcAddress(name);

            Uniform1i = (delegate* unmanaged[Stdcall]<int, int, void>)P("glUniform1i");
            Uniform2f = (delegate* unmanaged[Stdcall]<int, float, float, void>)P("glUniform2f");
            Uniform3f = (delegate* unmanaged[Stdcall]<int, float, float, float, void>)P("glUniform3f");
            Uniform4f = (delegate* unmanaged[Stdcall]<int, float, float, float, float, void>)P("glUniform4f");
            Uniform4fv = (delegate* unmanaged[Stdcall]<int, int, float*, void>)P("glUniform4fv");
            BlendFunc = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBlendFunc");
            CullFace = (delegate* unmanaged[Stdcall]<int, void>)P("glCullFace");
            GenerateMipmap = (delegate* unmanaged[Stdcall]<int, void>)P("glGenerateMipmap");
            TexParameterf = (delegate* unmanaged[Stdcall]<int, int, float, void>)P("glTexParameterf");
            DepthMask = (delegate* unmanaged[Stdcall]<byte, void>)P("glDepthMask");
            DepthFunc = (delegate* unmanaged[Stdcall]<int, void>)P("glDepthFunc");
            ColorMask = (delegate* unmanaged[Stdcall]<byte, byte, byte, byte, void>)P("glColorMask");
            DrawArrays = (delegate* unmanaged[Stdcall]<int, int, int, void>)P("glDrawArrays");
            BindFramebuffer = (delegate* unmanaged[Stdcall]<int, int, void>)P("glBindFramebuffer");
            GenFramebuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)Optional("glGenFramebuffers");
            DeleteFramebuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)Optional("glDeleteFramebuffers");
            FramebufferTexture2D = (delegate* unmanaged[Stdcall]<int, int, int, int, int, void>)Optional("glFramebufferTexture2D");
            CheckFramebufferStatus = (delegate* unmanaged[Stdcall]<int, int>)Optional("glCheckFramebufferStatus");
            DrawBuffers = (delegate* unmanaged[Stdcall]<int, int*, void>)Optional("glDrawBuffers");
            ClearBufferfv = (delegate* unmanaged[Stdcall]<int, int, float*, void>)Optional("glClearBufferfv");
        }

        /// <summary>True when offscreen targets with several color attachments can be used.</summary>
        public bool HasFramebuffers =>
            GenFramebuffers != null && DeleteFramebuffers != null && FramebufferTexture2D != null &&
            CheckFramebufferStatus != null && DrawBuffers != null && ClearBufferfv != null;
    }

    // ---- Orbit interaction ----------------------------------------------

    // The GL control itself is not reliably hit-testable in Avalonia (no background
    // brush), so the view hosts it in a transparent panel and forwards input here.

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HandlePointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HandlePointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        HandlePointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        HandlePointerWheel(e);
    }

    public void HandlePointerPressed(PointerPressedEventArgs e)
    {
        if (e.Handled)
            return;
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            ResetView();
            return;
        }

        // Left orbits (Shift+left pans), middle pans, right flies, as in a level editor.
        var props = e.GetCurrentPoint(this).Properties;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        _rotating = props.IsLeftButtonPressed && !shift;
        _panning = props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && shift);
        _flying = props.IsRightButtonPressed;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
        Focus();
        if (_flying)
            StartFlying();
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (!_rotating && !_panning && !_flying)
            return;
        e.Handled = true;
        var p = e.GetPosition(this);
        float dx = (float)(p.X - _lastPointer.X);
        float dy = (float)(p.Y - _lastPointer.Y);
        _lastPointer = p;

        if (_flying)
        {
            // Look around from where the camera stands: the eye stays, the pivot moves.
            var eye = EyePosition();
            _yaw -= dx * 0.005f * OrbitSpeed;
            _pitch = Math.Clamp(_pitch + dy * 0.005f * OrbitSpeed * (InvertLook ? -1f : 1f), -1.5f, 1.5f);
            _center = eye - BackDirection() * (_radius * _zoom);
        }
        else if (_rotating)
        {
            _yaw -= dx * 0.01f * OrbitSpeed;
            _pitch += dy * 0.01f * OrbitSpeed * (InvertLook ? -1f : 1f);
            _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);
        }
        else
        {
            // Pan: shift the orbit center along the camera's right/up axes,
            // scaled so a full drag across the view moves about one view-height.
            float panScale = _radius * _zoom * 0.0016f;
            var forward = new Vector3(
                -MathF.Cos(_pitch) * MathF.Sin(_yaw),
                -MathF.Sin(_pitch),
                -MathF.Cos(_pitch) * MathF.Cos(_yaw));
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var up = Vector3.Cross(right, forward);
            _center += (-right * dx + up * dy) * panScale;
        }
        RequestNextFrameRendering();
    }

    public void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        _rotating = _panning = false;
        if (_flying)
            StopFlying();
        e.Pointer.Capture(null);
    }

    public void HandlePointerWheel(PointerWheelEventArgs e)
    {
        if (e.Handled)
            return;
        e.Handled = true;
        if (_flying)
        {
            // While flying the wheel sets the speed.
            _flySpeed = Math.Clamp(_flySpeed * (e.Delta.Y > 0 ? 1.25f : 0.8f), 0.05f, 40f);
            return;
        }

        // Dolly by a steady step (a share of the distance, never less than a fixed
        // minimum) so zooming never crawls to a halt; at the pivot, keep going by
        // carrying the pivot forward.
        float distance = _radius * _zoom;
        float step = Math.Max(distance * 0.18f, _radius * 0.05f) * DollySpeed;
        if (e.Delta.Y > 0)
        {
            float target = distance - step;
            float closest = _radius * MinZoom;
            if (target < closest)
            {
                _center -= BackDirection() * (closest - target);
                target = closest;
            }
            _zoom = target / _radius;
        }
        else
        {
            _zoom = Math.Min(40f, (distance + step) / _radius);
        }
        RequestNextFrameRendering();
    }

    /// <summary>
    /// F frames the model; W/A/S/D move and Q/E lower/raise the camera while the viewport has
    /// focus (click it first), holding the right button to look around as you go.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F)
        {
            ResetView();
            e.Handled = true;
            return;
        }
        if (IsFlyKey(e.Key) && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
        {
            if (_flyKeys.Count == 0)
                _lastFlyTick = DateTime.UtcNow;
            _flyKeys.Add(e.Key);
            e.Handled = true;
            RequestNextFrameRendering();
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_flyKeys.Remove(e.Key))
            e.Handled = true;
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _flyKeys.Clear();
    }

    private static bool IsFlyKey(Key key) => key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E;

    private void StartFlying() => _lastFlyTick = DateTime.UtcNow;

    private void StopFlying() => _flying = false;

    // Moves the camera for the fly keys held since the last frame; runs each frame and keeps
    // frames coming while a key is down.
    private void AdvanceFlight()
    {
        var now = DateTime.UtcNow;
        float dt = (float)Math.Min(0.1, (now - _lastFlyTick).TotalSeconds);
        _lastFlyTick = now;
        if (_flyKeys.Count == 0)
            return;
        RequestNextFrameRendering();
        var forward = -BackDirection();
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var move = Vector3.Zero;
        if (_flyKeys.Contains(Key.W)) move += forward;
        if (_flyKeys.Contains(Key.S)) move -= forward;
        if (_flyKeys.Contains(Key.D)) move += right;
        if (_flyKeys.Contains(Key.A)) move -= right;
        if (_flyKeys.Contains(Key.E)) move += Vector3.UnitY;
        if (_flyKeys.Contains(Key.Q)) move -= Vector3.UnitY;
        if (move == Vector3.Zero)
            return;
        _center += Vector3.Normalize(move) * (_radius * _flySpeed * dt);
    }

    // Unit vector from the pivot toward the camera.
    private Vector3 BackDirection() => new(
        MathF.Cos(_pitch) * MathF.Sin(_yaw),
        MathF.Sin(_pitch),
        MathF.Cos(_pitch) * MathF.Cos(_yaw));

    private Vector3 EyePosition() => _center + BackDirection() * (_radius * _zoom);
}
