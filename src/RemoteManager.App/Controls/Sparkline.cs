using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RemoteManager.App.Controls;

/// <summary>
/// The answer times of the last status checks as a small line. A check without an answer (offline, unknown)
/// breaks the line and leaves a mark on the baseline, so an unstable host shows as gaps.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double?>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> GapBrushProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(GapBrush));

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, GapBrushProperty);
    }

    /// <summary>Answer times in milliseconds, oldest first; null for a check without an answer.</summary>
    public IReadOnlyList<double?>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? GapBrush
    {
        get => GetValue(GapBrushProperty);
        set => SetValue(GapBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Values is not { Count: > 0 } values || Stroke is not { } stroke)
            return;

        Size size = Bounds.Size;
        const double pad = 3;
        double max = Math.Max(1, values.Max(v => v ?? 0));
        double step = values.Count > 1 ? (size.Width - (2 * pad)) / (values.Count - 1) : 0;
        Point At(int index, double value) => new(pad + (index * step), size.Height - pad - ((size.Height - (2 * pad)) * value / max));

        var pen = new Pen(stroke, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        Point? previous = null;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                if (GapBrush is { } gap)
                    context.DrawEllipse(gap, null, new Point(pad + (i * step), size.Height - pad), 1.6, 1.6);
                previous = null;
                continue;
            }

            Point point = At(i, value);
            if (previous is { } from)
                context.DrawLine(pen, from, point);
            else if (i == values.Count - 1 || values[i + 1] is null)
                context.DrawEllipse(stroke, null, point, 1.4, 1.4); // a single answer between gaps
            previous = point;
        }

        if (previous is { } last)
            context.DrawEllipse(stroke, null, last, 2.6, 2.6);
    }
}
