using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections.Rdp;
using RemoteManager.App.Resources;
using RemoteManager.App.Updates;
using RemoteManager.App.Views;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Model;
using RemoteManager.Core.Storage;
using RemoteManager.Core.Updates;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Updates: version in About, source and token settings, the startup throttle, the notice, the question while a
/// session is open, the backup before applying. A fake updater stands in for Velopack (a dev build is not
/// installed); backups go to a temporary folder, the token to a "selftest-" Credential Manager key.
/// </summary>
internal static partial class SelfTest
{
    private sealed class FakeUpdater(string? offer) : IAppUpdater
    {
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Applied { get; private set; }
        public UpdateSource? Source { get; set; }
        public string? Token { get; set; }

        public bool IsInstalled => true;
        public string? InstallFolder => @"C:\Users\selftest\AppData\Local\Botis.RemoteManager";

        public Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            return Task.FromResult(offer is null ? null : new AvailableUpdate(offer, "fake"));
        }

        public async Task DownloadAsync(AvailableUpdate update, Action<int> progress, CancellationToken cancellationToken = default)
        {
            progress(40);
            await Task.Delay(150, cancellationToken);
            progress(100);
            Downloads++;
        }

        public void ApplyAndRestart(AvailableUpdate update) => Applied++;
    }

    private static async Task RunUpdateChecksAsync(MainWindow window)
    {
        Log("--- Updates ---");
        AppHost app = AppHost.Current;
        string informational = typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Log($"  version {AppVersion.Current}, commit {AppVersion.Commit ?? "-"}, informational {informational}");
        Check("App version is the assembly version without the commit suffix",
            AppVersion.Current.Length > 0 && informational.StartsWith(AppVersion.Current + "+", StringComparison.Ordinal) || informational == AppVersion.Current);
        Check("A dev build is not installed and cannot update itself", !AppUpdates.IsInstalled && AppUpdates.InstallFolder is null);

        await window.CheckForUpdatesAsync(startup: false);
        Check("\"Nach Updates suchen\" in a dev build says it cannot update", window.ToastBar.IsOpen && window.ToastBar.Message == Strings.Update_NotInstalled);
        Check("The \"more\" menu offers the update check and About",
            window.MoreMenuItems().Select(i => i.Header as string).Intersect([Strings.Update_CheckMenu, Strings.About_Menu]).Count() == 2);

        string? sourceBefore = app.Library.GetSetting(AppUpdates.SourceSetting);
        string? lastCheckBefore = app.Library.GetSetting(AppUpdates.LastCheckSetting);
        string tokenKeyBefore = AppUpdates.TokenKey;
        Func<UpdateSource, string?, IAppUpdater> createBefore = AppUpdates.CreateUpdater;
        string backupsBefore = AppUpdates.BackupFolder;
        string temp = Path.Combine(Path.GetTempPath(), TestId("updates"));
        string releases = Path.Combine(temp, "releases");
        Directory.CreateDirectory(releases);
        AppUpdates.TokenKey = TestId("update-token");
        AppUpdates.BackupFolder = Path.Combine(temp, "backups");
        // Not a real token; only proves where the value goes.
        string token = $"ghp_selftest{RunId}";
        ConnectionItem? session = null;
        try
        {
            await RunAboutChecksAsync(window, releases, token);
            await RunUpdateFlowChecksAsync(window, releases, token, s => session = s);
        }
        finally
        {
            if (session is not null)
                window.SessionWindows.FirstOrDefault(w => ((Connections.ISessionWindow)w).Connection == session)?.Close();
            window.DismissUpdate();
            await app.Credentials.DefaultSource.DeleteAsync(AppUpdates.TokenKey);
            app.Library.SetSetting(AppUpdates.SourceSetting, sourceBefore);
            app.Library.SetSetting(AppUpdates.LastCheckSetting, lastCheckBefore);
            AppUpdates.TokenKey = tokenKeyBefore;
            AppUpdates.CreateUpdater = createBefore;
            AppUpdates.BackupFolder = backupsBefore;
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task RunAboutChecksAsync(MainWindow window, string releases, string token)
    {
        AppHost app = AppHost.Current;
        Task about = AboutView.ShowAsync(window);
        await Settle(800);
        AboutView? view = AboutView.OpenContent;
        Check("About shows the version", view is not null && view.VersionLabel.Contains(AppVersion.Current, StringComparison.Ordinal));
        SaveScreenProbe(window, "about.png");
        if (view is null)
            return;

        string? sourceBefore = app.Library.GetSetting(AppUpdates.SourceSetting);
        view.Enter("releases", "");
        Check("About rejects a relative folder as update source and keeps the old one",
            !await view.TrySaveAsync() && view.SourceError == Strings.About_SourceInvalid && app.Library.GetSetting(AppUpdates.SourceSetting) == sourceBefore);

        view.Enter(releases, token);
        bool saved = await view.TrySaveAsync();
        Check("About saves a folder as update source", saved && AppUpdates.Source == new UpdateSource(UpdateSourceKind.Folder, releases));
        Check("The GitHub token goes to the Credential Manager, not into the database",
            await AppUpdates.HasTokenAsync() && WindowsCredentialSource.Read(AppUpdates.TokenKey) == token && !FileContains(app.StoreReport.DatabasePath, token));
        AboutView.OpenDialog?.Hide();
        await about;
    }

    private static async Task RunUpdateFlowChecksAsync(MainWindow window, string releases, string token, Action<ConnectionItem> opened)
    {
        AppHost app = AppHost.Current;
        var fake = new FakeUpdater("9.9.9");
        AppUpdates.CreateUpdater = (source, t) =>
        {
            fake.Source = source;
            fake.Token = t;
            return fake;
        };

        // Startup: checks once, then not again within the interval.
        app.Library.SetSetting(AppUpdates.LastCheckSetting, null);
        await window.CheckForUpdatesAsync(startup: true);
        Check("The startup check runs when none ran yet and shows the notice",
            fake.Checks == 1 && window.UpdateNotice.IsOpen && window.PendingUpdate?.Version == "9.9.9");
        Check("The notice names the new and the installed version, with \"Jetzt aktualisieren\" and \"Später\"",
            window.UpdateNotice.Message == string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Update_Available, "9.9.9", AppVersion.Current)
            && (string?)window.UpdateNowButtonControl.Content == Strings.Update_Now && (string?)window.UpdateLaterButtonControl.Content == Strings.Update_Later);
        Check("A folder source gets no token", fake.Source?.Kind == UpdateSourceKind.Folder && fake.Token is null);
        window.Activate();
        await Settle(700);
        SaveScreenProbe(window, "update-notice.png");

        window.UpdateLaterButtonControl.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle(200);
        Check("\"Später\" hides the notice without downloading", !window.UpdateNotice.IsOpen && fake.Downloads == 0 && fake.Applied == 0);
        await window.CheckForUpdatesAsync(startup: true);
        Check("A second start within the interval does not check again", fake.Checks == 1 && !window.UpdateNotice.IsOpen);

        // GitHub (the default) gets the stored token.
        AppUpdates.TrySetSource("");
        await window.CheckForUpdatesAsync(startup: false);
        Check("The GitHub source gets the token from the Credential Manager", fake.Source == UpdateSource.Default && fake.Token == token && fake.Checks == 2);

        // An open session: the update asks first; "Abbrechen" keeps everything as it is.
        ConnectionItem session = window.CreateItem(new Connection { Name = "Selftest Update", Host = "127.0.0.1", Description = "Open session during an update" });
        opened(session);
        var sessionWindow = (SessionWindow)window.OpenSession(session)!;
        await Settle(800);
        window.Activate();
        Task applying = window.ApplyUpdateAsync();
        bool asked = await WaitFor(() => window.UpdateSessionsDialog is not null, TimeSpan.FromSeconds(5));
        await Settle(400);
        SaveScreenProbe(window, "update-sessions.png");
        window.UpdateSessionsDialog?.Hide(FAContentDialogResult.None);
        await applying;
        Check("With a session open, \"Jetzt aktualisieren\" asks first; cancel keeps the session and applies nothing",
            asked && fake.Downloads == 1 && fake.Applied == 0 && window.SessionWindows.Contains(sessionWindow) && window.UpdateNotice.IsOpen
            && DatabaseBackup.List(AppUpdates.BackupFolder).Count == 0);

        // Confirmed: the session ends, the database is backed up, then the update is applied.
        int connections = app.Library.Connections.Count;
        applying = window.ApplyUpdateAsync();
        await WaitFor(() => window.UpdateSessionsDialog is not null, TimeSpan.FromSeconds(5));
        window.UpdateSessionsDialog?.Hide(FAContentDialogResult.Primary);
        await applying;
        IReadOnlyList<string> backups = DatabaseBackup.List(AppUpdates.BackupFolder);
        Check("Confirmed: the session closes, the database is backed up, then the update is applied",
            fake.Applied == 1 && !window.SessionWindows.Contains(sessionWindow) && backups.Count == 1 && !window.UpdateNotice.IsOpen);
        Check("The backup holds every connection", backups.Count == 1 && CountConnections(backups[0]) == connections);

        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(5);
            AppUpdates.BackupDatabase();
        }

        Check("Only the last three backups are kept", DatabaseBackup.List(AppUpdates.BackupFolder).Count == 3);

        // No update: the menu check says so.
        AppUpdates.CreateUpdater = (_, _) => new FakeUpdater(null);
        await window.CheckForUpdatesAsync(startup: false);
        Check("Without a newer version the menu check reports the current one",
            window.ToastBar.Message == string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Update_Current, AppVersion.Current) && !window.UpdateNotice.IsOpen);
        Check("The token is neither in the database nor in the self-test log",
            !FileContains(app.StoreReport.DatabasePath, token) && !Lines.Any(l => l.Contains(token, StringComparison.Ordinal)));
    }

    private static int CountConnections(string database) => new SqliteConnectionStore(database).LoadConnections().Count;
}
