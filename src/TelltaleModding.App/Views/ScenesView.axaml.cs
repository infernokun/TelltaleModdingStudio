using Avalonia.Controls;
using TelltaleModding.App.ViewModels;
using TelltaleModding.Core.Scene;

namespace TelltaleModding.App.Views;

public partial class ScenesView : UserControl
{
    private ScenesViewModel? _vm;
    private IDisposable? _look;

    public ScenesView()
    {
        InitializeComponent();

        // Scenes are explored from inside, so allow a much closer camera than for characters.
        Viewport.MinZoom = 0.01f;
        Viewport.NearPlaneFactor = 0.002f;

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.SceneReady -= OnSceneReady;
                _vm.BatchVisibilityChanged -= Viewport.SetBatchVisibility;
                _vm.ResetViewRequested -= Viewport.ResetView;
                _vm.SceneCleared -= Viewport.Clear;
                _vm.LightingChanged -= Viewport.SetLighting;
                _look?.Dispose();
            }
            _vm = DataContext as ScenesViewModel;
            if (_vm is not null)
            {
                _vm.SceneReady += OnSceneReady;
                _vm.BatchVisibilityChanged += Viewport.SetBatchVisibility;
                _vm.ResetViewRequested += Viewport.ResetView;
                _vm.SceneCleared += Viewport.Clear;
                _vm.LightingChanged += Viewport.SetLighting;
                _look = LookToggle.Attach(Viewport, _vm.Look);
                Viewport.SetLighting(_vm.CurrentRig);
            }
        };

        ViewportHost.PointerPressed += (_, e) => Viewport.HandlePointerPressed(e);
        ViewportHost.PointerMoved += (_, e) => Viewport.HandlePointerMoved(e);
        ViewportHost.PointerReleased += (_, e) => Viewport.HandlePointerReleased(e);
        ViewportHost.PointerWheelChanged += (_, e) => Viewport.HandlePointerWheel(e);
    }

    private void OnSceneReady(AssembledScene scene, bool keepCamera)
    {
        Viewport.SetModel(scene.Model, keepCamera);
        if (scene.Lightmaps is { } lightmaps)
            Viewport.SetLightmaps(lightmaps.Uv2, lightmaps.BatchLightmaps, SceneLightmaps.Scale, lightmaps.Intensity);
        Viewport.SetLighting(_vm?.CurrentRig);
    }
}
