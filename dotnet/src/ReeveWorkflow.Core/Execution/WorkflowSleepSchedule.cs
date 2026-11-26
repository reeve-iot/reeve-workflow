namespace ReeveWorkflow.Core.Execution;

/// <summary>The arithmetic a sleeping run does while it waits. A sleep is not one long delay: it wakes on a
/// poll interval to notice a cancellation it cannot be told about, and to say it is still alive.</summary>
public static class WorkflowSleepSchedule
{
    /// <summary>How long to wait next, or zero when the sleep is over. Never longer than a poll interval,
    /// because a run cancelled in the database has no other way to reach a replica that is asleep.</summary>
    public static TimeSpan NextWait(DateTimeOffset now, DateTimeOffset wakeUpAtUtc, TimeSpan pollInterval)
    {
        var remaining = wakeUpAtUtc - now;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return remaining < pollInterval ? remaining : pollInterval;
    }

    /// <summary>Whether this poll should also renew the sleeping row. A row whose last transition is older
    /// than the stale cutoff gets reclaimed and re-run, so a live sleeper has to keep saying so.</summary>
    public static bool ShouldRenew(DateTimeOffset now, DateTimeOffset lastRenewUtc, TimeSpan renewInterval) =>
        now - lastRenewUtc >= renewInterval;
}
