using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RemoteManager.App.Controls;

/// <summary>
/// The picture of a connection that has no preview yet: a panel of lamps, like the front of a rack, lit in a
/// pattern that is derived from <see cref="Seed"/> (the connection id). The same connection always gets the
/// same pattern, so machines are told apart at a glance before a first session leaves a real picture.
/// </summary>
public sealed class HostGlyph : Control
{
    public static readonly StyledProperty<string?> SeedProperty =
        AvaloniaProperty.Register<HostGlyph, string?>(nameof(Seed));

    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<HostGlyph, IBrush?>(nameof(Brush));

    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<HostGlyph, double>(nameof(Pitch), 12);

    static HostGlyph()
    {
        AffectsRender<HostGlyph>(SeedProperty, BrushProperty, PitchProperty);
    }

    /// <summary>What the pattern is derived from.</summary>
    public string? Seed
    {
        get => GetValue(SeedProperty);
        set => SetValue(SeedProperty, value);
    }

    /// <summary>The lamp color (the connection type's color).</summary>
    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>Distance between two lamps.</summary>
    public double Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Size size = Bounds.Size;
        double pitch = Math.Max(4, Pitch);
        int columns = (int)(size.Width / pitch);
        int rows = (int)(size.Height / pitch);
        if (columns < 1 || rows < 1)
            return;

        Color color = (Brush as ISolidColorBrush)?.Color ?? Color.FromRgb(0x6B, 0x78, 0x84);
        var lit = new ImmutableSolidColorBrushSet(color);
        double radius = pitch * 0.2;
        double left = ((size.Width - (columns * pitch)) / 2) + (pitch / 2);
        double top = ((size.Height - (rows * pitch)) / 2) + (pitch / 2);

        // Mirrored left to right: a symmetric pattern reads as one sign instead of noise.
        uint state = Hash(Seed ?? "");
        int half = (columns + 1) / 2;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < half; column++)
            {
                state = Next(state);
                uint roll = (state >> 8) % 100;
                IBrush brush = roll < 17 ? lit.Bright : roll < 34 ? lit.Medium : lit.Dim;
                double y = top + (row * pitch);
                context.DrawEllipse(brush, null, new Point(left + (column * pitch), y), radius, radius);
                int mirrored = columns - 1 - column;
                if (mirrored != column)
                    context.DrawEllipse(brush, null, new Point(left + (mirrored * pitch), y), radius, radius);
            }
        }
    }

    /// <summary>FNV-1a: stable across runs and machines (string.GetHashCode is not).</summary>
    private static uint Hash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return hash == 0 ? 0x9E3779B9 : hash;
    }

    private static uint Next(uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private readonly struct ImmutableSolidColorBrushSet(Color color)
    {
        public IBrush Bright { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color, 0.95);
        public IBrush Medium { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color, 0.42);
        public IBrush Dim { get; } = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(color, 0.11);
    }
}
