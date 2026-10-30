using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Views;
using RemoteManager.Core;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.RemoteTools;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Phase 2 checks: start options editor, launching TeamViewer/AnyDesk (this exe stands in for the tools,
/// see <see cref="FakeRemoteTool"/>; a real tool is never started), the password never in the process
/// list for AnyDesk (read back with Get-CimInstance Win32_Process), the TeamViewer -p warning, file
/// transfer, TeamViewer sync against a fake Web API, AnyDesk CSV import. Credentials: "selftest-*" only.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunRemoteToolChecksAsync(MainWindow window)
    {
        Log("--- Remote tools (Phase 2) ---");
        AppHost app = AppHost.Current;
        string[] ids = ["selftest-tv-launch", "selftest-ad-launch", "selftest-teamviewer-token"];
        foreach (string id in ids)
            WindowsCredentialSource.Delete(id);

        string secret = $"Rm-{Guid.NewGuid():N}";
        ConnectionItem tv = window.CreateItem(new Connection
        {
            Id = ids[0], Name = "Selftest TeamViewer", Type = ConnectionType.TeamViewer, Host = "123 456 789", Description = "Fake tool",
        });
        ConnectionItem ad = window.CreateItem(new Connection
        {
            Id = ids[1], Name = "Selftest AnyDesk", Type = ConnectionType.AnyDesk, Host = "987 654 321", Description = "Fake tool",
        });
        await app.Credentials.SaveOwnSecretAsync(tv.Model, secret);
        await app.Credentials.SaveOwnSecretAsync(ad.Model, secret);
        tv.RefreshCredentialState();
        ad.RefreshCredentialState();
        window.ShowUnsaved(tv, true);
        window.ShowUnsaved(ad, true);

        IClipboard clipboard = window.Clipboard!;
        string? userClipboard = await clipboard.TryGetTextAsync();
        string report = Path.Combine(AppPaths.Spike, "fake-tool-report.txt");
        RemoteToolLauncher.LocatorOverride = FakeLocator(Environment.ProcessPath);
        Environment.SetEnvironmentVariable(FakeRemoteTool.Variable, report);
        try
        {
            await RunRemoteToolEditorChecksAsync(window, tv, ad);
            await RunLaunchChecksAsync(window, tv, ad, secret, report, clipboard);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FakeRemoteTool.Variable, null);
            RemoteToolLauncher.LocatorOverride = null;
            window.ShowUnsaved(tv, false);
            window.ShowUnsaved(ad, false);
            foreach (string id in ids)
                WindowsCredentialSource.Delete(id);
            File.Delete(report);
            if (userClipboard is not null)
                await clipboard.SetTextAsync(userClipboard);
        }

        await RunTeamViewerSyncChecksAsync(window, ids[2]);
        await RunAnyDeskCsvChecksAsync(window);
        await RunToolPathChecksAsync(window);
    }

    private static async Task RunRemoteToolEditorChecksAsync(MainWindow window, ConnectionItem tv, ConnectionItem ad)
    {
        // TeamViewer with "-p" switched on: the warning shows, the preview masks the password.
        tv.Model.Settings = new RemoteToolSettings { CredentialOnCommandLine = true, Mode = RemoteToolMode.FileTransfer, Quality = 2 };
        Task<bool> editing = ConnectionEditor.ShowAsync(tv.Model, isNew: false);
        await Settle(900);
        RemoteToolSettingsEditor? editor = ConnectionEditor.OpenEditor?.GetVisualDescendants().OfType<RemoteToolSettingsEditor>().FirstOrDefault();
        editor?.BringIntoView();
        await Settle(300);
        SaveRenderProbe(window, "remote-tool-editor-teamviewer.png");
        string preview = editor?.PreviewCommand ?? "";
        Log($"  TeamViewer preview: {preview}");
        Check("TeamViewer editor: -p warning visible, preview \"-i <ID> -p •••••• -m fileTransfer -q 2\"",
            editor is { IsPasswordWarningShown: true } && preview.Contains("-i <ID> -p •••••• -m fileTransfer -q 2", StringComparison.Ordinal));

        editor!.CommandLineCredentialBox.IsChecked = false;
        await Settle(200);
        bool warningGone = !editor.IsPasswordWarningShown && !editor.PreviewCommand.Contains("-p", StringComparison.Ordinal);
        editor.Arguments.Text = "--quiet -p geheim";
        bool rejected = editor.Validate()?.Contains("-p", StringComparison.Ordinal) == true;
        Check("Without -p the warning goes away; a password switch in the free arguments is rejected", warningGone && rejected);
        ConnectionEditor.OpenDialog?.Hide();
        await editing;

        // AnyDesk: password through stdin, shown as such in the preview.
        ad.Model.Settings = new RemoteToolSettings { FullScreen = true, ExtraArguments = "--plain" };
        editing = ConnectionEditor.ShowAsync(ad.Model, isNew: false);
        await Settle(900);
        editor = ConnectionEditor.OpenEditor?.GetVisualDescendants().OfType<RemoteToolSettingsEditor>().FirstOrDefault();
        editor?.BringIntoView();
        await Settle(300);
        SaveRenderProbe(window, "remote-tool-editor-anydesk.png");
        preview = editor?.PreviewCommand ?? "";
        Log($"  AnyDesk preview: {preview.ReplaceLineEndings(" | ")}");
        Check("AnyDesk editor: no -p option, preview \"<ID> --full-screen --plain --with-password\" and stdin note",
            editor is { IsPasswordWarningShown: false, CommandLineCredentialBox.IsVisible: false }
            && preview.Contains("<ID> --full-screen --plain --with-password", StringComparison.Ordinal) && preview.Contains("stdin", StringComparison.Ordinal));
        ConnectionEditor.OpenDialog?.Hide();
        await editing;
    }

    private static async Task RunLaunchChecksAsync(MainWindow window, ConnectionItem tv, ConnectionItem ad, string secret, string report, IClipboard clipboard)
    {
        string secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

        // AnyDesk: password via stdin, never on the command line.
        ad.Model.Settings = new RemoteToolSettings();
        (string? commandLine, string[] fake) = await LaunchFakeAsync(window, ad, null, report);
        Log($"  AnyDesk Win32_Process.CommandLine: {Mask(commandLine, secret)}");
        Check("AnyDesk: password reaches the tool through stdin and is not in Win32_Process.CommandLine",
            commandLine is not null && !commandLine.Contains(secret, StringComparison.Ordinal)
            && commandLine.EndsWith("987654321 --with-password", StringComparison.Ordinal) && fake.Contains($"stdin={secretHash}"));

        // TeamViewer without -p: nothing secret on the command line; the password waits on the clipboard.
        tv.Model.Settings = new RemoteToolSettings();
        (commandLine, _) = await LaunchFakeAsync(window, tv, null, report);
        Log($"  TeamViewer Win32_Process.CommandLine: {Mask(commandLine, secret)}");
        bool onClipboard = await clipboard.TryGetTextAsync() == secret;
        Check("TeamViewer default: \"-i 123456789\" without password, password on the clipboard",
            commandLine is not null && commandLine.EndsWith("-i 123456789", StringComparison.Ordinal) && !commandLine.Contains(secret, StringComparison.Ordinal) && onClipboard);
        await SecretClipboard.ClearPendingAsync();

        // TeamViewer with -p: the password is in the process list, so the user gets a visible warning.
        tv.Model.Settings = new RemoteToolSettings { CredentialOnCommandLine = true };
        (commandLine, _) = await LaunchFakeAsync(window, tv, null, report);
        await Settle(300);
        SaveRenderProbe(window, "remote-tool-teamviewer-p-warning.png");
        Check("TeamViewer -p: visible warning toast (and the password really is in the process list)",
            commandLine?.Contains(secret, StringComparison.Ordinal) == true && window.ToastBar.IsOpen
            && window.ToastBar.Severity == FAInfoBarSeverity.Warning
            && window.ToastBar.Message == string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Warning_PasswordOnCommandLine, "TeamViewer"));

        // File transfer straight from the context menu.
        window.SetListView(false);
        window.Tiles.SelectedItem = ad;
        window.Tiles.ScrollIntoView(ad);
        await Settle(300);
        ContextMenu menu = window.Tiles.ContextMenu!;
        menu.Open(window.Tiles);
        await Settle(400);
        bool menuItem = menu.Items.OfType<MenuItem>().Any(i => i.Header as string == Strings.RemoteTool_Action_FileTransfer && i.IsVisible);
        SaveScreenProbe(window, "remote-tool-context-menu.png");
        menu.Close();
        tv.Model.Settings = new RemoteToolSettings();
        (string? tvTransfer, _) = await LaunchFakeAsync(window, tv, RemoteToolMode.FileTransfer, report);
        (string? adTransfer, _) = await LaunchFakeAsync(window, ad, RemoteToolMode.FileTransfer, report);
        Check("\"Dateiübertragung\" in the context menu starts TeamViewer -m fileTransfer and AnyDesk --file-transfer",
            menuItem && tvTransfer?.EndsWith("-i 123456789 -m fileTransfer", StringComparison.Ordinal) == true
            && adTransfer?.EndsWith("987654321 --file-transfer --with-password", StringComparison.Ordinal) == true);
        await SecretClipboard.ClearPendingAsync();
    }

    /// <summary>Launches through the real launcher, reads the command line back like Get-CimInstance does, and ends the fake tool.</summary>
    private static async Task<(string? CommandLine, string[] Report)> LaunchFakeAsync(MainWindow window, ConnectionItem connection, RemoteToolMode? mode, string report)
    {
        File.Delete(report);
        using Process? process = await RemoteToolLauncher.LaunchAsync(connection, window, mode);
        if (process is null)
            return (null, []);
        try
        {
            await WaitFor(() => File.Exists(report), TimeSpan.FromSeconds(15));
            await Task.Delay(200);
            string? commandLine = await Task.Run(() => CommandLineOf(process.Id));
            string[] lines = File.Exists(report) ? await File.ReadAllLinesAsync(report) : [];
            return (commandLine, lines);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
        }
    }

    /// <summary>The process's command line from <c>Get-CimInstance Win32_Process</c>, as any user on the machine can read it.</summary>
    private static string? CommandLineOf(int processId)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", $"(Get-CimInstance Win32_Process -Filter 'ProcessId={processId}').CommandLine" })
            start.ArgumentList.Add(argument);
        using Process powershell = Process.Start(start)!;
        string output = powershell.StandardOutput.ReadToEnd();
        powershell.WaitForExit();
        return output.Trim() is { Length: > 0 } line ? line : null;
    }

    private static string Mask(string? text, string secret) => (text ?? "(none)").Replace(secret, RemoteToolCommand.Mask, StringComparison.Ordinal);

    private static async Task RunTeamViewerSyncChecksAsync(MainWindow window, string tokenKey)
    {
        AppHost app = AppHost.Current;
        string alias = "Selftest Buchhaltung";
        var fake = new FakeTeamViewerApi(alias);
        string previousKey = TeamViewerAccount.TokenKey;
        TeamViewerApiClient previousClient = TeamViewerAccount.Client;
        TeamViewerAccount.TokenKey = tokenKey;
        TeamViewerAccount.Client = new TeamViewerApiClient(new HttpClient(fake));
        int before = app.Library.Connections.Count;
        try
        {
            await TeamViewerAccount.SaveTokenAsync("selftest-token");
            Task dialog = RemoteToolsDialog.ShowAsync();
            await Settle(900);
            RemoteToolsDialog content = RemoteToolsDialog.OpenContent!;
            await content.SyncNowAsync();
            await Settle(400);
            SaveRenderProbe(window, "remote-tools-dialog.png");
            bool success = content.SyncResultBar is { IsOpen: true, Severity: FAInfoBarSeverity.Success };
            Log($"  sync: {content.SyncResultBar.Message}");
            Check("TeamViewer sync (fake Web API) adds the account's devices with alias", success
                && window.Connections.Any(c => c.Name == alias && c.Model.Host == "111222333")
                && window.Connections.Any(c => c.Name == "Selftest Lager" && c.Model.Host == "444555666")
                && fake.Tokens.All(t => t == "selftest-token"));

            // Local notes and tags, then a rename in TeamViewer and a second sync.
            ConnectionItem synced = window.Connections.First(c => c.Model.Host == "111222333");
            synced.Model.Notes = "Selftest-Notiz";
            synced.Model.Tags = ["selftest-tag"];
            window.Save(synced);
            fake.Alias = alias + " (neu)";
            await content.SyncNowAsync();
            Connection stored = new Core.Library.ConnectionLibrary(app.Library.Store).Connections.First(c => c.Id == synced.Id);
            Check("Second sync changes the alias, keeps local notes and tags",
                synced.Name == fake.Alias && stored.Name == fake.Alias && stored.Notes == "Selftest-Notiz" && stored.Tags.SequenceEqual(["selftest-tag"]));
            RemoteToolsDialog.OpenDialog?.Hide();
            await dialog;

            // Online status comes from the API through the TeamViewer status probe.
            ConnectionItem lager = window.Connections.First(c => c.Model.Host == "444555666");
            await app.Status.CheckAsync([synced.Model, lager.Model]);
            await Settle(300);
            window.Tiles.ScrollIntoView(synced);
            await Settle(300);
            SaveRenderProbe(window, "teamviewer-sync-tiles.png");
            Check($"Synced tiles show the API online status (\"{synced.StatusLabel}\" / \"{lager.StatusLabel}\")",
                synced.Status == OnlineStatus.Online && lager.Status == OnlineStatus.Offline);
        }
        catch (TeamViewerApiException ex)
        {
            Fail($"TeamViewer sync threw {ex.Error}: {ex.Message}");
        }
        finally
        {
            RemoteToolsDialog.OpenDialog?.Hide();
            foreach (ConnectionItem item in window.Connections.Where(c => c.Model.Settings is RemoteToolSettings { Source: RemoteToolSource.TeamViewerApi }).ToList())
                await window.RemoveConnectionAsync(item);
            RemoveEmptyGroup(Strings.RemoteTool_Sync_NewGroup);
            await TeamViewerAccount.DeleteTokenAsync();
            TeamViewerAccount.TokenKey = previousKey;
            TeamViewerAccount.Client = previousClient;
        }

        Check("Sync clean-up leaves the library as before", app.Library.Connections.Count == before && !WindowsCredentialSource.Exists(tokenKey));
    }

    private static async Task RunAnyDeskCsvChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        string csv = Path.Combine(AppPaths.Spike, "selftest-anydesk.csv");
        await File.WriteAllTextAsync(csv, "ID;Alias;Comment\r\n111 000 111;selftest-kasse@ad;Laden\r\n222000222;;Lager\r\n");
        int before = app.Library.Connections.Count;
        try
        {
            Task dialog = RemoteToolsDialog.ShowAsync();
            await Settle(900);
            RemoteToolsDialog content = RemoteToolsDialog.OpenContent!;
            await content.ImportCsvAsync(csv);
            string first = content.CsvResultBar.Message ?? "";
            await content.ImportCsvAsync(csv);
            string second = content.CsvResultBar.Message ?? "";
            content.CsvResultBar.BringIntoView();
            await Settle(300);
            SaveRenderProbe(window, "anydesk-csv-import.png");
            RemoteToolsDialog.OpenDialog?.Hide();
            await dialog;
            Log($"  csv: {first} / {second}");
            Check("AnyDesk CSV import adds 2 connections in \"Import aus AnyDesk\"; importing again skips them",
                app.Library.Connections.Count == before + 2
                && window.Connections.Any(c => c.Name == "selftest-kasse@ad" && c.Model.Host == "111000111" && c.GroupLabel == Strings.RemoteTool_Csv_Group)
                && first == string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Csv_Result, 2, 0, 0)
                && second == string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Csv_Result, 0, 2, 0));
        }
        finally
        {
            RemoteToolsDialog.OpenDialog?.Hide();
            foreach (ConnectionItem item in window.Connections.Where(c => c.Model.Settings is RemoteToolSettings { Source: RemoteToolSource.AnyDeskCsv }).ToList())
                await window.RemoveConnectionAsync(item);
            RemoveEmptyGroup(Strings.RemoteTool_Csv_Group);
            File.Delete(csv);
        }
    }

    private static async Task RunToolPathChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        string key = RemoteToolLocator.InfoFor(ConnectionType.RustDesk).SettingKey;
        string? before = app.Library.Store.GetSetting(key);
        try
        {
            Task dialog = RemoteToolsDialog.ShowAsync(ConnectionType.RustDesk);
            await Settle(900);
            RemoteToolsDialog content = RemoteToolsDialog.OpenContent!;
            content.SetPath(ConnectionType.RustDesk, @"C:\does-not-exist\rustdesk.exe");
            bool rejected = !content.TrySavePaths() && app.Library.Store.GetSetting(key) == before;
            await Settle(300);
            SaveRenderProbe(window, "remote-tools-path-invalid.png");
            content.SetPath(ConnectionType.RustDesk, Environment.ProcessPath!);
            bool saved = content.TrySavePaths() && app.Library.Store.GetSetting(key) == Environment.ProcessPath;
            ToolLocation location = new RemoteToolLocator(app.Library.Store.GetSetting).Locate(ConnectionType.RustDesk);
            RemoteToolsDialog.OpenDialog?.Hide();
            await dialog;
            Check("Tool path setting: a missing .exe is rejected, an existing one is saved and wins over detection",
                rejected && saved && location.FromSetting && location.Path == Environment.ProcessPath);
        }
        finally
        {
            RemoteToolsDialog.OpenDialog?.Hide();
            app.Library.Store.SetSetting(key, before);
        }
    }

    private static void RemoveEmptyGroup(string name)
    {
        Core.Library.ConnectionLibrary library = AppHost.Current.Library;
        foreach (Core.Model.ConnectionGroup group in library.Groups.Where(g => g.ParentId is null && g.Name == name).ToList())
        {
            List<Core.Model.ConnectionGroup> subtree = [.. library.Groups.Where(g => library.AncestorsOf(g.Id).Contains(group))];
            if (library.Connections.Any(c => subtree.Any(g => g.Id == c.GroupId)))
                continue;
            foreach (Core.Model.ConnectionGroup g in subtree.OrderByDescending(g => library.AncestorsOf(g.Id).Count()))
                library.RemoveGroup(g);
        }
    }

    /// <summary>A locator that "finds" every tool at <paramref name="path"/> (or nothing), ignoring settings and the real installation.</summary>
    private static RemoteToolLocator FakeLocator(string? path) =>
        new(_ => null, p => path is not null && p == path, _ => path is null ? [] : [path]);

    /// <summary>Answers /devices and /groups like the TeamViewer Web API.</summary>
    private sealed class FakeTeamViewerApi(string alias) : HttpMessageHandler
    {
        public string Alias { get; set; } = alias;
        public List<string> Tokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.Authorization?.Parameter ?? "");
            string json = request.RequestUri!.AbsolutePath.EndsWith("/groups", StringComparison.Ordinal)
                ? """{"groups":[{"id":"g1","name":"Selftest Kunde"}]}"""
                : $$"""
                  {"devices":[
                    {"remotecontrol_id":"r111222333","device_id":"dSelftest1","alias":"{{Alias}}","groupid":"g1","online_state":"Online","description":"Fake API"},
                    {"remotecontrol_id":"r444555666","device_id":"dSelftest2","alias":"Selftest Lager","groupid":"g1","online_state":"Offline"}
                  ]}
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
