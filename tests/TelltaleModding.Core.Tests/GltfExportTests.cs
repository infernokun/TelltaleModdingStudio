using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using TelltaleModding.Core.Export;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

public class GltfExportTests
{
    private static ModelAssembler SamplesAssembler() => new(new FolderAssetResolver(SampleData.SamplesDir));

    private static AssembledModel Clementine()
    {
        string[] meshes = { "sk62_clementine400_head.d3dmesh", "sk62_clementine400_eyesMouth.d3dmesh" };
        SampleData.Require(meshes.Append("sk62_clementine400.skl").ToArray());
        return SamplesAssembler().Build(meshes, "sk62_clementine400.skl");
    }

    // Characters with a clip in samples/: compressed pose keys (Tales from the Borderlands),
    // keyframed tracks (Wolf Among Us) and a Michonne-era clip.
    public static TheoryData<string, string, string> AnimatedCharacters => new()
    {
        { "sk55_sasha.d3dmesh", "sk55_sasha.skl", "sk55_idle_sashaHandsHips.anm" },
        { "sk54_bigby_head.d3dmesh", "sk54_bigby.skl", "sk54_bigbyCrouch_weightShiftAShort.anm" },
        { "skB4_banditMarauder_body.d3dmesh", "skB4_banditMarauder.skl", "skB4_banditSquat_toRun.anm" },
    };

    private static (AssembledModel Model, AnimationPlayer Player) Animated(string mesh, string skeleton, string anim)
    {
        SampleData.Require(mesh, skeleton);
        var model = SamplesAssembler().Build(new[] { mesh }, skeleton);
        Assert.True(model.CanAnimate);
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(anim)), out var clip, out var error), error);
        var player = new AnimationPlayer(model.Geometry, model.Skeleton!);
        player.Load(clip!, anim);
        Assert.True(player.HasMotion);
        return (model, player);
    }

    private static Glb Export(AssembledModel model, IReadOnlyList<GltfClip>? clips = null, GltfExportOptions? options = null)
    {
        using var stream = new MemoryStream();
        var summary = GltfExporter.WriteGlb(model, stream, clips, options);
        Assert.Equal(stream.Length, summary.Bytes);
        return Glb.Parse(stream.ToArray());
    }

    [SkippableFact]
    public void Writes_a_well_formed_glb()
    {
        var model = Clementine();
        var glb = Export(model, options: new GltfExportOptions { Name = "Clementine" });
        var root = glb.Json;

        Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
        Assert.Equal(glb.Bin.Length, root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32());

        // Every buffer view lies inside the buffer, every accessor inside its view.
        foreach (var view in root.GetProperty("bufferViews").EnumerateArray())
        {
            int offset = view.GetProperty("byteOffset").GetInt32(), length = view.GetProperty("byteLength").GetInt32();
            Assert.True(offset >= 0 && length > 0 && offset + length <= glb.Bin.Length);
            Assert.Equal(0, offset % 4);
        }
        foreach (var accessor in root.GetProperty("accessors").EnumerateArray())
        {
            var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            int size = Glb.ElementSize(accessor) * accessor.GetProperty("count").GetInt32();
            Assert.True(size <= view.GetProperty("byteLength").GetInt32());
        }

        // Positions: bounds match the data, and the data is the model's rest pose.
        var primitives = root.GetProperty("meshes")[0].GetProperty("primitives");
        int position = primitives[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
        var positions = glb.Vec3(position);
        Assert.Equal(model.Geometry.Positions, positions);
        var accessorJson = root.GetProperty("accessors")[position];
        var min = accessorJson.GetProperty("min").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var max = accessorJson.GetProperty("max").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(new[] { positions.Min(p => p.X), positions.Min(p => p.Y), positions.Min(p => p.Z) }, min);
        Assert.Equal(new[] { positions.Max(p => p.X), positions.Max(p => p.Y), positions.Max(p => p.Z) }, max);

        // Exactly the visible batches are drawn (the hidden eye-wetness shell stays out),
        // with indices in range.
        int drawn = 0;
        foreach (var primitive in primitives.EnumerateArray())
        {
            var indices = glb.Indices(primitive.GetProperty("indices").GetInt32());
            Assert.All(indices, i => Assert.InRange(i, 0, positions.Length - 1));
            drawn += indices.Length;
            Assert.True(primitive.TryGetProperty("material", out _));
        }
        Assert.Equal(model.Geometry.Batches.Sum(b => b.PrimitiveCount) * 3, drawn);

        // Skin: one joint per skeleton bone, named, with inverse bind matrices.
        var skin = root.GetProperty("skins")[0];
        var joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
        Assert.Equal(model.Skeleton!.Bones.Count, joints.Length);
        Assert.Equal(joints.Length, root.GetProperty("accessors")[skin.GetProperty("inverseBindMatrices").GetInt32()]
            .GetProperty("count").GetInt32());
        var names = joints.Select(j => root.GetProperty("nodes")[j].GetProperty("name").GetString()).ToList();
        Assert.Contains("head", names);
        Assert.Contains("eye_L", names);
        Assert.Equal(names.Count, names.Distinct().Count());

        // Weights are normalised; joints index the skin.
        var attributes = primitives[0].GetProperty("attributes");
        var weights = glb.Floats(attributes.GetProperty("WEIGHTS_0").GetInt32());
        var vertexJoints = glb.Indices(attributes.GetProperty("JOINTS_0").GetInt32());
        for (int v = 0; v < positions.Length; v++)
            Assert.InRange(weights[v * 4] + weights[v * 4 + 1] + weights[v * 4 + 2] + weights[v * 4 + 3], 0.9999f, 1.0001f);
        Assert.All(vertexJoints, j => Assert.InRange(j, 0, joints.Length - 1));

        // Textures are embedded PNGs; materials are not metallic.
        var images = root.GetProperty("images").EnumerateArray().ToList();
        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            var png = glb.View(image.GetProperty("bufferView").GetInt32());
            Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }));
        }
        Assert.Contains(images, i => i.GetProperty("name").GetString() == "sk62_clementine400_head");
        Assert.All(root.GetProperty("materials").EnumerateArray(), m =>
            Assert.Equal(0, m.GetProperty("pbrMetallicRoughness").GetProperty("metallicFactor").GetDouble()));
    }

    [SkippableFact]
    public void Model_without_a_skeleton_exports_without_skin()
    {
        var model = SamplesAssembler().Build(new[] { "obj_screenCUILayer1.d3dmesh" });
        Assert.False(model.CanAnimate);
        var glb = Export(model);
        Assert.False(glb.Json.TryGetProperty("skins", out _));
        Assert.Equal(model.Geometry.Positions, glb.Vec3(
            glb.Json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32()));
    }

    [SkippableFact]
    public void Rest_pose_skins_to_the_mesh_as_authored()
    {
        var model = Clementine();
        var glb = Export(model);
        var (positions, _) = glb.Skin(animation: null, time: 0);
        AssertClose(model.Geometry.Positions, positions, 1e-4f);
    }

    [SkippableTheory]
    [MemberData(nameof(AnimatedCharacters))]
    public void Baked_animation_reproduces_the_players_pose(string mesh, string skeleton, string anim)
    {
        var (model, player) = Animated(mesh, skeleton, anim);
        var glb = Export(model, new[] { new GltfClip("clip", player) });

        // A vertex names each joint at most once (some meshes repeat a bone across slots).
        var attributes = glb.Json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
        var vertexJoints = glb.Indices(attributes.GetProperty("JOINTS_0").GetInt32());
        var weights = glb.Floats(attributes.GetProperty("WEIGHTS_0").GetInt32());
        for (int v = 0; v < weights.Length / 4; v++)
        {
            var used = Enumerable.Range(0, 4).Where(k => weights[v * 4 + k] > 0).Select(k => vertexJoints[v * 4 + k]).ToList();
            Assert.Equal(used.Count, used.Distinct().Count());
        }

        var animation = glb.Json.GetProperty("animations")[0];
        Assert.Equal("clip", animation.GetProperty("name").GetString());
        var times = glb.AnimationTimes(animation);
        Assert.Equal(player.Length, times[^1], 1e-5f);
        Assert.Equal((int)MathF.Ceiling(player.Length * 30f - 1e-3f) + 1, times.Length);

        var expected = new Vector3[model.Geometry.Positions.Length];
        var expectedNormals = new Vector3[expected.Length];
        foreach (int frame in new[] { 0, times.Length / 3, 2 * times.Length / 3, times.Length - 1 })
        {
            float t = times[frame];
            player.Evaluate(t, expected, expectedNormals);
            var (positions, normals) = glb.Skin(animation, t);
            AssertClose(expected, positions, 1e-4f);
            AssertClose(expectedNormals, normals, 1e-3f);
        }

        // Between keys the glTF interpolation stays close to the player's own sampling.
        float between = (times[1] + times[2]) / 2;
        player.Evaluate(between, expected, expectedNormals);
        AssertClose(expected, glb.Skin(animation, between).Positions, 5e-3f);
    }

    [SkippableFact]
    public void Exports_several_clips_as_separate_animations()
    {
        var (model, sasha) = Animated("sk55_sasha.d3dmesh", "sk55_sasha.skl", "sk55_idle_sashaHandsHips.anm");
        var second = new AnimationPlayer(model.Geometry, model.Skeleton!);
        const string name = "sk55_sasha103_sk55_idle_fionaHoldsSashaDying.anm";
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(name)), out var clip, out _));
        second.Load(clip!, name);

        var glb = Export(model, new[] { new GltfClip("handsHips", sasha), new GltfClip("dying", second) });
        var animations = glb.Json.GetProperty("animations").EnumerateArray().ToList();
        Assert.Equal(new[] { "handsHips", "dying" }, animations.Select(a => a.GetProperty("name").GetString()));
        // Each clip drives every bone, so switching clips never leaves a stale pose behind.
        int bones = model.Skeleton!.Bones.Count;
        Assert.All(animations, a => Assert.Equal(bones * 2, a.GetProperty("channels").GetArrayLength()));

        var expected = new Vector3[model.Geometry.Positions.Length];
        var normals = new Vector3[expected.Length];
        second.Evaluate(second.Length, expected, normals);
        AssertClose(expected, glb.Skin(animations[1], second.Length).Positions, 1e-4f);
    }

    private static void AssertClose(Vector3[] expected, Vector3[] actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        float worst = 0;
        int worstAt = -1;
        for (int i = 0; i < expected.Length; i++)
        {
            float d = Vector3.Distance(expected[i], actual[i]);
            if (d > worst)
                (worst, worstAt) = (d, i);
        }
        Assert.True(worst <= tolerance, $"vertex {worstAt} off by {worst} (expected {expected[Math.Max(0, worstAt)]}, got {actual[Math.Max(0, worstAt)]})");
    }

    /// <summary>A minimal .glb reader: the chunks, accessors, and skinning of the first mesh.</summary>
    private sealed class Glb
    {
        public required JsonElement Json { get; init; }
        public required byte[] Bin { get; init; }

        public static Glb Parse(byte[] file)
        {
            Assert.True(file.Length >= 20);
            Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(file));
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
            Assert.Equal((uint)file.Length, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));

            int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12));
            Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)));
            Assert.Equal(0, jsonLength % 4);
            var json = JsonDocument.Parse(Encoding.UTF8.GetString(file, 20, jsonLength)).RootElement.Clone();

            int binAt = 20 + jsonLength;
            int binLength = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(binAt));
            Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(binAt + 4)));
            Assert.Equal(0, binLength % 4);
            Assert.Equal(file.Length, binAt + 8 + binLength);
            return new Glb { Json = json, Bin = file.AsSpan(binAt + 8, binLength).ToArray() };
        }

        public static int ElementSize(JsonElement accessor)
        {
            int component = accessor.GetProperty("componentType").GetInt32() switch
            {
                5120 or 5121 => 1,
                5122 or 5123 => 2,
                _ => 4,
            };
            int components = accessor.GetProperty("type").GetString() switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
                var t => throw new InvalidDataException(t),
            };
            return component * components;
        }

        public byte[] View(int index)
        {
            var view = Json.GetProperty("bufferViews")[index];
            return Bin.AsSpan(view.GetProperty("byteOffset").GetInt32(), view.GetProperty("byteLength").GetInt32()).ToArray();
        }

        private (byte[] Data, JsonElement Accessor) Raw(int index)
        {
            var accessor = Json.GetProperty("accessors")[index];
            var data = View(accessor.GetProperty("bufferView").GetInt32());
            return (data.AsSpan(0, ElementSize(accessor) * accessor.GetProperty("count").GetInt32()).ToArray(), accessor);
        }

        public float[] Floats(int index)
        {
            var (data, accessor) = Raw(index);
            Assert.Equal(5126, accessor.GetProperty("componentType").GetInt32());
            var values = new float[data.Length / 4];
            Buffer.BlockCopy(data, 0, values, 0, data.Length);
            return values;
        }

        public int[] Indices(int index)
        {
            var (data, accessor) = Raw(index);
            return accessor.GetProperty("componentType").GetInt32() switch
            {
                5121 => data.Select(b => (int)b).ToArray(),
                5123 => Enumerable.Range(0, data.Length / 2).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2))).ToArray(),
                5125 => Enumerable.Range(0, data.Length / 4).Select(i => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i * 4))).ToArray(),
                var c => throw new InvalidDataException($"component type {c}"),
            };
        }

        public Vector3[] Vec3(int index)
        {
            var f = Floats(index);
            return Enumerable.Range(0, f.Length / 3).Select(i => new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2])).ToArray();
        }

        public float[] AnimationTimes(JsonElement animation) =>
            animation.GetProperty("samplers").EnumerateArray()
                .Select(s => Floats(s.GetProperty("input").GetInt32()))
                .MaxBy(t => t.Length)!;

        /// <summary>
        /// Skins the first mesh as a glTF viewer would: node TRS (overridden by the
        /// animation's channels sampled at <paramref name="time"/>), composed down the node
        /// tree, times the inverse bind matrices, blended by the vertex weights.
        /// </summary>
        public (Vector3[] Positions, Vector3[] Normals) Skin(JsonElement? animation, float time)
        {
            var nodes = Json.GetProperty("nodes");
            int count = nodes.GetArrayLength();
            var translation = new Vector3[count];
            var rotation = new Quaternion[count];
            var scale = new Vector3[count];
            var parent = new int[count];
            Array.Fill(parent, -1);
            for (int i = 0; i < count; i++)
            {
                var node = nodes[i];
                translation[i] = node.TryGetProperty("translation", out var t) ? new Vector3(F(t, 0), F(t, 1), F(t, 2)) : Vector3.Zero;
                rotation[i] = node.TryGetProperty("rotation", out var r) ? new Quaternion(F(r, 0), F(r, 1), F(r, 2), F(r, 3)) : Quaternion.Identity;
                scale[i] = node.TryGetProperty("scale", out var s) ? new Vector3(F(s, 0), F(s, 1), F(s, 2)) : Vector3.One;
                if (node.TryGetProperty("children", out var children))
                    foreach (var c in children.EnumerateArray())
                        parent[c.GetInt32()] = i;
            }

            if (animation is { } anim)
            {
                var samplers = anim.GetProperty("samplers");
                foreach (var channel in anim.GetProperty("channels").EnumerateArray())
                {
                    var sampler = samplers[channel.GetProperty("sampler").GetInt32()];
                    Assert.Equal("LINEAR", sampler.GetProperty("interpolation").GetString());
                    var input = Floats(sampler.GetProperty("input").GetInt32());
                    var output = Floats(sampler.GetProperty("output").GetInt32());
                    int node = channel.GetProperty("target").GetProperty("node").GetInt32();
                    string path = channel.GetProperty("target").GetProperty("path").GetString()!;
                    int width = path == "rotation" ? 4 : 3;
                    Assert.Equal(input.Length * width, output.Length);

                    int k = 0;
                    while (k < input.Length - 1 && input[k + 1] <= time)
                        k++;
                    int k1 = Math.Min(k + 1, input.Length - 1);
                    float u = k1 == k || input[k1] <= input[k] ? 0 : Math.Clamp((time - input[k]) / (input[k1] - input[k]), 0, 1);
                    if (path == "rotation")
                    {
                        var a = new Quaternion(output[k * 4], output[k * 4 + 1], output[k * 4 + 2], output[k * 4 + 3]);
                        var b = new Quaternion(output[k1 * 4], output[k1 * 4 + 1], output[k1 * 4 + 2], output[k1 * 4 + 3]);
                        rotation[node] = Quaternion.Slerp(a, b, u);
                    }
                    else
                    {
                        var a = new Vector3(output[k * 3], output[k * 3 + 1], output[k * 3 + 2]);
                        var b = new Vector3(output[k1 * 3], output[k1 * 3 + 1], output[k1 * 3 + 2]);
                        if (path == "translation")
                            translation[node] = Vector3.Lerp(a, b, u);
                        else
                            scale[node] = Vector3.Lerp(a, b, u);
                    }
                }
            }

            // Row-vector convention: local = S·R·T, global = local·parentGlobal.
            var global = new Matrix4x4?[count];
            Matrix4x4 Global(int i) => global[i] ??=
                Matrix4x4.CreateScale(scale[i]) * Matrix4x4.CreateFromQuaternion(rotation[i]) * Matrix4x4.CreateTranslation(translation[i])
                * (parent[i] >= 0 ? Global(parent[i]) : Matrix4x4.Identity);

            var skin = Json.GetProperty("skins")[0];
            var joints = skin.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            var ibm = Floats(skin.GetProperty("inverseBindMatrices").GetInt32());
            var jointMatrices = new Matrix4x4[joints.Length];
            for (int j = 0; j < joints.Length; j++)
            {
                var m = new Matrix4x4(
                    ibm[j * 16 + 0], ibm[j * 16 + 1], ibm[j * 16 + 2], ibm[j * 16 + 3],
                    ibm[j * 16 + 4], ibm[j * 16 + 5], ibm[j * 16 + 6], ibm[j * 16 + 7],
                    ibm[j * 16 + 8], ibm[j * 16 + 9], ibm[j * 16 + 10], ibm[j * 16 + 11],
                    ibm[j * 16 + 12], ibm[j * 16 + 13], ibm[j * 16 + 14], ibm[j * 16 + 15]);
                jointMatrices[j] = m * Global(joints[j]);
            }

            var attributes = Json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
            var positions = Vec3(attributes.GetProperty("POSITION").GetInt32());
            var normals = Vec3(attributes.GetProperty("NORMAL").GetInt32());
            var vertexJoints = Indices(attributes.GetProperty("JOINTS_0").GetInt32());
            var weights = Floats(attributes.GetProperty("WEIGHTS_0").GetInt32());
            var outPositions = new Vector3[positions.Length];
            var outNormals = new Vector3[positions.Length];
            for (int v = 0; v < positions.Length; v++)
            {
                Vector3 p = Vector3.Zero, n = Vector3.Zero;
                for (int k = 0; k < 4; k++)
                {
                    float w = weights[v * 4 + k];
                    if (w == 0)
                        continue;
                    var m = jointMatrices[vertexJoints[v * 4 + k]];
                    p += Vector3.Transform(positions[v], m) * w;
                    n += Vector3.TransformNormal(normals[v], m) * w;
                }
                outPositions[v] = p;
                outNormals[v] = n == Vector3.Zero ? normals[v] : Vector3.Normalize(n);
            }
            return (outPositions, outNormals);

            static float F(JsonElement array, int i) => array[i].GetSingle();
        }
    }
}
