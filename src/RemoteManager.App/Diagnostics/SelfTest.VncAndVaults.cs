using System.Globalization;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using MarcusW.VncClient;
using RemoteManager.App.Connections.Vnc;
using RemoteManager.App.Credentials;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Credentials.KeePass;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Phase 3 checks: embedded VNC against <see cref="FakeVncServer"/>, the external viewer fallback, KeePass
/// (a throwaway .kdbx with a random master password) and 1Password (a fake op) as password sources. Only
/// temporary connections ("selftest-vnc*", never stored), temporary files, and dummy secrets; nothing is logged.
/// RM_VNC_HOST (+ RM_VNC_PORT, RM_VNC_PASSWORD) adds a session against a real VNC server.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunVncAndVaultChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        KeePassCredentialSource? keePass = app.Credentials.Sources.OfType<KeePassCredentialSource>().SingleOrDefault();
        OnePasswordCredentialSource? onePassword = app.Credentials.Sources.OfType<OnePasswordCredentialSource>().SingleOrDefault();
        Check("Credential sources: Credential Manager (writable), KeePass and 1Password (read-only)",
            app.Credentials.DefaultSource.Id == WindowsCredentialSource.SourceId
            && keePass is { IsWritable: false, Prompt: MasterPasswordPrompt } && onePassword is { IsWritable: false });
        Check("VNC handler has a settings editor and embeds by default",
            app.Handlers[ConnectionType.Vnc].CreateSettingsEditor() is VncSettingsEditor && !new Core.Model.Settings.VncSettings().OpenExternally);
        if (keePass is null || onePassword is null)
            return;

        // Under the data folder (REMOTE_MANAGER_DATA for test runs); removed at the end.
        string folder = Path.Combine(Core.AppPaths.Root, "selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string master = $"Master-{Guid.NewGuid():N}";
        // VNC authentication uses at most 8 characters.
        string vncSecret = "v" + RandomNumberGenerator.GetHexString(7, lowercase: true);
        string database = Path.Combine(folder, "selftest.kdbx");
        using (KdbxCompositeKey key = KdbxCompositeKey.Create(master, null))
        {
            KdbxWriter.Write(database, key,
            [
                new("Selbsttest/VNC", "Testserver", "", vncSecret),
                new("Selbsttest", "Router", "admin", "router-" + Guid.NewGuid().ToString("N")),
                new("", "Notiz", "", "-"),
            ]);
        }

        keePass.LockAll();
        string? lastDatabase = app.Library.GetSetting(PasswordSourcePanel.LastDatabaseSetting);
        string? opPath = onePassword.ExecutablePath;
        IClipboard clipboard = window.Clipboard!;
        string? userClipboard = await clipboard.TryGetTextAsync();
        using var server = new FakeVncServer(vncSecret);
        ConnectionItem vnc = window.CreateItem(new Connection
        {
            Id = "selftest-vnc", Name = "Selftest VNC", Type = ConnectionType.Vnc, Host = "127.0.0.1", Port = server.Port,
            Description = "Selbsttest-Server, Passwort aus KeePass", Tags = ["selftest"],
        });
        window.ShowUnsaved(vnc, true);
        try
        {
            await RunKeePassEditorChecksAsync(window, vnc, database, master, vncSecret);
            await RunGroupVaultChecksAsync(window, database, vncSecret);
            await RunEmbeddedVncChecksAsync(window, vnc, server, master);
            await RunVncPasswordFailureChecksAsync(window, server);
            await RunOnePasswordChecksAsync(window, vnc, onePassword, folder, vncSecret);
            await RunExternalViewerChecksAsync(window, vnc, folder, clipboard, vncSecret);
        }
        finally
        {
            window.ShowUnsaved(vnc, false);
            foreach (Window open in window.SessionWindows.Where(w => w is VncSessionWindow).ToList())
                open.Close();
            ExternalVncViewer.FindOverride = null;
            onePassword.ExecutablePath = opPath;
            keePass.LockAll();
            app.Library.SetSetting(PasswordSourcePanel.LastDatabaseSetting, lastDatabase);
            WindowsCredentialSource.Delete(vnc.Id);
            if (userClipboard is not null)
                await clipboard.SetTextAsync(userClipboard);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        if (Environment.GetEnvironmentVariable("RM_VNC_HOST") is { Length: > 0 } liveHost)
            await RunLiveVncSessionAsync(window, liveHost);
        else
            Log("SKIP live VNC session (RM_VNC_HOST not set).");
    }

    /// <summary>
    /// A group can take its password from KeePass too (group editor and the editor's group-logon flyout); its
    /// connections inherit the reference, and nothing goes to the Credential Manager. Runs while the database is unlocked.
    /// </summary>
    private static async Task RunGroupVaultChecksAsync(MainWindow window, string database, string secret)
    {
        AppHost app = AppHost.Current;
        var group = new ConnectionGroup { Name = $"Selbsttest Tresor {Guid.NewGuid():N}"[..26], UserName = "tresor" };
        app.Library.AddGroup(group);
        var member = new Connection { Id = "selftest-vault-member", Name = "Selftest Tresor-Mitglied", Type = ConnectionType.Vnc, GroupId = group.Id };
        try
        {
            Task<(bool Saved, string? ParentId)> editing = GroupEditor.ShowAsync(group, isNew: false);
            await Settle(700);
            GroupEditor editor = GroupEditor.OpenEditor!;
            editor.Source.Source.SelectedIndex = (int)PasswordSourceKind.KeePass;
            editor.Source.Database.Text = database;
            editor.Source.KeyFile.Text = "";
            editor.Source.Entry.Text = "Selbsttest/VNC/Testserver";
            await Settle(200);
            bool ownHidden = !editor.IsOwnPasswordShown;
            SaveRenderProbe(window, "group-editor-keepass.png");
            bool committed = await editor.TryCommitAsync(group);
            GroupEditor.OpenDialog?.Hide();
            await editing;
            app.Library.UpdateGroup(group);

            EffectiveCredential inherited = app.Credentials.Resolve(member);
            string? read = await app.Credentials.ReadAsync(inherited.Secret);
            Check("Group editor: KeePass as the group password hides the own password box and saves only a reference",
                ownHidden && committed && group.Credential?.Source == KeePassCredentialSource.SourceId
                && !group.Credential.Key.Contains(secret, StringComparison.Ordinal) && !WindowsCredentialSource.Exists($"group-{group.Id}"));
            Check("A connection without its own logon inherits the group's KeePass entry",
                inherited.InheritedFrom == group && inherited.UserName == "tresor" && read == secret);

            // The flyout of the connection editor shows the same source and can switch back to the app's own password.
            var panel = new GroupLogonPanel(group);
            bool shownAgain = panel.Source.Kind == PasswordSourceKind.KeePass && panel.Source.Entry.Text == "Selbsttest/VNC/Testserver";
            panel.Source.Source.SelectedIndex = (int)PasswordSourceKind.App;
            panel.Password.Text = "gruppe-" + Guid.NewGuid().ToString("N")[..8];
            bool applied = await panel.ApplyAsync();
            Check("Group logon flyout: shows the KeePass reference and switches back to a Credential Manager password",
                shownAgain && applied && group.Credential?.Source == WindowsCredentialSource.SourceId && WindowsCredentialSource.Exists($"group-{group.Id}"));
        }
        finally
        {
            GroupEditor.OpenDialog?.Hide();
            WindowsCredentialSource.Delete($"group-{group.Id}");
            app.Library.RemoveGroup(group);
        }
    }

    private static async Task RunKeePassEditorChecksAsync(MainWindow window, ConnectionItem vnc, string database, string master, string secret)
    {
        Task<bool> editing = ConnectionEditor.ShowAsync(vnc.Model, isNew: false);
        await Settle(700);
        ConnectionEditor editor = ConnectionEditor.OpenEditor!;
        PasswordSourcePanel panel = editor.Source;
        panel.Source.SelectedIndex = (int)PasswordSourceKind.KeePass;
        panel.Database.Text = database;
        panel.KeyFile.Text = "";
        panel.Entry.Text = "";
        await Settle(300);
        Check("Editor: choosing KeePass hides the own password box", panel.IsExternal && !editor.Password.IsEffectivelyVisible);

        // The picker unlocks first: a wrong master password asks again with a message.
        Task picking = panel.PickEntryAsync();
        bool asked = await WaitFor(() => MasterPasswordWindow.Current is { IsVisible: true }, TimeSpan.FromSeconds(10));
        await Settle(400);
        if (MasterPasswordWindow.Current is { } first)
        {
            SaveRenderProbe(first, "keepass-unlock.png");
            first.Password.Text = "falsch";
            first.Unlock();
        }

        bool askedAgain = await WaitFor(() => MasterPasswordWindow.Current is { IsVisible: true, ShowsWrongPassword: true }, TimeSpan.FromSeconds(20));
        await Settle(300);
        if (MasterPasswordWindow.Current is { } second)
        {
            SaveRenderProbe(second, "keepass-unlock-wrong.png");
            second.Password.Text = master;
            second.Unlock();
        }

        Check("KeePass unlock dialog asks, and asks again after a wrong master password", asked && askedAgain);
        bool pickerShown = await WaitFor(() => KeePassEntryPicker.Current is { IsVisible: true }, TimeSpan.FromSeconds(20));
        await Settle(400);
        if (KeePassEntryPicker.Current is { } picker)
        {
            SaveRenderProbe(picker, "keepass-picker.png");
            IReadOnlyList<KeePassEntryChoice> shown = picker.Shown;
            Check($"Entry picker lists the entries ({shown.Count}) without any password",
                shown.Count == 3 && !shown.Any(c => c.Title.Contains(secret, StringComparison.Ordinal) || c.Detail.Contains(secret, StringComparison.Ordinal) || c.Reference.Contains(secret, StringComparison.Ordinal)));
            picker.Select(shown.First(c => c.Title == "Testserver"));
        }

        await picking;
        Check("Picking fills the entry path", pickerShown && panel.Entry.Text == "Selbsttest/VNC/Testserver");

        await panel.CheckAsync();
        Check("\"Prüfen\" reads the entry while the database is unlocked (no second prompt)", panel.CheckSucceeded && MasterPasswordWindow.Current is null);
        await Settle(300);
        SaveRenderProbe(window, "editor-keepass.png");

        bool committed = await editor.TryCommitAsync(vnc.Model);
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
        vnc.RefreshCredentialState();
        CredentialRef? reference = vnc.Model.Credential;
        Check("Saved: only a KeePass reference, no secret in it, nothing in the Credential Manager",
            committed && reference?.Source == KeePassCredentialSource.SourceId
            && !reference.Key.Contains(secret, StringComparison.Ordinal) && !reference.Key.Contains(master, StringComparison.Ordinal)
            && !WindowsCredentialSource.Exists(vnc.Id) && vnc.HasStoredPassword);

        // Reopening shows the reference again.
        Task<bool> reopened = ConnectionEditor.ShowAsync(vnc.Model, isNew: false);
        await Settle(600);
        PasswordSourcePanel again = ConnectionEditor.OpenEditor!.Source;
        Check("Editor shows the KeePass reference on reopen",
            again.Kind == PasswordSourceKind.KeePass && again.Database.Text == database && again.Entry.Text == "Selbsttest/VNC/Testserver");
        ConnectionEditor.OpenDialog?.Hide();
        await reopened;
    }

    private static async Task RunEmbeddedVncChecksAsync(MainWindow window, ConnectionItem vnc, FakeVncServer server, string master)
    {
        KeePassCredentialSource keePass = AppHost.Current.Credentials.Sources.OfType<KeePassCredentialSource>().Single();
        keePass.LockAll();

        var session = (VncSessionWindow)window.OpenSession(vnc)!;
        session.StatusLogged += (_, message) => Log($"  [{session.Title}] {message}");
        bool asked = await WaitFor(() => MasterPasswordWindow.Current is { IsVisible: true }, TimeSpan.FromSeconds(15));
        await Settle(300);
        MasterPasswordWindow.Current?.Password.Text = master;
        MasterPasswordWindow.Current?.Unlock();
        Check("Connecting asks for the KeePass master password (the secret is fetched at connect time)", asked);

        bool connected = await WaitFor(() => session.IsConnected && session.Vnc.FrameCount > 0, TimeSpan.FromSeconds(20));
        Check($"Embedded VNC session connects and renders ({session.Status})", connected && session.UsedStoredPassword && server.AuthFailures == 0);
        await Settle(800);

        (int X, int Y)[] probes = [(10, 10), (630, 10), (10, 390), (630, 390), (320, 200)];
        Check("Frame shows the server's test pattern (all four quadrants)",
            probes.All(p => session.Vnc.PixelAt(p.X, p.Y) == FakeVncServer.ColorAt(p.X, p.Y)));
        Check("Remote size is the server's", session.Vnc.RemoteSize == new PixelSize(FakeVncServer.Width, FakeVncServer.Height));
        session.Activate();
        await Settle(500);
        SaveRenderProbe(session, "vnc-session-selftest.png");

        // Scaled: the view's center maps to the remote center.
        Position center = session.Vnc.ToRemote(new Point(session.Vnc.Bounds.Width / 2, session.Vnc.Bounds.Height / 2));
        Check($"Scaled view maps pointer positions to remote pixels ({center.X},{center.Y})",
            Math.Abs(center.X - FakeVncServer.Width / 2) <= 2 && Math.Abs(center.Y - FakeVncServer.Height / 2) <= 2);

        server.Keys.Clear();
        session.Vnc.SendCtrlAltDel();
        await WaitFor(() => server.Keys.Count >= 6, TimeSpan.FromSeconds(5));
        uint[] downs = [.. server.Keys.Where(k => k.Down).Select(k => k.Key)];
        Check("Strg+Alt+Entf reaches the server as Control_L, Alt_L, Delete",
            downs.SequenceEqual([(uint)KeySymbol.Control_L, (uint)KeySymbol.Alt_L, (uint)KeySymbol.Delete]) && server.Keys.Count(k => !k.Down) == 3);

        server.Keys.Clear();
        session.Vnc.ViewOnly = true;
        session.Vnc.SendCtrlAltDel();
        await Settle(600);
        Check("View only sends no input", server.Keys.IsEmpty);
        session.Vnc.ViewOnly = false;

        session.Close();
        await Settle(500);
        Check("Closing the VNC window ends the session and saves the preview tile",
            !window.SessionWindows.Contains(session) && File.Exists(vnc.Model.ResolvedPreviewPath));
        if (File.Exists(vnc.Model.ResolvedPreviewPath))
            File.Delete(vnc.Model.ResolvedPreviewPath);
        vnc.Model.PreviewPath = null;
        vnc.ReloadPreview();
    }

    private static async Task RunVncPasswordFailureChecksAsync(MainWindow window, FakeVncServer server)
    {
        ConnectionItem bare = window.CreateItem(new Connection
        {
            Id = "selftest-vnc-bare", Name = "Selftest VNC ohne Passwort", Type = ConnectionType.Vnc, Host = "127.0.0.1", Port = server.Port,
        });
        var session = (VncSessionWindow)window.OpenSession(bare)!;
        bool asked = await WaitFor(() => session.CardError is not null, TimeSpan.FromSeconds(15));
        Check($"No password: the card asks for one ({session.CardError})", asked && session.CardError == Strings.Vnc_PasswordRequired);

        int failuresBefore = server.AuthFailures;
        session.Password.Text = "falsch12";
        await session.ConnectAsync();
        await Settle(300);
        Check($"Wrong password: the card says so ({session.CardError})",
            session.CardError == Strings.Vnc_AuthFailed && server.AuthFailures == failuresBefore + 1 && session.Password.Text?.Length == 0);
        SaveRenderProbe(session, "vnc-auth-failed.png");
        session.Close();
        await Settle(300);

        ConnectionItem closed = window.CreateItem(new Connection { Id = "selftest-vnc-closed", Name = "Selftest VNC geschlossen", Type = ConnectionType.Vnc, Host = "127.0.0.1", Port = 1 });
        var refused = (VncSessionWindow)window.OpenSession(closed)!;
        bool failed = await WaitFor(() => refused.CardError is not null, TimeSpan.FromSeconds(20));
        Check($"Closed port: connect failure on the card ({refused.CardError})",
            failed && refused.CardError!.StartsWith(Strings.Vnc_ConnectFailed.Split('{')[0], StringComparison.Ordinal));
        refused.Close();
        await Settle(300);
    }

    private static async Task RunOnePasswordChecksAsync(MainWindow window, ConnectionItem vnc, OnePasswordCredentialSource onePassword, string folder, string secret)
    {
        // A fake op: prints the secret for one reference, fails like op for any other. Only the reference
        // is on its command line.
        string fake = Path.Combine(folder, "op.cmd");
        string argsFile = Path.Combine(folder, "op-args.txt");
        await File.WriteAllTextAsync(fake, string.Join("\r\n",
            "@echo off",
            $"echo %* > \"{argsFile}\"",
            "if not \"%~3\"==\"op://Selbsttest/VNC/password\" goto missing",
            $"<nul set /p \"={secret}\"",
            "exit /b 0",
            ":missing",
            ">&2 echo [ERROR] 2026/10/30 12:00:00 could not read secret: \"%~3\" isn't an item.",
            "exit /b 1",
            ""));
        onePassword.ExecutablePath = fake;

        Task<bool> editing = ConnectionEditor.ShowAsync(vnc.Model, isNew: false);
        await Settle(700);
        ConnectionEditor editor = ConnectionEditor.OpenEditor!;
        PasswordSourcePanel panel = editor.Source;
        panel.Source.SelectedIndex = (int)PasswordSourceKind.OnePassword;
        panel.OpReference.Text = "Selbsttest/VNC";
        Check("1Password: a malformed reference is rejected", panel.Validate() == Strings.Vault_Error_OpReference);
        panel.OpReference.Text = "op://Selbsttest/Fehlt/password";
        await panel.CheckAsync();
        Check($"1Password: a missing item is reported ({panel.CheckResult})",
            !panel.CheckSucceeded && panel.CheckResult.StartsWith(Strings.Vault_Err_EntryNotFound.Split('{')[0], StringComparison.Ordinal));
        panel.OpReference.Text = "op://Selbsttest/VNC/password";
        await panel.CheckAsync();
        Check("1Password: \"Prüfen\" reads the item through op", panel.CheckSucceeded);
        await Settle(300);
        SaveRenderProbe(window, "editor-1password.png");
        bool committed = await editor.TryCommitAsync(vnc.Model);
        ConnectionEditor.OpenDialog?.Hide();
        await editing;

        string? read = await AppHost.Current.Credentials.ReadAsync(vnc.Model.Credential);
        string args = File.Exists(argsFile) ? await File.ReadAllTextAsync(argsFile) : "";
        Check("1Password reference saved; the secret comes from stdout, never the command line",
            committed && vnc.Model.Credential == new CredentialRef(OnePasswordCredentialSource.SourceId, "op://Selbsttest/VNC/password")
            && read == secret && args.Contains("read --no-newline", StringComparison.Ordinal) && !args.Contains(secret, StringComparison.Ordinal));
    }

    private static async Task RunExternalViewerChecksAsync(MainWindow window, ConnectionItem vnc, string folder, IClipboard clipboard, string secret)
    {
        string fakeViewer = Path.Combine(folder, "viewer.cmd");
        string argsFile = Path.Combine(folder, "viewer-args.txt");
        await File.WriteAllTextAsync(fakeViewer, $"@echo off\r\necho %* > \"{argsFile}\"\r\n");
        ExternalVncViewer.FindOverride = () => new VncViewer("Testviewer", fakeViewer, "-viewonly");

        vnc.Model.Settings = vnc.Model.SettingsAs<Core.Model.Settings.VncSettings>() with { OpenExternally = true, ViewOnly = true };
        Window? opened = window.OpenSession(vnc);
        bool started = await WaitFor(() => File.Exists(argsFile), TimeSpan.FromSeconds(10));
        await WaitFor(() => window.ToastBar.IsOpen, TimeSpan.FromSeconds(10));
        await Settle(500);
        string args = started ? await File.ReadAllTextAsync(argsFile) : "";
        string? onClipboard = await clipboard.TryGetTextAsync();
        Check($"External viewer: started with \"{args.Trim()}\" and no password on its command line",
            opened is null && args.Contains($"-viewonly 127.0.0.1::{vnc.Model.Port}", StringComparison.Ordinal) && !args.Contains(secret, StringComparison.Ordinal));
        // The clipboard is shared with every other program (and parallel self-test runs).
        Check($"External viewer: the password goes to the clipboard ({window.ToastBar.Message})", onClipboard == secret);
        await Services.SecretClipboard.ClearPendingAsync();

        ExternalVncViewer.FindOverride = () => null;
        window.ToastBar.IsOpen = false;
        window.OpenSession(vnc);
        await WaitFor(() => window.ToastBar.IsOpen, TimeSpan.FromSeconds(5));
        Check("No viewer installed: a clear message", window.ToastBar.Message == Strings.Vnc_ViewerMissing);
        await Settle(300);
        SaveRenderProbe(window, "vnc-viewer-missing.png");
        window.ToastBar.IsOpen = false;
        vnc.Model.Settings = vnc.Model.SettingsAs<Core.Model.Settings.VncSettings>() with { OpenExternally = false, ViewOnly = false };
    }

    /// <summary>A session against a real server (RM_VNC_HOST); the password (RM_VNC_PASSWORD) is typed into the card, never stored or logged.</summary>
    private static async Task RunLiveVncSessionAsync(MainWindow window, string host)
    {
        int port = int.TryParse(Environment.GetEnvironmentVariable("RM_VNC_PORT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 0;
        ConnectionItem live = window.CreateItem(new Connection
        {
            Id = "selftest-vnc-live", Name = "VNC-Testserver", Type = ConnectionType.Vnc, Host = host, Port = port,
            Description = "Echter VNC-Server (RM_VNC_HOST)",
        });
        var session = (VncSessionWindow)window.OpenSession(live)!;
        session.StatusLogged += (_, message) => Log($"  [{session.Title}] {message}");
        if (Environment.GetEnvironmentVariable("RM_VNC_PASSWORD") is { Length: > 0 } password)
        {
            await WaitFor(() => session.CardError is not null || session.IsConnected, TimeSpan.FromSeconds(20));
            if (!session.IsConnected)
            {
                session.Password.Text = password;
                await session.ConnectAsync();
            }
        }

        bool connected = await WaitFor(() => session.IsConnected && session.Vnc.FrameCount > 0, TimeSpan.FromSeconds(20));
        Check($"Live VNC session to {host} ({session.Status})", connected);
        if (session.Vnc.Connection is { } rfb)
        {
            Log($"  security {rfb.UsedSecurityType?.Name}, encodings {string.Join(", ", rfb.UsedEncodingTypes.Where(e => e.Id >= 0).Select(e => e.Name))}");
            // RM_VNC_KEYS: characters typed into the session (the test container runs busybox top: "N" sorts by PID).
            foreach (char c in Environment.GetEnvironmentVariable("RM_VNC_KEYS") ?? "")
            {
                KeySymbol symbol = (KeySymbol)c;
                rfb.EnqueueMessage(new MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing.KeyEventMessage(true, symbol));
                rfb.EnqueueMessage(new MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing.KeyEventMessage(false, symbol));
            }
        }

        await Settle(2500);
        session.Activate();
        await Settle(500);
        SaveRenderProbe(session, "vnc-session-live.png");
        session.Close();
        await Settle(300);
        if (File.Exists(live.Model.ResolvedPreviewPath))
            File.Delete(live.Model.ResolvedPreviewPath);
    }
}
