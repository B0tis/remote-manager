using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RemoteManager.App.Controls;

/// <summary>
/// One thin bar for the connections on screen: how many answer, how many refuse, and how many are unknown,
/// as proportional segments. Reads faster than three numbers when the list is long.
/// </summary>
public sealed class HealthBar : Control
{
    public static readonly StyledProperty<int> OnlineProperty = AvaloniaProperty.Register<HealthBar, int>(nameof(Online));
    public static readonly StyledProperty<int> OfflineProperty = AvaloniaProperty.Register<HealthBar, int>(nameof(Offline));
    public static readonly StyledProperty<int> UnknownProperty = AvaloniaProperty.Register<HealthBar, int>(nameof(Unknown));
    public static readonly StyledProperty<IBrush?> OnlineBrushProperty = AvaloniaProperty.Register<HealthBar, IBrush?>(nameof(OnlineBrush));
    public static readonly StyledProperty<IBrush?> OfflineBrushProperty = AvaloniaProperty.Register<HealthBar, IBrush?>(nameof(OfflineBrush));
    public static readonly StyledProperty<IBrush?> UnknownBrushProperty = AvaloniaProperty.Register<HealthBar, IBrush?>(nameof(UnknownBrush));

    private const double Gap = 3;

    static HealthBar()
    {
        AffectsRender<HealthBar>(OnlineProperty, OfflineProperty, UnknownProperty, OnlineBrushProperty, OfflineBrushProperty, UnknownBrushProperty);
    }

    public int Online
    {
        get => GetValue(OnlineProperty);
        set => SetValue(OnlineProperty, value);
    }

    public int Offline
    {
        get => GetValue(OfflineProperty);
        set => SetValue(OfflineProperty, value);
    }

    public int Unknown
    {
        get => GetValue(UnknownProperty);
        set => SetValue(UnknownProperty, value);
    }

    public IBrush? OnlineBrush
    {
        get => GetValue(OnlineBrushProperty);
        set => SetValue(OnlineBrushProperty, value);
    }

    public IBrush? OfflineBrush
    {
        get => GetValue(OfflineBrushProperty);
        set => SetValue(OfflineBrushProperty, value);
    }

    public IBrush? UnknownBrush
    {
        get => GetValue(UnknownBrushProperty);
        set => SetValue(UnknownBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Size size = Bounds.Size;
        (int Count, IBrush? Brush)[] parts = [(Online, OnlineBrush), (Offline, OfflineBrush), (Unknown, UnknownBrush)];
        int total = parts.Sum(p => Math.Max(0, p.Count));
        double radius = size.Height / 2;
        if (total == 0)
        {
            if (UnknownBrush is { } empty)
                context.DrawRectangle(empty, null, new RoundedRect(new Rect(size), radius));
            return;
        }

        int shown = parts.Count(p => p.Count > 0);
        double available = size.Width - (Gap * (shown - 1));
        double x = 0;
        foreach ((int count, IBrush? brush) in parts)
        {
            if (count <= 0 || brush is null)
                continue;
            // Even one connection among hundreds stays visible.
            double width = Math.Max(size.Height, available * count / total);
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, 0, Math.Min(width, size.Width - x), size.Height), radius));
            x += width + Gap;
        }
    }
}
