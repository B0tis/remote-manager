using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;

namespace RemoteManager.App.Views;

/// <summary>
/// The look in the overview: the S/M/L tile size switch in the header, and "Darstellung" in the "Mehr" menu
/// (theme, signal color, tile size). The choices are kept by <see cref="Appearance"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Tile widths per size: as many tiles per row as fit at the first value, none wider than the second (item margin included).</summary>
    private static (double Min, double Max) TileWidths(TileSize size) => size switch
    {
        TileSize.Small => (214, 290),
        TileSize.Large => (334, 430),
        _ => (254, 334),
    };

    public TileSize TileSize => app.Appearance.TileSize;

    public void SetTileSize(TileSize size) => app.Appearance.SetTileSize(size);

    private void InitializeAppearance()
    {
        SizeSmallToggle.Click += (_, _) => SetTileSize(TileSize.Small);
        SizeMediumToggle.Click += (_, _) => SetTileSize(TileSize.Medium);
        SizeLargeToggle.Click += (_, _) => SetTileSize(TileSize.Large);
        app.Appearance.TileSizeChanged += OnTileSizeChanged;
        app.Appearance.Changed += OnAppearanceChanged;
        Closed += (_, _) =>
        {
            app.Appearance.TileSizeChanged -= OnTileSizeChanged;
            app.Appearance.Changed -= OnAppearanceChanged;
        };
        // The panel exists once the list has its template; apply the saved size then, and now for the switch.
        TileList.TemplateApplied += (_, _) => ApplyTileSize();
        TileList.Loaded += (_, _) => ApplyTileSize();
        ApplyTileSize();
    }

    private void OnTileSizeChanged(object? sender, EventArgs e) => ApplyTileSize();

    /// <summary>Theme or signal color changed: brushes that items hand out (status lamps) are looked up again.</summary>
    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        foreach (ConnectionItem item in items)
            item.Changed();
        Details.Refresh();
    }

    private void ApplyTileSize()
    {
        TileSize size = app.Appearance.TileSize;
        SizeSmallToggle.IsChecked = size == TileSize.Small;
        SizeMediumToggle.IsChecked = size == TileSize.Medium;
        SizeLargeToggle.IsChecked = size == TileSize.Large;
        TileList.Classes.Set("small", size == TileSize.Small);
        TileList.Classes.Set("large", size == TileSize.Large);
        if (TilePanel is { } panel)
        {
            (panel.MinTileWidth, panel.MaxTileWidth) = TileWidths(size);
        }

        // The size switch belongs to the tiles.
        SizeSwitch.IsEnabled = !IsListView;
    }

    /// <summary>"Darstellung": theme, signal color, tile size.</summary>
    private MenuItem CreateAppearanceMenu()
    {
        Appearance look = app.Appearance;
        var themes = new (AppTheme Theme, string Label)[]
        {
            (AppTheme.System, Strings.Appearance_Theme_System),
            (AppTheme.Light, Strings.Appearance_Theme_Light),
            (AppTheme.Dark, Strings.Appearance_Theme_Dark),
        };
        var sizes = new (TileSize Size, string Label)[]
        {
            (TileSize.Small, Strings.TileSize_Small),
            (TileSize.Medium, Strings.TileSize_Medium),
            (TileSize.Large, Strings.TileSize_Large),
        };

        List<MenuItem> themeItems = [.. themes.Select(t => Radio(t.Label, "appearanceTheme", () => look.SetTheme(t.Theme)))];
        List<MenuItem> accentItems = [.. Appearance.Accents.Select(a => Radio(AccentName(a), "appearanceAccent", () => look.SetAccent(a)))];
        List<MenuItem> sizeItems = [.. sizes.Select(s => Radio(s.Label, "appearanceTileSize", () => look.SetTileSize(s.Size)))];

        var accent = Item(Strings.Appearance_Accent, FASymbol.ColorLine);
        accent.ItemsSource = accentItems;
        accent.SubmenuOpened += (_, _) => Mark(accentItems, Appearance.Accents.ToList().IndexOf(look.Accent));
        var tileSize = Item(Strings.Appearance_TileSize, FASymbol.ViewAll);
        tileSize.ItemsSource = sizeItems;
        tileSize.SubmenuOpened += (_, _) => Mark(sizeItems, Array.FindIndex(sizes, s => s.Size == look.TileSize));

        var menu = Item(Strings.Appearance_Menu, FASymbol.DarkTheme);
        Control[] entries = [.. themeItems, new Separator(), accent, tileSize];
        menu.ItemsSource = entries;
        menu.SubmenuOpened += (_, _) => Mark(themeItems, Array.FindIndex(themes, t => t.Theme == look.Theme));
        return menu;

        static MenuItem Radio(string header, string group, Action onClick)
        {
            MenuItem item = Item(header);
            item.ToggleType = MenuItemToggleType.Radio;
            item.GroupName = group;
            item.Click += (_, _) => onClick();
            return item;
        }

        static void Mark(List<MenuItem> choices, int index)
        {
            for (int i = 0; i < choices.Count; i++)
                choices[i].IsChecked = i == index;
        }
    }

    internal static string AccentName(AccentPreset accent) => accent.Id switch
    {
        "bernstein" => Strings.Accent_Bernstein,
        "polar" => Strings.Accent_Polar,
        "minze" => Strings.Accent_Minze,
        "flieder" => Strings.Accent_Flieder,
        "koralle" => Strings.Accent_Koralle,
        _ => Strings.Accent_Windows,
    };
}
