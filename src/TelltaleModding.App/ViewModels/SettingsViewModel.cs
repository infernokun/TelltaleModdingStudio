using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;
using TelltaleModding.Core.Catalog;

namespace TelltaleModding.App.ViewModels;

/// <summary>
/// The Settings page: the user's preferences for every workspace, plus a little upkeep
/// (the game index cache and the crash log). Changes apply at once and are saved.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(UserPreferences preferences, ViewportLookViewModel look)
    {
        Preferences = preferences;
        Look = look;
        RefreshCacheSize();
    }

    public UserPreferences Preferences { get; }

    /// <summary>The default viewport look (Studio or Game), shared with the viewports.</summary>
    public ViewportLookViewModel Look { get; }

    /// <summary>Set by the shell so the export folder can be picked.</summary>
    public IDialogService? Dialogs { get; set; }

    public IReadOnlyList<string> StartWorkspaces { get; } = new[] { "Characters", "Scenes", "Dialogue", "Scripts", "Assets" };
    public IReadOnlyList<string> Backgrounds { get; } = new[] { "Dark", "Black", "Grey", "Light" };
    public IReadOnlyList<double> Speeds { get; } = new[] { 0.25, 0.5, 1.0, 1.5, 2.0 };
    public IReadOnlyList<int> FrameRates { get; } = new[] { 15, 24, 30, 60 };

    public IReadOnlyList<string> Languages { get; } = new[]
    {
        "english", "french", "german", "italian", "spanish", "portuguese", "brazilian_portuguese",
        "russian", "polish", "arabic", "chinese", "traditional_chinese", "japanese", "korean",
    };

    public IReadOnlyList<SceneTextureOption> SceneTextureSizes { get; } = new[]
    {
        new SceneTextureOption("Low (512)", 512), new SceneTextureOption("Medium (1024)", 1024),
        new SceneTextureOption("High (2048)", 2048), new SceneTextureOption("Full size", 0),
    };

    /// <summary>The scene texture option matching <see cref="UserPreferences.SceneTextureSize"/>.</summary>
    public SceneTextureOption SelectedSceneTextureSize
    {
        get => SceneTextureSizes.FirstOrDefault(o => o.Size == Preferences.SceneTextureSize) ?? SceneTextureSizes[1];
        set
        {
            if (value is not null)
                Preferences.SceneTextureSize = value.Size;
        }
    }

    // ---- Upkeep ---------------------------------------------------------------

    [ObservableProperty]
    private string _cacheSize = "";

    private void RefreshCacheSize()
    {
        long bytes = 0;
        try
        {
            var folder = CatalogOptions.DefaultCacheFolder;
            if (Directory.Exists(folder))
                bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        CacheSize = bytes == 0 ? "empty" : AssetsViewModel.FormatSize(bytes);
    }

    /// <summary>
    /// Deletes the saved game indexes; each game re-indexes (a few seconds) the next
    /// time it opens. The game files themselves are never touched.
    /// </summary>
    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            var folder = CatalogOptions.DefaultCacheFolder;
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder))
                    File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        RefreshCacheSize();
    }

    public bool HasCrashLog => File.Exists(CrashLog.FilePath);

    [RelayCommand]
    private void OpenCrashLog()
    {
        if (File.Exists(CrashLog.FilePath))
            Process.Start(new ProcessStartInfo(CrashLog.FilePath) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task BrowseExportFolder()
    {
        if (Dialogs is null)
            return;
        var folder = await Dialogs.OpenFolderAsync("Folder for exports and saves");
        if (folder is not null)
            Preferences.ExportFolder = folder;
    }

    [RelayCommand]
    private void ClearExportFolder() => Preferences.ExportFolder = "";

    /// <summary>Puts every preference back to its default.</summary>
    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = new UserPreferences();
        foreach (var property in typeof(UserPreferences).GetProperties())
        {
            if (property.CanWrite && property.GetSetMethod() is not null && property.DeclaringType == typeof(UserPreferences))
                property.SetValue(Preferences, property.GetValue(defaults));
        }
        OnPropertyChanged(nameof(SelectedSceneTextureSize));
    }

    /// <summary>Refreshes values that live outside the preferences when the page is shown.</summary>
    public void Refresh()
    {
        RefreshCacheSize();
        OnPropertyChanged(nameof(HasCrashLog));
    }
}

/// <summary>A scene texture detail choice.</summary>
public sealed record SceneTextureOption(string Label, int Size);
