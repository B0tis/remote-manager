using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
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
        var link = new HyperlinkButton
        {
            Content = info.DownloadUrl,
            NavigateUri = new Uri(info.DownloadUrl),
            Padding = new Avalonia.Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var dialog = new FAContentDialog
        {
            Title = string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Missing_Title, info.ProductName),
            Content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 440,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Missing_Message, info.ProductName, info.ExeName),
                    },
                    link,
                },
            },
            PrimaryButtonText = Strings.RemoteTool_Missing_SetPath,
            SecondaryButtonText = Strings.RemoteTool_Missing_Download,
            CloseButtonText = Strings.Action_Cancel,
            DefaultButton = FAContentDialogButton.Primary,
        };

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
