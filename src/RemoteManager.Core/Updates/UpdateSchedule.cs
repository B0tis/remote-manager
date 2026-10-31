using System.Globalization;

namespace RemoteManager.Core.Updates;

/// <summary>The check at startup runs at most once per <see cref="Interval"/> (last check in setting "update.lastCheck").</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(20);

    /// <summary>True when there was no check yet, the stored time is unreadable or in the future, or the last check is older than <see cref="Interval"/>.</summary>
    public static bool IsStartupCheckDue(string? lastCheck, DateTime utcNow) =>
        !DateTime.TryParse(lastCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime last)
        || last.ToUniversalTime() > utcNow
        || utcNow - last.ToUniversalTime() >= Interval;

    public static string Format(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
