using Avalonia.Controls;
using TelltaleModding.App.ViewModels;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.Views;

public partial class AssetsView : UserControl
{
    private AssetsViewModel? _vm;

    public AssetsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.MeshPreviewReady -= OnMeshPreviewReady;
            _vm = DataContext as AssetsViewModel;
            if (_vm is not null)
                _vm.MeshPreviewReady += OnMeshPreviewReady;
        };

        ViewportHost.PointerPressed += (_, e) => Viewport.HandlePointerPressed(e);
        ViewportHost.PointerMoved += (_, e) => Viewport.HandlePointerMoved(e);
        ViewportHost.PointerReleased += (_, e) => Viewport.HandlePointerReleased(e);
        ViewportHost.PointerWheelChanged += (_, e) => Viewport.HandlePointerWheel(e);
    }

    private void OnMeshPreviewReady(AssembledModel model) =>
        Viewport.SetMesh(model.Geometry, model.BatchTextures, model.BatchNormalMaps);
}
