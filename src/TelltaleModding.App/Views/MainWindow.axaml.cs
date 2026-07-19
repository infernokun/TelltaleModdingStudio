using Avalonia.Controls;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Hand the view-model a dialog service once the window exists.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Dialogs = new DialogService(this);
                vm.MeshPreviewReady += (geometry, textures, normalMaps) => Viewport.SetMesh(geometry, textures, normalMaps);
            }
        };

        // The GL viewport is not reliably hit-testable, so its transparent host
        // panel forwards pointer input (handlers no-op when the viewport handled it).
        ViewportHost.PointerPressed += (_, e) => Viewport.HandlePointerPressed(e);
        ViewportHost.PointerMoved += (_, e) => Viewport.HandlePointerMoved(e);
        ViewportHost.PointerReleased += (_, e) => Viewport.HandlePointerReleased(e);
        ViewportHost.PointerWheelChanged += (_, e) => Viewport.HandlePointerWheel(e);

        // Debug hook: --preview-mesh <path> loads a mesh at startup (used for
        // automated screenshot verification of the GL pipeline).
        Opened += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "--preview-mesh");
            if (flag >= 0 && flag + 1 < args.Length && DataContext is MainWindowViewModel vm)
                vm.ShowD3dMeshPreview(args[flag + 1]);
        };
    }
}
