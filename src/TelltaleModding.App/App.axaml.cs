using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TelltaleModding.App.ViewModels;
using TelltaleModding.App.Views;

namespace TelltaleModding.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel();
            var window = new MainWindow { DataContext = shell };
            // Only the real app looks for updates on start (test windows never reach here).
            // It runs in the background and stays silent unless there is something new.
            window.Opened += (_, _) => _ = shell.Updates.CheckOnStartAsync();
            LoadUserBoneNames();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Bone names the user has worked out themselves (see BoneNames.UserFilePath); an
    // unreadable file only leaves those bones unnamed.
    private static void LoadUserBoneNames()
    {
        try
        {
            Core.Export.BoneNames.LoadFile(Core.Export.BoneNames.UserFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.CrashLog.Write("reading user bone names", ex);
        }
    }
}
