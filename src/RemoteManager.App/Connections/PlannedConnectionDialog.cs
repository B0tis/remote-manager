using System.Globalization;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;

namespace RemoteManager.App.Connections;

/// <summary>
/// Stand-in for types whose launch is not built yet: says which phase brings it and offers
/// "Passwort kopieren". A handler replaces its call with the real launch.
/// </summary>
public static class PlannedConnectionDialog
{
    /// <summary>The dialog while open (used by the self-test).</summary>
    public static FAContentDialog? Current { get; private set; }

    public static async void Show(ConnectionItem connection, IConnectionHost host, string message)
    {
        var dialog = new FAContentDialog
        {
            Title = string.Format(CultureInfo.CurrentCulture, Strings.Planned_Title, connection.TypeLabel, connection.Name),
            Content = message,
            CloseButtonText = Strings.Common_Ok,
            // An empty text hides the button.
            PrimaryButtonText = connection.HasStoredPassword ? Strings.Action_CopyPassword : "",
            DefaultButton = connection.HasStoredPassword ? FAContentDialogButton.Primary : FAContentDialogButton.Close,
        };
        Current = dialog;
        FAContentDialogResult result = await dialog.ShowAsync();
        Current = null;
        if (result == FAContentDialogResult.Primary)
            await host.CopyPasswordAsync(connection);
    }
}
