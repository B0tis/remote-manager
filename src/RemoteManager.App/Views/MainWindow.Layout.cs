using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace RemoteManager.App.Views;

/// <summary>
/// Responsive layout (docs/ui.md). One breakpoint for the window: from <see cref="WideLayoutWidth"/> the sidebar is
/// expanded (FANavigationView "Auto", same threshold in MainWindow.axaml) and the detail column follows the saved
/// setting; below it the sidebar shows icons only and the detail column is closed unless opened for the moment.
/// The header drops button labels below <see cref="NarrowHeaderWidth"/>; the list hides low-priority columns.
/// </summary>
public partial class MainWindow
{
    /// <summary>Window width (DIP) from which the sidebar is expanded and the detail column opens by default.</summary>
    public const double WideLayoutWidth = 1180;

    /// <summary>Header width (DIP) below which "Verbinden" and "Neue Verbindung" show only their icon.</summary>
    public const double NarrowHeaderWidth = 700;

    /// <summary>List width (DIP) below which group and tags hide (900), and also "zuletzt verbunden" (640).</summary>
    public const double CompactListWidth = 900, NarrowListWidth = 640;

    private bool detailsWanted = true;
    private bool detailsOnNarrow;
    private bool wideLayout = true;

    /// <summary>True while the window is at least <see cref="WideLayoutWidth"/> wide.</summary>
    public bool IsWideLayout => wideLayout;

    public bool IsDetailsVisible => DetailPane.IsVisible;

    /// <summary>The tile view's panel (self-test: tile width).</summary>
    internal SectionWrapPanel? TilePanel => TileList.GetVisualDescendants().OfType<SectionWrapPanel>().FirstOrDefault();

    /// <summary>Shows a sidebar filter ("all", "fav", "group:&lt;id&gt;", ...) as if its entry was clicked (self-test).</summary>
    internal void ShowFilter(string tag)
    {
        filter = tag;
        BuildNavigation();
    }

    private void InitializeLayout()
    {
        FitToScreen();
        SizeChanged += (_, e) => UpdateLayoutMode(e.NewSize.Width);
        Header.SizeChanged += (_, e) => Header.Classes.Set("narrow", e.NewSize.Width < NarrowHeaderWidth);
        ListPanel.SizeChanged += (_, e) =>
        {
            ListPanel.Classes.Set("compact", e.NewSize.Width < CompactListWidth);
            ListPanel.Classes.Set("narrow", e.NewSize.Width < NarrowListWidth);
        };
        UpdateLayoutMode(Width);
    }

    private void UpdateLayoutMode(double width)
    {
        bool wide = width >= WideLayoutWidth;
        if (wide == wideLayout)
            return;
        wideLayout = wide;
        detailsOnNarrow = false; // a column opened on a narrow window does not stay open across modes
        ApplyDetailsVisibility();
    }

    private void ApplyDetailsVisibility()
    {
        bool show = wideLayout ? detailsWanted : detailsOnNarrow;
        DetailPane.IsVisible = show;
        DetailsToggle.IsChecked = show;
    }

    /// <summary>
    /// The default size (1320 x 840) does not fit a laptop at 125 to 150 % scaling: start at most 92 % of the
    /// working area of the screen the window opens on.
    /// </summary>
    private void FitToScreen()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        double maxWidth = screen.WorkingArea.Width / scaling * 0.92;
        double maxHeight = screen.WorkingArea.Height / scaling * 0.92;
        Width = Math.Max(MinWidth, Math.Min(Width, maxWidth));
        Height = Math.Max(MinHeight, Math.Min(Height, maxHeight));
    }
}
