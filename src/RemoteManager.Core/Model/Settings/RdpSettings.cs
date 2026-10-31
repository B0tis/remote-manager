namespace RemoteManager.Core.Model.Settings;

public enum RdpDisplayMode
{
    /// <summary>Session resolution follows the window (dynamic resolution).</summary>
    FitToWindow,
    /// <summary>Session keeps its resolution and is scaled into the window.</summary>
    SmartSizing,
    /// <summary><see cref="RdpSettings.DesktopWidth"/> × <see cref="RdpSettings.DesktopHeight"/>, scaled into the window.</summary>
    FixedResolution,
}

public enum RdpGatewayUsage { None, Always, Detect }

public enum RdpAudioMode { Local, Remote, None }

public enum RdpKeyboardMode
{
    /// <summary>Win, Alt+Tab and similar go to the session while it has focus.</summary>
    Session,
    Local,
    FullScreenOnly,
}

/// <summary>What the session may use from the local machine.</summary>
public sealed record RdpRedirections
{
    public bool Clipboard { get; init; } = true;
    public bool Drives { get; init; }
    public bool Printers { get; init; }
    public bool SmartCards { get; init; }
    public bool Ports { get; init; }
    public bool Microphone { get; init; }
    public RdpAudioMode Audio { get; init; } = RdpAudioMode.Local;
}

/// <summary>Start parameters of an RDP connection (embedded MsRdpEx control or external mstsc).</summary>
public sealed record RdpSettings : ConnectionSettings
{
    public RdpDisplayMode Display { get; init; } = RdpDisplayMode.FitToWindow;
    public int DesktopWidth { get; init; }
    public int DesktopHeight { get; init; }
    public bool UseMultimon { get; init; }
    public RdpKeyboardMode Keyboard { get; init; } = RdpKeyboardMode.Session;

    /// <summary>NLA / CredSSP.</summary>
    public bool EnableNla { get; init; } = true;

    /// <summary>0 connect anyway, 1 refuse on certificate problems, 2 warn.</summary>
    public int AuthenticationLevel { get; init; } = 2;

    /// <summary>Connect to the console / admin session.</summary>
    public bool AdminSession { get; init; }
    public bool RestrictedAdmin { get; init; }
    public bool RemoteCredentialGuard { get; init; }

    public string GatewayHost { get; init; } = "";
    public RdpGatewayUsage GatewayUsage { get; init; } = RdpGatewayUsage.None;

    /// <summary>True: the gateway uses the connection's credentials; false: <see cref="GatewayCredential"/>.</summary>
    public bool GatewayUsesConnectionCredentials { get; init; } = true;
    public string GatewayUserName { get; init; } = "";
    public CredentialRef? GatewayCredential { get; init; }

    public RdpRedirections Redirections { get; init; } = new();

    /// <summary>RemoteApp program (empty: full desktop).</summary>
    public string RemoteAppProgram { get; init; } = "";
    public string StartProgram { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";

    /// <summary>Open with mstsc instead of the embedded control.</summary>
    public bool OpenExternally { get; init; }

    /// <summary>SSH connection to tunnel through (Phase 3), or null.</summary>
    public string? JumpHostId { get; init; }

    /// <summary>The overview may show a periodically refreshed picture of the open session (off for sensitive hosts).</summary>
    public bool LivePreview { get; init; } = true;
}
