using Avalonia.Media;
using RemoteManager.App.Resources;
using RemoteManager.Core.Model;
using RemoteManager.Core.Status;

namespace RemoteManager.App.Connections.RemoteTools;

public sealed class TeamViewerHandler : RemoteToolHandler
{
    public override ConnectionType Type => ConnectionType.TeamViewer;
    public override string DisplayName => "TeamViewer";
    public override string Initials => "TV";
    public override Color AccentColor => Color.FromRgb(0x0E, 0xA5, 0xE9);
    public override string StatusHint => Strings.Status_Hint_TeamViewer;

    /// <summary>Online state from the TeamViewer Web API (needs a token in the remote-tool settings).</summary>
    public override IStatusProbe? StatusProbe => TeamViewerAccount.StatusProbe;
}

public sealed class AnyDeskHandler : RemoteToolHandler
{
    public override ConnectionType Type => ConnectionType.AnyDesk;
    public override string DisplayName => "AnyDesk";
    public override string Initials => "AD";
    public override Color AccentColor => Color.FromRgb(0xEF, 0x44, 0x44);
}

public sealed class RustDeskHandler : RemoteToolHandler
{
    public override ConnectionType Type => ConnectionType.RustDesk;
    public override string DisplayName => "RustDesk";
    public override string Initials => "RD";
    public override Color AccentColor => Color.FromRgb(0x8B, 0x5C, 0xF6);
}
