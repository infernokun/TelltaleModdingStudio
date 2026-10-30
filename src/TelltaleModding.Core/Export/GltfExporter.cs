using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;
using TelltaleModding.Core.Textures;

namespace TelltaleModding.Core.Export;

/// <summary>Settings for <see cref="GltfExporter"/>.</summary>
public sealed record GltfExportOptions
{
    /// <summary>Name of the model's mesh and node (e.g. the character's name).</summary>
    public string? Name { get; init; }

    /// <summary>Sampling rate for baked animation clips.</summary>
    public float FramesPerSecond { get; init; } = 30f;

    /// <summary>Converts and embeds normal maps (with vertex tangents).</summary>
    public bool IncludeNormalMaps { get; init; } = true;

    /// <summary>Draws both sides of every face, as the viewer does.</summary>
    public bool DoubleSided { get; init; } = true;

    /// <summary>Alpha threshold of cut-out (MASK) materials such as eyelashes.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Names a bone by its joint symbol; falls back to <see cref="BoneNames"/> when it returns null.</summary>
    public Func<ulong, string?>? BoneName { get; init; }
}

/// <summary>An animation clip to bake: a player with the clip loaded, for the exported model.</summary>
public sealed record GltfClip(string Name, AnimationPlayer Player)
{
    /// <summary>
    /// A fixed pose to bake instead of the player's clip (bone local transforms by joint
    /// symbol; bones it leaves out keep their rest transform), as a single-frame animation.
    /// </summary>
    public IReadOnlyDictionary<ulong, (Vector3 Pos, Quaternion Rot)>? Pose { get; init; }
}

/// <summary>What an export wrote.</summary>
public sealed record GltfExportSummary(
    int Primitives, int Materials, int Textures, int Joints, int Animations, long Bytes);

/// <summary>
/// Writes an assembled model to glTF 2.0 binary (.glb): the visible batches with normals,
/// UVs and their textures as embedded PNGs, the skeleton as a skin (rest pose and inverse
/// bind matrices) and, optionally, animation clips baked to per-bone translation and
/// rotation keys. Telltale models are already Y-up, metres, facing +Z with the character's
/// left on +X, which is glTF's convention, so no axis conversion is applied.
/// </summary>
public static class GltfExporter
{
    private const int Float = 5126, UnsignedByte = 5121, UnsignedShort = 5123, UnsignedInt = 5125;
    private const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    // Matches the viewer's untextured color.
    private static readonly Vector3 UntexturedColor = new(0.72f, 0.72f, 0.75f);

    /// <summary>Writes <paramref name="model"/> as a .glb file to <paramref name="output"/>.</summary>
    /// <param name="clips">Clips to bake; ignored when the model cannot be animated. Each player must have been created for this model's geometry and skeleton.</param>
    public static GltfExportSummary WriteGlb(AssembledModel model, Stream output,
        IReadOnlyList<GltfClip>? clips = null, GltfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new GltfExportOptions();
        if (!(options.FramesPerSecond > 0))
            throw new ArgumentOutOfRangeException(nameof(options), "FramesPerSecond must be positive.");

        var builder = new Builder(model, options);
        builder.Build(model.CanAnimate ? clips ?? Array.Empty<GltfClip>() : Array.Empty<GltfClip>());
        long bytes = builder.WriteGlb(output);
        return builder.Summary(bytes);
    }

    /// <summary>Writes <paramref name="model"/> to a .glb file at <paramref name="path"/>.</summary>
    public static GltfExportSummary WriteGlb(AssembledModel model, string path,
        IReadOnlyList<GltfClip>? clips = null, GltfExportOptions? options = null)
    {
        using var file = File.Create(path);
        return WriteGlb(model, file, clips, options);
    }

    private sealed class Builder
    {
        private readonly AssembledModel _model;
        private readonly MeshGeometry _geometry;
        private readonly GltfExportOptions _options;
        private readonly MemoryStream _bin = new();
        private readonly JsonArray _bufferViews = new(), _accessors = new(), _nodes = new(), _meshes = new(),
            _materials = new(), _textures = new(), _images = new(), _skins = new(), _animations = new();
        private int _primitiveCount;

        public Builder(AssembledModel model, GltfExportOptions options)
        {
            _model = model;
            _geometry = model.Geometry;
            _options = options;
        }

        public GltfExportSummary Summary(long bytes) => new(
            _primitiveCount, _materials.Count, _images.Count,
            _skins.Count > 0 ? _skins[0]!["joints"]!.AsArray().Count : 0, _animations.Count, bytes);

        // ---- Scene ----------------------------------------------------------

        public void Build(IReadOnlyList<GltfClip> clips)
        {
            var skeleton = _model.CanAnimate ? _model.Skeleton : null;
            Skinning? skinning = skeleton is null ? null : ResolveSkinning(skeleton);
            var sceneNodes = new JsonArray();

            int[]? boneNodes = null;
            if (skeleton is not null)
            {
                int armature = AddNode(new JsonObject { ["name"] = "Armature" });
                sceneNodes.Add((JsonNode)armature);
                boneNodes = AddBones(skeleton, armature, skinning!.Value.NeedsStaticJoint, out int? staticNode);
                AddSkin(skeleton, armature, boneNodes, staticNode);
            }

            var mesh = AddMesh(skinning);
            var meshNode = new JsonObject { ["name"] = _options.Name ?? "Model", ["mesh"] = mesh };
            if (skeleton is not null)
                meshNode["skin"] = 0;
            sceneNodes.Add((JsonNode)AddNode(meshNode));

            if (skeleton is not null)
            {
                foreach (var clip in clips)
                    AddAnimation(clip, skeleton, boneNodes!);
            }

            _scene = new JsonObject
            {
                ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Telltale Modding Studio" },
                ["scene"] = 0,
                ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }),
                ["nodes"] = _nodes,
                ["meshes"] = _meshes,
            };
            AddIfAny("materials", _materials);
            AddIfAny("textures", _textures);
            AddIfAny("images", _images);
            if (_images.Count > 0)
                _scene["samplers"] = new JsonArray(new JsonObject
                {
                    ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497,
                });
            AddIfAny("skins", _skins);
            AddIfAny("animations", _animations);
            _scene["accessors"] = _accessors;
            _scene["bufferViews"] = _bufferViews;
            _scene["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = Align4(_bin.Length) });
        }

        private JsonObject _scene = new();

        private void AddIfAny(string key, JsonArray array)
        {
            if (array.Count > 0)
                _scene[key] = array;
        }

        private int AddNode(JsonObject node)
        {
            _nodes.Add(node);
            return _nodes.Count - 1;
        }

        private string BoneName(SkeletonBone bone) =>
            _options.BoneName?.Invoke(bone.JointSymbol) ?? BoneNames.For(bone.JointSymbol);

        /// <summary>One node per bone at its rest transform, parented under the armature node.</summary>
        private int[] AddBones(SkeletonFile skeleton, int armature, bool needsStaticJoint, out int? staticNode)
        {
            var nodes = new int[skeleton.Bones.Count];
            var children = new JsonArray?[skeleton.Bones.Count];
            var rootChildren = new JsonArray();
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                var bone = skeleton.Bones[i];
                var rot = Quaternion.Normalize(bone.LocalRotation);
                nodes[i] = AddNode(new JsonObject
                {
                    ["name"] = BoneName(bone),
                    ["translation"] = Vec(bone.LocalPosition),
                    ["rotation"] = Vec(rot),
                });
                int parent = bone.ParentIndex;
                if (parent >= 0 && parent < i)
                    (children[parent] ??= new JsonArray()).Add((JsonNode)nodes[i]);
                else
                    rootChildren.Add((JsonNode)nodes[i]);
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                if (children[i] is { } list)
                    _nodes[nodes[i]]!["children"] = list;
            }

            // Vertices the viewer leaves unskinned follow a joint that never moves.
            staticNode = null;
            if (needsStaticJoint)
            {
                staticNode = AddNode(new JsonObject { ["name"] = "unskinned" });
                rootChildren.Add((JsonNode)staticNode.Value);
            }
            _nodes[armature]!["children"] = rootChildren;
            return nodes;
        }

        private void AddSkin(SkeletonFile skeleton, int armature, int[] boneNodes, int? staticNode)
        {
            var bind = skeleton.BindPoseWorld();
            int count = bind.Length + (staticNode is null ? 0 : 1);
            var inverse = new Matrix4x4[count];
            for (int i = 0; i < bind.Length; i++)
            {
                if (!Matrix4x4.Invert(bind[i], out inverse[i]))
                    inverse[i] = Matrix4x4.Identity;
                // Keep the affine row exact (inversion leaves tiny round-off there).
                inverse[i].M14 = inverse[i].M24 = inverse[i].M34 = 0;
                inverse[i].M44 = 1;
            }
            if (staticNode is not null)
                inverse[^1] = Matrix4x4.Identity;

            // Row-vector matrices written row by row are the column-vector matrices glTF
            // expects, in its column-major order.
            int view = AddView(MemoryMarshal.AsBytes(inverse.AsSpan()), null);
            int accessor = AddAccessor(view, Float, count, "MAT4");

            var joints = new JsonArray();
            foreach (var node in boneNodes)
                joints.Add((JsonNode)node);
            if (staticNode is not null)
                joints.Add((JsonNode)staticNode.Value);
            _skins.Add(new JsonObject
            {
                ["name"] = _options.Name ?? "Skin",
                ["inverseBindMatrices"] = accessor,
                ["skeleton"] = armature,
                ["joints"] = joints,
            });
        }

        // ---- Mesh -----------------------------------------------------------

        private readonly record struct Skinning(ushort[] Joints, float[] Weights, bool NeedsStaticJoint);

        /// <summary>
        /// Per-vertex joints and normalised weights, resolved the way <see cref="MeshSkinner"/>
        /// does: palette-relative indices through the drawing batch's bone palette into
        /// skeleton bones. Vertices it leaves at rest go to an extra static joint.
        /// </summary>
        private Skinning ResolveSkinning(SkeletonFile skeleton)
        {
            int vertexCount = _geometry.Positions.Length;
            var bones = MeshSkinner.ResolveVertexBones(_geometry, skeleton);

            // Vertices no visible batch draws (e.g. a hidden overlay's) need no real binding.
            var drawn = new bool[vertexCount];
            foreach (var batch in _geometry.Batches)
            {
                int end = Math.Min(_geometry.Indices.Length, batch.StartIndex + batch.PrimitiveCount * 3);
                for (int i = Math.Max(0, batch.StartIndex); i < end; i++)
                {
                    if ((uint)_geometry.Indices[i] < (uint)vertexCount)
                        drawn[_geometry.Indices[i]] = true;
                }
            }

            int staticJoint = skeleton.Bones.Count;
            bool needsStatic = false;
            var joints = new ushort[vertexCount * 4];
            var weights = new float[vertexCount * 4];
            Span<float> w = stackalloc float[4];
            for (int v = 0; v < vertexCount; v++)
            {
                if (!drawn[v])
                {
                    weights[v * 4] = 1f;
                    continue;
                }
                var bw = _geometry.BlendWeights![v];
                (w[0], w[1], w[2], w[3]) = (bw.X, bw.Y, bw.Z, bw.W);
                float total = w[0] + w[1] + w[2] + w[3];
                if (total < 1e-4f || bones[v * 4] < 0)
                {
                    needsStatic = true;
                    joints[v * 4] = (ushort)staticJoint;
                    weights[v * 4] = 1f;
                    continue;
                }

                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    if (w[k] > 0 && bones[v * 4 + k] >= 0)
                        sum += w[k];
                    else
                        w[k] = 0;
                }
                // A bone named in two slots blends linearly, so its weights simply add up.
                for (int k = 1; k < 4; k++)
                {
                    for (int j = 0; j < k && w[k] > 0; j++)
                    {
                        if (w[j] > 0 && bones[v * 4 + j] == bones[v * 4 + k])
                        {
                            w[j] += w[k];
                            w[k] = 0;
                        }
                    }
                }
                if (sum <= 0)
                {
                    needsStatic = true;
                    joints[v * 4] = (ushort)staticJoint;
                    weights[v * 4] = 1f;
                    continue;
                }
                int largest = 0;
                float rest = 0;
                for (int k = 0; k < 4; k++)
                {
                    float value = (float)(w[k] / sum);
                    joints[v * 4 + k] = value > 0 ? (ushort)bones[v * 4 + k] : (ushort)0;
                    weights[v * 4 + k] = value;
                    if (value > weights[v * 4 + largest])
                        largest = k;
                }
                for (int k = 0; k < 4; k++)
                {
                    if (k != largest)
                        rest += weights[v * 4 + k];
                }
                weights[v * 4 + largest] = 1f - rest;
            }
            return new Skinning(joints, weights, needsStatic);
        }

        private readonly record struct MaterialKey(DdsImage? Diffuse, DdsImage? Normal);

        private int AddMesh(Skinning? skinning)
        {
            var g = _geometry;
            int vertexCount = g.Positions.Length;

            // Batches sharing a material become one primitive.
            var groups = new List<(MaterialKey Key, List<MeshBatch> Batches)>();
            for (int b = 0; b < g.Batches.Count; b++)
            {
                var key = new MaterialKey(
                    b < _model.BatchTextures.Count ? _model.BatchTextures[b] : null,
                    _options.IncludeNormalMaps && b < _model.BatchNormalMaps.Count ? _model.BatchNormalMaps[b] : null);
                int at = groups.FindIndex(x => x.Key == key);
                if (at < 0)
                    groups.Add((key, new List<MeshBatch> { g.Batches[b] }));
                else
                    groups[at].Batches.Add(g.Batches[b]);
            }
            if (g.Batches.Count == 0)
                groups.Add((default, new List<MeshBatch> { new(0, g.Indices.Length / 3, -1, 0, vertexCount - 1, -1, -1) }));
            bool anyNormalMap = groups.Any(x => x.Key.Normal is not null) && g.Tangents.Length == vertexCount;

            // Shared vertex attributes.
            var attributes = new JsonObject();
            var positions = g.Positions.Select(Finite).ToArray();
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            if (vertexCount == 0)
                min = max = Vector3.Zero;
            attributes["POSITION"] = AddAccessor(AddView(MemoryMarshal.AsBytes(positions.AsSpan()), ArrayBuffer),
                Float, vertexCount, "VEC3", Vec(min), Vec(max));

            if (g.Normals.Length == vertexCount)
            {
                var normals = g.Normals.Select(n => UnitOr(Finite(n), Vector3.UnitY)).ToArray();
                attributes["NORMAL"] = AddAccessor(AddView(MemoryMarshal.AsBytes(normals.AsSpan()), ArrayBuffer),
                    Float, vertexCount, "VEC3");
            }
            if (g.TexCoords.Length == vertexCount)
            {
                var uvs = g.TexCoords.Select(t => new Vector2(FiniteOr0(t.X), FiniteOr0(t.Y))).ToArray();
                attributes["TEXCOORD_0"] = AddAccessor(AddView(MemoryMarshal.AsBytes(uvs.AsSpan()), ArrayBuffer),
                    Float, vertexCount, "VEC2");
            }
            int? tangentAccessor = null;
            if (anyNormalMap)
            {
                var tangents = new Vector4[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                    tangents[v] = ValidTangent(g.Tangents[v], g.Normals.Length == vertexCount ? g.Normals[v] : Vector3.UnitY);
                tangentAccessor = AddAccessor(AddView(MemoryMarshal.AsBytes(tangents.AsSpan()), ArrayBuffer),
                    Float, vertexCount, "VEC4");
            }
            if (skinning is { } skin)
            {
                bool wide = skin.Joints.Any(j => j > byte.MaxValue);
                attributes["JOINTS_0"] = wide
                    ? AddAccessor(AddView(MemoryMarshal.AsBytes(skin.Joints.AsSpan()), ArrayBuffer), UnsignedShort, vertexCount, "VEC4")
                    : AddAccessor(AddView(skin.Joints.Select(j => (byte)j).ToArray(), ArrayBuffer), UnsignedByte, vertexCount, "VEC4");
                attributes["WEIGHTS_0"] = AddAccessor(AddView(MemoryMarshal.AsBytes(skin.Weights.AsSpan()), ArrayBuffer),
                    Float, vertexCount, "VEC4");
            }

            var diffuseNames = DiffuseNames();
            var imageIndex = new Dictionary<(DdsImage Image, bool Normal), int>();
            var primitives = new JsonArray();
            foreach (var (key, batches) in groups)
            {
                var indices = new List<int>();
                foreach (var batch in batches)
                {
                    int start = Math.Clamp(batch.StartIndex, 0, g.Indices.Length);
                    int end = Math.Clamp(batch.StartIndex + batch.PrimitiveCount * 3, start, g.Indices.Length);
                    end = start + (end - start) / 3 * 3;
                    for (int i = start; i < end; i++)
                        indices.Add(g.Indices[i]);
                }
                if (indices.Count == 0)
                    continue;

                int indexAccessor = vertexCount <= ushort.MaxValue
                    ? AddAccessor(AddView(MemoryMarshal.AsBytes(indices.Select(i => (ushort)i).ToArray().AsSpan()), ElementArrayBuffer),
                        UnsignedShort, indices.Count, "SCALAR")
                    : AddAccessor(AddView(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)), ElementArrayBuffer),
                        UnsignedInt, indices.Count, "SCALAR");

                var primitiveAttributes = attributes.DeepClone().AsObject();
                if (key.Normal is not null && tangentAccessor is not null)
                    primitiveAttributes["TANGENT"] = tangentAccessor.Value;
                primitives.Add(new JsonObject
                {
                    ["attributes"] = primitiveAttributes,
                    ["indices"] = indexAccessor,
                    ["material"] = AddMaterial(key, tangentAccessor is not null, diffuseNames, imageIndex),
                    ["mode"] = 4,
                });
                _primitiveCount++;
            }

            _meshes.Add(new JsonObject { ["name"] = _options.Name ?? "Model", ["primitives"] = primitives });
            return _meshes.Count - 1;
        }

        /// <summary>
        /// The texture file each diffuse image came from. The assembler lists the textures it
        /// used in the order its batches first use them, so the distinct batch images line up
        /// with that list; when they do not, images go unnamed.
        /// </summary>
        private Dictionary<DdsImage, string> DiffuseNames()
        {
            var distinct = new List<DdsImage>();
            foreach (var image in _model.BatchTextures)
            {
                if (image is not null && !distinct.Contains(image))
                    distinct.Add(image);
            }
            var names = new Dictionary<DdsImage, string>();
            if (distinct.Count == _model.TexturesUsed.Count)
            {
                for (int i = 0; i < distinct.Count; i++)
                    names[distinct[i]] = Path.GetFileNameWithoutExtension(_model.TexturesUsed[i]);
            }
            return names;
        }

        private int AddMaterial(MaterialKey key, bool hasTangents, Dictionary<DdsImage, string> diffuseNames,
            Dictionary<(DdsImage, bool), int> imageIndex)
        {
            string name = key.Diffuse is not null && diffuseNames.TryGetValue(key.Diffuse, out var n)
                ? n
                : key.Diffuse is null ? "untextured" : $"material{_materials.Count}";
            var pbr = new JsonObject { ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.8 };
            var material = new JsonObject { ["name"] = name, ["pbrMetallicRoughness"] = pbr };
            if (_options.DoubleSided)
                material["doubleSided"] = true;

            if (key.Diffuse is { } diffuse)
            {
                pbr["baseColorTexture"] = new JsonObject { ["index"] = Texture(diffuse, false, name) };
                switch (ClassifyAlpha(diffuse))
                {
                    case AlphaUse.Cutout:
                        material["alphaMode"] = "MASK";
                        material["alphaCutoff"] = _options.AlphaCutoff;
                        break;
                    case AlphaUse.Blend:
                        material["alphaMode"] = "BLEND";
                        break;
                }
            }
            else
            {
                pbr["baseColorFactor"] = new JsonArray(UntexturedColor.X, UntexturedColor.Y, UntexturedColor.Z, 1.0f);
            }
            if (key.Normal is { } normal && hasTangents)
                material["normalTexture"] = new JsonObject { ["index"] = Texture(normal, true, name + "_normal") };

            _materials.Add(material);
            return _materials.Count - 1;

            int Texture(DdsImage image, bool isNormal, string imageName)
            {
                if (!imageIndex.TryGetValue((image, isNormal), out int index))
                {
                    var png = isNormal ? EncodeNormalMap(image) : PngWriter.Encode(image.Width, image.Height, image.Rgba);
                    int view = AddView(png, null);
                    _images.Add(new JsonObject { ["name"] = imageName, ["bufferView"] = view, ["mimeType"] = "image/png" });
                    _textures.Add(new JsonObject { ["sampler"] = 0, ["source"] = _images.Count - 1 });
                    index = imageIndex[(image, isNormal)] = _textures.Count - 1;
                }
                return index;
            }
        }

        private enum AlphaUse { Opaque, Cutout, Blend }

        /// <summary>
        /// How a color texture's alpha is used. Like the viewer, a texture with more than a
        /// few clearly transparent pixels is see-through, unless they are only a stray handful
        /// (block-compression noise); mostly hard-edged alpha (eyelash and hair cards) is a
        /// cut-out, mostly soft alpha blends.
        /// </summary>
        private static AlphaUse ClassifyAlpha(DdsImage image)
        {
            int transparent = 0, soft = 0;
            var px = image.Rgba;
            for (int i = 3; i < px.Length; i += 4)
            {
                byte a = px[i];
                if (a >= 240)
                    continue;
                transparent++;
                if (a is >= 32 and <= 224)
                    soft++;
            }
            int noise = Math.Max(16, image.Width * image.Height / 2000);
            if (transparent <= noise)
                return AlphaUse.Opaque;
            return soft < transparent / 4 ? AlphaUse.Cutout : AlphaUse.Blend;
        }

        /// <summary>
        /// A standard tangent-space normal map. Telltale's pack X in blue and Y in alpha (the
        /// viewer reconstructs Z), so those become red/green with Z rebuilt into blue; a map
        /// without alpha is taken as an ordinary RGB normal map.
        /// </summary>
        private static byte[] EncodeNormalMap(DdsImage image)
        {
            var src = image.Rgba;
            bool packed = false;
            for (int i = 3; i < src.Length; i += 4)
            {
                if (src[i] < 250)
                {
                    packed = true;
                    break;
                }
            }
            if (!packed)
                return PngWriter.Encode(image.Width, image.Height, src);

            var dst = new byte[src.Length];
            for (int i = 0; i < src.Length; i += 4)
            {
                float x = src[i + 2] / 255f * 2f - 1f;
                float y = src[i + 3] / 255f * 2f - 1f;
                float z = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y));
                dst[i] = src[i + 2];
                dst[i + 1] = src[i + 3];
                dst[i + 2] = (byte)Math.Clamp(MathF.Round((z * 0.5f + 0.5f) * 255f), 0, 255);
                dst[i + 3] = 255;
            }
            return PngWriter.Encode(image.Width, image.Height, dst);
        }

        // ---- Animation --------------------------------------------------------

        /// <summary>
        /// Bakes a clip: every bone gets translation and rotation channels sampled at the
        /// export frame rate from the player's bone-local transforms (a single key where the
        /// value never changes), so each clip fully defines the pose on its own.
        /// </summary>
        private void AddAnimation(GltfClip clip, SkeletonFile skeleton, int[] boneNodes)
        {
            var player = clip.Player;
            if (player.Geometry.Positions.Length != _geometry.Positions.Length)
                throw new ArgumentException($"Clip '{clip.Name}' was set up for a different model.", nameof(clip));

            var pose = clip.Pose;
            float length = pose is null ? Math.Max(0f, player.Length) : 0f;
            float fps = _options.FramesPerSecond;
            // A pose is one frame; a clip needs at least its first and last frame.
            int frames = pose is not null ? 1 : Math.Max(2, (int)MathF.Ceiling(length * fps - 1e-3f) + 1);
            var times = new float[frames];
            for (int f = 0; f < frames; f++)
                times[f] = Math.Min(length, f / fps);
            times[^1] = length;

            int input = AddAccessor(AddView(MemoryMarshal.AsBytes(times.AsSpan()), null), Float, frames, "SCALAR",
                new JsonArray(times[0]), new JsonArray(times[^1]));
            int? singleInput = null;
            int SingleInput() => singleInput ??= AddAccessor(AddView(MemoryMarshal.AsBytes(new[] { 0f }.AsSpan()), null),
                Float, 1, "SCALAR", new JsonArray(0f), new JsonArray(0f));

            var samplers = new JsonArray();
            var channels = new JsonArray();
            var translations = new Vector3[frames];
            var rotations = new Quaternion[frames];
            for (int i = 0; i < skeleton.Bones.Count; i++)
            {
                var bone = skeleton.Bones[i];
                if (pose is null ? player.Animates(bone.JointSymbol) : pose.ContainsKey(bone.JointSymbol))
                {
                    for (int f = 0; f < frames; f++)
                    {
                        var (pos, rot) = pose?[bone.JointSymbol] ?? player.SampleLocal(bone, times[f]);
                        translations[f] = Finite(pos);
                        // A degenerate rotation (zero length or not finite) falls back to the rest rotation.
                        float lengthSq = rot.LengthSquared();
                        rot = float.IsFinite(lengthSq) && lengthSq > 1e-12f ? Quaternion.Normalize(rot) : Quaternion.Normalize(bone.LocalRotation);
                        // Keep neighbouring keys in the same hemisphere so interpolation takes the short way.
                        if (f > 0 && Quaternion.Dot(rot, rotations[f - 1]) < 0)
                            rot = Quaternion.Negate(rot);
                        rotations[f] = rot;
                    }
                }
                else
                {
                    Array.Fill(translations, bone.LocalPosition);
                    Array.Fill(rotations, Quaternion.Normalize(bone.LocalRotation));
                }

                bool constantT = translations.All(t => Vector3.DistanceSquared(t, translations[0]) < 1e-14f);
                bool constantR = rotations.All(r => MathF.Abs(Quaternion.Dot(r, rotations[0])) > 1f - 1e-7f);
                AddChannel("translation", constantT ? SingleInput() : input,
                    MemoryMarshal.AsBytes(constantT ? translations.AsSpan(0, 1) : translations.AsSpan()),
                    constantT ? 1 : frames, "VEC3", boneNodes[i]);
                AddChannel("rotation", constantR ? SingleInput() : input,
                    MemoryMarshal.AsBytes(constantR ? rotations.AsSpan(0, 1) : rotations.AsSpan()),
                    constantR ? 1 : frames, "VEC4", boneNodes[i]);
            }
            _animations.Add(new JsonObject { ["name"] = clip.Name, ["samplers"] = samplers, ["channels"] = channels });

            void AddChannel(string path, int inputAccessor, ReadOnlySpan<byte> values, int count, string type, int node)
            {
                int output = AddAccessor(AddView(values, null), Float, count, type);
                samplers.Add(new JsonObject { ["input"] = inputAccessor, ["output"] = output, ["interpolation"] = "LINEAR" });
                channels.Add(new JsonObject
                {
                    ["sampler"] = samplers.Count - 1,
                    ["target"] = new JsonObject { ["node"] = node, ["path"] = path },
                });
            }
        }

        // ---- Buffers ----------------------------------------------------------

        private int AddView(ReadOnlySpan<byte> data, int? target)
        {
            while (_bin.Length % 4 != 0)
                _bin.WriteByte(0);
            long offset = _bin.Length;
            _bin.Write(data);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length };
            if (target is not null)
                view["target"] = target.Value;
            _bufferViews.Add(view);
            return _bufferViews.Count - 1;
        }

        private int AddAccessor(int view, int componentType, int count, string type,
            JsonArray? min = null, JsonArray? max = null)
        {
            var accessor = new JsonObject
            {
                ["bufferView"] = view,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            };
            if (min is not null)
                accessor["min"] = min;
            if (max is not null)
                accessor["max"] = max;
            _accessors.Add(accessor);
            return _accessors.Count - 1;
        }

        public long WriteGlb(Stream output)
        {
            var json = Encoding.UTF8.GetBytes(_scene.ToJsonString());
            int jsonLength = Align4(json.Length);
            int binLength = Align4(_bin.Length);
            long total = 12 + 8 + jsonLength + (binLength > 0 ? 8 + binLength : 0);
            if (total > uint.MaxValue)
                throw new InvalidOperationException("The model is too large for a single .glb file.");

            Span<byte> header = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x46546C67);         // "glTF"
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)total);
            output.Write(header);

            WriteChunkHeader(output, jsonLength, 0x4E4F534A);                         // "JSON"
            output.Write(json);
            for (int i = json.Length; i < jsonLength; i++)
                output.WriteByte((byte)' ');

            if (binLength > 0)
            {
                WriteChunkHeader(output, binLength, 0x004E4942);                      // "BIN\0"
                _bin.Position = 0;
                _bin.CopyTo(output);
                for (long i = _bin.Length; i < binLength; i++)
                    output.WriteByte(0);
            }
            return total;
        }

        private static void WriteChunkHeader(Stream output, int length, uint type)
        {
            Span<byte> chunk = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk, (uint)length);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], type);
            output.Write(chunk);
        }
    }

    // ---- Helpers ----------------------------------------------------------

    private static int Align4(long length) => checked((int)((length + 3) & ~3L));

    private static JsonArray Vec(Vector3 v) => new(v.X, v.Y, v.Z);

    private static JsonArray Vec(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static float FiniteOr0(float value) => float.IsFinite(value) ? value : 0f;

    private static Vector3 Finite(Vector3 v) => new(FiniteOr0(v.X), FiniteOr0(v.Y), FiniteOr0(v.Z));

    private static Vector3 UnitOr(Vector3 v, Vector3 fallback)
    {
        float length = v.Length();
        return length > 1e-6f ? v / length : fallback;
    }

    /// <summary>A unit tangent with w = ±1; degenerate ones are replaced by any direction perpendicular to the normal.</summary>
    private static Vector4 ValidTangent(Vector4 tangent, Vector3 normal)
    {
        var n = UnitOr(Finite(normal), Vector3.UnitY);
        var t = Finite(new Vector3(tangent.X, tangent.Y, tangent.Z));
        t -= n * Vector3.Dot(n, t);
        if (t.LengthSquared() < 1e-10f)
        {
            t = Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
        }
        t = Vector3.Normalize(t);
        return new Vector4(t, tangent.W < 0 ? -1f : 1f);
    }
}
