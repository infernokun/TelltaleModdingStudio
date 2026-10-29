using Avalonia.Headless.XUnit;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;
using TelltaleModding.Core.Scene;
using Xunit;

namespace TelltaleModding.App.Tests;

public class SettingsTests
{
    private static string TempSettings() => Path.Combine(Path.GetTempPath(), $"tms-settings-{Guid.NewGuid():N}.json");

    [AvaloniaFact]
    public void Preferences_apply_to_the_workspaces_and_are_saved()
    {
        AudioOutputs.Shared = new SilentAudioOutput();
        var file = TempSettings();
        try
        {
            using var shell = new ShellViewModel(new AppSettings { FilePath = file });
            var prefs = shell.Preferences;

            prefs.LoopClips = false;
            prefs.PlaybackSpeed = 0.5;
            prefs.Volume = 40;
            prefs.VoicedOnly = false;
            prefs.ShowSpeaker = false;
            prefs.ScriptsDecompiled = false;
            prefs.ConvertOnExport = false;
            prefs.SubtitleLanguage = "french";

            Assert.False(shell.Characters.Loop);
            Assert.Equal(0.5, shell.Characters.Speed);
            Assert.Equal(0.4f, AudioOutputs.Shared.Volume, 1e-3f);
            Assert.False(shell.Dialogue.VoicedOnly);
            Assert.False(shell.Dialogue.ShowSpeaker);
            Assert.Equal(ScriptViewMode.Disassembly, shell.Scripts.ViewMode);
            Assert.False(shell.Assets.ConvertOnExport);
            Assert.Equal("french", shell.Dialogue.PreferredLanguage);

            // Saved and restored with the rest of the settings.
            var reloaded = AppSettings.Load(file);
            Assert.False(reloaded.Preferences.LoopClips);
            Assert.Equal(40, reloaded.Preferences.Volume);
            Assert.Equal("french", reloaded.Preferences.SubtitleLanguage);
            shell.Look.ShowGraphicBlackCommand.Execute(null);
            prefs.GraphicBlackIntensity = 0.5;
            prefs.EngineDefaultIntensity = 1.5;
            reloaded = AppSettings.Load(file);
            Assert.Equal(1.5, reloaded.Preferences.EngineDefaultIntensity);
            Assert.Equal(RenderMode.GraphicBlack, reloaded.ViewportLook);
            Assert.Equal(0.5, reloaded.Preferences.GraphicBlackIntensity);
            using var shell2 = new ShellViewModel(reloaded);
            Assert.True(shell2.Look.IsGraphicBlack);
            Assert.False(shell2.Characters.Loop);
            Assert.Equal(ScriptViewMode.Disassembly, shell2.Scripts.ViewMode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [AvaloniaFact]
    public void Reset_restores_every_default()
    {
        var file = TempSettings();
        try
        {
            using var shell = new ShellViewModel(new AppSettings { FilePath = file });
            shell.Preferences.FieldOfView = 70;
            shell.Preferences.Background = "Light";
            shell.Preferences.SceneTextureSize = 0;
            shell.Settings.ResetDefaultsCommand.Execute(null);
            var defaults = new UserPreferences();
            Assert.Equal(defaults.FieldOfView, shell.Preferences.FieldOfView);
            Assert.Equal(defaults.Background, shell.Preferences.Background);
            Assert.Equal(defaults.SceneTextureSize, shell.Preferences.SceneTextureSize);
            Assert.Equal(1024, shell.Settings.SelectedSceneTextureSize.Size);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [AvaloniaFact]
    public void The_gear_opens_and_closes_settings()
    {
        using var shell = new ShellViewModel(new AppSettings { FilePath = TempSettings() });
        shell.OpenSettingsCommand.Execute(null);
        Assert.True(shell.IsSettings);
        shell.OpenSettingsCommand.Execute(null);
        Assert.False(shell.IsSettings);
    }
}
