using System.Globalization;

namespace RemoteManager.Core.Preview;

/// <summary>An open session whose picture can become the connection's preview (embedded RDP, VNC).</summary>
public interface ILivePreviewSource
{
    /// <summary>Connected and showing the remote screen.</summary>
    bool IsLive { get; }

    /// <summary>The session window is minimized; captures are skipped then and the last image stays.</summary>
    bool IsMinimized { get; }

    /// <summary>The connection's own "Live-Vorschau für diese Verbindung" setting.</summary>
    bool IsLivePreviewAllowed { get; }

    /// <summary>
    /// Captures the session and overwrites the connection's preview file. False when there was no usable
    /// picture (blank, failed, the session ended meanwhile); the last image stays then.
    /// </summary>
    Task<bool> CaptureAsync();
}

/// <summary>
/// Refreshes the preview of every registered live session every <see cref="Interval"/>. Driven by
/// <see cref="TickAsync"/> (a UI timer in the app, the clock in the tests); never captures a source twice at once.
/// </summary>
public sealed class LivePreviewScheduler
{
    /// <summary><c>app_settings</c> key: interval in seconds, "0" = off.</summary>
    public const string IntervalSetting = "preview.live.interval";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>The intervals the settings menu offers; <see cref="TimeSpan.Zero"/> is "Aus".</summary>
    public static readonly IReadOnlyList<TimeSpan> Choices =
        [TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly Dictionary<ILivePreviewSource, Entry> sources = new(ReferenceEqualityComparer.Instance);

    /// <summary>Time between two captures of a session; <see cref="TimeSpan.Zero"/> turns live previews off.</summary>
    public TimeSpan Interval { get; set; } = DefaultInterval;

    /// <summary>Raised on the ticking thread after a source wrote a new preview.</summary>
    public event Action<ILivePreviewSource, DateTimeOffset>? Captured;

    /// <summary>Reads <see cref="IntervalSetting"/>: missing or invalid is the default, "0" is off.</summary>
    public static TimeSpan ParseInterval(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 0)
            return DefaultInterval;
        return seconds == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(seconds, MinInterval.TotalSeconds));
    }

    public static string FormatInterval(TimeSpan interval) => ((int)interval.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    public void Add(ILivePreviewSource source) => sources.TryAdd(source, new Entry());

    /// <summary>The session closed: no more captures, and <see cref="LiveAt"/> is null.</summary>
    public void Remove(ILivePreviewSource source) => sources.Remove(source);

    /// <summary>
    /// Time of the last live capture while the source is live and allowed to show one, otherwise null
    /// (the tile shows no "Live" badge then). A minimized session keeps its last time, so the age grows.
    /// </summary>
    public DateTimeOffset? LiveAt(ILivePreviewSource source) =>
        Interval > TimeSpan.Zero && source.IsLive && source.IsLivePreviewAllowed && sources.TryGetValue(source, out Entry? entry)
            ? entry.LastCapture
            : null;

    /// <summary>Captures every source that is due at <paramref name="now"/>, one after another.</summary>
    public async Task TickAsync(DateTimeOffset now)
    {
        if (Interval <= TimeSpan.Zero)
            return;

        foreach ((ILivePreviewSource source, Entry entry) in sources.ToList())
        {
            if (!source.IsLive)
            {
                // Disconnected: start over after a reconnect instead of showing an old capture as live.
                entry.LastAttempt = entry.LastCapture = null;
                continue;
            }

            if (entry.Busy || !source.IsLivePreviewAllowed || source.IsMinimized
                || (entry.LastAttempt is { } last && now - last < Interval))
                continue;

            entry.LastAttempt = now;
            entry.Busy = true;
            bool captured;
            try
            {
                captured = await source.CaptureAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A preview is never worth breaking the session or the overview over; the last image stays.
                captured = false;
            }
            finally
            {
                entry.Busy = false;
            }

            if (captured && sources.ContainsKey(source))
            {
                entry.LastCapture = now;
                Captured?.Invoke(source, now);
            }
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset? LastAttempt;
        public DateTimeOffset? LastCapture;
        public bool Busy;
    }
}
