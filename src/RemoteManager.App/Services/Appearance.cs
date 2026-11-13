using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using RemoteManager.Core.Library;

namespace RemoteManager.App.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Tile size of the overview: small is a compact card without a preview.</summary>
public enum TileSize { Small, Medium, Large }

/// <summary>A signal color the user can pick; <see cref="Dark"/> and <see cref="Light"/> are its fills on the two themes.</summary>
public sealed record AccentPreset(string Id, Color Dark, Color Light)
{
    /// <summary>Follows the accent color set in Windows.</summary>
    public bool IsSystem => Id == "windows";
}

/// <summary>
/// The look the user chose: theme (system, light, dark), signal color, and tile size. Saved in
/// app_settings ("appearance.*") and applied to the running app at once. The accent brushes
/// (RmAccent*, the accent button) are written to the application resources from here, because they
/// depend on both the preset and the theme that is actually shown.
/// </summary>
public sealed class Appearance
{
    private const string ThemeSetting = "appearance.theme";
    private const string AccentSetting = "appearance.accent";
    private const string TileSizeSetting = "appearance.tileSize";

    public static readonly IReadOnlyList<AccentPreset> Accents =
    [
        new("bernstein", Color.Parse("#FFB224"), Color.Parse("#F0A000")),
        new("polar", Color.Parse("#62A0FF"), Color.Parse("#2F6FEB")),
        new("minze", Color.Parse("#3DDBB4"), Color.Parse("#0E9F7C")),
        new("flieder", Color.Parse("#AC93FF"), Color.Parse("#7559E0")),
        new("koralle", Color.Parse("#FF8370"), Color.Parse("#DD4F3A")),
        new("windows", Colors.Transparent, Colors.Transparent),
    ];

    private readonly ConnectionLibrary library;

    public Appearance(ConnectionLibrary library)
    {
        this.library = library;
        Theme = Enum.TryParse(library.GetSetting(ThemeSetting), ignoreCase: true, out AppTheme theme) ? theme : AppTheme.System;
        Accent = Accents.FirstOrDefault(a => a.Id == library.GetSetting(AccentSetting)) ?? Accents[0];
        TileSize = Enum.TryParse(library.GetSetting(TileSizeSetting), ignoreCase: true, out TileSize size) ? size : TileSize.Medium;
    }

    public AppTheme Theme { get; private set; }
    public AccentPreset Accent { get; private set; }
    public TileSize TileSize { get; private set; }

    /// <summary>Raised after the tile size changed (the overview re-styles its tiles).</summary>
    public event EventHandler? TileSizeChanged;

    /// <summary>Raised after theme or accent changed (controls that paint themselves repaint).</summary>
    public event EventHandler? Changed;

    public void SetTheme(AppTheme theme)
    {
        Theme = theme;
        library.SetSetting(ThemeSetting, theme.ToString().ToLowerInvariant());
        Apply();
    }

    public void SetAccent(AccentPreset accent)
    {
        Accent = accent;
        library.SetSetting(AccentSetting, accent.Id);
        Apply();
    }

    public void SetTileSize(TileSize size)
    {
        TileSize = size;
        library.SetSetting(TileSizeSetting, size.ToString().ToLowerInvariant());
        TileSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hooks the application: applies the saved look now and again whenever Windows switches light and dark.</summary>
    public void Attach(Application application)
    {
        application.ActualThemeVariantChanged += (_, _) => ApplyAccent(application);
        Apply();
    }

    /// <summary>Applies theme and accent to the running application.</summary>
    public void Apply()
    {
        if (Application.Current is not { } application)
            return;

        application.RequestedThemeVariant = Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        ApplyAccent(application);
    }

    private void ApplyAccent(Application application)
    {
        bool dark = application.ActualThemeVariant == ThemeVariant.Dark;
        FluentAvaloniaTheme? fluent = application.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();

        Color accent;
        if (Accent.IsSystem)
        {
            if (fluent is not null)
            {
                fluent.CustomAccentColor = null;
                fluent.PreferUserAccentColor = true;
            }

            // FluentAvalonia fills accent controls with the second lighter shade on dark, the first darker one on light.
            string key = dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1";
            accent = application.TryGetResource(key, application.ActualThemeVariant, out object? value) && value is Color system
                ? system
                : Accents[1].Dark;
        }
        else
        {
            accent = dark ? Accent.Dark : Accent.Light;
            if (fluent is not null)
            {
                fluent.PreferUserAccentColor = false;
                fluent.CustomAccentColor = accent;
            }
        }

        Color onAccent = InkOn(accent);
        // As text on a surface the fill itself is too pale on the light theme.
        Color accentText = dark ? accent : Mix(accent, Colors.Black, 0.38);

        var resources = application.Resources;
        resources["RmAccentColor"] = accent;
        resources["RmAccentBrush"] = new SolidColorBrush(accent);
        resources["RmAccentHoverBrush"] = new SolidColorBrush(Mix(accent, dark ? Colors.White : Colors.Black, 0.12));
        resources["RmAccentPressedBrush"] = new SolidColorBrush(Mix(accent, dark ? Colors.Black : Colors.Black, 0.2));
        resources["RmAccentSoftBrush"] = new SolidColorBrush(accent, dark ? 0.16 : 0.18);
        resources["RmAccentFaintBrush"] = new SolidColorBrush(accent, 0.08);
        resources["RmAccentTextBrush"] = new SolidColorBrush(accentText);
        resources["RmOnAccentBrush"] = new SolidColorBrush(onAccent);
        // The ring around a selected tile: outside its frame, so nothing moves when the selection changes.
        resources["RmSelectionRing"] = new BoxShadows(new BoxShadow { Spread = 1.5, Color = accent });

        // The accent button, exactly in the signal color with readable text (FluentAvalonia's own shades and
        // its white-on-accent text do not work for a bright amber).
        resources["AccentButtonBackground"] = resources["RmAccentBrush"];
        resources["AccentButtonBackgroundPointerOver"] = resources["RmAccentHoverBrush"];
        resources["AccentButtonBackgroundPressed"] = resources["RmAccentPressedBrush"];
        resources["AccentButtonForeground"] = resources["RmOnAccentBrush"];
        resources["AccentButtonForegroundPointerOver"] = resources["RmOnAccentBrush"];
        resources["AccentButtonForegroundPressed"] = new SolidColorBrush(onAccent, 0.75);
        resources["AccentButtonBorderBrush"] = Brushes.Transparent;
        resources["AccentButtonBorderBrushPointerOver"] = Brushes.Transparent;
        resources["AccentButtonBorderBrushPressed"] = Brushes.Transparent;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The text color on a fill of the signal color: the warm near-black or white, whichever reads better
    /// (WCAG contrast ratio). Where neither reaches 4.5 (a narrow band of mid tones, possible with the
    /// Windows accent) plain black does.
    /// </summary>
    internal static Color InkOn(Color fill)
    {
        Color dark = Color.Parse("#14100A");
        double luminance = RelativeLuminance(fill);
        double withDark = (luminance + 0.05) / (RelativeLuminance(dark) + 0.05);
        double withWhite = 1.05 / (luminance + 0.05);
        if (Math.Max(withDark, withWhite) < 4.5)
            return Colors.Black;
        return withWhite >= withDark ? Colors.White : dark;
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + ((b.R - a.R) * amount)),
        (byte)Math.Round(a.G + ((b.G - a.G) * amount)),
        (byte)Math.Round(a.B + ((b.B - a.B) * amount)));

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}
