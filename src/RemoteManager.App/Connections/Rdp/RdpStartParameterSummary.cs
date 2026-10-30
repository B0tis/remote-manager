using System.Globalization;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>RDP start parameters for the detail panel: display, security, gateway, redirections, keyboard, program.</summary>
public sealed class RdpStartParameterSummary : IStartParameterSummary
{
    public ConnectionType Type => ConnectionType.Rdp;

    public IReadOnlyList<string> Describe(ConnectionSettings settings)
    {
        if (settings is not RdpSettings s)
            return [];

        var lines = new List<string>();
        string display = s.Display switch
        {
            RdpDisplayMode.SmartSizing => Strings.StartParams_Rdp_SmartSizing,
            RdpDisplayMode.FixedResolution => string.Format(CultureInfo.CurrentCulture, Strings.StartParams_Rdp_Fixed, s.DesktopWidth, s.DesktopHeight),
            _ => Strings.StartParams_Rdp_Fit,
        };
        lines.Add(s.UseMultimon ? $"{display} · {Strings.StartParams_Rdp_Multimon}" : display);
        lines.Add(s.EnableNla ? Strings.StartParams_Rdp_NlaOn : Strings.StartParams_Rdp_NlaOff);
        if (s.AdminSession)
            lines.Add(Strings.StartParams_Rdp_Admin);
        if (s.RestrictedAdmin)
            lines.Add(Strings.StartParams_Rdp_RestrictedAdmin);
        if (s.RemoteCredentialGuard)
            lines.Add(Strings.StartParams_Rdp_CredentialGuard);
        if (s.GatewayUsage != RdpGatewayUsage.None && s.GatewayHost.Length > 0)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.StartParams_Rdp_Gateway, s.GatewayHost));

        RdpRedirections r = s.Redirections;
        string[] redirected =
        [
            .. r.Clipboard ? [Strings.StartParams_Rdp_Clipboard] : Array.Empty<string>(),
            .. r.Drives ? [Strings.StartParams_Rdp_Drives] : Array.Empty<string>(),
            .. r.Printers ? [Strings.StartParams_Rdp_Printers] : Array.Empty<string>(),
            .. r.SmartCards ? [Strings.StartParams_Rdp_SmartCards] : Array.Empty<string>(),
            .. r.Ports ? [Strings.StartParams_Rdp_Ports] : Array.Empty<string>(),
            .. r.Microphone ? [Strings.StartParams_Rdp_Microphone] : Array.Empty<string>(),
        ];
        if (redirected.Length > 0)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.StartParams_Rdp_Redirected, string.Join(", ", redirected)));
        if (r.Audio != RdpAudioMode.Local)
            lines.Add(r.Audio == RdpAudioMode.Remote ? Strings.StartParams_Rdp_AudioRemote : Strings.StartParams_Rdp_AudioNone);

        lines.Add(s.Keyboard switch
        {
            RdpKeyboardMode.Local => Strings.StartParams_Rdp_KeysLocal,
            RdpKeyboardMode.FullScreenOnly => Strings.StartParams_Rdp_KeysFullScreen,
            _ => Strings.StartParams_Rdp_KeysSession,
        });
        if (s.RemoteAppProgram.Length > 0)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.StartParams_Rdp_RemoteApp, s.RemoteAppProgram));
        else if (s.StartProgram.Length > 0)
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.StartParams_Rdp_StartProgram, s.StartProgram));
        if (s.OpenExternally)
            lines.Add(Strings.StartParams_Rdp_External);
        return lines;
    }
}
