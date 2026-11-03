using Avalonia;
using Avalonia.Controls;

namespace RemoteManager.App.Views;

/// <summary>
/// A wrap panel for the tile view where group headers take a whole row: tiles flow left to right,
/// a header (an item whose data is a <see cref="GroupHeaderItem"/>) starts a new line and spans it.
/// Tiles share the row evenly: as many as fit at <see cref="MinTileWidth"/>, each at most
/// <see cref="MaxTileWidth"/> wide (the width includes the item margin).
/// </summary>
public sealed class SectionWrapPanel : Panel
{
    public static readonly StyledProperty<double> MinTileWidthProperty =
        AvaloniaProperty.Register<SectionWrapPanel, double>(nameof(MinTileWidth), 254);

    public static readonly StyledProperty<double> MaxTileWidthProperty =
        AvaloniaProperty.Register<SectionWrapPanel, double>(nameof(MaxTileWidth), 334);

    static SectionWrapPanel() => AffectsMeasure<SectionWrapPanel>(MinTileWidthProperty, MaxTileWidthProperty);

    public double MinTileWidth
    {
        get => GetValue(MinTileWidthProperty);
        set => SetValue(MinTileWidthProperty, value);
    }

    public double MaxTileWidth
    {
        get => GetValue(MaxTileWidthProperty);
        set => SetValue(MaxTileWidthProperty, value);
    }

    /// <summary>The width every tile gets for a row of <paramref name="width"/> (the last layout's, for tests).</summary>
    public double TileWidth { get; private set; }

    private double SlotWidth(double width)
    {
        if (width <= 0 || double.IsInfinity(width))
            return MinTileWidth;
        int columns = Math.Max(1, (int)Math.Floor(width / MinTileWidth));
        return Math.Min(MaxTileWidth, Math.Floor(width / columns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double slot = SlotWidth(width);
        TileWidth = slot;
        double x = 0, y = 0, rowHeight = 0, widest = 0;
        foreach (Control child in Children)
        {
            if (IsHeader(child))
            {
                child.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
                y += rowHeight + child.DesiredSize.Height;
                x = rowHeight = 0;
                widest = Math.Max(widest, child.DesiredSize.Width);
                continue;
            }

            child.Measure(new Size(slot, double.PositiveInfinity));
            if (x > 0 && width > 0 && x + slot > width + 0.5)
            {
                y += rowHeight;
                x = rowHeight = 0;
            }

            x += slot;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            widest = Math.Max(widest, x);
        }

        return new Size(width > 0 ? width : widest, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double slot = SlotWidth(finalSize.Width);
        double x = 0, y = 0, rowHeight = 0;
        foreach (Control child in Children)
        {
            if (IsHeader(child))
            {
                y += rowHeight;
                child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
                y += child.DesiredSize.Height;
                x = rowHeight = 0;
                continue;
            }

            if (x > 0 && x + slot > finalSize.Width + 0.5)
            {
                y += rowHeight;
                x = rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, slot, child.DesiredSize.Height));
            x += slot;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        return finalSize;
    }

    private static bool IsHeader(Control child) => child is ContentControl { Content: GroupHeaderItem };
}
