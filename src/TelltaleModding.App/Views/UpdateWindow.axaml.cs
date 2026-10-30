using Avalonia.Controls;
using Avalonia.Interactivity;
using TelltaleModding.App.ViewModels;

namespace TelltaleModding.App.Views;

/// <summary>The popup that offers a newer release; closes with the user's <see cref="UpdateChoice"/>.</summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
    }

    public UpdateWindow(UpdatePrompt prompt) : this()
    {
        DataContext = prompt;
    }

    /// <summary>Shows the popup over <paramref name="owner"/>; closing it any other way means Later.</summary>
    public static async Task<UpdateChoice> AskAsync(Window owner, UpdatePrompt prompt) =>
        await new UpdateWindow(prompt).ShowDialog<UpdateChoice?>(owner) ?? UpdateChoice.Later;

    private void OnDownload(object? sender, RoutedEventArgs e) => Close(UpdateChoice.Download);

    private void OnSkip(object? sender, RoutedEventArgs e) => Close(UpdateChoice.Skip);

    private void OnLater(object? sender, RoutedEventArgs e) => Close(UpdateChoice.Later);
}
