using System.Text.Json;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What a block parking with <see cref="Model.WorkflowSuspensionKind.WaitTemplate"/> hands the executor: the name of the
/// parked node's <c>Expression&lt;Boolean&gt;</c> input, whose compiled tree the waker evaluates until it answers true.</summary>
/// <remarks>A park whose <see cref="Input"/> names no such input of the node fails the node with <c>suspend-config-invalid</c>.</remarks>
public sealed record WorkflowWaitTemplateConfig
{
    private readonly string input = null!;

    /// <summary>The parked node's <c>Expression&lt;Boolean&gt;</c> input; an empty name throws <see cref="ArgumentException"/>.</summary>
    public required string Input
    {
        get => input;
        init => input = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A template wait names the Expression<Boolean> input it waits on.", nameof(Input))
            : value;
    }

    /// <summary>A poll interval in seconds, kept in the stored row's config for a host that reads it; the library's waker does not.</summary>
    public int? PollIntervalSecondsOverride { get; init; }
}

internal sealed record WorkflowStoredTemplateConfig(string Input, int? PollIntervalSecondsOverride, string Expression);

/// <summary>What a block parking with <see cref="Model.WorkflowSuspensionKind.Predicate"/> hands the executor: its node's input the
/// waker evaluates on a host's wake, the keys a host finds the row by, and the host's own data. The block implements <c>IWorkflowResumedOutputs</c>.</summary>
public sealed record WorkflowPredicateWaitConfig(string Input, IReadOnlyCollection<string> Keys, JsonElement? Host)
{
    /// <summary>The parked node's <c>Expression&lt;Boolean&gt;</c> input; an empty name throws <see cref="ArgumentException"/>, and one naming
    /// no such input fails the park with <c>suspend-config-invalid</c>.</summary>
    public string Input { get; } = string.IsNullOrWhiteSpace(Input)
        ? throw new ArgumentException("A predicate wait names the Expression<Boolean> input it waits on.", nameof(Input))
        : Input;

    /// <summary>Each key is a dependency key of the row behind <see cref="Hosting.WorkflowDependencyKeys.PredicatePrefix"/>; a blank key
    /// throws <see cref="ArgumentException"/>.</summary>
    public IReadOnlyCollection<string> Keys { get; } = Keys is null
        ? throw new ArgumentNullException(nameof(Keys))
        : Keys.Any(string.IsNullOrWhiteSpace)
            ? throw new ArgumentException("A predicate wait's key is blank.", nameof(Keys))
            : [.. Keys];

    /// <summary>The host's own data, stored with the row and never read by the library.</summary>
    public JsonElement? Host { get; } = Host?.Clone();
}

/// <summary>What a <see cref="Model.WorkflowSuspensionKind.WaitForTrigger"/> row's config says: the trigger
/// ids any one of which resumes it. Nothing in the library fires those, so a host satisfies these itself.</summary>
public sealed record WorkflowWaitForTriggerConfig(IReadOnlyCollection<string> TriggerIds);

/// <summary>What a <see cref="Model.WorkflowSuspensionKind.Sleep"/> row's config says: how long the block was
/// asked to sleep and when that ends. The row's timeout is the same instant, which is what the waker reads.</summary>
public sealed record WorkflowSleepConfig(long DurationMs, DateTimeOffset WakeAtUtc);

/// <summary>The values a wait's outcome (<c>outcome</c>) takes.</summary>
public static class WorkflowWaitOutcomes
{
    /// <summary>What it was waiting for happened.</summary>
    public const string Satisfied = "satisfied";

    /// <summary>It did not happen before the block's timeout.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>The condition threw: the outcome's <c>error</c> fails the resumed node through its policy.</summary>
    public const string Faulted = "faulted";

    /// <summary>The waker could not compile the pinned version or read the parked payload: the resumer abandons the run.</summary>
    public const string Unevaluable = "unevaluable";
}
