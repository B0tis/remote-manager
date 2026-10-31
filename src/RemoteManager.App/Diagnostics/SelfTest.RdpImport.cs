using System.Text;
using Avalonia.Controls;
using MSTSCLib;
using RemoteManager.App.Connections.Rdp;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.ImportExport;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Self-test checks of the RDP start parameters (settings editor, embedded client, mstsc start), the
/// group-logon inheritance in the editor, and import/export. Credentials only under
/// "RemoteManager:selftest-*" and TERMSRV/rm-selftest-*.invalid; imported test data is removed again.
/// </summary>
internal static partial class SelfTest
{
    private static readonly RdpSettings FullRdpSettings = new()
    {
        Display = RdpDisplayMode.FixedResolution,
        DesktopWidth = 1600,
        DesktopHeight = 900,
        UseMultimon = true,
        Keyboard = RdpKeyboardMode.Local,
        EnableNla = false,
        AuthenticationLevel = 1,
        AdminSession = true,
        RestrictedAdmin = true,
        GatewayHost = "rm-selftest-gateway.invalid",
        GatewayUsage = RdpGatewayUsage.Always,
        Redirections = new RdpRedirections { Clipboard = false, Drives = true, Printers = true, SmartCards = true, Ports = true, Microphone = true, Audio = RdpAudioMode.None },
        StartProgram = @"C:\Windows\System32\notepad.exe",
        WorkingDirectory = @"C:\Windows",
    };

    private static async Task RunRdpImportChecksAsync(MainWindow window)
    {
        RunRdpEditorChecks(window);
        await RunEmbeddedStartParameterChecksAsync(window);
        await RunExternalStartChecksAsync();
        await RunInheritanceEditorChecksAsync(window);
        await RunImportExportChecksAsync(window);
    }

    /// <summary>Every start parameter survives load and save in the RDP part of the editor.</summary>
    private static void RunRdpEditorChecks(MainWindow window)
    {
        var editor = new RdpSettingsEditor();
        editor.Load(FullRdpSettings);
        Check("RDP editor: every start parameter survives load and save", editor.Validate() is null && editor.Commit() == FullRdpSettings);

        editor.Load(FullRdpSettings with { DesktopWidth = 10 });
        Check("RDP editor: an invalid fixed resolution is refused", editor.Validate() is { Length: > 0 });

        // Screenshot of the whole RDP part, all sections open (rendered, so it does not need to fit the screen).
        editor.Load(FullRdpSettings with { RestrictedAdmin = false, RemoteCredentialGuard = true, GatewayUsesConnectionCredentials = false, GatewayUserName = @"SELFTEST\gateway" });
        editor.ExpandAll();
        var host = new Window
        {
            Title = "RDP-Startparameter",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            Content = new Border { Padding = new Avalonia.Thickness(20), Child = editor },
            ShowInTaskbar = false,
        };
        host.Show(window);
        host.UpdateLayout();
        SaveRenderProbe(host, "rdp-start-parameters.png");
        host.Close();
    }

    /// <summary>Applies the parameters to a real (unconnected) RDP client and reads them back.</summary>
    private static async Task RunEmbeddedStartParameterChecksAsync(MainWindow window)
    {
        ConnectionItem item = window.CreateItem(new Connection { Name = "Selftest Startparameter", Host = "rm-selftest-rdp.invalid", Port = 3390, Settings = FullRdpSettings });
        var session = (SessionWindow)window.OpenSession(item)!;
        try
        {
            if (!await WaitFor(() => session.Rdp.IsClientReady, TimeSpan.FromSeconds(20)))
            {
                Fail("Embedded start parameters: RDP client not ready");
                return;
            }

            var logon = new RdpLogon("rm-selftest-rdp.invalid", 3390, "selftest", "SELFTEST", "", "");
            RdpClientConfigurator.Apply(session.Rdp, FullRdpSettings, logon, FullRdpSettings.DesktopWidth, FullRdpSettings.DesktopHeight);

            IMsRdpClient10 client = session.Rdp.GetClient<IMsRdpClient10>();
            IMsRdpClientAdvancedSettings8 advanced = client.AdvancedSettings9;
            IMsRdpClientTransportSettings4 transport = client.TransportSettings4;
            var read = new Dictionary<string, (object Actual, object Expected)>
            {
                ["server"] = (client.Server.ToString(), "rm-selftest-rdp.invalid"),
                ["user"] = (client.UserName.ToString(), "selftest"),
                ["domain"] = (client.Domain.ToString(), "SELFTEST"),
                ["port"] = (advanced.RDPPort, 3390),
                ["size"] = ((client.DesktopWidth, client.DesktopHeight), (1600, 900)),
                ["NLA"] = (advanced.EnableCredSspSupport, false),
                ["authentication level"] = (advanced.AuthenticationLevel, 1u),
                ["admin"] = (advanced.ConnectToAdministerServer, true),
                ["clipboard"] = (advanced.RedirectClipboard, false),
                ["drives"] = (advanced.RedirectDrives, true),
                ["printers"] = (advanced.RedirectPrinters, true),
                ["smart cards"] = (advanced.RedirectSmartCards, true),
                ["ports"] = (advanced.RedirectPorts, true),
                ["audio"] = (advanced.AudioRedirectionMode, 2u),
                ["microphone"] = (advanced.AudioCaptureRedirectionMode, true),
                ["gateway"] = (transport.GatewayHostname.ToString(), "rm-selftest-gateway.invalid"),
                ["gateway usage"] = (transport.GatewayUsageMethod, 1u),
                ["start program"] = (client.SecuredSettings3.StartProgram.ToString(), FullRdpSettings.StartProgram),
            };
            List<string> wrong = [.. read.Where(r => !Equals(r.Value.Actual, r.Value.Expected)).Select(r => $"{r.Key}={r.Value.Actual} (expected {r.Value.Expected})")];
            Check($"Embedded start parameters reach the RDP client ({read.Count} values{(wrong.Count > 0 ? ": " + string.Join(", ", wrong) : "")})", wrong.Count == 0);

            // And back to the defaults on the same client: a reconnect after an edit must not keep old values.
            RdpClientConfigurator.Apply(session.Rdp, new RdpSettings(), logon with { Port = 3389 }, 0, 0);
            Check("Default start parameters reset the reused client (NLA on, admin off, no gateway, clipboard on)",
                advanced.EnableCredSspSupport && !advanced.ConnectToAdministerServer && transport.GatewayUsageMethod == 0 && advanced.RedirectClipboard && !advanced.RedirectDrives);
        }
        finally
        {
            session.Close();
            await Settle(300);
        }
    }

    /// <summary>The mstsc start writes an .rdp file without the password and a temporary TERMSRV entry.</summary>
    private static async Task RunExternalStartChecksAsync()
    {
        string id = TestId("mstsc"), host = $"rm-selftest-mstsc-{RunId}.invalid";
        string secret = $"Rm-{Guid.NewGuid():N}";
        WindowsCredentialSource.Delete(id);
        TermsrvCredential.Delete(host);
        WindowsCredentialSource.Write(id, secret);
        try
        {
            var connection = new Connection
            {
                Id = id, Name = "Selftest mstsc", Host = host, Port = 3390, UserName = "selftest", Domain = "SELFTEST",
                Credential = new CredentialRef(WindowsCredentialSource.SourceId, id),
                Settings = FullRdpSettings with { OpenExternally = true },
            };
            ExternalRdpStart start = await ExternalRdpLauncher.PrepareAsync(connection, AppHost.Current.Credentials);
            string text = File.ReadAllText(start.RdpPath, Encoding.Unicode);
            Log($"  .rdp file {start.RdpPath}, switches {string.Join(' ', start.Arguments.Skip(1))}");
            Check("External start: the .rdp file has address, user, and options",
                text.Contains($"full address:s:{host}:3390", StringComparison.Ordinal) && text.Contains(@"username:s:SELFTEST\selftest", StringComparison.Ordinal)
                && text.Contains("enablecredsspsupport:i:0", StringComparison.Ordinal) && text.Contains("gatewayhostname:s:rm-selftest-gateway.invalid", StringComparison.Ordinal)
                && text.Contains("drivestoredirect:s:*", StringComparison.Ordinal));
            Check("External start: no password in the .rdp file or on the command line",
                !text.Contains(secret, StringComparison.Ordinal) && !text.Contains("password", StringComparison.OrdinalIgnoreCase)
                && start.Arguments.All(a => !a.Contains(secret, StringComparison.Ordinal)));
            Check("External start: /admin and /restrictedAdmin switches", start.Arguments.Contains("/admin") && start.Arguments.Contains("/restrictedAdmin"));
            Check($"External start: password handed to mstsc as {TermsrvCredential.TargetFor(host)} in the Credential Manager",
                start.WroteCredential && TermsrvCredential.Exists(host));

            start.CleanUp();
            Check("External start: clean-up removes the .rdp file and the TERMSRV entry", !File.Exists(start.RdpPath) && !TermsrvCredential.Exists(host));
        }
        finally
        {
            WindowsCredentialSource.Delete(id);
            TermsrvCredential.Delete(host);
        }
    }

    /// <summary>The editor shows the group logon a connection inherits, and edits it in place.</summary>
    private static async Task RunInheritanceEditorChecksAsync(MainWindow window)
    {
        string secretId = TestId("group");
        AppHost app = AppHost.Current;
        WindowsCredentialSource.Delete(secretId);
        WindowsCredentialSource.Write(secretId, $"Rm-{Guid.NewGuid():N}");
        ConnectionGroup group = app.Library.EnsureGroupPath($"Selftest Gruppe {Guid.NewGuid():N}"[..24])!;
        group.UserName = "gruppen-admin";
        group.Domain = "SELFTEST";
        group.Credential = new CredentialRef(WindowsCredentialSource.SourceId, secretId);
        app.Library.UpdateGroup(group);
        try
        {
            var connection = new Connection { Name = "Selftest geerbt", Host = "rm-selftest-inherit.invalid", GroupId = group.Id };
            Task<bool> editing = ConnectionEditor.ShowAsync(connection, isNew: true);
            await Settle(800);
            ConnectionEditor editor = ConnectionEditor.OpenEditor!;
            string inherited = editor.InheritanceHint.Text ?? "";
            Check($"Editor shows the inherited group logon (\"{inherited}\")",
                editor.InheritanceHint.IsVisible && inherited.Contains(@"SELFTEST\gruppen-admin", StringComparison.Ordinal)
                && inherited.Contains(Resources.Strings.Editor_InheritedWithPassword.Trim(), StringComparison.Ordinal));
            SaveRenderProbe(window, "editor-inherited-logon.png");

            editor.User.Text = "eigener-user";
            await Settle(200);
            Check($"An own user replaces the group logon, and the hint says so (\"{editor.InheritanceHint.Text}\")",
                editor.InheritanceHint.Text?.Contains("ersetzt", StringComparison.Ordinal) == true);
            editor.User.Text = "";

            editor.Group.Text = "";
            await Settle(200);
            bool hiddenWithoutGroup = !editor.InheritanceHint.IsVisible && !editor.GroupLogon.IsVisible;
            editor.Group.Text = app.Library.PathOf(group.Id);
            await Settle(200);
            Check("The hint follows the group field (gone without a group, back with it)", hiddenWithoutGroup && editor.InheritanceHint.IsVisible);

            editor.GroupLogon.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Settle(600);
            GroupLogonPanel? panel = editor.OpenGroupLogon;
            Check("\"Anmeldung der Gruppe\" opens the group logon", panel is not null);
            if (panel is not null)
            {
                // The flyout is a native popup, which a render does not include: render the same panel in its own window.
                var shot = new Window
                {
                    Title = "Anmeldung der Gruppe",
                    Width = 380,
                    SizeToContent = SizeToContent.Height,
                    ShowInTaskbar = false,
                    Content = new Border { Padding = new Avalonia.Thickness(20), Child = new GroupLogonPanel(group) },
                };
                shot.Show(window);
                shot.UpdateLayout();
                SaveRenderProbe(shot, "editor-group-logon.png");
                shot.Close();
                panel.User.Text = "neu-admin";
                bool applied = await panel.ApplyAsync();
                await Settle(300);
                Check("Changing the group logon in the flyout saves the group and updates the hint",
                    applied && group.UserName == "neu-admin" && group.Credential is not null
                    && editor.InheritanceHint.Text?.Contains(@"SELFTEST\neu-admin", StringComparison.Ordinal) == true);
            }

            ConnectionEditor.OpenDialog?.Hide();
            Check("Cancel adds nothing", !await editing && !app.Library.Contains(connection));
        }
        finally
        {
            app.Library.RemoveGroup(group);
            WindowsCredentialSource.Delete(secretId);
            window.SyncWithLibrary();
        }
    }

    private const string SelftestRdg = """
        <?xml version="1.0" encoding="utf-8"?>
        <RDCMan programVersion="2.93" schemaVersion="3">
          <file>
            <properties><expanded>True</expanded><name>Selftest RDCMan</name></properties>
            <logonCredentials inherit="None"><profileName scope="Local">Custom</profileName><userName>rz-admin</userName><password /><domain>SELFTEST</domain></logonCredentials>
            <group>
              <properties><expanded>True</expanded><name>Kunde X</name></properties>
              <server>
                <properties><displayName>st-dc01</displayName><name>rm-selftest-dc01.invalid</name><comment>Domain Controller (Selftest)</comment></properties>
                <logonCredentials inherit="FromParent" />
                <connectionSettings inherit="None"><connectToConsole>True</connectToConsole><startProgram /><workingDir /><port>3390</port><loadBalanceInfo /></connectionSettings>
              </server>
              <server>
                <properties><displayName>st-app01</displayName><name>rm-selftest-app01.invalid</name></properties>
                <logonCredentials inherit="None"><profileName scope="Local">Custom</profileName><userName>SELFTEST\app-admin</userName><password /><domain /></logonCredentials>
              </server>
            </group>
          </file>
        </RDCMan>
        """;

    private const string SelftestPrma = """
        [ { "Protocol": "RDP", "DisplayName": "st-1remote", "Tags": [ "selftest", "#prod" ], "Address": "rm-selftest-1remote.invalid", "Port": "3391", "UserName": "SELFTEST\\jdoe" } ]
        """;

    /// <summary>Import through the dialog (RDCMan, 1Remote), summary, overview, JSON backup; removes the data again.</summary>
    private static async Task RunImportExportChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        const string target = "Selftest Import";
        string folder = Path.Combine(Path.GetTempPath(), $"rm-selftest-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string rdg = Path.Combine(folder, "selftest.rdg"), prma = Path.Combine(folder, "selftest.prma");
        File.WriteAllText(rdg, SelftestRdg);
        File.WriteAllText(prma, SelftestPrma);
        int before = window.Connections.Count;
        try
        {
            // The "more" menu with import and export.
            Check("The \"more\" button offers import and JSON backup",
                window.More.Flyout is MenuFlyout { ItemsSource: { } entries }
                && entries.OfType<MenuItem>().Select(m => m.Header as string).Take(2).SequenceEqual([Strings.Action_Import, Strings.Action_ExportBackup]));

            Task importing = ImportExportCommands.ShowImportAsync(window);
            await Settle(800);
            ImportView view = ImportExportCommands.OpenView!;
            view.SelectFormat("rdcman");
            view.SetFiles([rdg]);
            view.TargetGroup = target;
            await Settle(300);
            SaveRenderProbe(window, "import-dialog.png");
            Check($"Import dialog lists the formats ({string.Join(", ", app.Importers.Select(i => i.Id))})",
                app.Importers.Any(i => i is OneRemoteImporter) && app.Importers.Any(i => i is RdcManImporter) && app.Importers.Any(i => i is MRemoteNgImporter)
                && app.Importers.Any(i => i is RdpFileImporter) && app.Importers.Any(i => i is JsonBackupImporter));

            view.SelectFormat("1remote");
            view.SetFiles([rdg]);
            Check("Import refuses a file that does not match the format", await view.RunAsync() is null);

            view.SelectFormat("rdcman");
            view.SetFiles([rdg]);
            view.TargetGroup = target;
            ImportSummary? rdcman = await view.RunAsync();
            ImportExportCommands.OpenDialog?.Hide();
            await importing;
            window.SyncWithLibrary();
            Check($"RDCMan import adds 2 connections and 2 groups ({rdcman?.AddedConnections}, {rdcman?.AddedGroups})",
                rdcman is { AddedConnections: 2, AddedGroups: 2 } && window.Connections.Count == before + 2);

            Task summaryShown = ImportExportCommands.ShowSummaryAsync(rdcman! with { Warnings = ["Beispiel: Intelligente Gruppe „Alle Drucker“ übersprungen (ihre Regeln haben hier keine Entsprechung)."] });
            await Settle(600);
            SaveRenderProbe(window, "import-summary.png");
            ImportExportCommands.OpenSummary?.Hide();
            await summaryShown;

            ConnectionItem? dc = window.Connections.FirstOrDefault(c => c.Name == "st-dc01");
            Check("Imported RDCMan server: host, port, group path, admin session, inherited file logon",
                dc is not null && dc.Model.Host == "rm-selftest-dc01.invalid" && dc.Model.EffectivePort == 3390
                && app.Library.PathOf(dc.Model.GroupId) == $"{target} / Selftest RDCMan / Kunde X"
                && dc.Model.SettingsAs<RdpSettings>().AdminSession
                && app.Credentials.Resolve(dc.Model) is { UserName: "rz-admin", Domain: "SELFTEST", IsInherited: true });

            Task second = ImportExportCommands.ShowImportAsync(window);
            await Settle(500);
            ImportView again = ImportExportCommands.OpenView!;
            again.SelectFormat("1remote");
            again.SetFiles([prma]);
            again.TargetGroup = target;
            ImportSummary? oneRemote = await again.RunAsync();
            again.SelectFormat("rdcman");
            again.SetFiles([rdg]);
            ImportSummary? repeat = await again.RunAsync();
            ImportExportCommands.OpenDialog?.Hide();
            await second;
            window.SyncWithLibrary();
            ConnectionItem? tagged = window.Connections.FirstOrDefault(c => c.Name == "st-1remote");
            Check("1Remote import keeps tags, port, and user; repeating the RDCMan import adds nothing",
                oneRemote?.AddedConnections == 1 && tagged is not null && tagged.Model.Tags.SequenceEqual(["selftest", "#prod"])
                && tagged.Model.EffectivePort == 3391 && tagged.Model.UserName == "jdoe" && tagged.Model.Domain == "SELFTEST"
                && repeat is { AddedConnections: 0, SkippedDuplicates: 2 });

            await Settle(500);
            SaveRenderProbe(window, "overview-after-import.png");

            // JSON backup of the whole library: everything in it, no secret.
            using var stream = new MemoryStream();
            await ImportExportCommands.ExportToAsync(app.Exporters.First(e => e is JsonBackupExporter), stream);
            string json = Encoding.UTF8.GetString(stream.ToArray());
            ImportResult restored = JsonBackup.FromJson(System.Text.Json.Nodes.JsonNode.Parse(json)!);
            Check($"JSON backup holds every connection and group ({restored.Connections.Count}/{app.Library.Connections.Count}, {restored.Groups.Count}/{app.Library.Groups.Count}) and no password field",
                restored.Connections.Count == app.Library.Connections.Count && restored.Groups.Count == app.Library.Groups.Count
                && !json.Contains("\"password\"", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Each removal rebuilds the sidebar. FANavigationView (FluentAvalonia 3.1) re-posts its selection
            // animation forever when the sidebar is rebuilt again within the same dispatcher pass, so let it settle.
            foreach (ConnectionItem item in window.Connections.Where(c => c.Name is "st-dc01" or "st-app01" or "st-1remote").ToList())
            {
                await window.RemoveConnectionAsync(item);
                await Settle(100);
            }

            foreach (ConnectionGroup group in app.Library.Groups.Where(g => app.Library.PathOf(g.Id).StartsWith(target, StringComparison.Ordinal))
                .OrderByDescending(g => app.Library.AncestorsOf(g.Id).Count()).ToList())
            {
                app.Library.RemoveGroup(group);
            }

            window.SyncWithLibrary();
            Directory.Delete(folder, recursive: true);
            Check("Imported self-test data removed again", window.Connections.Count == before && !app.Library.GroupPaths().Any(p => p.StartsWith(target, StringComparison.Ordinal)));
        }
    }
}
