using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.App.Views;

/// <summary>
/// Orbit-camera 3D preview of a decoded mesh. Left-drag rotates, right- or
/// middle-drag pans, wheel zooms, double-click resets the view. Renders with a
/// simple headlight-style lambert shade, textured when a diffuse texture is supplied.
/// </summary>
public class MeshViewport : OpenGlControlBase
{
    // GL constants (the subset we use).
    private const int GL_ARRAY_BUFFER = 0x8892, GL_ELEMENT_ARRAY_BUFFER = 0x8893,
        GL_STATIC_DRAW = 0x88E4, GL_FLOAT = 0x1406, GL_TRIANGLES = 0x0004,
        GL_UNSIGNED_INT = 0x1405, GL_DEPTH_TEST = 0x0B71, GL_TEXTURE_2D = 0x0DE1,
        GL_RGBA = 0x1908, GL_UNSIGNED_BYTE = 0x1401, GL_VERTEX_SHADER = 0x8B31,
        GL_FRAGMENT_SHADER = 0x8B30, GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x0100,
        GL_LINEAR = 0x2601, GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800,
        GL_TEXTURE0 = 0x84C0, GL_TEXTURE1 = 0x84C1,
        GL_BLEND = 0x0BE2, GL_SRC_ALPHA = 0x0302, GL_ONE_MINUS_SRC_ALPHA = 0x0303;

    private const int VertexFloats = 12; // pos3 + normal3 + uv2 + tangent4

    private float[]? _pendingVertices;
    private int[]? _pendingIndices;
    private IReadOnlyList<DdsImage?>? _pendingBatchTextures;
    private IReadOnlyList<DdsImage?>? _pendingBatchNormalMaps;
    private IReadOnlyList<MeshBatch>? _pendingBatches;
    private bool _meshDirty, _textureDirty;

    private int _program, _vao, _vbo, _ebo;
    private int _uMvp = -1, _uHasTex = -1, _uHasNmap = -1;
    private Action<int, int>? _uniform1i;
    private Action<int, int>? _blendFunc;
    private int _uTex = -1, _uNmap = -1;
    private int _indexCount;
    private readonly List<int> _textures = new();          // GL ids shared by both slot arrays
    private readonly List<bool> _textureHasAlpha = new();  // parallel to _textures
    private int[] _batchTextureSlots = Array.Empty<int>(); // per-batch index into _textures, -1 = none
    private int[] _batchNormalSlots = Array.Empty<int>();
    private MeshBatch[] _batches = Array.Empty<MeshBatch>();
    private bool _glReady;

    private Vector3 _center, _homeCenter;
    private float _radius = 1f;
    private float _yaw = 0.8f, _pitch = 0.35f, _zoom = 2.4f;
    private Point _lastPointer;
    private bool _rotating, _panning;

    /// <summary>
    /// Loads a decoded mesh into the viewport. The per-batch lists hold one entry
    /// per batch of the geometry (null where nothing matched).
    /// </summary>
    public void SetMesh(MeshGeometry geometry, IReadOnlyList<DdsImage?> batchTextures,
        IReadOnlyList<DdsImage?>? batchNormalMaps = null)
    {
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
        }

        _pendingVertices = v;
        _pendingIndices = geometry.Indices;
        _pendingBatches = geometry.Batches;
        _pendingBatchTextures = batchTextures;
        _pendingBatchNormalMaps = batchNormalMaps;
        _meshDirty = _textureDirty = true;

        _homeCenter = _center = (geometry.BoundsMin + geometry.BoundsMax) / 2f;
        _radius = Math.Max(0.01f, (geometry.BoundsMax - geometry.BoundsMin).Length() / 2f);
        _zoom = 2.4f;
        RequestNextFrameRendering();
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
    private delegate void GlUniform1i(int location, int value);

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

    private string ShaderHeader =>
        GlVersion.Type == GlProfileType.OpenGL
            ? "#version 330 core\n"
            : "#version 300 es\nprecision mediump float;\n";

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _program = gl.CreateProgram();
        int vs = gl.CreateShader(GL_VERTEX_SHADER);
        var vsError = gl.CompileShaderAndGetError(vs, ShaderHeader + """
            in vec3 aPos;
            in vec3 aNormal;
            in vec2 aUv;
            in vec4 aTangent;
            uniform mat4 uMvp;
            out vec3 vNormal;
            out vec2 vUv;
            out vec4 vTangent;
            void main()
            {
                gl_Position = uMvp * vec4(aPos, 1.0);
                vNormal = aNormal;
                vUv = aUv;
                vTangent = aTangent;
            }
            """);
        int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        var fsError = gl.CompileShaderAndGetError(fs, ShaderHeader + """
            in vec3 vNormal;
            in vec2 vUv;
            in vec4 vTangent;
            uniform sampler2D uTex;
            uniform sampler2D uNmap;
            uniform float uHasTex;
            uniform float uHasNmap;
            out vec4 outColor;
            void main()
            {
                float lit = 1.0;
                if (length(vNormal) > 0.1)
                {
                    vec3 n = normalize(vNormal);
                    if (uHasNmap > 0.5 && length(vTangent.xyz) > 0.1)
                    {
                        // Telltale BC3 normal maps pack X in blue, Y in alpha; reconstruct Z.
                        vec4 packed = texture(uNmap, vUv);
                        vec2 nxy = packed.ba * 2.0 - 1.0;
                        vec3 tsn = vec3(nxy, sqrt(max(0.0, 1.0 - dot(nxy, nxy))));
                        vec3 t = normalize(vTangent.xyz - n * dot(n, vTangent.xyz));
                        float handed = vTangent.w < 0.0 ? -1.0 : 1.0;
                        vec3 b = cross(n, t) * handed;
                        n = normalize(mat3(t, b, n) * tsn);
                    }
                    lit = 0.35 + 0.65 * abs(dot(n, normalize(vec3(0.4, 0.75, 0.55))));
                }
                vec4 texel = texture(uTex, vUv);
                vec3 base = mix(vec3(0.72, 0.72, 0.75), texel.rgb, uHasTex);
                float alpha = mix(1.0, texel.a, uHasTex);
                if (alpha < 0.04)
                    discard;
                outColor = vec4(base * lit, alpha);
            }
            """);
        if (vsError is not null || fsError is not null)
            throw new InvalidOperationException($"shader compile failed: {vsError ?? fsError}");

        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        var linkError = gl.LinkProgramAndGetError(_program);
        if (linkError is not null)
            throw new InvalidOperationException($"shader link failed: {linkError}");
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        _uMvp = gl.GetUniformLocationString(_program, "uMvp");
        _uHasTex = gl.GetUniformLocationString(_program, "uHasTex");
        _uHasNmap = gl.GetUniformLocationString(_program, "uHasNmap");
        _uTex = gl.GetUniformLocationString(_program, "uTex");
        _uNmap = gl.GetUniformLocationString(_program, "uNmap");

        // GlInterface has no Uniform1i/BlendFunc; fetch them directly.
        var uniform1iPtr = gl.GetProcAddress("glUniform1i");
        if (uniform1iPtr != IntPtr.Zero)
            _uniform1i = System.Runtime.InteropServices.Marshal
                .GetDelegateForFunctionPointer<GlUniform1i>(uniform1iPtr).Invoke;
        var blendFuncPtr = gl.GetProcAddress("glBlendFunc");
        if (blendFuncPtr != IntPtr.Zero)
            _blendFunc = System.Runtime.InteropServices.Marshal
                .GetDelegateForFunctionPointer<GlUniform1i>(blendFuncPtr).Invoke;

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        _glReady = true;
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _glReady = false;
        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_ebo);
        gl.DeleteVertexArray(_vao);
        DeleteTextures(gl);
        gl.DeleteProgram(_program);
    }

    private void DeleteTextures(GlInterface gl)
    {
        foreach (var id in _textures)
            gl.DeleteTexture(id);
        _textures.Clear();
        _textureHasAlpha.Clear();
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        gl.ClearColor(0.13f, 0.13f, 0.15f, 1f);
        gl.ClearDepth(1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        if (!_glReady)
            return;

        // Bounds are logical units; the framebuffer is at physical pixels.
        double scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        gl.Viewport(0, 0, (int)(Bounds.Width * scaling), (int)(Bounds.Height * scaling));
        gl.Enable(GL_DEPTH_TEST);
        gl.UseProgram(_program);
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
            int aPos = gl.GetAttribLocationString(_program, "aPos");
            int aNormal = gl.GetAttribLocationString(_program, "aNormal");
            int aUv = gl.GetAttribLocationString(_program, "aUv");
            int aTangent = gl.GetAttribLocationString(_program, "aTangent");
            gl.VertexAttribPointer(aPos, 3, GL_FLOAT, 0, stride, IntPtr.Zero);
            gl.EnableVertexAttribArray(aPos);
            if (aNormal >= 0)
            {
                gl.VertexAttribPointer(aNormal, 3, GL_FLOAT, 0, stride, new IntPtr(12));
                gl.EnableVertexAttribArray(aNormal);
            }
            if (aUv >= 0)
            {
                gl.VertexAttribPointer(aUv, 2, GL_FLOAT, 0, stride, new IntPtr(24));
                gl.EnableVertexAttribArray(aUv);
            }
            if (aTangent >= 0)
            {
                gl.VertexAttribPointer(aTangent, 4, GL_FLOAT, 0, stride, new IntPtr(32));
                gl.EnableVertexAttribArray(aTangent);
            }
        }

        if (_textureDirty)
        {
            _textureDirty = false;
            DeleteTextures(gl);
            _batches = _pendingBatches?.ToArray() ?? Array.Empty<MeshBatch>();
            _batchTextureSlots = new int[_batches.Length];
            _batchNormalSlots = new int[_batches.Length];
            Array.Fill(_batchTextureSlots, -1);
            Array.Fill(_batchNormalSlots, -1);

            // Upload each distinct image once; batches sharing an image share a slot.
            var slotByImage = new Dictionary<DdsImage, int>();
            int SlotFor(DdsImage? image)
            {
                if (image is null)
                    return -1;
                if (!slotByImage.TryGetValue(image, out int slot))
                {
                    int id = gl.GenTexture();
                    gl.BindTexture(GL_TEXTURE_2D, id);
                    fixed (byte* p = image.Rgba)
                        gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, image.Width, image.Height, 0, GL_RGBA, GL_UNSIGNED_BYTE, new IntPtr(p));
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                    slot = _textures.Count;
                    _textures.Add(id);
                    _textureHasAlpha.Add(HasMeaningfulAlpha(image));
                    slotByImage[image] = slot;
                }
                return slot;
            }

            for (int b = 0; b < _batches.Length; b++)
            {
                _batchTextureSlots[b] = SlotFor(
                    _pendingBatchTextures is not null && b < _pendingBatchTextures.Count ? _pendingBatchTextures[b] : null);
                _batchNormalSlots[b] = SlotFor(
                    _pendingBatchNormalMaps is not null && b < _pendingBatchNormalMaps.Count ? _pendingBatchNormalMaps[b] : null);
            }
        }

        if (_indexCount == 0)
            return;

        float aspect = (float)(Bounds.Width / Math.Max(1.0, Bounds.Height));
        float distance = _radius * _zoom;
        var eye = _center + new Vector3(
            distance * MathF.Cos(_pitch) * MathF.Sin(_yaw),
            distance * MathF.Sin(_pitch),
            distance * MathF.Cos(_pitch) * MathF.Cos(_yaw));
        var view = Matrix4x4.CreateLookAt(eye, _center, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, aspect, _radius * 0.02f, _radius * 50f);

        // .NET matrices are row-major; GL reads column-major, so passing the bytes
        // unchanged yields the transpose — exactly what column-vector shaders need.
        var mvp = view * proj;
        gl.UniformMatrix4fv(_uMvp, 1, false, &mvp);

        _uniform1i?.Invoke(_uTex, 0);
        _uniform1i?.Invoke(_uNmap, 1);
        _blendFunc?.Invoke(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
        gl.Enable(GL_BLEND);

        if (_batches.Length == 0)
        {
            gl.Uniform1f(_uHasTex, 0f);
            gl.Uniform1f(_uHasNmap, 0f);
            gl.DrawElements(GL_TRIANGLES, _indexCount, GL_UNSIGNED_INT, IntPtr.Zero);
        }
        else
        {
            // Opaque batches first, alpha-textured batches after, so hair/eyes
            // blend over the surfaces behind them.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int b = 0; b < _batches.Length; b++)
                {
                    int slot = _batchTextureSlots[b];
                    bool alphaBatch = slot >= 0 && _textureHasAlpha[slot];
                    if (alphaBatch != (pass == 1))
                        continue;

                    int nmapSlot = _uniform1i is null ? -1 : _batchNormalSlots[b];
                    if (slot >= 0)
                    {
                        gl.ActiveTexture(GL_TEXTURE0);
                        gl.BindTexture(GL_TEXTURE_2D, _textures[slot]);
                    }
                    if (nmapSlot >= 0)
                    {
                        gl.ActiveTexture(GL_TEXTURE1);
                        gl.BindTexture(GL_TEXTURE_2D, _textures[nmapSlot]);
                        gl.ActiveTexture(GL_TEXTURE0);
                    }
                    gl.Uniform1f(_uHasTex, slot >= 0 ? 1f : 0f);
                    gl.Uniform1f(_uHasNmap, nmapSlot >= 0 ? 1f : 0f);
                    gl.DrawElements(GL_TRIANGLES, _batches[b].PrimitiveCount * 3, GL_UNSIGNED_INT,
                        new IntPtr(_batches[b].StartIndex * sizeof(int)));
                }
            }
        }
        gl.Disable(GL_BLEND);
        gl.BindVertexArray(0);
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
            // Double-click: reset the view.
            _center = _homeCenter;
            _yaw = 0.8f; _pitch = 0.35f; _zoom = 2.4f;
            RequestNextFrameRendering();
            return;
        }

        var props = e.GetCurrentPoint(this).Properties;
        _rotating = props.IsLeftButtonPressed;
        _panning = props.IsRightButtonPressed || props.IsMiddleButtonPressed;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    public void HandlePointerMoved(PointerEventArgs e)
    {
        if (!_rotating && !_panning)
            return;
        e.Handled = true;
        var p = e.GetPosition(this);
        float dx = (float)(p.X - _lastPointer.X);
        float dy = (float)(p.Y - _lastPointer.Y);
        _lastPointer = p;

        if (_rotating)
        {
            _yaw -= dx * 0.01f;
            _pitch += dy * 0.01f;
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
        e.Pointer.Capture(null);
    }

    public void HandlePointerWheel(PointerWheelEventArgs e)
    {
        if (e.Handled)
            return;
        e.Handled = true;
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 0.85f : 1.18f), 1.05f, 20f);
        RequestNextFrameRendering();
    }
}
