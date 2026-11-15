using Avalonia.Input;
using RemoteManager.App.Resources;

namespace RemoteManager.App.Services;

/// <summary>
/// The keys of a shortcut as they are printed on a German keyboard: "Strg", "Umschalt", "Entf", "Eingabe". One
/// entry per key, so a view can draw each as its own key cap; the shortcut overview and the tray hint use it.
/// </summary>
public static class KeyCaps
{
    /// <summary>Modifiers first (Strg, Alt, Umschalt, Win), then the key.</summary>
    public static IReadOnlyList<string> For(KeyGesture gesture)
    {
        var caps = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) caps.Add(Strings.Key_Ctrl);
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) caps.Add("Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) caps.Add(Strings.Key_Shift);
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) caps.Add("Win");
        caps.Add(Name(gesture.Key));
        return caps;
    }

    /// <summary>"Strg+Umschalt+N": the same text a menu shows next to its entry.</summary>
    public static string Text(KeyGesture gesture) => string.Join("+", For(gesture));

    public static string Name(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Enter => Strings.Key_Enter,
        Key.Delete => Strings.Key_Delete,
        Key.Escape => Strings.Key_Escape,
        Key.Back => Strings.Key_Backspace,
        Key.Insert => Strings.Key_Insert,
        Key.Space => Strings.Key_Space,
        // The RDP client's "Break" is the Pause key.
        Key.Pause or Key.Cancel => Strings.Key_Pause,
        Key.Up => "↑",
        Key.Down => "↓",
        _ => key.ToString(),
    };
}
