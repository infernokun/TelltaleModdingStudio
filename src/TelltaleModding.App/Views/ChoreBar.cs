using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TelltaleModding.App.Views;

/// <summary>
/// Where a resource plays within its chore: a thin track for the whole chore with a bar
/// for each span the resource plays in (fractions of the chore's length).
/// </summary>
public sealed class ChoreBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<(double Start, double End)>?> SpansProperty =
        AvaloniaProperty.Register<ChoreBar, IReadOnlyList<(double Start, double End)>?>(nameof(Spans));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ChoreBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<ChoreBar, IBrush?>(nameof(Track));

    static ChoreBar() => AffectsRender<ChoreBar>(SpansProperty, FillProperty, TrackProperty);

    public IReadOnlyList<(double Start, double End)>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        double mid = size.Height / 2;
        if (Track is { } track)
            context.FillRectangle(track, new Rect(0, mid - 1, size.Width, 2));
        if (Fill is not { } fill || Spans is not { } spans)
            return;
        foreach (var (start, end) in spans)
        {
            double x = Math.Clamp(start, 0, 1) * size.Width;
            double w = Math.Max(2, (Math.Clamp(end, 0, 1) - Math.Clamp(start, 0, 1)) * size.Width);
            context.DrawRectangle(fill, null, new RoundedRect(new Rect(x, mid - 4, w, 8), 2));
        }
    }
}
