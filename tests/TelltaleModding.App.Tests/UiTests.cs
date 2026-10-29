using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;
using TelltaleModding.App.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TelltaleModding.App.Tests.TestAppBuilder))]

namespace TelltaleModding.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the real window off-screen (no GL, so 3D viewports stay blank) and saves
/// PNGs for layout review to <see cref="ShotsFolder"/>. Settings go to a temp file,
/// never the user's library.
/// </summary>
public class UiTests : IDisposable
{
    public static string ShotsFolder => Path.Combine(Path.GetTempPath(), "TelltaleModdingStudio.UiShots");

    private readonly List<(MainWindow Window, ShellViewModel Shell)> _opened = new();

    // Close each test's window so its views stop following the shell into later tests.
    public void Dispose()
    {
        foreach (var (window, shell) in _opened)
        {
            window.Close();
            shell.Dispose();
        }
        _opened.Clear();
    }

    private (MainWindow Window, ShellViewModel Shell) CreateWindow()
    {
        var settingsFile = Path.Combine(Path.GetTempPath(), $"tms-ui-{Guid.NewGuid():N}.json");
        var shell = new ShellViewModel(new AppSettings { FilePath = settingsFile });
        // DataContext is set after construction so the window's Opened hook (which
        // restores the library) sees a fresh shell; it is not shown via Opened here.
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        _opened.Add((window, shell));
        return (window, shell);
    }

    private static void Save(MainWindow window, string name)
    {
        Directory.CreateDirectory(ShotsFolder);
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ShotsFolder, name + ".png"));
    }

    [AvaloniaFact]
    public void Renders_every_workspace_without_errors()
    {
        var (window, shell) = CreateWindow();
        window.Show();
        foreach (var workspace in Enum.GetValues<Workspace>())
        {
            shell.CurrentWorkspace = workspace;
            Save(window, $"empty-{workspace}".ToLowerInvariant());
        }
    }

    [AvaloniaFact]
    public void Loose_files_fill_the_character_viewer()
    {
        var samples = FindSamples();
        var (window, shell) = CreateWindow();
        window.Show();
        shell.OpenLooseFiles(new[]
        {
            Path.Combine(samples, "sk62_clementine400_head.d3dmesh"),
            Path.Combine(samples, "sk62_clementine400_eyesMouth.d3dmesh"),
        }, null);

        // Model assembly runs on a background thread; pump the UI until it lands.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!shell.Characters.HasModel && shell.Characters.Error is null && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }
        Assert.True(shell.Characters.HasModel, shell.Characters.Error);
        Assert.Contains(shell.Characters.Details, d => d.StartsWith("Skeleton: sk62_clementine400.skl"));
        Assert.True(shell.Characters.Animations.Count > 0, "sample animations should be listed");
        Save(window, "loose-characters");
    }

    private static string FindSamples()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "samples")) && Directory.Exists(Path.Combine(dir, "engine")))
                return Path.Combine(dir, "samples");
        }
        throw new DirectoryNotFoundException("samples folder not found");
    }
}
