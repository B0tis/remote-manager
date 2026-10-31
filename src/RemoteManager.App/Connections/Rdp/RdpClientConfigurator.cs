using System.Runtime.InteropServices;
using Devolutions.MsRdpEx.Avalonia;
using MSTSCLib;
using BinaryString = MsRdpEx.Interop.BinaryString;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Rdp;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>Logon data for one connect; the passwords only live as long as the connect call.</summary>
internal sealed record RdpLogon(string Host, int Port, string UserName, string Domain, string Password, string GatewayPassword);

/// <summary>
/// Puts the start parameters of an RDP connection (<see cref="RdpSettings"/>) onto the embedded
/// MsRdpEx client before <see cref="RdpClientView.ConnectConfigured"/>. That path is used instead of
/// <see cref="RdpClientView.Connect"/>, which would overwrite NLA and clipboard redirection with its own
/// defaults. Every option is set on every connect, because the window reuses its client for a reconnect.
/// </summary>
internal static class RdpClientConfigurator
{
    // TSC_PROXY_MODE_*: none (direct), always, detect. TSC_PROXY_CREDS_MODE_ANY.
    private const uint GatewayNone = 0, GatewayAlways = 1, GatewayDetect = 2, GatewayCredsAny = 4;

    public static void Apply(RdpClientView view, RdpSettings s, RdpLogon logon, int desktopWidth, int desktopHeight)
    {
        IMsRdpClient10 client = view.GetClient<IMsRdpClient10>();
        client.Server = Bstr(logon.Host);
        client.UserName = Bstr(logon.UserName);
        client.Domain = Bstr(logon.Domain);
        if (desktopWidth > 0 && desktopHeight > 0)
        {
            client.DesktopWidth = desktopWidth;
            client.DesktopHeight = desktopHeight;
        }

        client.ColorDepth = 32;

        IMsRdpClientAdvancedSettings8 advanced = client.AdvancedSettings9;
        advanced.RDPPort = logon.Port;
        advanced.EnableCredSspSupport = s.EnableNla;
        // Server authentication: 0 connect anyway, 1 refuse on certificate problems, 2 warn.
        advanced.AuthenticationLevel = (uint)Math.Clamp(s.AuthenticationLevel, 0, 2);
        advanced.ConnectToAdministerServer = s.AdminSession;
        advanced.SmartSizing = false;
        advanced.DisplayConnectionBar = true;

        RdpRedirections r = s.Redirections;
        advanced.RedirectClipboard = r.Clipboard;
        advanced.RedirectDrives = r.Drives;
        advanced.RedirectPrinters = r.Printers;
        advanced.RedirectPorts = r.Ports;
        advanced.RedirectSmartCards = r.SmartCards;
        advanced.AudioRedirectionMode = r.Audio switch { RdpAudioMode.Remote => 1u, RdpAudioMode.None => 2u, _ => 0u };
        advanced.AudioCaptureRedirectionMode = r.Microphone;

        IMsRdpClientSecuredSettings2 secured = client.SecuredSettings3;
        // A RemoteApp is opened with mstsc (RdpHandler); here only a start program.
        secured.StartProgram = Bstr(s.StartProgram.Trim());
        secured.WorkDir = Bstr(s.WorkingDirectory.Trim());

        ApplyGateway(client, s, logon);
        ApplyExtended(view, s);

        IMsTscNonScriptable credentials = view.GetClient<IMsTscNonScriptable>();
        if (logon.Password.Length == 0)
            credentials.ResetPassword();
        else
            credentials.ClearTextPassword = Bstr(logon.Password);
    }

    /// <summary>A line for the session log: the options in effect, never a credential.</summary>
    public static string Describe(RdpSettings s) =>
        $"Start parameters: NLA {s.EnableNla}, authentication level {s.AuthenticationLevel}, admin {s.AdminSession}, " +
        $"restricted admin {s.RestrictedAdmin}, remote guard {s.RemoteCredentialGuard}, gateway {s.GatewayUsage} {s.GatewayHost}, " +
        $"display {s.Display} {s.DesktopWidth}x{s.DesktopHeight}, keyboard {s.Keyboard}, clipboard {s.Redirections.Clipboard}, " +
        $"drives {s.Redirections.Drives}, printers {s.Redirections.Printers}, smart cards {s.Redirections.SmartCards}, " +
        $"ports {s.Redirections.Ports}, audio {s.Redirections.Audio}, microphone {s.Redirections.Microphone}.";

    private static void ApplyGateway(IMsRdpClient10 client, RdpSettings s, RdpLogon logon)
    {
        IMsRdpClientTransportSettings4 transport = client.TransportSettings4;
        if (s.GatewayUsage == RdpGatewayUsage.None || s.GatewayHost.Trim().Length == 0)
        {
            transport.GatewayUsageMethod = GatewayNone;
            return;
        }

        transport.GatewayHostname = Bstr(s.GatewayHost.Trim());
        transport.GatewayUsageMethod = s.GatewayUsage == RdpGatewayUsage.Always ? GatewayAlways : GatewayDetect;
        transport.GatewayProfileUsageMethod = 1;
        transport.GatewayCredsSource = GatewayCredsAny;
        if (s.GatewayUsesConnectionCredentials)
        {
            transport.GatewayCredSharing = 1;
            return;
        }

        transport.GatewayCredSharing = 0;
        (string user, string domain) = RdpFileMapping.SplitUser(s.GatewayUserName);
        transport.GatewayUsername = Bstr(user);
        transport.GatewayDomain = Bstr(domain);
        transport.GatewayPassword = Bstr(logon.GatewayPassword);
    }

    /// <summary>Restricted Admin and Remote Credential Guard go through IMsRdpExtendedSettings.</summary>
    private static void ApplyExtended(RdpClientView view, RdpSettings s)
    {
        try
        {
            IMsRdpExtendedSettings extended = view.GetClient<IMsRdpExtendedSettings>();
            bool restricted = s.RestrictedAdmin;
            bool guard = !restricted && s.RemoteCredentialGuard;
            extended.SetProperty(Bstr("RestrictedLogon"), restricted);
            extended.SetProperty(Bstr("RedirectedAuthentication"), guard);
            extended.SetProperty(Bstr("DisableCredentialsDelegation"), restricted || guard);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Older RDP clients: the options stay off; the session log says so.
            System.Diagnostics.Trace.WriteLine($"Extended RDP settings not available: {ex.Message}");
        }

        try
        {
            view.GetClient<IMsRdpClientNonScriptable5>().UseMultimon = s.UseMultimon;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            System.Diagnostics.Trace.WriteLine($"Multimon not available: {ex.Message}");
        }
    }

    private static BinaryString Bstr(string value) => new(value);
}
