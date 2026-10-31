using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Updates;
using RemoteManager.Core;
using RemoteManager.Core.Updates;

namespace RemoteManager.App.Views;

/// <summary>
/// "Über Remote-Manager": version and build, install and data folder, and the update settings (source, GitHub
/// token in the Credential Manager). "Speichern und suchen" saves and runs the check in the overview.
/// </summary>
public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        VersionText.Text = AppVersion.Commit is { } commit
            ? string.Format(CultureInfo.CurrentCulture, Strings.About_VersionBuild, AppVersion.Current, commit)
            : string.Format(CultureInfo.CurrentCulture, Strings.About_Version, AppVersion.Current);
        InstallText.Text = AppUpdates.InstallFolder is { } folder
            ? string.Format(CultureInfo.CurrentCulture, Strings.About_Installed, folder)
            : Strings.About_NotInstalled;
        DataText.Text = string.Format(CultureInfo.CurrentCulture, Strings.About_Data, AppPaths.Root);

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
    public string? SourceError => SourceMessage.IsVisible ? SourceMessage.Text : null;

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
        if (!AppUpdates.SourceFromEnvironment && !AppUpdates.TrySetSource(SourceBox.Text))
        {
            ShowSourceMessage(Strings.About_SourceInvalid, error: true);
            return false;
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
        DeleteTokenButton.IsEnabled = stored;
    }

    private async Task<bool> RunCredentialAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Win32Exception ex)
        {
            TokenState.Text = string.Format(CultureInfo.CurrentCulture, Strings.About_TokenSaveFailed, ex.Message);
            return false;
        }
    }

    private void ShowSourceMessage(string text, bool error)
    {
        SourceMessage.Text = text;
        SourceMessage.Opacity = error ? 1 : 0.7;
        if (error && this.TryFindResource("SystemFillColorCriticalBrush", ActualThemeVariant, out object? brush) && brush is IBrush critical)
            SourceMessage.Foreground = critical;
        SourceMessage.IsVisible = true;
    }
}
