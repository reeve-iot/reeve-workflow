using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The compilation sweep: <see cref="WorkflowCompilationSweepRunner"/>, which a host can call on demand
/// whatever these say, and the timer that calls it when <see cref="Enabled"/>. Read by <c>AddWorkflows</c>, which
/// refuses a value out of bounds with <see cref="InvalidOperationException"/> and registers a copy, so a change made
/// after that call has no effect.</summary>
public sealed class WorkflowCompilationSweepOptions
{
    /// <summary>The shortest <see cref="Interval"/> and <see cref="ReadinessRetry"/> accepted.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    /// <summary>The longest <see cref="Interval"/> accepted, and so the longest <see cref="ReadinessRetry"/>:
    /// 4 294 967 294 milliseconds, about 49.7 days, the longest whole-millisecond delay <c>Task.Delay</c> accepts.</summary>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>The largest <see cref="PageSize"/> accepted.</summary>
    public const int MaximumPageSize = 10_000;

    /// <summary>Whether a hosted service runs the sweep: once after the host has started, then every
    /// <see cref="Interval"/>. False unless set.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long after one run the next one starts. Six hours unless set; at least <see cref="MinimumInterval"/>
    /// and at most <see cref="MaximumInterval"/>.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How long after a run that found the database not ready the timer asks again, in place of
    /// <see cref="Interval"/>. One minute unless set; at least <see cref="MinimumInterval"/>, at most <see cref="Interval"/>.</summary>
    public TimeSpan ReadinessRetry { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary><see cref="WorkflowCompilationSweeper.Retention"/> for the registered sweeper. Thirty days unless set;
    /// at least <see cref="WorkflowCompilationSweeper.MinimumRetention"/>.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How many workflows one read of the listing takes. 500 unless set; 1 to <see cref="MaximumPageSize"/>.</summary>
    public int PageSize { get; set; } = 500;

    internal WorkflowCompilationSweepOptions CheckedCopy()
    {
        var copy = (WorkflowCompilationSweepOptions)MemberwiseClone();
        copy.RefuseOutOfBounds();
        return copy;
    }

    private void RefuseOutOfBounds()
    {
        if (Interval < MinimumInterval || Interval > MaximumInterval)
            throw Refused(nameof(Interval), Interval, $"at least {MinimumInterval} and at most {MaximumInterval}");
        if (ReadinessRetry < MinimumInterval || ReadinessRetry > Interval)
            throw Refused(nameof(ReadinessRetry), ReadinessRetry, $"at least {MinimumInterval} and at most Interval ({Interval})");
        if (Retention < WorkflowCompilationSweeper.MinimumRetention)
            throw Refused(nameof(Retention), Retention, $"at least {WorkflowCompilationSweeper.MinimumRetention}");
        if (PageSize is < 1 or > MaximumPageSize)
            throw Refused(nameof(PageSize), PageSize, $"1 to {MaximumPageSize}");
    }

    private static InvalidOperationException Refused(string option, object value, string bounds) =>
        new($"CompilationSweep.{option} is {value}; it must be {bounds}.");
}
