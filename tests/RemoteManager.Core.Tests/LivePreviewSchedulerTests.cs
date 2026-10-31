using RemoteManager.Core.Model;
using RemoteManager.Core.Model.Settings;
using RemoteManager.Core.Preview;

namespace RemoteManager.Core.Tests;

public sealed class LivePreviewSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConnectedSessionIsCapturedRightAwayAndThenEveryInterval()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(30) };
        var session = new FakeSource();
        scheduler.Add(session);

        await scheduler.TickAsync(Start);
        await scheduler.TickAsync(Start.AddSeconds(5));
        await scheduler.TickAsync(Start.AddSeconds(29));
        Assert.Equal(1, session.Captures);

        await scheduler.TickAsync(Start.AddSeconds(30));
        await scheduler.TickAsync(Start.AddSeconds(35));
        Assert.Equal(2, session.Captures);
        Assert.Equal(Start.AddSeconds(30), scheduler.LiveAt(session));
    }

    [Fact]
    public async Task CapturedEventTellsTheOverviewWhichSessionHasANewPicture()
    {
        var scheduler = new LivePreviewScheduler();
        var first = new FakeSource();
        var second = new FakeSource { Allowed = false };
        scheduler.Add(first);
        scheduler.Add(second);
        List<(ILivePreviewSource Source, DateTimeOffset At)> events = [];
        scheduler.Captured += (source, at) => events.Add((source, at));

        await scheduler.TickAsync(Start);

        Assert.Equal([(first, Start)], events);
    }

    [Fact]
    public async Task OffCapturesNothingAndShowsNoLiveBadge()
    {
        var scheduler = new LivePreviewScheduler();
        var session = new FakeSource();
        scheduler.Add(session);
        await scheduler.TickAsync(Start);

        scheduler.Interval = TimeSpan.Zero;
        await scheduler.TickAsync(Start.AddMinutes(10));

        Assert.Equal(1, session.Captures);
        Assert.Null(scheduler.LiveAt(session));
    }

    [Fact]
    public async Task OptedOutConnectionIsNeverCaptured()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(10) };
        var sensitive = new FakeSource { Allowed = false };
        scheduler.Add(sensitive);

        for (int i = 0; i < 5; i++)
            await scheduler.TickAsync(Start.AddSeconds(i * 10));

        Assert.Equal(0, sensitive.Captures);
        Assert.Null(scheduler.LiveAt(sensitive));
    }

    [Fact]
    public async Task OptingOutDuringASessionStopsCapturesAndHidesTheBadge()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(10) };
        var session = new FakeSource();
        scheduler.Add(session);
        await scheduler.TickAsync(Start);

        session.Allowed = false;
        await scheduler.TickAsync(Start.AddSeconds(10));

        Assert.Equal(1, session.Captures);
        Assert.Null(scheduler.LiveAt(session));
    }

    [Fact]
    public async Task MinimizedSessionKeepsItsLastPictureAndResumesWhenRestored()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(10) };
        var session = new FakeSource();
        scheduler.Add(session);
        await scheduler.TickAsync(Start);

        session.Minimized = true;
        await scheduler.TickAsync(Start.AddSeconds(10));
        await scheduler.TickAsync(Start.AddSeconds(60));
        Assert.Equal(1, session.Captures);
        // Still live, but the age grows: the badge keeps showing the last capture time.
        Assert.Equal(Start, scheduler.LiveAt(session));

        session.Minimized = false;
        await scheduler.TickAsync(Start.AddSeconds(65));
        Assert.Equal(2, session.Captures);
        Assert.Equal(Start.AddSeconds(65), scheduler.LiveAt(session));
    }

    [Fact]
    public async Task BlankCaptureKeepsTheLastGoodTimeAndRetriesAfterTheInterval()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(10) };
        var session = new FakeSource();
        scheduler.Add(session);
        await scheduler.TickAsync(Start);

        session.Succeeds = false;
        await scheduler.TickAsync(Start.AddSeconds(10));
        await scheduler.TickAsync(Start.AddSeconds(15));
        Assert.Equal(2, session.Captures);
        Assert.Equal(Start, scheduler.LiveAt(session));

        session.Succeeds = true;
        await scheduler.TickAsync(Start.AddSeconds(20));
        Assert.Equal(Start.AddSeconds(20), scheduler.LiveAt(session));
    }

    [Fact]
    public async Task FailingCaptureDoesNotBreakTheOtherSessions()
    {
        var scheduler = new LivePreviewScheduler();
        var broken = new FakeSource { Throws = true };
        var healthy = new FakeSource();
        scheduler.Add(broken);
        scheduler.Add(healthy);

        await scheduler.TickAsync(Start);

        Assert.Null(scheduler.LiveAt(broken));
        Assert.Equal(Start, scheduler.LiveAt(healthy));
    }

    [Fact]
    public async Task ClosedSessionIsNoLongerCapturedNorLive()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(10) };
        var session = new FakeSource();
        scheduler.Add(session);
        await scheduler.TickAsync(Start);

        scheduler.Remove(session);
        await scheduler.TickAsync(Start.AddSeconds(10));
        await scheduler.TickAsync(Start.AddSeconds(20));

        Assert.Equal(1, session.Captures);
        Assert.Null(scheduler.LiveAt(session));
    }

    [Fact]
    public async Task SessionClosedDuringACaptureRaisesNoEvent()
    {
        var scheduler = new LivePreviewScheduler();
        var session = new FakeSource();
        session.OnCapture = () => scheduler.Remove(session);
        scheduler.Add(session);
        bool raised = false;
        scheduler.Captured += (_, _) => raised = true;

        await scheduler.TickAsync(Start);

        Assert.False(raised);
        Assert.Null(scheduler.LiveAt(session));
    }

    [Fact]
    public async Task DisconnectedSessionStopsAndCapturesAgainRightAfterReconnect()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(30) };
        var session = new FakeSource();
        scheduler.Add(session);
        await scheduler.TickAsync(Start);

        session.Live = false;
        await scheduler.TickAsync(Start.AddSeconds(30));
        Assert.Null(scheduler.LiveAt(session));
        Assert.Equal(1, session.Captures);

        session.Live = true;
        await scheduler.TickAsync(Start.AddSeconds(35));
        Assert.Equal(2, session.Captures);
        Assert.Equal(Start.AddSeconds(35), scheduler.LiveAt(session));
    }

    [Fact]
    public async Task SlowCaptureIsNotStartedTwice()
    {
        var scheduler = new LivePreviewScheduler { Interval = TimeSpan.FromSeconds(1) };
        var gate = new TaskCompletionSource<bool>();
        var session = new FakeSource { Gate = gate.Task };
        scheduler.Add(session);

        Task first = scheduler.TickAsync(Start);
        await scheduler.TickAsync(Start.AddSeconds(5));
        Assert.Equal(1, session.Captures);

        gate.SetResult(true);
        await first;
        Assert.Equal(Start, scheduler.LiveAt(session));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("abc", 30)]
    [InlineData("-5", 30)]
    [InlineData("0", 0)]
    [InlineData("10", 10)]
    [InlineData("300", 300)]
    public void IntervalSettingDefaultsToThirtySecondsAndZeroMeansOff(string? stored, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), LivePreviewScheduler.ParseInterval(stored));
    }

    [Fact]
    public void IntervalSettingRoundTrips()
    {
        foreach (TimeSpan choice in LivePreviewScheduler.Choices)
            Assert.Equal(choice, LivePreviewScheduler.ParseInterval(LivePreviewScheduler.FormatInterval(choice)));
    }

    [Fact]
    public void ConnectionsSavedBeforeTheOptionKeepLivePreviewOnAndTheOptOutIsSaved()
    {
        var oldRdp = (RdpSettings)ConnectionSettings.Deserialize(ConnectionType.Rdp, """{"display":"SmartSizing"}""");
        var oldVnc = (VncSettings)ConnectionSettings.Deserialize(ConnectionType.Vnc, """{"viewOnly":true}""");
        Assert.True(oldRdp.LivePreview);
        Assert.True(oldVnc.LivePreview);

        string json = ConnectionSettings.Serialize(new RdpSettings { LivePreview = false });
        Assert.False(((RdpSettings)ConnectionSettings.Deserialize(ConnectionType.Rdp, json)).LivePreview);
    }

    private sealed class FakeSource : ILivePreviewSource
    {
        public bool Live { get; set; } = true;
        public bool Minimized { get; set; }
        public bool Allowed { get; set; } = true;
        public bool Succeeds { get; set; } = true;
        public bool Throws { get; set; }
        public Task<bool>? Gate { get; set; }
        public Action? OnCapture { get; set; }
        public int Captures { get; private set; }

        public bool IsLive => Live;
        public bool IsMinimized => Minimized;
        public bool IsLivePreviewAllowed => Allowed;

        public async Task<bool> CaptureAsync()
        {
            Captures++;
            OnCapture?.Invoke();
            if (Throws)
                throw new IOException("capture failed");
            if (Gate is not null)
                await Gate;
            return Succeeds;
        }
    }
}
