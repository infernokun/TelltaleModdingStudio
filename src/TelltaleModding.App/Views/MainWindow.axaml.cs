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
                vm.MeshPreviewReady += (geometry, texture) => Viewport.SetMesh(geometry, texture);
            }
        };

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
