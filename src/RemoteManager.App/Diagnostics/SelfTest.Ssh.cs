using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using RemoteManager.App.Connections.Rdp;
using RemoteManager.App.Connections.Ssh;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Ssh;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// SSH, SFTP, and RDP-over-SSH checks. Without a server: editors, the xterm.js page in WebView2
/// (render and resize), and the host-key prompts. With the Docker testbed (tools/ssh-testbed/start.ps1,
/// variables RM_SSH_TEST_*): unknown and changed host keys, htop and vim, PTY resize, SFTP through a jump
/// host with a simulated Explorer drop, and RDP through an SSH tunnel (RM_SSH_TEST_RDP). Passwords go to
/// "RemoteManager:selftest-ssh-*" credentials only and are removed again; they are never logged.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunSshChecksAsync(MainWindow window)
    {
        // Written as it happens (selftest.log only at the end), so a hang can be diagnosed.
        using var live = new TextWriterTraceListener(Path.Combine(Core.AppPaths.Spike, "selftest-ssh.log"));
        Trace.Listeners.Add(live);
        Trace.AutoFlush = true;
        try
        {
            await RunSshChecksCoreAsync(window);
        }
        finally
        {
            Trace.Listeners.Remove(live);
        }
    }

    private static async Task RunSshChecksCoreAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        Check("SSH and SFTP have settings editors; RDP offers the SSH tunnel",
            app.Handlers[ConnectionType.Ssh].CreateSettingsEditor() is SshSettingsEditor
            && app.Handlers[ConnectionType.Sftp].CreateSettingsEditor() is SshSettingsEditor
            && app.Handlers[ConnectionType.Rdp].CreateSettingsEditor() is RdpSettingsEditor);

        // Edit dialog of an SSH connection (for review).
        var model = new Connection { Name = "linux01", Type = ConnectionType.Ssh, Host = "linux01.example", UserName = "root" };
        model.Settings = model.SettingsAs<SshSettings>() with
        {
            AuthMethod = SshAuthMethod.KeyFile,
            KeyPath = @"%USERPROFILE%\.ssh\id_ed25519",
            PortForwards = [new PortForward(PortForwardKind.Local, "", 8080, "intranet", 80), new PortForward(PortForwardKind.Dynamic, "", 1080, "", 0)],
            HostKeyFingerprint = "SHA256:Xq3pN1v2YkV9b7c5mJ8wQe4rT6yU0iO2pA3sD5fG7hK",
        };
        Task<bool> editing = ConnectionEditor.ShowAsync(model, isNew: false);
        await Settle(800);
        SaveRenderProbe(window, "ssh-edit-dialog.png");
        ConnectionEditor.OpenDialog?.Hide();
        Check("SSH edit dialog opens and Cancel leaves the connection unchanged", !await editing && model.Name == "linux01");

        await RunTerminalPageChecksAsync(window);
        await RunHostKeyPromptChecksAsync(window);

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RM_SSH_TEST_HOST")))
            await RunSshLiveChecksAsync(window);
        else
            Log("SKIP SSH live checks (RM_SSH_TEST_HOST not set; start tools/ssh-testbed/start.ps1): htop, vim, resize, host keys, SFTP, jump host, RDP over SSH.");
    }

    /// <summary>The logon card, then the xterm.js page without a server: it loads, renders text, and reports a resize.</summary>
    private static async Task RunTerminalPageChecksAsync(MainWindow window)
    {
        ConnectionItem item = window.CreateItem(new Connection { Name = "Selftest Terminal", Type = ConnectionType.Ssh, Host = "rm-selftest.invalid" });
        var terminal = (SshTerminalWindow)window.OpenSession(item)!;
        terminal.StatusLogged += (_, message) => Log($"  [{terminal.Title}] {message}");
        await Settle(800);
        SaveRenderProbe(terminal, "ssh-logon-card.png");
        Check("Without a user the SSH window shows the logon card", terminal.IsCardVisible && !terminal.IsConnected);

        bool loaded = await terminal.ShowPageAsync();
        await terminal.WriteLocalAsync("\u001b[1;32mRemote-Manager\u001b[0m xterm.js \u001b[7m ok \u001b[0m äöü ✓\r\n");
        await Settle(400);
        (string buffer, IReadOnlyList<string> lines) = loaded ? await terminal.ReadScreenAsync() : ("", []);
        (int columns, int rows) = terminal.TerminalSize;
        Check($"xterm.js loads in WebView2 and renders text ({columns}x{rows}, buffer {buffer})",
            loaded && buffer == "normal" && lines.Any(l => l.Contains("xterm.js  ok  äöü ✓", StringComparison.Ordinal)) && columns > 40 && rows > 10);

        terminal.Width += 240;
        terminal.Height += 120;
        bool resized = await WaitFor(() => terminal.TerminalSize.Columns > columns && terminal.TerminalSize.Rows > rows, TimeSpan.FromSeconds(5));
        Check($"Resizing the window resizes the terminal ({columns}x{rows} -> {terminal.TerminalSize.Columns}x{terminal.TerminalSize.Rows})", resized);
        terminal.Close();
        await Settle(300);

        // Closing while WebView2 is still starting (user cancels right away) must not take the app down.
        for (int i = 0; i < 3; i++)
        {
            var early = (SshTerminalWindow)window.OpenSession(item)!;
            early.PreloadPage();
            await Task.Delay(150 * i);
            early.Close();
        }

        await Settle(4000);
        Check("Closing a terminal window while WebView2 starts does not crash the app", window.IsVisible && window.SessionWindows.Count == 0);
    }

    /// <summary>Renders both host-key prompts; Cancel declines, Accept trusts.</summary>
    private static async Task RunHostKeyPromptChecksAsync(MainWindow window)
    {
        var login = new SshLogin
        {
            ConnectionId = "selftest", Name = "web01", Host = "web01.example", Port = 22, UserName = "root",
            KnownHostKey = "SHA256:Xq3pN1v2YkV9b7c5mJ8wQe4rT6yU0iO2pA3sD5fG7hK",
        };
        var presented = new SshHostKey("ssh-ed25519", "SHA256:p8vR2kD0LmZ7tQ1sW4eY6uI9oP3aS5dF7gH1jK2lZxC", "aa:bb", 256);

        Task<bool> unknown = HostKeyWindow.AskAsync(window, new SshHostKeyException(login with { KnownHostKey = "" }, presented, HostKeyStatus.Unknown));
        await WaitFor(() => HostKeyWindow.Current is not null, TimeSpan.FromSeconds(5));
        await Settle(500);
        HostKeyWindow? prompt = HostKeyWindow.Current;
        if (prompt is not null)
            SaveRenderProbe(prompt, "ssh-hostkey-unknown.png");
        prompt?.Accept();
        Check("Unknown host key: prompt opens, Accept trusts", prompt?.Status == HostKeyStatus.Unknown && await unknown);

        Task<bool> changed = HostKeyWindow.AskAsync(window, new SshHostKeyException(login, presented, HostKeyStatus.Changed));
        await WaitFor(() => HostKeyWindow.Current is not null, TimeSpan.FromSeconds(5));
        await Settle(500);
        prompt = HostKeyWindow.Current;
        if (prompt is not null)
            SaveRenderProbe(prompt, "ssh-hostkey-changed.png");
        prompt?.Close();
        Check("Changed host key: warning prompt opens, closing it declines", prompt?.Status == HostKeyStatus.Changed && !await changed);
    }

    private static async Task RunSshLiveChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        string[] ids = ["selftest-ssh-jump", "selftest-ssh-target", "selftest-ssh-rdp"];
        foreach (string id in ids)
            WindowsCredentialSource.Delete(id);

        var jump = new Connection
        {
            Id = ids[0], Name = "Selftest Jump", Type = ConnectionType.Ssh, Host = Environment.GetEnvironmentVariable("RM_SSH_TEST_HOST")!,
            Port = int.TryParse(Environment.GetEnvironmentVariable("RM_SSH_TEST_PORT"), out int port) ? port : 22,
            UserName = Environment.GetEnvironmentVariable("RM_SSH_TEST_USER") ?? "", Description = "SSH testbed (Docker)",
        };
        string folder = Path.Combine(Path.GetTempPath(), $"rm-selftest-ssh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        SshConnect.TemporaryConnections[jump.Id] = jump;
        try
        {
            await app.Credentials.SaveOwnSecretAsync(jump, Environment.GetEnvironmentVariable("RM_SSH_TEST_PASSWORD") ?? "");
            ConnectionItem jumpItem = window.CreateItem(jump);
            string? trusted = await RunLiveTerminalChecksAsync(window, jumpItem);
            if (trusted is null)
                return;

            await RunChangedHostKeyCheckAsync(window, jumpItem, trusted);
            if (Environment.GetEnvironmentVariable("RM_SSH_TEST_TARGET") is { Length: > 0 } target)
                await RunLiveSftpChecksAsync(window, jump, target, folder, ids[1]);
            else
                Log("SKIP SFTP through a jump host (RM_SSH_TEST_TARGET not set).");

            if (Environment.GetEnvironmentVariable("RM_SSH_TEST_RDP") is { Length: > 0 } rdpHost)
                await RunRdpOverSshCheckAsync(window, jump, rdpHost, ids[2]);
            else
                Log("SKIP RDP over SSH (RM_SSH_TEST_RDP not set; start the testbed with -WithRdp).");
        }
        finally
        {
            SshConnect.TemporaryConnections.Clear();
            foreach (string id in ids)
                WindowsCredentialSource.Delete(id);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException ex)
            {
                Log($"  could not delete {folder}: {ex.Message}");
            }
        }
    }

    /// <summary>First connect (unknown key, accepted), htop, resize, vim. Returns the trusted fingerprint, or null when the session did not start.</summary>
    private static async Task<string?> RunLiveTerminalChecksAsync(MainWindow window, ConnectionItem jumpItem)
    {
        var terminal = (SshTerminalWindow)window.OpenSession(jumpItem)!;
        terminal.StatusLogged += (_, message) => Log($"  [{terminal.Title}] {message}");
        bool prompted = await WaitFor(() => HostKeyWindow.Current is not null, TimeSpan.FromSeconds(30));
        HostKeyStatus? status = HostKeyWindow.Current?.Status;
        await Settle(300);
        if (HostKeyWindow.Current is { } prompt)
        {
            SaveRenderProbe(prompt, "ssh-hostkey-unknown-live.png");
            prompt.Accept();
        }

        bool connected = await WaitFor(() => terminal.IsConnected, TimeSpan.FromSeconds(40));
        string trusted = jumpItem.Model.SettingsAs<SshSettings>().HostKeyFingerprint;
        Check($"Unknown host key is asked before logon, then stored ({trusted}); auto-connect with the stored password",
            prompted && status == HostKeyStatus.Unknown && connected && trusted.StartsWith(SshHostKey.Sha256Prefix, StringComparison.Ordinal));
        if (!connected)
        {
            Log($"  card: {terminal.LogonCard.MessageText}");
            terminal.Close();
            return null;
        }

        terminal.Activate();
        await Settle(800);
        SaveWindowProbe(terminal, "ssh-terminal.png");

        // htop: alternate screen with its header.
        terminal.SendInput("htop\r");
        bool htop = await WaitForScreenAsync(terminal, s => s.Buffer == "alternate" && s.Text.Contains("Load average", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
        await Settle(1500);
        SaveWindowProbe(terminal, "ssh-terminal-htop.png");
        Check("htop renders (alternate screen, header with load average)", htop);

        // Resize while htop runs: the PTY follows, htop redraws to the new width.
        (int columns, int rows) = terminal.TerminalSize;
        terminal.Width += 260;
        terminal.Height += 140;
        bool grown = await WaitFor(() => terminal.TerminalSize.Columns > columns && terminal.TerminalSize.Rows > rows, TimeSpan.FromSeconds(5));
        await Settle(1500);
        SaveWindowProbe(terminal, "ssh-terminal-htop-resized.png");
        terminal.SendInput("q");
        await WaitForScreenAsync(terminal, s => s.Buffer == "normal", TimeSpan.FromSeconds(5));
        (int newColumns, int newRows) = terminal.TerminalSize;
        terminal.SendInput("clear; stty size\r");
        bool stty = await WaitForScreenAsync(terminal, s => s.Lines.Any(l => l.Trim() == $"{newRows} {newColumns}"), TimeSpan.FromSeconds(10));
        Check($"Resize reaches the server PTY ({columns}x{rows} -> {newColumns}x{newRows}, stty size)", grown && stty);

        // vim: alternate screen with its "~" filler lines, :q! goes back.
        terminal.SendInput("vim -u NONE /etc/hostname\r");
        bool vim = await WaitForScreenAsync(terminal, s => s.Buffer == "alternate" && s.Lines.Count(l => l.StartsWith('~')) > 3, TimeSpan.FromSeconds(10));
        await Settle(800);
        SaveWindowProbe(terminal, "ssh-terminal-vim.png");
        terminal.SendInput("\u001b:q!\r");
        bool back = await WaitForScreenAsync(terminal, s => s.Buffer == "normal", TimeSpan.FromSeconds(10));
        Check("vim renders (alternate screen with ~ lines) and :q! returns to the shell", vim && back);

        terminal.SendInput("exit\r");
        bool ended = await WaitFor(() => !terminal.IsConnected, TimeSpan.FromSeconds(10));
        await Settle(500);
        Check($"exit ends the session and offers reconnect (status \"{terminal.LastStatus}\")", ended && terminal.LastStatus == Strings.Ssh_Status_Closed);
        terminal.Close();
        await Settle(300);
        return trusted;
    }

    private static async Task RunChangedHostKeyCheckAsync(MainWindow window, ConnectionItem jumpItem, string trusted)
    {
        Connection jump = jumpItem.Model;
        jump.Settings = jump.SettingsAs<SshSettings>() with { HostKeyFingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" };
        var terminal = (SshTerminalWindow)window.OpenSession(jumpItem)!;
        terminal.StatusLogged += (_, message) => Log($"  [{terminal.Title}] {message}");
        bool prompted = await WaitFor(() => HostKeyWindow.Current is not null, TimeSpan.FromSeconds(30));
        HostKeyStatus? status = HostKeyWindow.Current?.Status;
        await Settle(300);
        if (HostKeyWindow.Current is { } prompt)
        {
            SaveRenderProbe(prompt, "ssh-hostkey-changed-live.png");
            prompt.Close();
        }

        bool declined = await WaitFor(() => terminal.IsCardVisible && !terminal.LogonCard.IsBusy, TimeSpan.FromSeconds(10));
        await Settle(300);
        SaveRenderProbe(terminal, "ssh-hostkey-declined.png");
        Check("Changed host key asks again with a warning; declining keeps the session closed and the stored key",
            prompted && status == HostKeyStatus.Changed && declined && !terminal.IsConnected
            && terminal.LogonCard.MessageText == Strings.HostKey_Declined
            && jump.SettingsAs<SshSettings>().HostKeyFingerprint.StartsWith("SHA256:AAAA", StringComparison.Ordinal));
        terminal.Close();
        jump.Settings = jump.SettingsAs<SshSettings>() with { HostKeyFingerprint = trusted };
        await Settle(300);
    }

    /// <summary>SFTP to the target behind the jump host, with a key file: drop a file onto the list, download it, delete it.</summary>
    private static async Task RunLiveSftpChecksAsync(MainWindow window, Connection jump, string target, string folder, string id)
    {
        var connection = new Connection
        {
            Id = id, Name = "Selftest SFTP (hinter Jump-Host)", Type = ConnectionType.Sftp, Host = target, UserName = jump.UserName,
            Description = "SFTP through the jump host, key file",
        };
        connection.Settings = connection.SettingsAs<SshSettings>() with
        {
            AuthMethod = SshAuthMethod.KeyFile,
            KeyPath = Environment.GetEnvironmentVariable("RM_SSH_TEST_KEY") ?? "",
            JumpHostId = jump.Id,
        };
        var sftp = (SftpWindow)window.OpenSession(window.CreateItem(connection))!;
        sftp.StatusLogged += (_, message) => Log($"  [{sftp.Title}] {message}");
        bool prompted = await WaitFor(() => HostKeyWindow.Current is not null, TimeSpan.FromSeconds(30));
        HostKeyWindow.Current?.Accept();
        bool connected = await WaitFor(() => sftp.IsConnected && sftp.CurrentPath.Length > 0 && !sftp.IsBusy, TimeSpan.FromSeconds(30));
        Check($"SFTP through the jump host with a key file (target key asked: {prompted}, folder {sftp.CurrentPath})", prompted && connected);
        if (!connected)
        {
            Log($"  card: {sftp.LogonCard.MessageText}");
            sftp.Close();
            return;
        }

        // A drop exactly like one from Explorer: a DataTransfer with a file item, raised on the list.
        string name = $"rm-selftest-{Guid.NewGuid():N}.txt";
        string local = Path.Combine(folder, name);
        byte[] content = Encoding.UTF8.GetBytes($"Remote-Manager SFTP-Test über den Jump-Host – {DateTime.Now:O}\n");
        await File.WriteAllBytesAsync(local, content);
        IStorageFile? file = await sftp.StorageProvider.TryGetFileFromPathAsync(new Uri(local));
        var data = new DataTransfer();
        if (file is not null)
            data.Add(DataTransferItem.CreateFile(file));
        sftp.EntryList.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, sftp.EntryList, new Point(20, 20), KeyModifiers.None));
        bool uploaded = await WaitFor(() => !sftp.IsBusy && sftp.Items.Any(i => i.Name == name), TimeSpan.FromSeconds(20));
        await Settle(500);
        SaveRenderProbe(sftp, "sftp-browser.png");
        Check("Dropping a file on the list uploads it", file is not null && uploaded);

        SftpItem? item = sftp.Items.FirstOrDefault(i => i.Name == name);
        string downloads = Directory.CreateDirectory(Path.Combine(folder, "download")).FullName;
        IReadOnlyList<string> paths = item is null ? [] : await sftp.DownloadAsync([item], downloads);
        Check("Download brings back the same bytes", paths.Count == 1 && File.Exists(paths[0]) && (await File.ReadAllBytesAsync(paths[0])).SequenceEqual(content));

        if (item is not null)
            await sftp.DeleteAsync([item], confirm: false);
        bool deleted = await WaitFor(() => !sftp.IsBusy && sftp.Items.All(i => i.Name != name), TimeSpan.FromSeconds(10));
        Check("Delete removes the remote file", deleted);
        sftp.Close();
        await Settle(300);
    }

    /// <summary>RDP to the xrdp container that only the jump host can reach (testbed -WithRdp).</summary>
    private static async Task RunRdpOverSshCheckAsync(MainWindow window, Connection jump, string rdpHost, string id)
    {
        var connection = new Connection
        {
            Id = id, Name = "Selftest RDP über SSH", Type = ConnectionType.Rdp, Host = rdpHost, UserName = "ubuntu",
            Description = "xrdp behind the jump host",
        };
        // xrdp has a self-signed certificate; level 0 connects without the certificate prompt.
        connection.Settings = connection.SettingsAs<RdpSettings>() with { JumpHostId = jump.Id, AuthenticationLevel = 0 };
        // The image's documented default logon, not a secret.
        await AppHost.Current.Credentials.SaveOwnSecretAsync(connection, "ubuntu");
        ConnectionItem item = window.CreateItem(connection);
        item.RefreshCredentialState();
        var session = (SessionWindow)window.OpenSession(item)!;
        session.StatusLogged += (_, message) => Log($"  [{session.Title}] {message}");
        bool tunneled = await WaitFor(() => session.IsTunneled, TimeSpan.FromSeconds(40));
        // xrdp never sends the "logon complete" notification, so wait for a desktop picture instead:
        // the session is still up and its surface shows something.
        bool desktop = await WaitFor(() => session.IsSessionActive && !CapturedBlank(session), TimeSpan.FromSeconds(90));
        await Task.Delay(TimeSpan.FromSeconds(4));
        SaveWindowProbe(session, "rdp-over-ssh.png");
        Check($"RDP through an SSH tunnel reaches a host behind the jump host (tunnel {tunneled}, desktop shown {desktop}, " +
            $"still connected {session.IsSessionActive}, logon event {session.IsLoggedIn}, status \"{session.LastStatus}\")",
            tunneled && desktop && session.IsSessionActive);
        session.Close();
        await Settle(500);
        Check("Closing the RDP window closes the SSH tunnel", !session.IsTunneled);
    }

    /// <summary>
    /// The window's own pixels via PrintWindow (includes WebView2 and RDP child windows and works while
    /// another window covers it, unlike a screen copy).
    /// </summary>
    private static void SaveWindowProbe(Window window, string name)
    {
        if (SessionCapture.Capture(HandleOf(window)) is not { } frame)
            return;
        string path = Path.Combine(Core.AppPaths.Spike, name);
        frame.SavePng(path);
        Log($"  probe saved: {path} ({frame.Method})");
    }

    private static async Task<bool> WaitForScreenAsync(SshTerminalWindow terminal, Func<(string Buffer, IReadOnlyList<string> Lines, string Text), bool> condition, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (true)
        {
            (string buffer, IReadOnlyList<string> lines) = ("", []);
            try
            {
                (buffer, lines) = await terminal.ReadScreenAsync();
            }
            catch (TimeoutException)
            {
                Log("  terminal page did not answer within 5 s");
            }

            if (condition((buffer, lines, string.Join('\n', lines))))
                return true;
            if (DateTime.UtcNow > end)
            {
                Log($"  screen ({buffer}): {string.Join(" | ", lines.Where(l => l.Trim().Length > 0).Take(6))}");
                return false;
            }

            await Task.Delay(200);
        }
    }
}
