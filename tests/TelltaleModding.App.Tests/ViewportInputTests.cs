using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;
using TelltaleModding.App.Views;
using Xunit;

namespace TelltaleModding.App.Tests;

public class ViewportInputTests
{
    private static T Field<T>(MeshViewport viewport, string name) =>
        (T)typeof(MeshViewport).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewport)!;

    [AvaloniaFact]
    public void Holding_right_and_pressing_w_flies()
    {
        var viewport = new MeshViewport();
        var host = new Panel { Background = Avalonia.Media.Brushes.Transparent, Children = { viewport } };
        var window = new Window { Width = 400, Height = 300, Content = host };
        host.PointerPressed += (_, e) => viewport.HandlePointerPressed(e);
        host.PointerMoved += (_, e) => viewport.HandlePointerMoved(e);
        host.PointerReleased += (_, e) => viewport.HandlePointerReleased(e);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(new Point(200, 150), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Field<bool>(viewport, "_flying"));
            Assert.True(viewport.IsFocused, $"focused: {TopLevel.GetTopLevel(viewport)?.FocusManager?.GetFocusedElement()}");

            window.KeyPress(Key.W, RawInputModifiers.RightMouseButton, PhysicalKey.W, "w");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(Key.W, Field<HashSet<Key>>(viewport, "_flyKeys"));
            window.KeyRelease(Key.W, RawInputModifiers.RightMouseButton, PhysicalKey.W, "w");
            window.MouseUp(new Point(200, 150), MouseButton.Right);

            // Once clicked, the keys move the camera without holding a button too.
            window.MouseDown(new Point(200, 150), MouseButton.Left);
            window.MouseUp(new Point(200, 150), MouseButton.Left);
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { Key.A }, Field<HashSet<Key>>(viewport, "_flyKeys"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Flying_works_in_the_character_viewer()
    {
        using var shell = new ShellViewModel(new AppSettings { FilePath = Path.Combine(Path.GetTempPath(), $"tms-fly-{Guid.NewGuid():N}.json") });
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        try
        {
            var samples = Path.Combine(Path.GetDirectoryName(typeof(ViewportInputTests).Assembly.Location)!, "..", "..", "..", "..", "samples");
            for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
                if (Directory.Exists(Path.Combine(dir, "samples")) && Directory.Exists(Path.Combine(dir, "engine")))
                {
                    samples = Path.Combine(dir, "samples");
                    break;
                }
            shell.OpenLooseFiles(new[] { Path.Combine(samples, "sk62_clementine400_head.d3dmesh") }, null);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!shell.Characters.HasModel && shell.Characters.Error is null && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
            }
            Assert.True(shell.Characters.HasModel, shell.Characters.Error);
            Dispatcher.UIThread.RunJobs();
            var viewport = window.GetVisualDescendants().OfType<MeshViewport>().First(v => v.IsEffectivelyVisible && v.Bounds.Width > 0);
            var centre = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;

            window.MouseDown(centre, MouseButton.Right);
            window.MouseMove(centre + new Point(5, 0), RawInputModifiers.RightMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.True(Field<bool>(viewport, "_flying"));
            Assert.True(viewport.IsFocused, $"focused: {TopLevel.GetTopLevel(viewport)?.FocusManager?.GetFocusedElement()}");

            window.KeyPress(Key.W, RawInputModifiers.RightMouseButton, PhysicalKey.W, "w");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(Key.W, Field<HashSet<Key>>(viewport, "_flyKeys"));
        }
        finally
        {
            window.Close();
        }
    }
}
