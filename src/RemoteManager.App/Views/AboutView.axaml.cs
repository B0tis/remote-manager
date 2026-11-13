using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Connections.RemoteTools;
using RemoteManager.App.Resources;
using RemoteManager.App.Updates;
using RemoteManager.Core;
using RemoteManager.Core.Updates;

namespace RemoteManager.App.Views;

/// <summary>
/// "Über Remote-Manager": logo, name, version and build, the data and program folders (copy the path, open the
/// folder), and the update settings (source, GitHub token in the Credential Manager). "Jetzt suchen" saves and
/// runs the check in the overview.
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        VersionText.Text = string.Format(CultureInfo.CurrentCulture, Strings.About_Version, AppVersion.Current);
        BuildTag.IsVisible = AppVersion.Commit is not null;
        BuildText.Text = string.Format(CultureInfo.CurrentCulture, Strings.About_Build, AppVersion.Commit);

        string? installed = AppUpdates.InstallFolder;
        DataText.Text = AppPaths.Root;
        InstallText.Text = installed ?? Strings.About_NotInstalledShort;
        if (installed is null)
        {
            InstallText.Classes.Remove("mono");
            InstallText.Classes.Add("muted");
        }

        NotInstalledNote.IsVisible = installed is null;
        CopyInstallButton.IsVisible = OpenInstallButton.IsVisible = installed is not null;
        CopyDataButton.Click += async (_, _) => await CopyAsync(AppPaths.Root);
        OpenDataButton.Click += (_, _) => MissingToolDialog.OpenUrl(AppPaths.Root);
        CopyInstallButton.Click += async (_, _) => await CopyAsync(installed);
        OpenInstallButton.Click += (_, _) =>
        {
            if (installed is not null)
                MissingToolDialog.OpenUrl(installed);
        };

        SourceBox.Text = AppUpdates.SourceText;
        SourceBox.PlaceholderText = UpdateSource.DefaultRepository;
        if (AppUpdates.SourceFromEnvironment)
        {
            SourceBox.IsReadOnly = true;
            ShowSourceMessage(string.Format(CultureInfo.CurrentCulture, Strings.About_SourceFixed, AppUpdates.SourceVariable), error: false);
        }

        TokenBox.PlaceholderText = Strings.About_TokenPlaceholder;
        DeleteTokenButton.Click += async (_, _) =>
        {
            await RunCredentialAsync(AppUpdates.DeleteTokenAsync);
            await UpdateTokenStateAsync();
        };
        LastCheckText.Text = AppUpdates.LastCheck is { } last
            ? string.Format(CultureInfo.CurrentCulture, Strings.About_LastCheck, last)
            : Strings.About_NeverChecked;
        _ = UpdateTokenStateAsync();
    }

    /// <summary>The dialog and its content while open (self-test).</summary>
    public static FAContentDialog? OpenDialog { get; private set; }
    public static AboutView? OpenContent { get; private set; }

    public string VersionLabel => VersionText.Text ?? "";
    public string? SourceError => SourceNote.IsVisible ? SourceMessage.Text : null;

    public static async Task ShowAsync(MainWindow window)
    {
        var content = new AboutView();
        var dialog = new FAContentDialog
        {
            Title = Strings.About_Title,
            Content = content,
            PrimaryButtonText = Strings.Action_Save,
            SecondaryButtonText = Strings.About_CheckNow,
            CloseButtonText = Strings.About_Close,
            DefaultButton = FAContentDialogButton.Close,
        };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.Cancel = !await content.TrySaveAsync();
            deferral.Complete();
        };
        dialog.SecondaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.Cancel = !await content.TrySaveAsync();
            deferral.Complete();
        };

        OpenDialog = dialog;
        OpenContent = content;
        FAContentDialogResult result = await dialog.ShowAsync();
        OpenDialog = null;
        OpenContent = null;

        if (result == FAContentDialogResult.Secondary)
            await window.CheckForUpdatesAsync(startup: false);
    }

    /// <summary>Sets the fields (self-test).</summary>
    internal void Enter(string source, string token)
    {
        SourceBox.Text = source;
        TokenBox.Text = token;
    }

    /// <summary>Saves source and token; false (with a message under the box) when the source is not valid.</summary>
    internal async Task<bool> TrySaveAsync()
    {
        if (!AppUpdates.SourceFromEnvironment)
        {
            if (!AppUpdates.TrySetSource(SourceBox.Text))
            {
                ShowSourceMessage(Strings.About_SourceInvalid, error: true);
                return false;
            }

            SourceNote.IsVisible = false;
        }

        if (TokenBox.Text is { Length: > 0 } token && !string.IsNullOrWhiteSpace(token))
        {
            if (!await RunCredentialAsync(() => AppUpdates.SaveTokenAsync(token)))
                return false;
            TokenBox.Text = "";
        }

        await UpdateTokenStateAsync();
        return true;
    }

    private async Task UpdateTokenStateAsync()
    {
        bool stored = await AppUpdates.HasTokenAsync();
        TokenState.Text = stored ? Strings.About_TokenStored : Strings.About_TokenNone;
        DialogKit.SetLamp(TokenLamp, stored ? "ok" : "idle");
        DeleteTokenButton.IsEnabled = stored;
    }

    private async Task<bool> RunCredentialAsync(Func<Task> action)
    {
        try
        {
            await action();
            TokenNote.IsVisible = false;
            return true;
        }
        catch (Win32Exception ex)
        {
            TokenError.Text = string.Format(CultureInfo.CurrentCulture, Strings.About_TokenSaveFailed, ex.Message);
            TokenNote.IsVisible = true;
            return false;
        }
    }

    private void ShowSourceMessage(string text, bool error)
    {
        SourceMessage.Text = text;
        SourceNote.Classes.Set("error", error);
        SourceNote.IsVisible = true;
    }

    private async Task CopyAsync(string? path)
    {
        if (path is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(path);
    }
}
