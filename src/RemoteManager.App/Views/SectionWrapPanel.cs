using Avalonia;
using Avalonia.Controls;

namespace RemoteManager.App.Views;

/// <summary>
/// A wrap panel for the tile view where group headers take a whole row: tiles flow left to right,
/// a header (an item whose data is a <see cref="GroupHeaderItem"/>) starts a new line and spans it.
/// </summary>
public sealed class SectionWrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
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

            child.Measure(availableSize);
            Size size = child.DesiredSize;
            if (x > 0 && width > 0 && x + size.Width > width)
            {
                y += rowHeight;
                x = rowHeight = 0;
            }

            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            widest = Math.Max(widest, x);
        }

        return new Size(width > 0 ? width : widest, y + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
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

            Size size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width)
            {
                y += rowHeight;
                x = rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        return finalSize;
    }

    private static bool IsHeader(Control child) => child is ContentControl { Content: GroupHeaderItem };
}
