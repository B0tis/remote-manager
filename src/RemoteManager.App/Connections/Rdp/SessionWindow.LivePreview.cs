using Avalonia.Controls;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Preview;

namespace RemoteManager.App.Connections.Rdp;

/// <summary>
/// Live preview of the embedded session: the overview's <see cref="LivePreviewScheduler"/> calls
/// <see cref="CaptureAsync"/> every few seconds. The copy uses the same path as the disconnect snapshot
/// (<see cref="SessionCapture"/>, PrintWindow without focus or input changes); scaling and PNG encoding run
/// on the thread pool.
/// </summary>
public partial class SessionWindow : ILivePreviewSource
{
    // Counts SaveSnapshot calls: a full snapshot (disconnect, close, manual) written while a live capture was
    // being encoded wins over that older, smaller picture.
    private int savedSnapshots;

    public bool IsLive => IsLoggedIn && !disposed;

    /// <summary>A minimized RDP client stops painting (black or stale frames), so the last good image stays.</summary>
    public bool IsMinimized => WindowState == WindowState.Minimized;

    public bool IsLivePreviewAllowed => Connection.Model.SettingsAs<RdpSettings>().LivePreview;

    public async Task<bool> CaptureAsync()
    {
        SessionFrame? frame = SessionCapture.Capture(RdpView.HostWindowHandle);
        if (frame is null || frame.IsBlank)
        {
            Log($"Live preview: capture {(frame is null ? "failed" : "was blank")}, keeping the last image.");
            return false;
        }

        lastGoodFrame = frame;
        int snapshots = savedSnapshots;
        byte[] png = await LivePreviewImage.EncodeAsync(frame.Pixels, frame.Width, frame.Height);
        if (!IsLive || snapshots != savedSnapshots)
            return false;

        LivePreviewImage.Write(Connection.Model.ResolvedPreviewPath, png);
        return true;
    }
}
