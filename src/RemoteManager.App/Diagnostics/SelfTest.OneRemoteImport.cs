using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Microsoft.Data.Sqlite;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// 1Remote database import through the real dialog button ("Importieren"), with the database held open the way a
/// running 1Remote holds it (WAL, a write transaction, the file shared for read and write). This crashed the app
/// before: the import must read a copy, show a summary, and never end the process.
/// </summary>
internal static partial class SelfTest
{
    private static async Task RunOneRemoteDatabaseImportChecksAsync(MainWindow window)
    {
        AppHost app = AppHost.Current;
        const string target = "Selftest 1Remote";
        string folder = Path.Combine(Path.GetTempPath(), $"rm-selftest-1remote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string db = Path.Combine(folder, "1Remote.db");
        string[] names = ["st-1r-rdp", "st-1r-ssh", "st-1r-vnc"];
        int before = window.Connections.Count;
        var writer = new SqliteConnection($"Data Source={db};Mode=ReadWriteCreate;Pooling=False");
        SqliteTransaction? pending = null;
        FileStream? held = null;
        Task? importing = null;
        try
        {
            writer.Open();
            Execute(writer, """
                PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;
                CREATE TABLE IF NOT EXISTS `Configs` (`Key` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Value` TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS `Servers` (`Id` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Protocol` VARCHAR (32) NOT NULL, `ClassVersion` VARCHAR (32) NOT NULL, `Json` TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS `Credentials` (`Id` VARCHAR (64) PRIMARY KEY NOT NULL UNIQUE, `Hash` VARCHAR (32) NOT NULL, `Json` TEXT NOT NULL);
                INSERT INTO `Servers` VALUES ('st-1', 'RDP', '1', '{"Protocol":"RDP","DisplayName":"st-1r-rdp","Address":"rm-selftest-1r-rdp.invalid","Port":"3392","UserName":"SELFTEST\\jdoe","Tags":["selftest"]}');
                INSERT INTO `Servers` VALUES ('st-2', 'SSH', '1', '{"Protocol":"SSH","DisplayName":"st-1r-ssh","Address":"rm-selftest-1r-ssh.invalid","Port":"22","UserName":"root","PrivateKey":"QUJDREVGR0hJSktMTU5PUA=="}');
                INSERT INTO `Servers` VALUES ('st-3', 'RDP', '1', '{not json');
                INSERT INTO `Servers` VALUES ('st-4', 'VNC', '1', '{"Protocol":"VNC","DisplayName":"st-1r-vnc","Address":"rm-selftest-1r-vnc.invalid","Port":"5901"}');
                """);
            pending = writer.BeginTransaction();
            Execute(writer, "INSERT INTO `Configs` VALUES ('selftest', 'pending')");
            held = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            importing = ImportExportCommands.ShowImportAsync(window);
            await Settle(600);
            ImportView view = ImportExportCommands.OpenView!;
            view.SelectFormat("1remote");
            view.SetFiles([db]);
            view.TargetGroup = target;
            Button? start = ImportExportCommands.OpenDialog?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "PrimaryButton");
            Check("Import dialog shows the \"Importieren\" button", start is not null);
            if (start is null)
                return;

            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FAContentDialog? summary = null;
            // An error keeps the dialog open with the message; success closes it and opens the summary.
            for (int i = 0; i < 50 && (summary = ImportExportCommands.OpenSummary) is null; i++)
                await Settle(100);
            await Settle(300);
            SaveRenderProbe(window, "import-1remote-db.png");
            Check("Importing a 1Remote.db that 1Remote holds open shows the summary (no crash, no error)", summary is not null && ImportExportCommands.OpenView is null);
            ImportExportCommands.OpenSummary?.Hide();
            await importing;
            importing = null;

            window.SyncWithLibrary();
            ConnectionItem? rdp = window.Connections.FirstOrDefault(c => c.Name == "st-1r-rdp");
            Check("1Remote.db (Servers table): RDP, SSH, and VNC imported, the damaged row skipped, the encrypted key not taken as a path",
                names.All(n => window.Connections.Any(c => c.Name == n)) && window.Connections.Count == before + 3
                && rdp is not null && rdp.Model.EffectivePort == 3392 && rdp.Model.UserName == "jdoe"
                && string.IsNullOrEmpty(window.Connections.First(c => c.Name == "st-1r-ssh").Model.SettingsAs<Core.Model.Settings.SshSettings>().KeyPath));

            pending.Commit();
            pending = null;
            Check("The held-open 1Remote database is untouched by the import", ScalarLong(writer, "SELECT COUNT(*) FROM `Configs`") == 1 && ScalarLong(writer, "SELECT COUNT(*) FROM `Servers`") == 4);
        }
        finally
        {
            if (importing is not null)
            {
                ImportExportCommands.OpenSummary?.Hide();
                ImportExportCommands.OpenDialog?.Hide();
                await importing;
            }

            pending?.Dispose();
            held?.Dispose();
            writer.Dispose();
            foreach (ConnectionItem item in window.Connections.Where(c => names.Contains(c.Name)).ToList())
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
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }

            Check("1Remote self-test data removed again", window.Connections.Count == before && !app.Library.GroupPaths().Any(p => p.StartsWith(target, StringComparison.Ordinal)));
        }

        static void Execute(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        static long ScalarLong(SqliteConnection connection, string sql)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() is long value ? value : -1;
        }
    }
}
