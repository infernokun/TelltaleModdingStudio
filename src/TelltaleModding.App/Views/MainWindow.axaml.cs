using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel vm)
            {
                vm.Dialogs = new DialogService(this);
                vm.Updates.ShowPrompt = prompt => UpdateWindow.AskAsync(this, prompt);
            }
        };

        // Debug hooks for automated verification with loose extracted files:
        //   --preview-mesh <path> [<path>...]  shows the meshes as one model
        //   --play-anim <path>                  plays an animation on it
        // Otherwise the library is restored and the last game reopened.
        Opened += async (_, _) =>
        {
            if (DataContext is not ShellViewModel vm)
                return;
            var args = Environment.GetCommandLineArgs();
            int meshFlag = Array.IndexOf(args, "--preview-mesh");
            if (meshFlag >= 0 && meshFlag + 1 < args.Length)
            {
                var meshPaths = args.Skip(meshFlag + 1).TakeWhile(a => !a.StartsWith("--")).ToList();
                int animFlag = Array.IndexOf(args, "--play-anim");
                vm.OpenLooseFiles(meshPaths, animFlag >= 0 && animFlag + 1 < args.Length ? args[animFlag + 1] : null);
                return;
            }
            await vm.InitializeAsync();
        };
    }

    // The header stands in for the system title bar: dragging it moves the window (with the
    // system's snapping) and a double-click maximizes or restores. Buttons in it handle their
    // own presses, so only its empty parts and labels get here.
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
            ToggleMaximized();
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximized();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || MaximizeIcon is null)
            return;
        bool maximized = WindowState == WindowState.Maximized;
        if (this.FindResource(maximized ? "CaptionRestore" : "CaptionMaximize") is Geometry icon)
            MaximizeIcon.Data = icon;
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
    }
}
