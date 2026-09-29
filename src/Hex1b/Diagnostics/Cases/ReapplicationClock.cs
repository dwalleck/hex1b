namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The virtual clock a re-applied model runs on. Time moves only when the reapplier advances it for a recorded
/// timeout, and timers fire synchronously as it passes them; however slowly the events are applied, no timer
/// fires on its own.
/// </summary>
internal sealed class ReapplicationClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<Timer> _timers = [];
    private long _now;

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Volatile.Read(ref _now));

    public override long GetTimestamp() => Volatile.Read(ref _now);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        lock (_gate)
            _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, firing each timer it passes at its due time, in due order.</summary>
    internal void Advance(TimeSpan delta)
    {
        var target = Volatile.Read(ref _now) + delta.Ticks;
        while (true)
        {
            Timer? next = null;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.Due <= target && (next is null || timer.Due < next.Due))
                        next = timer;
                }
            }

            if (next is null)
                break;
            Volatile.Write(ref _now, next.Due);
            next.Fire();
        }

        Volatile.Write(ref _now, target);
    }

    private sealed class Timer(ReapplicationClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due { get; private set; } = long.MaxValue;
        private long _period = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._now + Math.Max(0, dueTime.Ticks);
                _period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? -1 : period.Ticks;
            }
            return true;
        }

        public void Fire()
        {
            lock (clock._gate)
                Due = _period > 0 ? Due + _period : long.MaxValue;
            callback(state);
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                Due = long.MaxValue;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
