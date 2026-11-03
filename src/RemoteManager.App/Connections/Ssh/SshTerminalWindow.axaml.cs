using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteManager.App.Connections.Ssh;

/// <summary>
/// An SSH terminal: SSH.NET shell (PTY "xterm-256color") rendered by xterm.js in a WebView2.
/// Output goes to the page base64 encoded and batched per UI frame; keys, resizes, copy and paste
/// come back as JSON messages. Closing the window ends the session.
/// </summary>
public partial class SshTerminalWindow : Window, ISessionWindow
{
    private const int MaxChunk = 256 * 1024;

    private readonly object outputLock = new();
    private readonly MemoryStream pendingOutput = new();
    private bool flushScheduled;
    private TaskCompletionSource<bool> pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool navigated;
    private SshRoute? route;
    private SshClient? client;
    private ShellStream? shell;
    private SftpWindow? files;
    private bool closed;

    static SshTerminalWindow()
    {
        // Avalonia.Controls.WebView 12.1.1: when the window closes while WebView2 is still starting, the
        // adapter's initialization fails with E_ABORT on the dispatcher, which would end the whole app.
        // Nothing is lost then (the window is gone), so only that case is marked handled.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (IsWebViewStartAbort(e.Exception))
            {
                Trace.WriteLine("SSH terminal: WebView2 start aborted (window closed early).");
                e.Handled = true;
            }
        };
    }

    /// <summary>The E_ABORT that WebView2 raises when the window closes during its start (also used by the crash log).</summary>
    internal static bool IsWebViewStartAbort(Exception exception) =>
        exception is COMException { HResult: unchecked((int)0x80004004) } abort
        && abort.StackTrace?.Contains("WebView2", StringComparison.Ordinal) == true;

    // Required by the XAML loader; the app always uses the ConnectionItem overload.
    public SshTerminalWindow() : this(new ConnectionItem(new Connection { Name = "SSH", Type = ConnectionType.Ssh }, AppHost.Current))
    {
    }

    public SshTerminalWindow(ConnectionItem connection)
    {
        InitializeComponent();
        Connection = connection;
        Title = connection.Name;
        Card.Load(connection);
        Card.ConnectRequested += (_, _) => Connect();
        Card.SecretSaved += (_, _) => PasswordSaved?.Invoke(this, EventArgs.Empty);
        ReconnectButton.Click += (_, _) => Connect();
        DisconnectButton.Click += (_, _) => Disconnect();
        FilesButton.Click += (_, _) => OpenFiles();
        PasteButton.Click += async (_, _) => await PasteAsync();

        Terminal.EnvironmentRequested += (_, e) =>
        {
            // WebView2 keeps its profile next to the exe by default; use the app's data folder instead.
            if (e is WindowsWebView2EnvironmentRequestedEventArgs webView2)
                webView2.UserDataFolder = Path.Combine(AppPaths.Root, "WebView2");
        };
        Terminal.WebMessageReceived += (_, e) => OnPageMessage(e.Body ?? "");
        Terminal.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
                pageReady.TrySetResult(false);
        };

        Opened += (_, _) =>
        {
            if (Card.CanConnectWithoutInput)
                Connect();
            else
                Card.FocusInput();
        };
        Activated += (_, _) =>
        {
            if (IsConnected)
                FocusTerminal();
        };
    }

    public ConnectionItem Connection { get; }

    public event EventHandler? SessionConnected;
    public event EventHandler? SessionDisconnected;
    /// <summary>A typed password was stored after a successful logon.</summary>
    public event EventHandler? PasswordSaved;
    /// <summary>Status lines (no secrets).</summary>
    public event EventHandler<string>? StatusLogged;

    public bool IsConnected => shell is not null;
    public bool IsPageReady => pageReady.Task is { IsCompletedSuccessfully: true, Result: true };
    public (int Columns, int Rows) TerminalSize { get; private set; } = (80, 24);
    public string LastStatus => StatusText.Text ?? "";
    public bool IsCardVisible => Card.IsVisible;
    public SshLogonCard LogonCard => Card;
    public SftpWindow? FilesWindow => files;

    public void Connect() => _ = ConnectAsync();

    public async Task ConnectAsync()
    {
        if (Card.IsBusy || IsConnected)
            return;

        ReconnectButton.IsVisible = false;
        SetStatusDot(Color.FromRgb(0xF5, 0x9E, 0x0B));
        // Load the page while SSH connects.
        StartPage();
        (SshRoute Route, SshClient Client)? result = await Card.ConnectAsync(this, info => new SshClient(info), SetStatus);
        if (result is null)
        {
            SetStatus(Card.MessageText.Length > 0 ? Card.MessageText : Strings.Session_NotConnected);
            SetStatusDot(Color.FromRgb(0x9C, 0xA3, 0xAF));
            ShowCard();
            return;
        }

        (route, client) = result.Value;
        ShowTerminal();
        if (!await WaitForPageAsync())
        {
            CloseSession();
            Card.ShowMessage(Strings.Ssh_Terminal_PageFailed);
            SetStatus(Strings.Ssh_Terminal_PageFailed);
            ShowCard();
            return;
        }

        try
        {
            (int columns, int rows) = TerminalSize;
            SshClient connected = client;
            shell = await Task.Run(() => connected.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 64 * 1024));
            Trace.WriteLine($"SSH terminal: shell opened with {columns}x{rows}.");
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or ObjectDisposedException)
        {
            CloseSession();
            Card.ShowMessage(SshConnect.Describe(ex));
            ShowCard();
            return;
        }

        shell.DataReceived += (_, e) => QueueOutput(e.Data);
        shell.Closed += (_, _) => Dispatcher.UIThread.Post(() => OnShellEnded(null));
        shell.ErrorOccurred += (_, e) => Dispatcher.UIThread.Post(() => OnShellEnded(e.Exception));

        IReadOnlyList<string> problems = SshRoute.StartForwards(client, Connection.Model.SettingsAs<SshSettings>().PortForwards);
        string status = string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Status_Connected, Card.LastLogin?.UserName, Connection.Model.Host);
        SetStatus(problems.Count == 0 ? status : $"{status} · {string.Format(CultureInfo.CurrentCulture, Strings.Ssh_Status_ForwardFailed, string.Join("; ", problems))}");
        SetStatusDot(Color.FromRgb(0x22, 0xC5, 0x5E));
        DisconnectButton.IsEnabled = FilesButton.IsEnabled = PasteButton.IsEnabled = true;
        FocusTerminal();
        SessionConnected?.Invoke(this, EventArgs.Empty);
    }

    public void Disconnect()
    {
        if (!IsConnected)
            return;
        CloseSession();
        OnShellEnded(null);
    }

    /// <summary>Starts loading the page in the background (the web view is created and starts WebView2).</summary>
    public void PreloadPage() => StartPage();

    /// <summary>Shows and loads the terminal page without a session (self-test: WebView2 and xterm.js work).</summary>
    public async Task<bool> ShowPageAsync()
    {
        ShowTerminal();
        StartPage();
        return await WaitForPageAsync();
    }

    /// <summary>Writes text into the terminal (not to the server).</summary>
    public async Task WriteLocalAsync(string text) => await Terminal.InvokeScript($"rm.writeText({JsonSerializer.Serialize(text)})");

    /// <summary>Sends text as if typed.</summary>
    public void SendInput(string text) => WriteToShell(Encoding.UTF8.GetBytes(text));

    /// <summary>The visible screen as text lines and which buffer is active ("normal" or "alternate", the one htop and vim use).</summary>
    public async Task<(string Buffer, IReadOnlyList<string> Lines)> ReadScreenAsync()
    {
        string json = await Terminal.InvokeScript("rm.screen()").WaitAsync(TimeSpan.FromSeconds(5)) ?? "{}";
        using JsonDocument document = JsonDocument.Parse(UnwrapScriptResult(json));
        string buffer = document.RootElement.GetProperty("type").GetString() ?? "";
        List<string> lines = [.. document.RootElement.GetProperty("lines").EnumerateArray().Select(l => l.GetString() ?? "")];
        return (buffer, lines);
    }

    private void StartPage()
    {
        if (navigated)
            return;
        navigated = true;
        string page = Path.Combine(AppContext.BaseDirectory, "terminal", "terminal.html");
        Terminal.Navigate(new Uri(page));
    }

    private async Task<bool> WaitForPageAsync()
    {
        Task finished = await Task.WhenAny(pageReady.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        return finished == pageReady.Task && pageReady.Task.Result;
    }

    private void OnPageMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement message = document.RootElement;
            switch (message.GetProperty("t").GetString())
            {
                case "ready":
                    TerminalSize = (message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                    pageReady.TrySetResult(true);
                    Trace.WriteLine($"SSH terminal: page ready, {TerminalSize.Columns}x{TerminalSize.Rows}.");
                    break;
                case "data":
                    WriteToShell(Encoding.UTF8.GetBytes(message.GetProperty("d").GetString() ?? ""));
                    break;
                case "resize":
                    Resize(message.GetProperty("cols").GetInt32(), message.GetProperty("rows").GetInt32());
                    break;
                case "copy":
                    _ = Clipboard?.SetTextAsync(message.GetProperty("d").GetString() ?? "");
                    break;
                case "paste":
                    _ = PasteAsync();
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Trace.WriteLine($"SSH terminal: ignored page message ({ex.Message}).");
        }
    }

    private void Resize(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
            return;
        TerminalSize = (columns, rows);
        Trace.WriteLine($"SSH terminal: resize to {columns}x{rows}.");
        try
        {
            shell?.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
            Trace.WriteLine($"SSH terminal: resize failed ({ex.Message}).");
        }
    }

    private void WriteToShell(byte[] data)
    {
        if (shell is null || data.Length == 0)
            return;
        try
        {
            shell.Write(data, 0, data.Length);
            shell.Flush();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            OnShellEnded(ex);
        }
    }

    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard || shell is null)
            return;
        string? text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;
        // xterm.js wraps it in bracketed-paste markers when the remote program asked for them.
        await Terminal.InvokeScript($"rm.paste({JsonSerializer.Serialize(text)})");
        FocusTerminal();
    }

    private void QueueOutput(byte[] data)
    {
        lock (outputLock)
        {
            pendingOutput.Write(data);
            if (flushScheduled)
                return;
            flushScheduled = true;
        }

        Dispatcher.UIThread.Post(FlushOutput, DispatcherPriority.Background);
    }

    private async void FlushOutput()
    {
        while (true)
        {
            byte[] chunk;
            lock (outputLock)
            {
                if (pendingOutput.Length == 0 || closed)
                {
                    flushScheduled = false;
                    return;
                }

                byte[] all = pendingOutput.ToArray();
                int take = Math.Min(all.Length, MaxChunk);
                chunk = all[..take];
                pendingOutput.SetLength(0);
                pendingOutput.Write(all, take, all.Length - take);
            }

            try
            {
                // A script call that never comes back must not stop all later output.
                await Terminal.InvokeScript($"rm.write('{Convert.ToBase64String(chunk)}')").WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or TimeoutException)
            {
                Trace.WriteLine($"SSH terminal: output lost ({chunk.Length} bytes): {ex.Message}");
            }
        }
    }

    private void OnShellEnded(Exception? error)
    {
        if (shell is null && route is null)
            return;

        CloseSession();
        string message = error is null ? Strings.Ssh_Status_Closed : SshConnect.Describe(error);
        _ = Terminal.InvokeScript($"rm.writeText({JsonSerializer.Serialize($"\r\n\u001b[2m[{message}]\u001b[0m\r\n")})");
        SetStatus(message);
        SetStatusDot(Color.FromRgb(0x9C, 0xA3, 0xAF));
        DisconnectButton.IsEnabled = FilesButton.IsEnabled = PasteButton.IsEnabled = false;
        // The terminal stays visible with its last output; reconnect from the toolbar.
        ReconnectButton.IsVisible = true;
        SessionDisconnected?.Invoke(this, EventArgs.Empty);
    }

    private void CloseSession()
    {
        ShellStream? s = shell;
        SshRoute? r = route;
        shell = null;
        client = null;
        route = null;
        try
        {
            s?.Dispose();
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
        }

        r?.Dispose();
    }

    private void OpenFiles()
    {
        if (files is { } open)
        {
            open.Activate();
            return;
        }

        files = new SftpWindow(Connection, Card.LastLogin);
        files.Closed += (_, _) => files = null;
        files.Show();
    }

    private void ShowTerminal()
    {
        Card.IsVisible = false;
        Terminal.IsVisible = true;
    }

    private void ShowCard()
    {
        // The native web view would cover the card.
        Terminal.IsVisible = false;
        Card.IsVisible = true;
        Card.FocusInput();
    }

    private void FocusTerminal()
    {
        Terminal.Focus();
        _ = Terminal.InvokeScript("rm.focus()");
    }

    private void SetStatusDot(Color color) => StatusDot.Fill = new SolidColorBrush(color);

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusLogged?.Invoke(this, message);
    }

    /// <summary>WebView2 returns script results JSON encoded; a string result arrives quoted.</summary>
    private static string UnwrapScriptResult(string result) =>
        result.StartsWith('"') ? JsonSerializer.Deserialize<string>(result) ?? "" : result;

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        CloseSession();
        files?.Close();
        base.OnClosed(e);
    }
}
