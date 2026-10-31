using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using RemoteManager.App.Resources;

namespace RemoteManager.App.Services;

/// <summary>
/// One system-wide hotkey (RegisterHotKey) on a window's HWND; WM_HOTKEY arrives through Avalonia's
/// WndProc hook and raises <see cref="Pressed"/> on the UI thread. Opens the quick launcher.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    /// <summary>The default: Ctrl+Alt+R ("Remote"). Alt+M (1Remote) is avoided so both can run side by side.</summary>
    public static readonly KeyGesture Default = new(Key.R, KeyModifiers.Control | KeyModifiers.Alt);

    public const int WmHotkey = 0x0312;
    public const int HotkeyId = 0x524D;

    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    private readonly TopLevel owner;
    private readonly nint window;
    private readonly Win32Properties.CustomWndProcHookCallback hook;
    private bool registered;

    public GlobalHotkey(TopLevel owner)
    {
        this.owner = owner;
        window = owner.TryGetPlatformHandle()?.Handle ?? throw new InvalidOperationException("The window has no native handle yet.");
        hook = WndProc;
        Win32Properties.AddWndProcHookCallback(owner, hook);
    }

    public event EventHandler? Pressed;

    /// <summary>The registered combination, or null when none is (not set, or taken by another program).</summary>
    public KeyGesture? Gesture { get; private set; }

    public nint WindowHandle => window;

    /// <summary>Registers <paramref name="gesture"/> in place of the current one. False when Windows refuses it (already taken).</summary>
    public bool Register(KeyGesture gesture)
    {
        Unregister();
        if (!TryGetVirtualKey(gesture.Key, out uint key) || !IsValid(gesture))
            return false;

        uint modifiers = ModNoRepeat;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= ModAlt;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= ModControl;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= ModShift;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= ModWin;

        registered = RegisterHotKey(window, HotkeyId, modifiers, key);
        Gesture = registered ? gesture : null;
        return registered;
    }

    public void Unregister()
    {
        if (registered)
            UnregisterHotKey(window, HotkeyId);
        registered = false;
        Gesture = null;
    }

    public void Dispose()
    {
        Unregister();
        Win32Properties.RemoveWndProcHookCallback(owner, hook);
    }

    /// <summary>A usable global hotkey: a letter, digit, function key, or space with Ctrl, Alt, or Win (Shift alone is not enough).</summary>
    public static bool IsValid(KeyGesture gesture) =>
        TryGetVirtualKey(gesture.Key, out _)
        && (gesture.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;

    public static KeyGesture Parse(string? text)
    {
        try
        {
            return text is { Length: > 0 } && KeyGesture.Parse(text) is { } gesture && IsValid(gesture) ? gesture : Default;
        }
        catch (ArgumentException)
        {
            return Default;
        }
    }

    /// <summary>German key names for the UI: "Strg+Alt+R".</summary>
    public static string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add(Strings.Key_Ctrl);
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add(Strings.Key_Shift);
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(gesture.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (gesture.Key - Key.D0))).ToString(),
            Key.Space => Strings.Key_Space,
            _ => gesture.Key.ToString(),
        });
        return string.Join("+", parts);
    }

    private static bool TryGetVirtualKey(Key key, out uint virtualKey)
    {
        virtualKey = key switch
        {
            >= Key.A and <= Key.Z => (uint)(0x41 + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (uint)(0x30 + (key - Key.D0)),
            >= Key.F1 and <= Key.F24 => (uint)(0x70 + (key - Key.F1)),
            Key.Space => 0x20,
            _ => 0,
        };
        return virtualKey != 0;
    }

    private nint WndProc(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
