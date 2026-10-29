using Avalonia.Controls;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App.Views;

public partial class CharactersView : UserControl
{
    private CharactersViewModel? _vm;
    private IDisposable? _look;

    public CharactersView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.ModelReady -= OnModelReady;
                _vm.ModelCleared -= OnModelCleared;
                _vm.PoseReady -= Viewport.UpdateSkinMatrices;
                _vm.ResetViewRequested -= Viewport.ResetView;
                _vm.FaceViewRequested -= Viewport.FocusFace;
                _vm.FaceFrameRequested -= Viewport.FocusFace;
                _look?.Dispose();
            }
            _vm = DataContext as CharactersViewModel;
            if (_vm is not null)
            {
                _vm.ModelReady += OnModelReady;
                _vm.ModelCleared += OnModelCleared;
                _vm.PoseReady += Viewport.UpdateSkinMatrices;
                _vm.ResetViewRequested += Viewport.ResetView;
                _vm.FaceViewRequested += Viewport.FocusFace;
                _vm.FaceFrameRequested += Viewport.FocusFace;
                _look = LookToggle.Attach(Viewport, _vm.Look);
                SkeletonToggle.IsChecked = _vm.Look.Preferences.ShowSkeleton;
            }
        };

        // The GL viewport is not reliably hit-testable, so its transparent host
        // panel forwards pointer input (handlers no-op when the viewport handled it).
        SkeletonToggle.IsCheckedChanged += (_, _) => Viewport.ShowSkeleton = SkeletonToggle.IsChecked == true;
        ViewportHost.PointerPressed += (_, e) => Viewport.HandlePointerPressed(e);
        ViewportHost.PointerMoved += (_, e) => Viewport.HandlePointerMoved(e);
        ViewportHost.PointerReleased += (_, e) => Viewport.HandlePointerReleased(e);
        ViewportHost.PointerWheelChanged += (_, e) => Viewport.HandlePointerWheel(e);
    }

    private void OnModelReady(Core.Scene.AssembledModel model, bool frame)
    {
        Viewport.SetModel(model, keepCamera: !frame);
        if (model.CanAnimate)
            Viewport.SetSkinning(Core.Meshes.MeshSkinner.ResolveVertexBones(model.Geometry, model.Skeleton!),
                model.Geometry.BlendWeights);
        if (model.Skeleton is { } skeleton)
            Viewport.SetSkeleton(skeleton.BindPoseWorld().Select(m => m.Translation).ToList(),
                skeleton.Bones.Select(b => b.ParentIndex).ToList());
    }

    private void OnModelCleared() => Viewport.Clear();
}
