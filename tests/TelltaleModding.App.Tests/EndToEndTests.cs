using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;
using TelltaleModding.App.Views;
using Xunit;

namespace TelltaleModding.App.Tests;

/// <summary>
/// Drives the real shell against installed games (skipped when the install is
/// missing): open the game, pick a character, check the outfit and textures, and
/// play a clip. Screenshots go to <see cref="UiTests.ShotsFolder"/>.
/// </summary>
public class EndToEndTests : IDisposable
{
    private const string DefinitiveSeries = @"A:\SteamLibrary\steamapps\common\The Walking Dead The Telltale Definitive Series";
    private const string TalesFromTheBorderlands = @"F:\Steam\steamapps\common\Tales from the Borderlands";

    private readonly List<(MainWindow Window, ShellViewModel Shell)> _opened = new();

    // Each test opens whole games; close them when it ends so a full run stays within memory.
    public void Dispose()
    {
        foreach (var (window, shell) in _opened)
        {
            window.Close();
            shell.Dispose();
        }
        _opened.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    private (MainWindow Window, ShellViewModel Shell) CreateWindow()
    {
        AudioOutputs.Shared = new SilentAudioOutput();
        var shell = new ShellViewModel(new AppSettings
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"tms-e2e-{Guid.NewGuid():N}.json"),
        });
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        _opened.Add((window, shell));
        return (window, shell);
    }

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

    private static void RunUntilDone(Task task, int seconds, string what) => PumpUntil(() => task.IsCompleted, seconds, what);

    private static void Shot(MainWindow window, string name)
    {
        Directory.CreateDirectory(UiTests.ShotsFolder);
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(UiTests.ShotsFolder, name + ".png"));
    }

    [AvaloniaFact]
    public void Definitive_series_clementine_loads_dressed_textured_and_animates()
    {
        if (!Directory.Exists(DefinitiveSeries))
            return;
        var (window, shell) = CreateWindow();
        window.Show();

        RunUntilDone(shell.AddAndOpenAsync(DefinitiveSeries), 180, "the game to open");
        Assert.True(shell.IsCharacters, shell.StatusText);
        Assert.True(shell.Characters.Characters.Count > 300, $"{shell.Characters.Characters.Count} characters");
        Shot(window, "e2e-wdc-characters");

        shell.Characters.CharacterFilter = "clementine 400";
        var clem = shell.Characters.Characters.First(c => c.Entry.Id == "sk62_clementine400");
        shell.Characters.SelectedCharacter = clem;
        PumpUntil(() => shell.Characters.HasModel && !shell.Characters.IsLoading, 60, "Clementine's model");

        var worn = shell.Characters.Outfit.SelectMany(s => s.Parts).Where(p => p.IsOn)
            .Select(p => p.MeshName.Replace("sk62_clementine400_", "").Replace(".d3dmesh", ""))
            .OrderBy(n => n).ToList();
        Assert.Equal(new[] { "bodyLowerB", "bodyUpperB", "eyesMouth", "hairCap", "hands", "head" }, worn);
        Assert.Contains(shell.Characters.Details, d => d.Contains("sk62_clementine400_head.d3dtx"));
        Assert.Contains(shell.Characters.Details, d => d.Contains("sk62_clementine400_eyes.d3dtx"));
        // The cornea layer only has a normal map; it must stay hidden so the iris shows.
        Assert.Contains(shell.Characters.Details, d => d.Contains("hidden (texture-less overlay"));
        Assert.DoesNotContain(shell.Characters.Details, d => d.Contains("· sk_wd4SharedParts_cornea_nm.d3dtx"));
        Assert.True(shell.Characters.Animations.Count > 1000, $"{shell.Characters.Animations.Count} clips");

        // Play a jog and step it: the posed frame must differ from the rest pose.
        shell.Characters.AnimationFilter = "clementine400_jog";
        var jog = shell.Characters.Animations.First(a => a.FileName == "sk62_clementine400_jog.anm");
        System.Numerics.Matrix4x4[]? frame = null;
        shell.Characters.PoseReady += skin => frame = skin;
        shell.Characters.SelectedAnimation = jog;
        PumpUntil(() => shell.Characters.IsPlaying && frame is not null, 30, "the jog to start");
        Assert.True(shell.Characters.Duration > 0.5, shell.Characters.ClipInfo);
        shell.Characters.CurrentTime = shell.Characters.Duration / 2;
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(frame);
        // Mid-jog the skinning matrices move bones away from the rest pose.
        Assert.Contains(frame!, m => !m.IsIdentity);
        Shot(window, "e2e-wdc-clementine-jog");

        // Toggling a part rebuilds the model and keeps the clip playing.
        shell.Characters.Outfit.SelectMany(s => s.Parts).First(p => p.MeshName.EndsWith("hairCap.d3dmesh")).IsOn = false;
        shell.Characters.Outfit.SelectMany(s => s.Parts).First(p => p.MeshName.EndsWith("hairSidePony.d3dmesh")).IsOn = true;
        PumpUntil(() => shell.Characters.Details.Any(d => d.Contains("hairSidePony")) && !shell.Characters.IsLoading,
            30, "the outfit change");
        Assert.True(shell.Characters.HasClip);

        // Alvin's materials list engine placeholders (color_000) before his real textures.
        shell.Characters.CharacterFilter = "alvin";
        shell.Characters.SelectedCharacter = shell.Characters.Characters.First(c => c.Entry.Id == "sk54_alvin");
        PumpUntil(() => shell.Characters.Title == "Alvin" && shell.Characters.HasModel && !shell.Characters.IsLoading,
            60, "Alvin's model");
        Assert.Contains(shell.Characters.Details, d => d.Contains("· sk54_alvin_body.d3dtx"));
        Assert.Contains(shell.Characters.Details, d => d.Contains("· sk54_alvin_head.d3dtx"));
        Assert.DoesNotContain(shell.Characters.Details, d => d.Contains("· color_000.d3dtx"));

        // Clementine 305 (Season 3): damage masks are listed before her real head, and a
        // black "eyes glass" cornea layer covers her eyes.
        shell.Characters.CharacterFilter = "clementine 305";
        shell.Characters.SelectedCharacter = shell.Characters.Characters.First(c => c.Entry.Id == "sk62_clementine305");
        PumpUntil(() => shell.Characters.Subtitle.Contains("clementine305") && shell.Characters.HasModel
                        && !shell.Characters.IsLoading, 60, "Clementine 305's model");
        Assert.Contains(shell.Characters.Details, d => d.Contains("· sk62_clementine_head.d3dtx"));
        Assert.Contains(shell.Characters.Details, d => d.Contains("· sk62_clementine_hands.d3dtx"));
        Assert.DoesNotContain(shell.Characters.Details, d => d.Contains("DamageMask") || d.Contains("damageMask"));
        Assert.DoesNotContain(shell.Characters.Details, d => d.Contains("· sk_wd3SharedParts_eyesGlass.d3dtx"));

        // Assets: search the whole game and preview a texture.
        shell.CurrentWorkspace = Workspace.Assets;
        shell.Assets.Query = "clementine400_head.d3dtx";
        PumpUntil(() => shell.Assets.Results.Count > 0 &&
                        shell.Assets.Results.All(r => r.Name.Contains("clementine400_head.d3dtx")), 30, "asset search");
        shell.Assets.Selected = shell.Assets.Results.First(r => r.Name == "sk62_clementine400_head.d3dtx");
        PumpUntil(() => shell.Assets.PreviewImage is not null, 30, "texture preview");
        Shot(window, "e2e-wdc-assets");
    }

    [AvaloniaFact]
    public void Tales_from_the_borderlands_rhys_loads()
    {
        if (!Directory.Exists(TalesFromTheBorderlands))
            return;
        var (window, shell) = CreateWindow();
        window.Show();

        RunUntilDone(shell.AddAndOpenAsync(TalesFromTheBorderlands), 180, "the game to open");
        var rhys = shell.Characters.Characters.First(c => c.Entry.Id == "sk54_rhys");
        shell.Characters.SelectedCharacter = rhys;
        PumpUntil(() => shell.Characters.HasModel && !shell.Characters.IsLoading, 60, "Rhys's model");
        Assert.True(shell.Characters.Details.Count(d => d.Contains(".d3dtx")) > 0, string.Join("\n", shell.Characters.Details));
        Assert.True(shell.Characters.Animations.Count > 0);
        Shot(window, "e2e-tftb-rhys");
    }

    [AvaloniaFact]
    public void Tales_from_the_borderlands_dialogue_plays_and_scripts_decompile()
    {
        if (!Directory.Exists(TalesFromTheBorderlands))
            return;
        var (window, shell) = CreateWindow();
        window.Show();
        RunUntilDone(shell.AddAndOpenAsync(TalesFromTheBorderlands), 180, "the game to open");

        // Dialogue: every line indexed, Rhys's lines filtered, one decoded and playing.
        shell.CurrentWorkspace = Workspace.Dialogue;
        PumpUntil(() => !shell.Dialogue.IsIndexing && shell.Dialogue.Lines.Count > 0, 120, "the dialogue index");
        Assert.True(shell.Dialogue.Speakers.Count > 50, $"{shell.Dialogue.Speakers.Count} speakers");
        shell.Dialogue.SelectedSpeaker = shell.Dialogue.Speakers.First(s => s.Name == "RHYS");
        shell.Dialogue.Query = "Vasquez";
        PumpUntil(() => shell.Dialogue.Lines.Count > 0 &&
                        shell.Dialogue.Lines.All(l => l.Speaker == "Rhys" && (l.Entry.Line.RawText + l.Entry.Scene).Contains("vasquez", StringComparison.OrdinalIgnoreCase)), 30, "Rhys's lines");
        Assert.Equal("english", shell.Dialogue.SelectedLanguage);
        shell.Dialogue.SelectedLine = shell.Dialogue.Lines.First(l => l.HasAudio);
        PumpUntil(() => shell.Dialogue.Player.Clip is not null || shell.Dialogue.Player.Error is not null, 30, "the voice line");
        Assert.Null(shell.Dialogue.Player.Error);
        Assert.True(shell.Dialogue.Player.Duration > 0.3);
        Assert.True(shell.Dialogue.Player.IsPlaying);
        // The speaker appears beside the list, lip-synced in the line's mood.
        PumpUntil(() => shell.Dialogue.Speaker.HasModel && !shell.Dialogue.Speaker.IsLoading, 90, "the speaker's model");
        Assert.StartsWith("Rhys", shell.Dialogue.Speaker.Caption);
        Assert.DoesNotContain("no lip-sync", shell.Dialogue.Speaker.Caption);
        // UI timers don't tick headless: seek instead, which poses the mouth the same way.
        shell.Dialogue.Player.Position = 1.0;
        Shot(window, "e2e-tftb-dialogue");

        // One episode narrows lines, conversations and music together.
        int allScenes = shell.Dialogue.Conversations.Scenes.Count;
        int allAlbums = shell.Dialogue.Music.Albums.Count;
        shell.Dialogue.SelectedSpeaker = null;
        shell.Dialogue.Query = "";
        shell.Dialogue.SelectedEpisode = "Borderlands104";
        PumpUntil(() => shell.Dialogue.Lines.Count > 0 && shell.Dialogue.Lines.All(l => l.Entry.Episode == "Borderlands104"), 30, "episode 104's lines");
        Assert.InRange(shell.Dialogue.Conversations.Scenes.Count, 1, allScenes - 1);
        Assert.Contains(shell.Dialogue.Conversations.Scenes, s => s.Contains("env_", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(shell.Dialogue.Music.Albums.Count, 1, allAlbums - 1);
        Assert.All(shell.Dialogue.Music.Albums, a => Assert.Equal("Borderlands104", a.Episode));
        shell.Dialogue.Mode = DialogueMode.Music;
        Shot(window, "e2e-tftb-music-104");
        shell.Dialogue.Mode = DialogueMode.Conversations;
        Shot(window, "e2e-tftb-talks-104");
        shell.Dialogue.SelectedEpisode = shell.Dialogue.Episodes[0];
        Assert.Equal(allScenes, shell.Dialogue.Conversations.Scenes.Count);
        Assert.Equal(allAlbums, shell.Dialogue.Music.Albums.Count);
        shell.Dialogue.Mode = DialogueMode.Lines;

        // Leaving Dialogue pauses the line; coming back, Play resumes it.
        shell.Dialogue.PlayLineCommand.Execute(shell.Dialogue.SelectedLine);
        PumpUntil(() => shell.Dialogue.Player.IsPlaying, 30, "the line to play again");

        // Characters → "hear their lines" lands on the speaker.
        shell.CurrentWorkspace = Workspace.Characters;
        Assert.False(shell.Dialogue.Player.IsPlaying);
        shell.Characters.SelectedCharacter = shell.Characters.Characters.First(c => c.Entry.Id == "sk54_rhys");
        PumpUntil(() => shell.Characters.HasModel && !shell.Characters.IsLoading, 60, "Rhys's model");
        shell.Characters.ShowLinesCommand.Execute(null);
        Assert.True(shell.IsDialogue);
        Assert.Equal("RHYS", shell.Dialogue.SelectedSpeaker?.Name);

        // Scripts: a compiled script reads back as Lua.
        shell.CurrentWorkspace = Workspace.Scripts;
        PumpUntil(() => shell.Scripts.Scripts.Count > 100, 30, "the script list");
        shell.Scripts.Selected = shell.Scripts.Scripts.First(r => r.Name == "AchievementManager.lua");
        PumpUntil(() => !shell.Scripts.IsLoading && shell.Scripts.Code.Length > 0, 60, "the decompiled script");
        Assert.Contains("function", shell.Scripts.Code);
        Assert.True(shell.Scripts.Outline.Count > 0);
        Shot(window, "e2e-tftb-scripts");
        shell.Scripts.IsDisassemblyView = true;
        PumpUntil(() => shell.Scripts.Code.Length > 0 && shell.Scripts.IsDisassemblyView, 30, "the listing");

        // Assets: a voice file previews as audio.
        shell.CurrentWorkspace = Workspace.Assets;
        shell.Assets.Query = "362321480.wav";
        PumpUntil(() => shell.Assets.Results.Count == 1, 30, "asset search");
        shell.Assets.Selected = shell.Assets.Results[0];
        PumpUntil(() => shell.Assets.Player.Clip is not null, 30, "audio preview");
        Assert.True(shell.Assets.IsAudioPreview);
        Shot(window, "e2e-tftb-assets-audio");
    }
}
