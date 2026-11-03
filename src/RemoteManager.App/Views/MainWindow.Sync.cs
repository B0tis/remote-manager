using System.Globalization;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Sync;
using RemoteManager.Core.Sync;

namespace RemoteManager.App.Views;

/// <summary>Device sync in the overview: the header button (with the number of open conflicts), and the overview follows what a sync changed.</summary>
public partial class MainWindow
{
    private SyncResult? shownSyncResult;
    private string? shownSyncError;

    private void HookSync()
    {
        SyncButton.Click += async (_, _) => await SyncDialog.ShowAsync(app.Sync);
        app.Sync.StateChanged += OnSyncStateChanged;
        app.Sync.Start();
        UpdateSyncButton();
    }

    private void UnhookSync() => app.Sync.StateChanged -= OnSyncStateChanged;

    private void OnSyncStateChanged(object? sender, EventArgs e)
    {
        UpdateSyncButton();
        if (app.Sync.IsRunning)
            return;

        if (app.Sync.LastResult is { } result && result != shownSyncResult)
        {
            shownSyncResult = result;
            if (result.Pulled > 0)
                FollowSyncedChanges();
            if (result.NewConflicts > 0)
                ShowToast(Strings.Sync_Title, string.Format(CultureInfo.CurrentCulture, Strings.Sync_Toast_Conflicts, result.NewConflicts), FAInfoBarSeverity.Warning);
        }

        // An error is shown once, not every five minutes while the folder stays unreachable.
        if (app.Sync.LastError != shownSyncError)
        {
            shownSyncError = app.Sync.LastError;
            if (shownSyncError is not null)
                ShowToast(Strings.Sync_Failed, shownSyncError, FAInfoBarSeverity.Error);
        }
    }

    private void UpdateSyncButton()
    {
        int conflicts = app.Sync.IsEnabled ? app.Sync.OpenConflicts : 0;
        SyncConflictCount.Text = conflicts.ToString(CultureInfo.CurrentCulture);
        SyncConflictCount.IsVisible = conflicts > 0;
        SyncButton.Classes.Set("accent", conflicts > 0);
        // Setup lives in "Mehr"; the header button only shows while sync is on (state, conflicts).
        SyncButton.IsVisible = app.Sync.IsEnabled || conflicts > 0;
        Avalonia.Controls.ToolTip.SetTip(SyncButton,
            app.Sync.IsRunning ? Strings.Sync_Running
            : conflicts > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Sync_ShowConflicts, conflicts)
            : app.Sync.IsEnabled ? Strings.Sync_Tip_On : Strings.Sync_Tip_Off);
    }

    /// <summary>Records came in from another PC: drop items whose connection is gone, add new ones, refresh the rest.</summary>
    private void FollowSyncedChanges()
    {
        foreach (ConnectionItem gone in items.Where(i => !app.Library.Contains(i.Model)).ToList())
        {
            openHistory.Remove(gone);
            items.Remove(gone);
        }

        SyncWithLibrary();
        Details.Refresh();
    }
}
