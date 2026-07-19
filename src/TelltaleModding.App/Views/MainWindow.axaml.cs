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
                vm.DeformedFrameReady += (positions, normals) => Viewport.UpdateDeformedVertices(positions, normals);
            }
        };

        // The GL viewport is not reliably hit-testable, so its transparent host
        // panel forwards pointer input (handlers no-op when the viewport handled it).
        ViewportHost.PointerPressed += (_, e) => Viewport.HandlePointerPressed(e);
        ViewportHost.PointerMoved += (_, e) => Viewport.HandlePointerMoved(e);
        ViewportHost.PointerReleased += (_, e) => Viewport.HandlePointerReleased(e);
        ViewportHost.PointerWheelChanged += (_, e) => Viewport.HandlePointerWheel(e);

        // Debug hooks for automated screenshot verification:
        //   --preview-mesh <path>  loads a mesh (opening its folder for asset lookup)
        //   --play-anim <path>     starts an animation on the previewed mesh
        Opened += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            if (DataContext is not MainWindowViewModel vm)
                return;
            int meshFlag = Array.IndexOf(args, "--preview-mesh");
            if (meshFlag >= 0 && meshFlag + 1 < args.Length)
            {
                var meshPath = args[meshFlag + 1];
                if (System.IO.Path.GetDirectoryName(meshPath) is { } dir)
                    vm.OpenFolder(dir);
                vm.ShowD3dMeshPreview(meshPath);

                int animFlag = Array.IndexOf(args, "--play-anim");
                if (animFlag >= 0 && animFlag + 1 < args.Length)
                    vm.DebugPlayAnimation(args[animFlag + 1]);
            }
        };
    }
}
