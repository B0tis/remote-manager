using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using RemoteManager.Core.Library;

namespace RemoteManager.App.Services;

/// <summary>
/// Where windows open. The overview remembers its place and size (setting "window.main") and comes back on
/// the monitor it was closed on; session windows open on the monitor the overview is on, not always on the
/// primary one. The environment variable REMOTE_MANAGER_SCREEN ("secondary", a 1-based screen number, or part
/// of a display name) forces the overview onto that screen, whatever was saved: test runs use it to stay off
/// the monitor someone is working on.
/// </summary>
public static class WindowPlacement
{
    public const string ScreenVariable = "REMOTE_MANAGER_SCREEN";
    internal const string MainSetting = "window.main";

    /// <summary>The screen named by <see cref="ScreenVariable"/>, or null when it is not set or does not match.</summary>
    public static Screen? ForcedScreen(Screens screens)
    {
        if (Environment.GetEnvironmentVariable(ScreenVariable) is not { Length: > 0 } wanted)
            return null;

        IReadOnlyList<Screen> all = screens.All;
        if (wanted.Equals("secondary", StringComparison.OrdinalIgnoreCase))
        {
            // The largest screen that is not the primary one.
            return all.Where(s => !s.IsPrimary).OrderByDescending(s => (long)s.Bounds.Width * s.Bounds.Height).FirstOrDefault();
        }

        if (int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            return number >= 1 && number <= all.Count ? all[number - 1] : null;
        return all.FirstOrDefault(s => s.DisplayName?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>Places the overview before it is shown: forced screen, else the saved place, else centered.</summary>
    /// <returns>True when a saved place was applied (it is then not shrunk to the default share of the screen).</returns>
    public static bool RestoreMain(Window window, ConnectionLibrary library)
    {
        if (ForcedScreen(window.Screens) is { } forced)
        {
            Center(window, forced);
            return false;
        }

        if (library.GetSetting(MainSetting) is not { } saved || Parse(saved) is not { } place)
            return false;

        // Only when the saved place is still on a connected monitor (a docking station may be gone).
        var rect = new PixelRect(place.X, place.Y, 200, 60);
        if (!window.Screens.All.Any(s => s.WorkingArea.Intersects(rect)))
            return false;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(place.X, place.Y);
        window.Width = Math.Max(window.MinWidth, place.Width);
        window.Height = Math.Max(window.MinHeight, place.Height);
        if (place.Maximized)
            window.WindowState = WindowState.Maximized;
        return true;
    }

    /// <summary>Remembers where the overview is; a maximized window keeps the size it had before.</summary>
    public static void SaveMain(Window window, ConnectionLibrary library)
    {
        if (window.WindowState == WindowState.Minimized)
            return;

        bool maximized = window.WindowState == WindowState.Maximized;
        if (maximized && library.GetSetting(MainSetting) is { } before && Parse(before) is { } old)
        {
            Place kept = old with { Maximized = true };
            // Maximized on another monitor than the saved place: keep the size, move the place there.
            if (window.Screens.ScreenFromWindow(window) is { } screen && !screen.Bounds.Intersects(new PixelRect(old.X, old.Y, 200, 60)))
                kept = kept with { X = screen.WorkingArea.X + 40, Y = screen.WorkingArea.Y + 40 };
            library.SetSetting(MainSetting, Format(kept));
            return;
        }

        library.SetSetting(MainSetting, Format(new Place(window.Position.X, window.Position.Y, window.Width, window.Height, maximized)));
    }

    /// <summary>Centers <paramref name="window"/> on the screen <paramref name="reference"/> is on (call before Show).</summary>
    public static void OpenOnScreenOf(Window window, Window? reference)
    {
        if (reference is null)
            return;
        Screen? screen = reference.Screens.ScreenFromWindow(reference) ?? reference.Screens.ScreenFromPoint(reference.Position);
        if (screen is not null)
            Center(window, screen);
    }

    private static void Center(Window window, Screen screen)
    {
        PixelRect area = screen.WorkingArea;
        double width = double.IsNaN(window.Width) ? 800 : window.Width;
        double height = double.IsNaN(window.Height) ? 600 : window.Height;
        // Never larger than the screen it opens on.
        width = Math.Min(width, (area.Width / screen.Scaling) - 40);
        height = Math.Min(height, (area.Height / screen.Scaling) - 40);
        if (!double.IsNaN(window.Width))
            window.Width = width;
        if (!double.IsNaN(window.Height))
            window.Height = height;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(
            area.X + (int)((area.Width - (width * screen.Scaling)) / 2),
            area.Y + (int)((area.Height - (height * screen.Scaling)) / 2));
    }

    private sealed record Place(int X, int Y, double Width, double Height, bool Maximized);

    private static string Format(Place place) => string.Create(CultureInfo.InvariantCulture,
        $"{place.X},{place.Y},{place.Width:F0},{place.Height:F0},{(place.Maximized ? 1 : 0)}");

    private static Place? Parse(string text)
    {
        string[] parts = text.Split(',');
        return parts.Length == 5
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            && width > 0 && height > 0
            ? new Place(x, y, width, height, parts[4] == "1")
            : null;
    }
}
