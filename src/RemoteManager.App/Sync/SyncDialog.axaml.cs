using System.Globalization;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.Core.Sync;

namespace RemoteManager.App.Sync;

/// <summary>
/// Sync settings: on/off, folder, passphrase (the same on every PC), what a joining PC does with its own
/// connections, "Jetzt synchronisieren" with the last result, and the way to open conflicts.
/// </summary>
public partial class SyncDialog : UserControl
{
    public const int MinPassphraseLength = 10;

    private readonly SyncService service;

    public SyncDialog() : this(AppHost.Current.Sync)
    {
    }

    public SyncDialog(SyncService service)
    {
        InitializeComponent();
        this.service = service;
        EnabledSwitch.IsChecked = service.IsEnabled;
        FolderBox.Text = service.Folder;
        FolderBox.PlaceholderText = Strings.Sync_FolderPlaceholder;
        PickFolderButton.Click += async (_, _) => await PickFolderAsync();
        SyncNowButton.Click += async (_, _) => await SaveAndSyncAsync(waitForSync: true);
        ConflictsButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
                await SyncConflictsWindow.ShowAsync(owner, service);
            UpdateState();
        };
        service.StateChanged += OnServiceStateChanged;
        DetachedFromVisualTree += (_, _) => service.StateChanged -= OnServiceStateChanged;
        UpdateState();
    }

    /// <summary>The dialog and its content while open (used by the self-test).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }
    public static SyncDialog? OpenContent { get; private set; }

    public FAInfoBar Result => ResultBar;

    public static async Task ShowAsync(SyncService service)
    {
        var content = new SyncDialog(service);
        var dialog = new FAContentDialog
        {
            Title = Strings.Sync_Title,
            Content = content,
            PrimaryButtonText = Strings.Action_Save,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.Cancel = !await content.SaveAndSyncAsync(waitForSync: false);
            deferral.Complete();
        };

        OpenDialog = dialog;
        OpenContent = content;
        await dialog.ShowAsync();
        OpenDialog = null;
        OpenContent = null;
    }

    /// <summary>Fills the form (self-test).</summary>
    internal void Fill(bool enabled, string folder, string passphrase, bool replaceLocal = false)
    {
        EnabledSwitch.IsChecked = enabled;
        FolderBox.Text = folder;
        PassphraseBox.Text = RepeatBox.Text = passphrase;
        JoinReplace.IsChecked = replaceLocal;
        JoinMerge.IsChecked = !replaceLocal;
    }

    /// <summary>Validates and saves the form, then syncs when sync is on. False (with the reason shown) when the form is not valid.</summary>
    internal async Task<bool> SaveAndSyncAsync(bool waitForSync)
    {
        bool enabled = EnabledSwitch.IsChecked == true;
        string folder = (FolderBox.Text ?? "").Trim();
        string passphrase = PassphraseBox.Text ?? "";
        // Turning sync off needs nothing; turning it on (or setting a passphrase) needs a folder and a valid passphrase.
        if (enabled || passphrase.Length > 0)
        {
            string? problem = folder.Length == 0 || !Directory.Exists(folder) ? Strings.Sync_Error_PickFolder
                : passphrase.Length == 0 && !service.HasPassphrase ? Strings.Sync_Error_EnterPassphrase
                : passphrase.Length is > 0 and < MinPassphraseLength ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_Error_PassphraseShort, MinPassphraseLength)
                : passphrase != (RepeatBox.Text ?? "") ? Strings.Sync_Error_PassphraseMismatch
                : null;
            if (problem is not null)
            {
                ShowResult(Strings.Sync_Title, problem, FAInfoBarSeverity.Error);
                return false;
            }
        }

        if (enabled)
        {
            try
            {
                // A wrong passphrase is caught here, before anything is saved or written.
                await service.CheckAsync(folder, passphrase.Length > 0 ? passphrase : service.ReadPassphrase() ?? "");
            }
            catch (Exception ex) when (ex is SyncException or IOException or UnauthorizedAccessException)
            {
                ShowResult(Strings.Sync_Title, SyncService.Describe(ex), FAInfoBarSeverity.Error);
                return false;
            }
        }

        service.Configure(enabled, folder, passphrase.Length > 0 ? passphrase : null);
        PassphraseBox.Text = RepeatBox.Text = "";
        if (!enabled)
        {
            UpdateState();
            return true;
        }

        SyncJoin join = JoinReplace.IsChecked == true ? SyncJoin.ReplaceLocal : SyncJoin.Merge;
        Task<SyncResult?> syncing = service.SyncNowAsync(join);
        if (waitForSync)
            ShowSyncResult(await syncing);
        return true;
    }

    private void ShowSyncResult(SyncResult? result)
    {
        if (service.LastError is { } error)
            ShowResult(Strings.Sync_Failed, error, FAInfoBarSeverity.Error);
        else if (result is not null)
        {
            string message = string.Format(CultureInfo.CurrentCulture, Strings.Sync_ResultCounts, result.Pushed, result.Pulled);
            if (result.OpenConflicts > 0)
                message += " " + string.Format(CultureInfo.CurrentCulture, Strings.Sync_ResultConflicts, result.OpenConflicts);
            ShowResult(Strings.Sync_Done, message, result.OpenConflicts > 0 ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success);
        }

        UpdateState();
    }

    private void ShowResult(string title, string message, FAInfoBarSeverity severity)
    {
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
    }

    private void OnServiceStateChanged(object? sender, EventArgs e) => UpdateState();

    private void UpdateState()
    {
        PassphraseBox.PlaceholderText = service.HasPassphrase
            ? Strings.Sync_PassphraseStored
            : string.Format(CultureInfo.CurrentCulture, Strings.Sync_PassphrasePlaceholder, MinPassphraseLength);
        JoinPanel.IsVisible = !service.HasSynced;
        SyncNowButton.IsEnabled = !service.IsRunning;
        StatusText.Text = service.IsRunning ? Strings.Sync_Running
            : !service.IsEnabled ? Strings.Sync_StatusOff
            : service.LastError is not null ? Strings.Sync_Failed
            : service.LastSync is { } last ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_LastSync, last.ToLocalTime())
            : Strings.Sync_Never;
        int conflicts = service.OpenConflicts;
        ConflictsButton.IsVisible = conflicts > 0;
        ConflictsText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Sync_ShowConflicts, conflicts);
    }

    private async Task PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            return;
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Strings.Sync_PickFolder });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } folder)
            FolderBox.Text = folder;
    }
}
