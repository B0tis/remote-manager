using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using RemoteManager.App.Diagnostics;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;

namespace RemoteManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CrashLog.InstallDispatcherHandler();
        UseGermanKeyNames();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppHost host = AppHost.Start();
            desktop.Exit += (_, _) => host.Dispose();

            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            if (AppOptions.SelfTest)
                window.Opened += async (_, _) => desktop.Shutdown(await SelfTest.RunAsync(window));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Menu shortcuts (<see cref="MenuItem.InputGesture"/>) read "Strg+Umschalt+N" and "Entf" like the rest of the German
    /// UI. Avalonia formats them with the <see cref="KeyGestureFormatInfo"/> in its locator; the binding API is public in
    /// the runtime assembly but not in the reference assembly, hence reflection. Without it the names stay English.
    /// </summary>
    private static void UseGermanKeyNames()
    {
        var german = new KeyGestureFormatInfo(
            new Dictionary<Key, string> { [Key.Delete] = "Entf", [Key.Enter] = "Eingabe", [Key.Escape] = "Esc" },
            meta: "Win", ctrl: Strings.Key_Ctrl, alt: "Alt", shift: Strings.Key_Shift);
        object? locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        MethodInfo? bind = locator?.GetType().GetMethod("BindToSelf", BindingFlags.Public | BindingFlags.Instance);
        bind?.MakeGenericMethod(typeof(KeyGestureFormatInfo)).Invoke(locator, [german]);
    }
}
