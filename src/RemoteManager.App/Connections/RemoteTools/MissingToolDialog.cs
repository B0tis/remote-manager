using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model;
using RemoteManager.Core.RemoteTools;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>
/// "TeamViewer nicht gefunden": says what is missing and offers the path setting and the download page.
/// </summary>
public static class MissingToolDialog
{
    /// <summary>The dialog while open (used by the self-test).</summary>
    public static FAContentDialog? Current { get; private set; }

    public static async void Show(ConnectionType type, IConnectionHost host)
    {
        RemoteToolInfo info = RemoteToolLocator.InfoFor(type);
        // The address "Herunterladen" opens, so the user sees where the browser will go.
        var link = new HyperlinkButton
        {
            Content = new TextBlock { Text = info.DownloadUrl, Classes = { "mono" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            NavigateUri = new Uri(info.DownloadUrl),
            Padding = new Avalonia.Thickness(0),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var label = new TextBlock { Text = Strings.RemoteTool_Missing_DownloadLabel, Classes = { "fieldLabel" } };

        FAContentDialog dialog = DialogKit.Question(
            string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Missing_Title, info.ProductName),
            string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Missing_Message, info.ProductName, info.ExeName),
            Strings.RemoteTool_Missing_SetPath,
            destructive: false,
            DialogKit.Note(new StackPanel { Children = { label, link } }));
        dialog.SecondaryButtonText = Strings.RemoteTool_Missing_Download;

        Current = dialog;
        FAContentDialogResult result = await dialog.ShowAsync();
        Current = null;
        if (result == FAContentDialogResult.Primary)
            await RemoteToolsDialog.ShowAsync(type);
        else if (result == FAContentDialogResult.Secondary)
            OpenUrl(info.DownloadUrl);
    }

    public static void OpenUrl(string url)
    {
        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Trace.WriteLine($"Opening {url} failed: {ex.Message}");
        }
    }
}
