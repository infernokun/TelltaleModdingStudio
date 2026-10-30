using Avalonia.Controls;
using Avalonia.Input;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App.Views;

public partial class DialogueView : UserControl
{
    private DialogueViewModel? _vm;
    private IDisposable? _look;
    private GridLength _speakerWidth = new(0.45, GridUnitType.Star);

    public DialogueView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.Speaker.ModelReady -= OnSpeakerModel;
                _vm.Speaker.ModelCleared -= SpeakerViewport.Clear;
                _vm.Speaker.PoseReady -= SpeakerViewport.UpdateSkinMatrices;
                _vm.Speaker.FaceFramingReady -= OnSpeakerHead;
                _vm.PropertyChanged -= OnViewModelChanged;
                _look?.Dispose();
            }
            _vm = DataContext as DialogueViewModel;
            if (_vm is not null)
            {
                _vm.Speaker.ModelReady += OnSpeakerModel;
                _vm.Speaker.ModelCleared += SpeakerViewport.Clear;
                _vm.Speaker.PoseReady += SpeakerViewport.UpdateSkinMatrices;
                _vm.Speaker.FaceFramingReady += OnSpeakerHead;
                _vm.PropertyChanged += OnViewModelChanged;
                _look = LookToggle.Attach(SpeakerViewport, _vm.Look);
                FoldSpeaker(_vm.IsSpeakerVisible);
            }
        };
        SpeakerHost.PointerPressed += (_, e) => SpeakerViewport.HandlePointerPressed(e);
        SpeakerHost.PointerMoved += (_, e) => SpeakerViewport.HandlePointerMoved(e);
        SpeakerHost.PointerReleased += (_, e) => SpeakerViewport.HandlePointerReleased(e);
        SpeakerHost.PointerWheelChanged += (_, e) => SpeakerViewport.HandlePointerWheel(e);
        // Enter or Space replays the selected line.
        LineList.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && DataContext is DialogueViewModel vm && vm.SelectedLine is { } line)
            {
                vm.PlayLineCommand.Execute(line);
                e.Handled = true;
            }
        };
        LineList.DoubleTapped += (_, _) =>
        {
            if (DataContext is DialogueViewModel vm && vm.SelectedLine is { } line)
                vm.PlayLineCommand.Execute(line);
        };
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DialogueViewModel.IsSpeakerVisible) && _vm is not null)
            FoldSpeaker(_vm.IsSpeakerVisible);
    }

    // A hidden speaker gives its width (and the gap before it) back to the list; showing
    // it again restores the width it had, including any the user dragged it to.
    private void FoldSpeaker(bool visible)
    {
        var gap = Columns.ColumnDefinitions[2];
        var speaker = Columns.ColumnDefinitions[3];
        if (visible)
        {
            gap.Width = new GridLength(12);
            speaker.MinWidth = 240;
            speaker.Width = _speakerWidth;
        }
        else if (speaker.MinWidth > 0)
        {
            _speakerWidth = speaker.Width;
            gap.Width = new GridLength(0);
            speaker.MinWidth = 0;
            speaker.Width = new GridLength(0);
        }
    }

    private void OnSpeakerHead(System.Numerics.Vector3 headTop, float height) =>
        SpeakerViewport.FocusFaceAt(headTop, height, 3f);

    // A new speaker is framed on their face; the same speaker keeps the camera.
    private void OnSpeakerModel(TelltaleModding.Core.Scene.AssembledModel model, bool frame)
    {
        SpeakerViewport.SetModel(model, keepCamera: !frame);
        if (model.CanAnimate)
            SpeakerViewport.SetSkinning(TelltaleModding.Core.Meshes.MeshSkinner.ResolveVertexBones(model.Geometry, model.Skeleton!),
                model.Geometry.BlendWeights);
        if (frame)
            SpeakerViewport.FocusFace(distance: 3f);
    }
}
