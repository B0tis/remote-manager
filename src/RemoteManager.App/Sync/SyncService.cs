using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using RemoteManager.App.Resources;
using RemoteManager.Core.Credentials;
using RemoteManager.Core.Library;
using RemoteManager.Core.Sync;

namespace RemoteManager.App.Sync;

/// <summary>
/// Device sync in the app: settings (app_settings "sync.*"), the passphrase in the Credential Manager, and the
/// triggers: at start, a few seconds after a change, and every five minutes. Off until the user turns it on.
/// Use it from the UI thread; the engine moves file access and decryption to the thread pool.
/// </summary>
public sealed class SyncService : IDisposable
{
    public const string EnabledSetting = "sync.enabled";
    public const string FolderSetting = "sync.folder";
    public const string DeviceSetting = "sync.device";
    public const string LastSyncSetting = "sync.lastSync";

    /// <summary>Credential Manager key of the passphrase (target "RemoteManager:sync-passphrase").</summary>
    public const string PassphraseKey = "sync-passphrase";

    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ConnectionLibrary library;
    private readonly SyncStateStore state;
    private readonly string passphraseKey;
    private readonly DispatcherTimer changeTimer = new() { Interval = ChangeDelay };
    private readonly DispatcherTimer periodicTimer = new() { Interval = Interval };
    private bool running;
    private bool changedWhileRunning;

    /// <param name="passphraseKey">Credential Manager key of the passphrase; the self-test uses a per-run key.</param>
    public SyncService(ConnectionLibrary library, string databasePath, string passphraseKey = PassphraseKey)
    {
        this.library = library;
        this.passphraseKey = passphraseKey;
        state = new SyncStateStore(databasePath);
        changeTimer.Tick += async (_, _) =>
        {
            changeTimer.Stop();
            await SyncNowAsync();
        };
        periodicTimer.Tick += async (_, _) => await SyncNowAsync();
    }

    /// <summary>Raised on the UI thread when a sync starts or ends, and when the settings change.</summary>
    public event EventHandler? StateChanged;

    public ConnectionLibrary Library => library;

    public bool IsEnabled => library.GetSetting(EnabledSetting) == "1";
    public string Folder => library.GetSetting(FolderSetting) ?? "";
    public bool IsRunning => running;
    public bool HasPassphrase => WindowsCredentialSource.Exists(passphraseKey);

    public DateTime? LastSync => library.GetSetting(LastSyncSetting) is { } text
        ? DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        : null;

    public SyncResult? LastResult { get; private set; }

    /// <summary>German message of the last failed sync; null after a successful one.</summary>
    public string? LastError { get; private set; }

    public int OpenConflicts => state.LoadConflicts().Count;

    /// <summary>True once this device synced with the current folder.</summary>
    public bool HasSynced => state.LoadBases().Count > 0;

    /// <summary>This PC: a random id made once, and the computer name (shown in conflicts on other PCs).</summary>
    public SyncDevice Device
    {
        get
        {
            string? id = library.GetSetting(DeviceSetting);
            if (id is null)
            {
                id = Guid.NewGuid().ToString("N");
                library.SetSetting(DeviceSetting, id);
            }

            return new SyncDevice(id, Environment.MachineName);
        }
    }

    /// <summary>The stored passphrase, or null.</summary>
    public string? ReadPassphrase() => WindowsCredentialSource.Read(passphraseKey);

    /// <summary>Removes the stored passphrase (self-test cleanup, or turning sync off for good).</summary>
    public void DeletePassphrase() => WindowsCredentialSource.Delete(passphraseKey);

    public SyncEngine CreateEngine(string folder) => new(library, state, new FolderSyncTransport(folder), Device);

    /// <summary>Hooks the triggers; the first sync runs a few seconds after start.</summary>
    public void Start()
    {
        library.Changed += OnLibraryChanged;
        periodicTimer.Start();
        DispatcherTimer.RunOnce(() => _ = SyncNowAsync(), StartDelay);
    }

    /// <summary>
    /// Checks that <paramref name="passphrase"/> opens the sync file in <paramref name="folder"/>.
    /// Returns true when there is a file, false when the folder has none yet. Throws <see cref="SyncException"/>.
    /// </summary>
    public Task<bool> CheckAsync(string folder, string passphrase) => CreateEngine(folder).ProbeAsync(passphrase);

    /// <summary>Saves the settings; a new folder starts over (bases and conflicts belong to the old one).</summary>
    public void Configure(bool enabled, string folder, string? newPassphrase)
    {
        if (!string.Equals(Normalize(folder), Normalize(Folder), StringComparison.OrdinalIgnoreCase))
        {
            state.Clear();
            library.SetSetting(LastSyncSetting, null);
        }

        if (!string.IsNullOrEmpty(newPassphrase))
            WindowsCredentialSource.Write(passphraseKey, newPassphrase);
        library.SetSetting(FolderSetting, folder);
        library.SetSetting(EnabledSetting, enabled ? "1" : null);
        LastError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs a sync now (unless one is running or sync is off). Errors end up in <see cref="LastError"/>.</summary>
    public async Task<SyncResult?> SyncNowAsync(SyncJoin join = SyncJoin.Merge)
    {
        if (!IsEnabled || Folder.Length == 0 || running)
            return null;

        running = true;
        changedWhileRunning = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            string passphrase = WindowsCredentialSource.Read(passphraseKey)
                ?? throw new SyncException(SyncError.NoPassphrase, "No passphrase stored.");
            SyncResult result = await CreateEngine(Folder).SyncAsync(passphrase, join);
            LastResult = result;
            LastError = null;
            library.SetSetting(LastSyncSetting, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            Trace.WriteLine($"Sync: pushed {result.Pushed}, pulled {result.Pulled}, conflicts {result.OpenConflicts} (new {result.NewConflicts}), copies {result.MergedCopies}, wrote {result.Wrote}");
            return result;
        }
        catch (Exception ex) when (ex is SyncException or IOException or UnauthorizedAccessException)
        {
            LastError = Describe(ex);
            Trace.WriteLine($"Sync failed: {ex.GetType().Name} {(ex as SyncException)?.Error}: {ex.Message}");
            return null;
        }
        finally
        {
            running = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (changedWhileRunning)
                changeTimer.Start();
        }
    }

    /// <summary>Applies the user's choice for a conflict and sends it out right away.</summary>
    public async Task ResolveAsync(SyncKey key, SyncResolution resolution, string? copyName = null)
    {
        // A running sync would save its own (older) view of the conflicts over this choice.
        while (running)
            await Task.Delay(100);
        CreateEngine(Folder).Resolve(key, resolution, copyName);
        StateChanged?.Invoke(this, EventArgs.Empty);
        changeTimer.Stop();
        await SyncNowAsync();
    }

    /// <summary>A German message for a failed sync.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        SyncException { Error: SyncError.WrongPassphrase } => Strings.Sync_Error_WrongPassphrase,
        SyncException { Error: SyncError.Damaged } => Strings.Sync_Error_Damaged,
        SyncException { Error: SyncError.NewerFormat } => Strings.Sync_Error_NewerFormat,
        SyncException { Error: SyncError.FolderMissing } => Strings.Sync_Error_FolderMissing,
        SyncException { Error: SyncError.Locked or SyncError.ChangedDuringWrite } => Strings.Sync_Error_Locked,
        SyncException { Error: SyncError.NoPassphrase } => Strings.Sync_Error_NoPassphrase,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.Sync_Error_Other, ex.Message),
    };

    public void Dispose()
    {
        library.Changed -= OnLibraryChanged;
        changeTimer.Stop();
        periodicTimer.Stop();
    }

    private static string Normalize(string folder) => folder.Length == 0 ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private void OnLibraryChanged(object? sender, EventArgs e)
    {
        if (!IsEnabled)
            return;
        if (running)
        {
            // Our own apply raises this too; one extra (cheap, usually empty) round afterwards covers real edits.
            changedWhileRunning = true;
            return;
        }

        changeTimer.Stop();
        changeTimer.Start();
    }
}
