using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RemoteManager.App.Diagnostics;
using RemoteManager.App.Views;

namespace RemoteManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
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
}
