using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App.Views;

/// <summary>
/// A scene's conversations as a tree and as a click-through player. Set its DataContext
/// to a <see cref="ConversationViewModel"/>.
/// </summary>
public partial class ConversationView : UserControl
{
    private ConversationViewModel? _vm;

    public ConversationView()
    {
        InitializeComponent();
        // Enter or Space plays the selected line; double-click follows a jump.
        NodeTree.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space && NodeTree.SelectedItem is ConversationNodeViewModel item)
            {
                Activate(item);
                e.Handled = true;
            }
        };
        NodeTree.DoubleTapped += (_, _) =>
        {
            if (NodeTree.SelectedItem is ConversationNodeViewModel item)
                Activate(item);
        };
    }

    private static void Activate(ConversationNodeViewModel item)
    {
        if (item.CanPlay)
            item.PlayCommand.Execute(null);
        else if (item.IsLink)
            item.GoToCommand.Execute(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Transcript.CollectionChanged -= OnTranscriptChanged;
        }
        _vm = DataContext as ConversationViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Transcript.CollectionChanged += OnTranscriptChanged;
        }
    }

    // Keep the selected node in view once its ancestors have expanded.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationViewModel.SelectedNode) || _vm?.SelectedNode is not { } item)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (NodeTree.TreeContainerFromItem(item) is Control container)
                container.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.UIThread.Post(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
