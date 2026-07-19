using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.App.Views;

/// <summary>
/// Orbit-camera 3D preview of a decoded mesh. Drag to rotate, wheel to zoom.
/// Renders with a simple headlight-style lambert shade, textured when a
/// diffuse texture is supplied.
/// </summary>
public class MeshViewport : OpenGlControlBase
{
    // GL constants (the subset we use).
    private const int GL_ARRAY_BUFFER = 0x8892, GL_ELEMENT_ARRAY_BUFFER = 0x8893,
        GL_STATIC_DRAW = 0x88E4, GL_FLOAT = 0x1406, GL_TRIANGLES = 0x0004,
        GL_UNSIGNED_INT = 0x1405, GL_DEPTH_TEST = 0x0B71, GL_TEXTURE_2D = 0x0DE1,
        GL_RGBA = 0x1908, GL_UNSIGNED_BYTE = 0x1401, GL_VERTEX_SHADER = 0x8B31,
        GL_FRAGMENT_SHADER = 0x8B30, GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x0100,
        GL_LINEAR = 0x2601, GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800;

    private float[]? _pendingVertices;
    private int[]? _pendingIndices;
    private IReadOnlyList<DdsImage?>? _pendingBatchTextures;
    private IReadOnlyList<MeshBatch>? _pendingBatches;
    private bool _meshDirty, _textureDirty;

    private int _program, _vao, _vbo, _ebo;
    private int _uMvp = -1, _uHasTex = -1;
    private int _indexCount;
    private readonly List<int> _textures = new();          // GL ids, parallel to _batchTextureSlots
    private int[] _batchTextureSlots = Array.Empty<int>(); // per-batch index into _textures, -1 = none
    private MeshBatch[] _batches = Array.Empty<MeshBatch>();
    private bool _glReady;

    private Vector3 _center;
    private float _radius = 1f;
    private float _yaw = 0.8f, _pitch = 0.35f, _zoom = 2.4f;
    private Point _lastPointer;
    private bool _dragging;

    /// <summary>
    /// Loads a decoded mesh into the viewport. <paramref name="batchTextures"/> holds
    /// one entry per batch of the geometry (null where no texture matched).
    /// </summary>
    public void SetMesh(MeshGeometry geometry, IReadOnlyList<DdsImage?> batchTextures)
    {
        var v = new float[geometry.Positions.Length * 8];
        for (int i = 0; i < geometry.Positions.Length; i++)
        {
            int at = i * 8;
            v[at] = geometry.Positions[i].X;
            v[at + 1] = geometry.Positions[i].Y;
            v[at + 2] = geometry.Positions[i].Z;
            v[at + 3] = geometry.Normals[i].X;
            v[at + 4] = geometry.Normals[i].Y;
            v[at + 5] = geometry.Normals[i].Z;
            v[at + 6] = geometry.TexCoords[i].X;
            v[at + 7] = geometry.TexCoords[i].Y;
        }

        _pendingVertices = v;
        _pendingIndices = geometry.Indices;
        _pendingBatches = geometry.Batches;
        _pendingBatchTextures = batchTextures;
        _meshDirty = _textureDirty = true;

        _center = (geometry.BoundsMin + geometry.BoundsMax) / 2f;
        _radius = Math.Max(0.01f, (geometry.BoundsMax - geometry.BoundsMin).Length() / 2f);
        _zoom = 2.4f;
        RequestNextFrameRendering();
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
            uniform mat4 uMvp;
            out vec3 vNormal;
            out vec2 vUv;
            void main()
            {
                gl_Position = uMvp * vec4(aPos, 1.0);
                vNormal = aNormal;
                vUv = aUv;
            }
            """);
        int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        var fsError = gl.CompileShaderAndGetError(fs, ShaderHeader + """
            in vec3 vNormal;
            in vec2 vUv;
            uniform sampler2D uTex;
            uniform float uHasTex;
            out vec4 outColor;
            void main()
            {
                float lit = 1.0;
                if (length(vNormal) > 0.1)
                {
                    vec3 n = normalize(vNormal);
                    lit = 0.35 + 0.65 * abs(dot(n, normalize(vec3(0.4, 0.75, 0.55))));
                }
                vec3 base = mix(vec3(0.72, 0.72, 0.75), texture(uTex, vUv).rgb, uHasTex);
                outColor = vec4(base * lit, 1.0);
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

            int aPos = gl.GetAttribLocationString(_program, "aPos");
            int aNormal = gl.GetAttribLocationString(_program, "aNormal");
            int aUv = gl.GetAttribLocationString(_program, "aUv");
            gl.VertexAttribPointer(aPos, 3, GL_FLOAT, 0, 32, IntPtr.Zero);
            gl.EnableVertexAttribArray(aPos);
            if (aNormal >= 0)
            {
                gl.VertexAttribPointer(aNormal, 3, GL_FLOAT, 0, 32, new IntPtr(12));
                gl.EnableVertexAttribArray(aNormal);
            }
            if (aUv >= 0)
            {
                gl.VertexAttribPointer(aUv, 2, GL_FLOAT, 0, 32, new IntPtr(24));
                gl.EnableVertexAttribArray(aUv);
            }
        }

        if (_textureDirty)
        {
            _textureDirty = false;
            DeleteTextures(gl);
            _batches = _pendingBatches?.ToArray() ?? Array.Empty<MeshBatch>();
            _batchTextureSlots = new int[_batches.Length];
            Array.Fill(_batchTextureSlots, -1);

            // Upload each distinct image once; batches sharing an image share a slot.
            var slotByImage = new Dictionary<DdsImage, int>();
            for (int b = 0; b < _batches.Length; b++)
            {
                var image = _pendingBatchTextures is not null && b < _pendingBatchTextures.Count
                    ? _pendingBatchTextures[b] : null;
                if (image is null)
                    continue;
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
                    slotByImage[image] = slot;
                }
                _batchTextureSlots[b] = slot;
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

        if (_batches.Length == 0)
        {
            gl.Uniform1f(_uHasTex, 0f);
            gl.DrawElements(GL_TRIANGLES, _indexCount, GL_UNSIGNED_INT, IntPtr.Zero);
        }
        else
        {
            for (int b = 0; b < _batches.Length; b++)
            {
                int slot = _batchTextureSlots[b];
                if (slot >= 0)
                    gl.BindTexture(GL_TEXTURE_2D, _textures[slot]);
                gl.Uniform1f(_uHasTex, slot >= 0 ? 1f : 0f);
                gl.DrawElements(GL_TRIANGLES, _batches[b].PrimitiveCount * 3, GL_UNSIGNED_INT,
                    new IntPtr(_batches[b].StartIndex * sizeof(int)));
            }
        }
        gl.BindVertexArray(0);
    }

    // ---- Orbit interaction ----------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
            return;
        var p = e.GetPosition(this);
        _yaw -= (float)(p.X - _lastPointer.X) * 0.01f;
        _pitch += (float)(p.Y - _lastPointer.Y) * 0.01f;
        _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);
        _lastPointer = p;
        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 0.85f : 1.18f), 1.05f, 20f);
        RequestNextFrameRendering();
    }
}
