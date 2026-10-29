using System.Numerics;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meshes;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// The Wolf Among Us ships two copies of many gesture clips: the episode 1 original
/// (Fables101_anichore, Menu_anichore) and an episode 2-5 / patch re-export that overrides it.
/// The re-export of a subtle clip such as bigby_headGesture_nodYes keeps only the original's
/// first frame (one absolute key plus one zero delta), so it decodes, correctly, to a held pose.
/// Samples: <c>wau_bigby_headGesture_nodYes_ep1.anm</c> (Fables101_anichore) and
/// <c>wau_bigby_headGesture_nodYes_patch.anm</c> (PC-Mac_TTG_Update_03_Episode_05_Menu_all).
/// </summary>
public class HeldPoseClipTests
{
    private const string Ep1Sample = "wau_bigby_headGesture_nodYes_ep1.anm";
    private const string PatchSample = "wau_bigby_headGesture_nodYes_patch.anm";

    private static AnimationFile Clip(string sample)
    {
        Assert.True(AnimationFile.TryLoad(File.ReadAllBytes(SampleData.Sample(sample)), out var clip, out var error), error);
        return clip!;
    }

    [SampleFact(Ep1Sample, PatchSample)]
    public void Patch_copy_holds_the_first_frame_of_the_episode_1_nod()
    {
        var ep1 = CspkDecoder.TryDecode(Clip(Ep1Sample).PoseKeysPayload!)!;
        var patch = CspkDecoder.TryDecode(Clip(PatchSample).PoseKeysPayload!)!;

        Assert.Equal(6, ep1.SampleCount);
        Assert.Equal(2, patch.SampleCount);
        Assert.Equal(ep1.BoneSymbols, patch.BoneSymbols);
        Assert.Equal(ep1.SampleTimes[^1], patch.SampleTimes[^1], 1e-4f);
        for (int s = 0; s < patch.BoneSymbols.Count; s++)
        {
            if (patch.BoneSymbols[s] == 0)
                continue;
            Assert.True(Angle(ep1.Rotations[s][0], patch.Rotations[s][0]) < 0.1f, $"slot {s} key differs");
            Assert.True(Angle(patch.Rotations[s][0], patch.Rotations[s][1]) < 0.1f, $"slot {s} moves");
        }
    }

    [SampleFact(Ep1Sample, PatchSample, "sk54_bigbyCrouch_weightShiftAShort.anm")]
    public void HasMotion_tells_a_held_pose_from_a_moving_clip()
    {
        Assert.True(Clip(Ep1Sample).HasMotion);
        Assert.False(Clip(PatchSample).HasMotion);
        Assert.True(Clip("sk54_bigbyCrouch_weightShiftAShort.anm").HasMotion);
    }

    [SampleFact("sk62_idle_clementineHips.anm")]
    public void HasMotion_reads_compressed_pose_keys_2() =>
        Assert.True(Clip("sk62_idle_clementineHips.anm").HasMotion);

    [SampleFact(Ep1Sample, PatchSample, "sk54_bigby.skl", "sk54_bigby_head.d3dmesh")]
    public void Episode_1_copy_nods_the_head_and_the_patch_copy_does_not()
    {
        Assert.True(SkeletonFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_bigby.skl")), out var skeleton, out var error), error);
        Assert.True(D3dMeshFile.TryLoad(File.ReadAllBytes(SampleData.Sample("sk54_bigby_head.d3dmesh")), out var mesh, out error), error);
        var player = new AnimationPlayer(mesh!.GetGeometry(), skeleton!);

        float MaxBoneTurn(string sample)
        {
            player.Load(Clip(sample), "bigby_headGesture_nodYes.anm");
            var rest = player.Pose(0);
            float max = 0f;
            for (int i = 1; i <= 20; i++)
            {
                var pose = player.Pose(player.Length * i / 20f);
                for (int b = 0; b < pose.Length; b++)
                {
                    Matrix4x4.Decompose(rest[b], out _, out var r0, out _);
                    Matrix4x4.Decompose(pose[b], out _, out var r1, out _);
                    max = MathF.Max(max, Angle(r0, r1));
                }
            }
            return max;
        }

        Assert.True(MaxBoneTurn(Ep1Sample) > 5f);
        Assert.True(MaxBoneTurn(PatchSample) < 0.25f);
    }

    [SampleFact(Ep1Sample, PatchSample, "sk54_bigbyCrouch_weightShiftAShort.anm")]
    public async Task ReadAnimation_plays_the_overridden_copy_only_when_the_winner_holds_a_pose()
    {
        using var dir = new TempDir();
        var archives = Path.Combine(dir.Path, "Archives");
        Directory.CreateDirectory(archives);
        File.WriteAllBytes(Path.Combine(dir.Path, "WDC.exe"), Array.Empty<byte>());

        // Name-guessed priorities: the episode archive overrides the season archive.
        const string Low = "WDC_pc_ProjectSeason4_data.ttarch2";
        const string High = "WDC_pc_WalkingDead401_data.ttarch2";
        var ep1 = File.ReadAllBytes(SampleData.Sample(Ep1Sample));
        var patch = File.ReadAllBytes(SampleData.Sample(PatchSample));
        var walk = File.ReadAllBytes(SampleData.Sample("sk54_bigbyCrouch_weightShiftAShort.anm"));
        var files = new Dictionary<string, Dictionary<string, byte[]>>(StringComparer.OrdinalIgnoreCase)
        {
            [Low] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["nod.anm"] = ep1,         // overridden original that moves
                ["moving.anm"] = ep1,      // overridden by a copy that also moves
                ["held.anm"] = patch,      // every copy holds a pose
            },
            [High] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["nod.anm"] = patch,
                ["moving.anm"] = walk,
                ["held.anm"] = patch,
                ["single.anm"] = patch,
            },
        };
        foreach (var name in files.Keys)
            File.WriteAllBytes(Path.Combine(archives, name), new byte[] { 1 });

        var install = GameInstall.Detect(dir.Path, out var error) ?? throw new Exception(error);
        using var catalog = await GameCatalog.OpenAsync(install,
            path => new MemorySource(path, files[Path.GetFileName(path)]),
            new CatalogOptions { CacheFolder = Path.Combine(dir.Path, "cache"), MaxParallelism = 1 });

        var nod = catalog.Find("nod.anm")!;
        Assert.Equal(High, Path.GetFileName(nod.ArchivePath));
        Assert.Equal(patch, catalog.Read(nod));
        Assert.Equal(ep1, catalog.ReadAnimation(nod));
        Assert.Equal(walk, catalog.ReadAnimation(catalog.Find("moving.anm")!));
        Assert.Equal(patch, catalog.ReadAnimation(catalog.Find("held.anm")!));
        Assert.Equal(patch, catalog.ReadAnimation(catalog.Find("single.anm")!));
    }

    private static float Angle(Quaternion a, Quaternion b) =>
        2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0f, 1f)) * 180f / MathF.PI;

    private sealed class MemorySource : IArchiveSource
    {
        private readonly Dictionary<string, byte[]> _files;

        public MemorySource(string path, Dictionary<string, byte[]> files)
        {
            ArchivePath = path;
            _files = files;
            Entries = files.Select(f => new ArchiveEntryRef(path, f.Key, f.Value.Length)).ToArray();
        }

        public string ArchivePath { get; }
        public IReadOnlyList<ArchiveEntryRef> Entries { get; }
        public byte[] Read(ArchiveEntryRef entry) => _files[entry.Name];
        public void Dispose() { }
    }
}
