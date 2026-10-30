using Avalonia.Input;
using Avalonia.Input.Platform;

namespace RemoteManager.App;

/// <summary>
/// Puts a password on the clipboard and clears it again after a delay, but only if the clipboard
/// still holds that password (anything the user copied since stays). The text is marked so Windows
/// keeps it out of clipboard history and cloud clipboard.
/// </summary>
internal static class SecretClipboard
{
    public static readonly TimeSpan DefaultClearDelay = TimeSpan.FromSeconds(30);

    // Registered clipboard formats documented for Windows clipboard history (DWORD 0 = do not include).
    private static readonly DataFormat<byte[]> CanIncludeInClipboardHistory = DataFormat.CreateBytesPlatformFormat("CanIncludeInClipboardHistory");
    private static readonly DataFormat<byte[]> CanUploadToCloudClipboard = DataFormat.CreateBytesPlatformFormat("CanUploadToCloudClipboard");

    private static IClipboard? pendingClipboard;
    private static string? pendingSecret;
    private static CancellationTokenSource? pendingClear;

    public static bool HasPending => pendingSecret is not null;

    /// <summary>Copies <paramref name="secret"/> and schedules the clear. A newer copy replaces the pending clear.</summary>
    public static async Task CopyAsync(IClipboard clipboard, string secret, TimeSpan? clearAfter = null)
    {
        var item = DataTransferItem.CreateText(secret);
        item.Set(CanIncludeInClipboardHistory, new byte[4]);
        item.Set(CanUploadToCloudClipboard, new byte[4]);
        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);

        pendingClear?.Cancel();
        var cancel = pendingClear = new CancellationTokenSource();
        pendingClipboard = clipboard;
        pendingSecret = secret;
        _ = ClearLaterAsync(clearAfter ?? DefaultClearDelay, cancel.Token);
    }

    private static async Task ClearLaterAsync(TimeSpan delay, CancellationToken cancel)
    {
        try
        {
            await Task.Delay(delay, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ClearPendingAsync();
    }

    /// <summary>Clears now if the clipboard still holds the last copied secret (also used on app exit).</summary>
    public static async Task ClearPendingAsync()
    {
        IClipboard? clipboard = pendingClipboard;
        string? secret = pendingSecret;
        pendingClear?.Cancel();
        pendingClear = null;
        pendingClipboard = null;
        pendingSecret = null;
        if (clipboard is null || secret is null)
            return;

        try
        {
            if (await clipboard.TryGetTextAsync() == secret)
                await clipboard.ClearAsync();
        }
        catch (Exception)
        {
            // Another process may hold the clipboard open; nothing sensible to do then.
        }
    }
}
