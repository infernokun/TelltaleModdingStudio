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
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
