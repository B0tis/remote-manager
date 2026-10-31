using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Preview;

namespace RemoteManager.App.Connections.Vnc;

/// <summary>
/// Live preview of the embedded VNC session: the frame is already in memory (<see cref="VncView.Snapshot"/>),
/// so a capture is a copy plus scaling and PNG encoding on the thread pool.
/// </summary>
public partial class VncSessionWindow : ILivePreviewSource
{
    // Counts SaveSnapshot calls: the full snapshot written on disconnect or close wins over a live capture
    // that was still being encoded.
    private int savedSnapshots;

    public bool IsLive => IsConnected && !closing;

    /// <summary>The library keeps updating the in-memory frame while the window is minimized, so it is never skipped.</summary>
    public bool IsMinimized => false;

    public bool IsLivePreviewAllowed => Connection.Model.SettingsAs<VncSettings>().LivePreview;

    public async Task<bool> CaptureAsync()
    {
        if (View.FrameCount == 0 || View.Snapshot() is not { } frame)
            return false;

        int snapshots = savedSnapshots;
        byte[] png = await LivePreviewImage.EncodeAsync(frame);
        if (!IsLive || snapshots != savedSnapshots)
            return false;

        LivePreviewImage.Write(Connection.Model.ResolvedPreviewPath, png);
        return true;
    }
}
