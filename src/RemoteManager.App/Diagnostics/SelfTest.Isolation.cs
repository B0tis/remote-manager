using Avalonia.Input.Platform;

namespace RemoteManager.App.Diagnostics;

/// <summary>
/// Keeps self-test runs from disturbing each other (several worktrees, CI) and from other programs:
/// every Credential Manager target and TERMSRV host carries a per-run suffix, and the checks that put
/// something on the clipboard take a machine-wide lock and read it back with retries.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Suffix of this run: process id plus a random part, so leftovers of an aborted run never match either.</summary>
    private static readonly string RunId = $"{Environment.ProcessId:x}{Guid.NewGuid().ToString("N")[..4]}";

    /// <summary>A connection or credential id of this run: "selftest-&lt;name&gt;-&lt;run&gt;".</summary>
    private static string TestId(string name) => $"selftest-{name}-{RunId}";

    /// <summary>
    /// Runs <paramref name="checks"/> while no other self-test uses the clipboard, and puts the user's text back
    /// afterwards. The clipboard is per logon session, and so is the "Local\" mutex.
    /// </summary>
    private static async Task WithClipboardAsync(IClipboard clipboard, Func<Task> checks)
    {
        using var gate = new Mutex(false, @"Local\RemoteManager.SelfTest.Clipboard");
        bool owned = false;
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (!owned)
        {
            try
            {
                owned = gate.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // A run that crashed while holding it; the mutex is ours now.
                owned = true;
            }

            if (owned)
                break;
            if (DateTime.UtcNow > giveUp)
            {
                Log("  clipboard lock not free after 3 min; running the clipboard checks anyway");
                break;
            }

            // Never block the UI thread: the other run may need a while.
            await Task.Delay(200);
        }

        string? user = await ReadClipboardAsync(clipboard, _ => true, TimeSpan.FromSeconds(2));
        try
        {
            await checks();
        }
        finally
        {
            if (user is not null)
                await clipboard.SetTextAsync(user);
            // The selftest's continuations run on the UI thread, which took the mutex (it has thread affinity).
            if (owned)
            {
                try
                {
                    gate.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Resumed on another thread after all; closing the handle frees it when this process ends.
                    Log("  clipboard lock released by another thread; it frees with this process");
                }
            }
        }
    }

    /// <summary>
    /// The clipboard text once <paramref name="until"/> accepts it, or the last value read after <paramref name="timeout"/>
    /// (default 3 s). Another program may hold the clipboard open for a moment, which makes a single read fail.
    /// </summary>
    private static async Task<string?> ReadClipboardAsync(IClipboard clipboard, Func<string?, bool> until, TimeSpan? timeout = null)
    {
        DateTime end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        string? text = null;
        while (true)
        {
            try
            {
                text = await clipboard.TryGetTextAsync();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                text = null;
            }

            if (until(text) || DateTime.UtcNow > end)
                return text;
            await Task.Delay(100);
        }
    }
}
