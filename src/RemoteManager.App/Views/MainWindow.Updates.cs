using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Services;
using RemoteManager.App.Updates;

namespace RemoteManager.App.Views;

/// <summary>
/// Updates in the overview: a quiet check at startup (installed builds, throttled), "Nach Updates suchen" and
/// "Über Remote-Manager" in the "more" menu, and the notice with "Jetzt aktualisieren" / "Später". Applying asks
/// first while sessions are open, backs up the database, and restarts into the new version.
/// </summary>
public partial class MainWindow
{
    private UpdateOffer? updateOffer;
    private bool applyingUpdate;

    public FAInfoBar UpdateNotice => UpdateBar;
    public Button UpdateNowButtonControl => UpdateNowButton;
    public Button UpdateLaterButtonControl => UpdateLaterButton;
    public UpdateOffer? PendingUpdate => updateOffer;

    /// <summary>The "sessions are open" question while it is shown (self-test).</summary>
    internal FAContentDialog? UpdateSessionsDialog { get; private set; }

    private void InitializeUpdates()
    {
        UpdateNowButton.Click += async (_, _) => await ApplyUpdateAsync();
        UpdateLaterButton.Click += (_, _) => DismissUpdate();

        Opened += async (_, _) =>
        {
            if (AppUpdates.RestartedToVersion is { } version)
                ShowToast(Strings.Update_Title, string.Format(CultureInfo.CurrentCulture, Strings.Update_Done, version), FAInfoBarSeverity.Success);
            if (!AppOptions.SelfTest && AppUpdates.IsInstalled)
                await CheckForUpdatesAsync(startup: true);
        };
    }

    /// <summary>
    /// Asks the update source. At startup quietly (no message unless there is an update) and at most once per
    /// interval; from the menu always, with a message for "current" and for errors.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool startup)
    {
        if (!startup && !AppUpdates.IsInstalled)
        {
            ShowToast(Strings.Update_Title, Strings.Update_NotInstalled, FAInfoBarSeverity.Informational);
            return;
        }

        try
        {
            if (await AppUpdates.CheckAsync(startup) is { } offer)
                ShowUpdateNotice(offer);
            else if (!startup)
                ShowToast(Strings.Update_Title, string.Format(CultureInfo.CurrentCulture, Strings.Update_Current, AppVersion.Current), FAInfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            // Offline, source unreachable, GitHub rate limit: never a dialog at startup.
            Trace.WriteLine($"Update check failed: {ex.GetType().Name}: {ex.Message}");
            if (!startup)
                ShowToast(Strings.Update_Title, AppUpdates.Describe(ex), FAInfoBarSeverity.Error);
        }
    }

    public void ShowUpdateNotice(UpdateOffer offer)
    {
        updateOffer = offer;
        UpdateBar.Message = string.Format(CultureInfo.CurrentCulture, Strings.Update_Available, offer.Version, AppVersion.Current);
        UpdateBar.Severity = FAInfoBarSeverity.Informational;
        SetUpdateButtons(true);
        UpdateBar.IsOpen = true;
    }

    /// <summary>"Später": hides the notice; the next check (next start after the interval, or the menu) offers it again.</summary>
    public void DismissUpdate()
    {
        UpdateBar.IsOpen = false;
        updateOffer = null;
    }

    /// <summary>
    /// "Jetzt aktualisieren": download, ask when sessions are open (they end with the restart), close them,
    /// clear a copied password, back up the database, then install and restart.
    /// </summary>
    public async Task ApplyUpdateAsync()
    {
        if (updateOffer is not { } offer || applyingUpdate)
            return;

        applyingUpdate = true;
        SetUpdateButtons(false);
        try
        {
            await offer.Updater.DownloadAsync(offer.Update, percent => Dispatcher.UIThread.Post(() =>
                UpdateBar.Message = string.Format(CultureInfo.CurrentCulture, Strings.Update_Downloading, offer.Version, percent)));

            if (sessionWindows.Count > 0 && !await ConfirmEndSessionsAsync(sessionWindows.Count))
            {
                ShowUpdateNotice(offer);
                return;
            }

            foreach (Window session in sessionWindows.ToList())
                session.Close();
            if (SecretClipboard.HasPending)
                await SecretClipboard.ClearPendingAsync();

            UpdateBar.Message = Strings.Update_Installing;
            try
            {
                AppUpdates.BackupDatabase();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Without a backup the update does not start.
                ShowUpdateNotice(offer);
                ShowToast(Strings.Update_Title, string.Format(CultureInfo.CurrentCulture, Strings.Update_BackupFailed, ex.Message), FAInfoBarSeverity.Error);
                return;
            }

            offer.Updater.ApplyAndRestart(offer.Update);
            // Only a fake updater returns here.
            DismissUpdate();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Update failed: {ex.GetType().Name}: {ex.Message}");
            ShowUpdateNotice(offer);
            ShowToast(Strings.Update_Title, AppUpdates.Describe(ex), FAInfoBarSeverity.Error);
        }
        finally
        {
            applyingUpdate = false;
            if (UpdateBar.IsOpen)
                SetUpdateButtons(true);
        }
    }

    private async Task<bool> ConfirmEndSessionsAsync(int count)
    {
        var dialog = new FAContentDialog
        {
            Title = Strings.Update_SessionsTitle,
            Content = new TextBlock
            {
                Text = count == 1 ? Strings.Update_SessionsTextOne : string.Format(CultureInfo.CurrentCulture, Strings.Update_SessionsText, count),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = Strings.Update_SessionsConfirm,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Close,
        };
        UpdateSessionsDialog = dialog;
        FAContentDialogResult result = await dialog.ShowAsync();
        UpdateSessionsDialog = null;
        return result == FAContentDialogResult.Primary;
    }

    private void SetUpdateButtons(bool enabled)
    {
        UpdateNowButton.IsEnabled = enabled;
        UpdateLaterButton.IsEnabled = enabled;
    }
}
