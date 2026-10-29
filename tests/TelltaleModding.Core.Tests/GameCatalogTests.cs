using System.Collections.Concurrent;
using System.Text;
using TelltaleModding.Core.Archives;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Meta;

namespace TelltaleModding.Core.Tests;

/// <summary>
/// <see cref="GameInstall"/> and <see cref="GameCatalog"/> over a fake install: empty
/// archive files on disk (for detection and cache fingerprints) served by in-memory sources.
/// </summary>
public class GameCatalogTests
{
    private sealed class FakeSource : IArchiveSource
    {
        private readonly Dictionary<string, byte[]> _files;
        public string ArchivePath { get; }
        public IReadOnlyList<ArchiveEntryRef> Entries { get; }
        public bool Disposed { get; private set; }

        public FakeSource(string path, Dictionary<string, byte[]> files)
        {
            ArchivePath = path;
            _files = files;
            Entries = files.Select(f => new ArchiveEntryRef(path, f.Key, f.Value.Length)).ToArray();
        }

        public byte[] Read(ArchiveEntryRef entry) => _files[entry.Name];
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeGame : IDisposable
    {
        private readonly TempDir _dir = new();
        public readonly Dictionary<string, Dictionary<string, byte[]>> Archives = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, int> OpenCount = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentBag<FakeSource> Opened = new();

        public string Root => _dir.Path;
        public string ArchivesFolder => Path.Combine(Root, "Archives");
        public string CacheFolder => Path.Combine(Root, "cache");

        public FakeGame()
        {
            Directory.CreateDirectory(ArchivesFolder);
            File.WriteAllBytes(Path.Combine(Root, "WDC.exe"), Array.Empty<byte>());
        }

        public void Add(string archive, string name, byte[] content)
        {
            if (!Archives.TryGetValue(archive, out var files))
            {
                Archives[archive] = files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                File.WriteAllBytes(Path.Combine(ArchivesFolder, archive), new byte[] { 1 });
            }
            files[name] = content;
        }

        public void Add(string archive, string name, int size) => Add(archive, name, new byte[size]);

        public IArchiveSource Open(string path)
        {
            OpenCount.AddOrUpdate(Path.GetFileName(path), 1, (_, n) => n + 1);
            var source = new FakeSource(path, Archives[Path.GetFileName(path)]);
            Opened.Add(source);
            return source;
        }

        public GameInstall Install() => GameInstall.Detect(Root, out var error) ?? throw new Exception(error);

        public Task<GameCatalog> OpenCatalog() =>
            GameCatalog.OpenAsync(Install(), Open, new CatalogOptions { CacheFolder = CacheFolder, MaxParallelism = 2 });

        public void Dispose() => _dir.Dispose();
    }

    private static readonly string[] ClementineParts =
    {
        "head", "eyesMouth", "hairCap", "hairBandage", "hairSidePony",
        "bodyUpperB", "bodyUpperBBackpack", "bodyLowerB", "hands", "handsNoFinger",
    };

    private const string Season = "WDC_pc_ProjectSeason4_txmesh.ttarch2";
    private const string Episode = "WDC_pc_WalkingDead401_txmesh.ttarch2";
    private const string EpisodeData = "WDC_pc_WalkingDead401_data.ttarch2";
    private const string Patch = "WDC_pc_PC_P01_WalkingDead401_compressed.ttarch2";
    private const string French = "WDC_pc_FrenchProjectSeason4_compressed.ttarch2";
    private const string Mod = "WDC_pc_Mod_Example_data.ttarch2";

    private static FakeGame CreateGame()
    {
        var game = new FakeGame();
        game.Add(Season, "sk62_clementine400_head.d3dmesh", 1);
        game.Add(Season, "shared.d3dtx", 1);
        game.Add(Season, "onlyInSeason.d3dtx", 1);
        foreach (var part in ClementineParts)
            game.Add(Episode, $"sk62_clementine400_{part}.d3dmesh", 2);
        game.Add(Episode, "shared.d3dtx", 2);
        game.Add(Episode, "shared.d3dmesh", 2);
        game.Add(EpisodeData, "sk62_clementine400.skl", 2);
        game.Add(EpisodeData, "sk62_clementine400.prop", File.ReadAllBytes(SampleData.Sample("sk62_clementine400.prop")));
        foreach (var anm in new[]
                 {
                     "sk62_clementine400_jog.anm", "sk62_action_clementine400AjCloseDoor.anm", "sk62_idle_violetStand.anm",
                     "clementine400_phoneme_aa.anm", "sk62_clementineStandA.anm", "obj_door_sk62_action_clementine400Open.anm",
                     "sk54_rhys_walk.anm",
                 })
            game.Add(EpisodeData, anm, 2);
        game.Add(Patch, "shared.d3dtx", 4);
        game.Add(French, "shared.d3dtx", 8);
        game.Add(Mod, "shared.d3dtx", 16);
        game.Add(Mod, "modOnly.d3dtx", 16);

        // A plain-text description (as in the Definitive Series) gives the mod priority 700;
        // the other archives fall back to name-based priorities.
        File.WriteAllText(Path.Combine(game.ArchivesFolder, "_resdesc_50_Mod_Example.lua"),
            "local set = {}\nset.name = \"Mod_Example\"\nset.setName = \"Mod_Example\"\n" +
            "set.logicalName = \"<Mod_Example>\"\nset.logicalDestination = \"<Project>\"\nset.priority = 700\n" +
            "set.enableMode = \"constant\"\nset.gameDataArchives =\n{\n    _currentDirectory .. \"" + Mod + "\"\n}\n" +
            "RegisterSetDescription(set)\n");
        return game;
    }

    [SkippableFact]
    public void Detect_AcceptsInstallRootArchivesFolderOrAFileInside()
    {
        using var game = CreateGame();

        foreach (var folder in new[] { game.Root, game.ArchivesFolder, game.ArchivesFolder + Path.DirectorySeparatorChar, Path.Combine(game.Root, "WDC.exe") })
        {
            var install = GameInstall.Detect(folder, out var error);
            Assert.True(install is not null, error);
            Assert.Equal(67, install!.Game.GameNum);
            Assert.Equal("The Walking Dead: The Telltale Definitive Series", install.DisplayName);
            Assert.Equal(Path.GetFullPath(game.ArchivesFolder), Path.GetFullPath(install.ArchivesFolder));
            Assert.Equal(6, install.ArchivePaths.Count);
            Assert.Equal(Path.GetFullPath(game.Root), Path.GetFullPath(install.RootFolder));
        }
    }

    [SkippableFact]
    public void Detect_FindsSingleGameInParentFolder_AndRefusesAmbiguousParents()
    {
        using var parent = new TempDir();
        var tftb = Path.Combine(parent.Path, "Tales from the Borderlands", "archives");
        Directory.CreateDirectory(tftb);
        File.WriteAllBytes(Path.Combine(tftb, "Borderlands_pc_Boot_data.ttarch2"), new byte[] { 1 });

        var install = GameInstall.Detect(parent.Path, out var error);
        Assert.True(install is not null, error);
        Assert.Equal(56, install!.Game.GameNum);
        Assert.Equal("Tales from the Borderlands", install.DisplayName);

        var wau = Path.Combine(parent.Path, "The Wolf Among Us", "Pack");
        Directory.CreateDirectory(wau);
        File.WriteAllBytes(Path.Combine(wau, "Fables_pc_Boot_data.ttarch2"), new byte[] { 1 });
        Assert.Null(GameInstall.Detect(parent.Path, out error));
        Assert.Contains("several", error);
    }

    [SkippableFact]
    public void Detect_ExplainsMissingOrEmptyFolders()
    {
        Assert.Null(GameInstall.Detect(@"Z:\definitely\not\here", out var error));
        Assert.NotNull(error);
        using var empty = new TempDir();
        Assert.Null(GameInstall.Detect(empty.Path, out error));
        Assert.Contains("No supported Telltale game", error);
    }

    [SkippableFact]
    public async Task Find_HonoursOverridePriority()
    {
        using var game = CreateGame();
        using var catalog = await game.OpenCatalog();

        Assert.Equal(16, catalog.Find("shared.d3dtx")!.Size);             // mod (700) > patch > episode > season
        Assert.Equal(Mod, Path.GetFileName(catalog.Find("SHARED.D3DTX")!.ArchivePath));
        Assert.Equal(2, catalog.Find("sk62_clementine400_head.d3dmesh")!.Size); // episode (101) > season (50)
        Assert.Equal(1, catalog.Find("onlyInSeason.d3dtx")!.Size);
        Assert.Null(catalog.Find("missing.d3dtx"));
        Assert.False(catalog.Archives.Single(a => Path.GetFileName(a.Path) == French).Enabled);
        Assert.Equal(700, catalog.Archives.Single(a => Path.GetFileName(a.Path) == Mod).Priority);
        Assert.Equal(0, game.OpenCount.GetValueOrDefault(French)); // disabled archives are never opened
    }

    [SkippableFact]
    public async Task Entries_HoldOneWinnerPerName_SortedByName()
    {
        using var game = CreateGame();
        using var catalog = await game.OpenCatalog();

        var names = catalog.Entries.Select(e => e.Name).ToList();
        Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), names.Count);
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
        Assert.All(catalog.Entries, e => Assert.Same(e, catalog.Find(e.Name)));
    }

    [SkippableFact]
    public async Task FindBySymbol_ResolvesNamesWithAndWithoutExtension()
    {
        using var game = CreateGame();
        using var catalog = await game.OpenCatalog();

        Assert.Equal("shared.d3dtx", catalog.FindBySymbol(TelltaleCrc64.HashSymbol("shared.d3dtx"))!.Name);
        Assert.Equal("shared.d3dmesh", catalog.FindBySymbol(TelltaleCrc64.HashSymbol("shared.d3dmesh"))!.Name);
        // Extension-less symbol: textures win a stem collision.
        Assert.Equal("shared.d3dtx", catalog.FindBySymbol(TelltaleCrc64.HashSymbol("Shared"))!.Name);
        Assert.Equal("sk62_clementine400_hairCap.d3dmesh",
            catalog.FindBySymbol(TelltaleCrc64.HashSymbol("sk62_clementine400_haircap"))!.Name);
        Assert.Null(catalog.FindBySymbol(12345));
    }

    [SkippableFact]
    public async Task Characters_ComeFromTheAgentProp()
    {
        using var game = CreateGame();
        using var catalog = await game.OpenCatalog();

        var clem = Assert.Single(catalog.Characters);
        Assert.Equal(("Clementine", "400"), (clem.DisplayName, clem.Variant));
        Assert.True(clem.DefaultsFromGameData);
        Assert.Equal(new[] { "bodyLowerB", "bodyUpperB", "eyesMouth", "hairCap", "hands", "head" },
            clem.DefaultParts.Select(p => p.PartName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(clem.Parts, p => Assert.Same(p.Mesh, catalog.Find(p.Mesh.Name)));
    }

    [SkippableFact]
    public async Task AnimationsFor_ListsCharacterClipsFirst()
    {
        using var game = CreateGame();
        using var catalog = await game.OpenCatalog();

        var anims = catalog.AnimationsFor(catalog.Characters[0]);

        Assert.Equal(new[]
        {
            "action_clementine400AjCloseDoor", "clementine400_jog", "clementine400_phoneme_aa", // exact name
            "clementineStandA",                                                                   // the person
            "idle_violetStand",                                                                   // same family
        }, anims.Select(a => a.DisplayName));
        Assert.Equal(new[] { true, true, true, true, false }, anims.Select(a => a.NamedForCharacter));
    }

    [SkippableTheory]
    [InlineData("action_clementine400AjCloseDoor", "clementine400", true)]
    [InlineData("action_ajCarriesClementine400", "clementine400", true)]
    [InlineData("clementine4001_walk", "clementine400", false)]
    [InlineData("action_vaughnHugsRhys", "rhys", true)]
    [InlineData("action_rhysWakeUp", "rhys", true)]
    [InlineData("action_rhyson", "rhys", false)]
    [InlineData("idle_kenny203Stand", "kenny", true)]
    public void ContainsNameToken_MatchesWholeWords(string text, string token, bool expected) =>
        Assert.Equal(expected, GameCatalog.ContainsNameToken(text, token));

    [SkippableFact]
    public async Task Read_OpensArchivesLazilyOnceAndDisposesThem()
    {
        using var game = CreateGame();
        var catalog = await game.OpenCatalog();
        game.OpenCount.Clear();

        var entry = catalog.Find("modOnly.d3dtx")!;
        var bytes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => catalog.Read(entry))));

        Assert.All(bytes, b => Assert.Equal(16, b.Length));
        Assert.True(game.OpenCount.GetValueOrDefault(Mod) <= 1); // already open from the build, or opened once now
        catalog.Dispose();
        Assert.All(game.Opened, s => Assert.True(s.Disposed));
        Assert.Throws<ObjectDisposedException>(() => catalog.Read(entry));
    }

    [SkippableFact]
    public async Task Cache_ReopensWithoutTouchingArchives_AndRebuildsWhenAnArchiveChanges()
    {
        using var game = CreateGame();
        using (var first = await game.OpenCatalog())
            Assert.False(first.LoadedFromCache);
        game.OpenCount.Clear();

        using (var second = await game.OpenCatalog())
        {
            Assert.True(second.LoadedFromCache);
            Assert.Empty(game.OpenCount);
            Assert.Equal(16, second.Find("shared.d3dtx")!.Size);
            var clem = Assert.Single(second.Characters);
            Assert.True(clem.DefaultsFromGameData);
            Assert.Equal(6, clem.DefaultParts.Count());
            Assert.Equal(5, second.AnimationsFor(clem).Count);
            Assert.Equal(16, second.Read(second.Find("modOnly.d3dtx")!).Length);
        }

        // Updating an archive (new size/mtime) invalidates the cache.
        File.WriteAllBytes(Path.Combine(game.ArchivesFolder, Patch), new byte[] { 1, 2 });
        using var third = await game.OpenCatalog();
        Assert.False(third.LoadedFromCache);
    }

    [SkippableFact]
    public async Task UnreadableArchive_IsSkippedReportedAndNotCached()
    {
        using var game = CreateGame();
        var options = new CatalogOptions { CacheFolder = game.CacheFolder };
        IArchiveSource Flaky(string path) =>
            Path.GetFileName(path) == Mod ? throw new IOException("locked") : game.Open(path);

        using (var catalog = await GameCatalog.OpenAsync(game.Install(), Flaky, options))
        {
            Assert.Equal(Mod, Path.GetFileName(Assert.Single(catalog.UnreadableArchives)));
            Assert.Equal(4, catalog.Find("shared.d3dtx")!.Size); // next best: the patch
            Assert.Null(catalog.Find("modOnly.d3dtx"));
        }

        using var again = await GameCatalog.OpenAsync(game.Install(), game.Open, options);
        Assert.False(again.LoadedFromCache);
        Assert.Equal(16, again.Find("shared.d3dtx")!.Size);

        await Assert.ThrowsAsync<IOException>(() => GameCatalog.OpenAsync(game.Install(),
            _ => throw new IOException("no engine"), new CatalogOptions { UseCache = false }));
    }

    [SkippableFact]
    public async Task Progress_IsReported()
    {
        using var game = CreateGame();
        var messages = new ConcurrentQueue<CatalogProgress>();
        using var catalog = await GameCatalog.OpenAsync(game.Install(), game.Open,
            new CatalogOptions { UseCache = false }, new SyncProgress(messages.Enqueue));

        Assert.Contains(messages, m => m.Message.StartsWith("Reading archive tables", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Message.StartsWith("Reading character properties", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task OpenAsync_HonoursCancellation()
    {
        using var game = CreateGame();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GameCatalog.OpenAsync(game.Install(), game.Open, new CatalogOptions { UseCache = false }, null, cts.Token));
    }

    private sealed class SyncProgress : IProgress<CatalogProgress>
    {
        private readonly Action<CatalogProgress> _report;
        public SyncProgress(Action<CatalogProgress> report) => _report = report;
        public void Report(CatalogProgress value) => _report(value);
    }
}
