using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;
using TelltaleModding.App.Views;
using TelltaleModding.Core.Catalog;
using TelltaleModding.Core.Scene;
using Xunit;

namespace TelltaleModding.App.Tests;

/// <summary>
/// Drives the Scenes workspace against installed games (skipped when the install is
/// missing): list the locations, assemble one, toggle agents. Screenshots go to
/// <see cref="UiTests.ShotsFolder"/>.
/// </summary>
public class ScenesViewTests
{
    private const string DefinitiveSeries = @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";
    private const string TalesFromTheBorderlands = @"F:\Steam\steamapps\common\Tales from the Borderlands";

    private static void PumpUntil(Func<bool> done, int seconds, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
    }

    private static GameCatalog? OpenCatalog(string folder)
    {
        if (!Directory.Exists(folder) || GameInstall.Detect(folder, out _) is not { } install)
            return null;
        var task = GameCatalog.OpenAsync(install, ArchiveOpener.For(install));
        PumpUntil(() => task.IsCompleted, 180, "the game to open");
        return task.Result;
    }

    [AvaloniaTheory]
    [InlineData(DefinitiveSeries, "adv_bellTower.scene", "Bell Tower", "wdc-belltower")]
    [InlineData(TalesFromTheBorderlands, "adv_arenaHangar.scene", "Arena Hangar", "tftb-hangar")]
    public void Assembles_a_scene_and_toggles_its_agents(string game, string sceneFile, string displayName, string shot)
    {
        using var catalog = OpenCatalog(game);
        if (catalog is null)
            return;

        var vm = new ScenesViewModel();
        var window = new Window { Width = 1440, Height = 900, Content = new ScenesView { DataContext = vm } };
        // Closed even on failure, so the view stops following this view model into later tests.
        try
        {
            window.Show();
            AssembledScene? shown = null;
            IReadOnlyList<bool>? mask = null;
            vm.SceneReady += (scene, _) => shown = scene;
            vm.BatchVisibilityChanged += m => mask = m;

            vm.Load(catalog);
            Assert.True(vm.Scenes.Count > 50, $"{vm.Scenes.Count} scenes");
            Assert.All(vm.Scenes, s => Assert.True(s.IsLocation));
            vm.Filter = displayName;
            var row = vm.Scenes.First(s => s.FileName == sceneFile);
            Assert.Equal(displayName, row.DisplayName);

            vm.SelectedScene = row;
            PumpUntil(() => vm.HasScene && !vm.IsLoading, 180, "the scene to assemble");
            Assert.NotNull(shown);
            Assert.Null(vm.Error);
            Assert.Contains("agents", vm.Stats);
            Assert.True(shown!.TriangleCount > 50_000, vm.Stats);
            Assert.Contains(vm.Kinds, k => k.Kind == SceneAgentKind.Environment && k.IsOn);
            Assert.Contains(vm.Agents, a => a.Object.Kind == SceneAgentKind.Environment && a.IsOn);
            Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory(UiTests.ShotsFolder);
            window.CaptureRenderedFrame()!.Save(Path.Combine(UiTests.ShotsFolder, $"scenes-{shot}.png"));

            // Hiding props only masks batches; showing characters re-assembles.
            vm.Kinds.First(k => k.Kind == SceneAgentKind.Prop).IsOn = false;
            PumpUntil(() => mask is not null, 10, "props to hide");
            Assert.Contains(false, mask!);
            var before = shown;
            if (vm.Kinds.FirstOrDefault(k => k.Kind == SceneAgentKind.Character) is { } characters)
            {
                var characterIndices = vm.Agents.Where(a => a.Object.Kind == SceneAgentKind.Character)
                    .Select(a => a.Object.Agent.Index).ToHashSet();
                characters.IsOn = true;
                PumpUntil(() => shown != before && !vm.IsLoading, 120, "characters to be added");
                Assert.Contains(shown!.IncludedObjects, characterIndices.Contains);
                Assert.DoesNotContain(shown.IncludedObjects, i => vm.Agents.Any(a => a.Object.Agent.Index == i && a.Object.Kind == SceneAgentKind.Prop));
            }
        }
        finally
        {
            window.Close();
            vm.Clear();
        }
    }

    [AvaloniaFact]
    public void A_scene_that_starts_empty_shows_the_agents_switched_on()
    {
        using var catalog = OpenCatalog(TalesFromTheBorderlands);
        if (catalog is null)
            return;

        var vm = new ScenesViewModel();
        var window = new Window { Width = 1440, Height = 900, Content = new ScenesView { DataContext = vm } };
        try
        {
            window.Show();
            AssembledScene? shown = null;
            bool? keptCamera = null;
            vm.SceneReady += (scene, keep) => (shown, keptCamera) = (scene, keep);
            vm.Load(catalog);
            // jackAI.scene only holds hologram Jack, hidden until a chore shows him.
            vm.IncludeOtherScenes = true;
            vm.SelectedScene = vm.Scenes.First(s => s.FileName == "jackAI.scene");
            PumpUntil(() => vm.Error is not null && !vm.IsLoading, 120, "the scene to load");
            Assert.False(vm.HasScene);

            vm.Agents.First(a => a.Object.Name == "Jack").IsOn = true;
            PumpUntil(() => vm.HasScene && !vm.IsLoading, 120, "Jack to be assembled");
            Assert.Null(vm.Error);
            Assert.False(keptCamera, "the first agent shown is framed");
            Assert.All(shown!.Model.BatchMaterialParams, p => Assert.True(p!.DrawAsOneLayer));
        }
        finally
        {
            window.Close();
            vm.Clear();
        }
    }
}
