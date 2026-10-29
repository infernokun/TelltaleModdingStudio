using Avalonia.Controls;
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
                vm.Dialogs = new DialogService(this);
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
}
