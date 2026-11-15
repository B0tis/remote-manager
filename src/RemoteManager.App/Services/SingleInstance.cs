using System.Security.Cryptography;
using System.Text;
using RemoteManager.Core;

namespace RemoteManager.App.Services;

/// <summary>
/// One Remote-Manager per data folder. With "close to tray" the app keeps running after its window is closed, and
/// starting it again from the Start menu would otherwise open a second process on the same database (two copies of
/// the library overwriting each other, two tray icons, the hotkey taken). The second start asks the first to show
/// its window and ends. The names include the data folder, so a test run on a scratch folder is its own instance.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle showRequest;
    private readonly RegisteredWaitHandle? registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequest, Action onShowRequest)
    {
        this.mutex = mutex;
        this.showRequest = showRequest;
        registration = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => onShowRequest(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// The first instance gets an object to keep for its lifetime; <paramref name="onShowRequest"/> runs on a thread-pool
    /// thread when another start asks for the window. A later start signals the first one and gets null.
    /// </summary>
    public static SingleInstance? TryStart(Action onShowRequest)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppPaths.Root).ToUpperInvariant())))[..16];
        var mutex = new Mutex(initiallyOwned: true, $@"Local\RemoteManager.Instance.{id}", out bool created);
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\RemoteManager.Show.{id}");
        if (created)
            return new SingleInstance(mutex, showRequest, onShowRequest);

        showRequest.Set();
        showRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        registration?.Unregister(null);
        showRequest.Dispose();
        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released on another thread or already: nothing to do.
        }

        mutex.Dispose();
    }
}
