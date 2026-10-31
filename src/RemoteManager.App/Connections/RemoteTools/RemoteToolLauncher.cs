using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAvalonia.UI.Controls;
using RemoteManager.App.Resources;
using RemoteManager.App.Views;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.RemoteTools;

namespace RemoteManager.App.Connections.RemoteTools;

/// <summary>
/// Starts TeamViewer, AnyDesk, or RustDesk for a connection. Always through
/// <see cref="ProcessStartInfo.ArgumentList"/>, never a shell. The password goes, in this order of
/// preference: AnyDesk stdin; the clipboard (cleared later) for TeamViewer and RustDesk; their command
/// line only when the connection asks for it, with a visible warning.
/// </summary>
public static class RemoteToolLauncher
{
    /// <summary>The self-test injects a locator with fake candidates.</summary>
    internal static RemoteToolLocator? LocatorOverride { get; set; }

    public static RemoteToolLocator Locator => LocatorOverride ?? new RemoteToolLocator(AppHost.Current.Library.Store.GetSetting);

    /// <summary>
    /// Starts the tool. Returns the process (the self-test inspects it) or null when nothing was started
    /// (invalid ID, tool missing, start failed); the user has been told why.
    /// </summary>
    public static async Task<Process?> LaunchAsync(ConnectionItem connection, IConnectionHost host, RemoteToolMode? mode = null)
    {
        var settings = connection.Model.SettingsAs<RemoteToolSettings>();
        string id = RemoteToolCommandLine.NormalizeId(connection.Model.Host);
        if (!RemoteToolCommandLine.IsValidId(connection.Model.Type, id))
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_InvalidId, connection.Model.Host), FAInfoBarSeverity.Error);
            return null;
        }

        if (RemoteToolCommandLine.FindSecretArgument(RemoteToolCommandLine.SplitArguments(settings.ExtraArguments)) is { } secretArgument)
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Error_SecretInArguments, secretArgument), FAInfoBarSeverity.Error);
            return null;
        }

        ToolLocation location = Locator.Locate(connection.Model.Type);
        if (location.Path is not { } toolPath)
        {
            MissingToolDialog.Show(connection.Model.Type, host);
            return null;
        }

        string? secret;
        try
        {
            secret = await host.App.Credentials.ReadAsync(connection.Credentials.Secret);
        }
        catch (Win32Exception ex)
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.Password_ReadFailed, ex.Message), FAInfoBarSeverity.Error);
            return null;
        }

        RemoteToolCommand command = RemoteToolCommandLine.Build(connection.Model.Type, toolPath, id, settings, secret is not null, mode);
        Trace.WriteLine($"Launch {connection.Model.Type}: {command.Preview}");

        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(command.FileName) ?? "",
            RedirectStandardInput = command.SecretViaStdin,
        };
        if (command.SecretViaStdin)
            start.StandardInputEncoding = new UTF8Encoding(false);
        foreach (string argument in command.ArgumentsWith(secret))
            start.ArgumentList.Add(argument);

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_StartFailed, ex.Message), FAInfoBarSeverity.Error);
            return null;
        }

        if (process is null)
            return null;

        if (command.SecretViaStdin)
        {
            try
            {
                // Like "echo PW | AnyDesk.exe ID --with-password": one line, then end of input.
                await process.StandardInput.WriteLineAsync(secret);
                process.StandardInput.Close();
            }
            catch (IOException ex)
            {
                Trace.WriteLine($"Writing the password to {connection.Model.Type} stdin failed: {ex.Message}");
            }
        }

        host.MarkConnected(connection);
        if (command.ExposesSecret)
        {
            host.ShowToast(connection.Name, string.Format(CultureInfo.CurrentCulture, Strings.RemoteTool_Warning_PasswordOnCommandLine, connection.TypeLabel), FAInfoBarSeverity.Warning);
        }
        else if (secret is not null && !command.SecretViaStdin)
        {
            // TeamViewer and RustDesk ask for the password themselves: have it ready on the clipboard.
            await host.CopyPasswordAsync(connection);
        }

        return process;
    }
}
