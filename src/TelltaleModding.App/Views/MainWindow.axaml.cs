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
                vm.Dialogs = new DialogService(this);
        };
    }
}
