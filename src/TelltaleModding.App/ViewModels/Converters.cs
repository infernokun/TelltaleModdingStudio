using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TelltaleModding.App.ViewModels;

/// <summary>Small one-way converters used by the views.</summary>
public static class Converters
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#F0545C"));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#272C37"));

    /// <summary>Accent border for the active game card.</summary>
    public static readonly IValueConverter ActiveBorder =
        new FuncValueConverter<bool, IBrush>(active => active ? AccentBrush : LineBrush);

    public static readonly IValueConverter OpeningLabel =
        new FuncValueConverter<bool, string>(busy => busy ? "Opening…" : "Open");

    public static readonly IValueConverter ScanLabel =
        new FuncValueConverter<bool, string>(scanning => scanning
            ? "Looking for Telltale games in your Steam libraries…"
            : "Nothing new found. Use “Add game folder…” for games installed elsewhere.");

    /// <summary>What the empty character viewer says.</summary>
    public static readonly IValueConverter EmptyViewer =
        new FuncValueConverter<bool, string>(hasSource => hasSource
            ? "Pick a character to load their model."
            : "Open a game from Home to start.");

    /// <summary>Speed multiplier label, e.g. 0.5 → "0.5×".</summary>
    public static readonly IValueConverter SpeedLabel =
        new FuncValueConverter<double, string>(speed => $"{speed:0.##}×");
}
