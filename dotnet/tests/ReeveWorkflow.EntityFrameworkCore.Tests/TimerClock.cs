namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class TimerClock(DateTimeOffset start) : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ClockTimer> timers = [];
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (sync)
            return now;
    }

    public int Armed
    {
        get
        {
            lock (sync)
                return timers.Count(t => t.Due is not null);
        }
    }

    public TimeSpan? NextWait
    {
        get
        {
            lock (sync)
            {
                var waits = timers.Where(t => t.Due is not null).Select(t => t.Due!.Value - now).ToList();
                return waits.Count == 0 ? null : waits.Min();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (sync)
            timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ClockTimer> due;
        lock (sync)
        {
            now += by;
            due = [.. timers.Where(t => t.Due is { } at && at <= now)];
            foreach (var timer in due)
                timer.Due = timer.Period is { } period && period > TimeSpan.Zero ? now + period : null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    public async Task WaitForArmedAsync(int count, TimeSpan deadline)
    {
        var until = DateTime.UtcNow + deadline;
        while (Armed < count)
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"{Armed} timer(s) armed after {deadline}, not {count}.");
            await Task.Delay(5);
        }
    }

    private sealed class ClockTimer(TimerClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.sync)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock.sync)
            {
                Due = null;
                clock.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
