using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>A workflow's own answer to how often its runs write their row, stored as JSON on the workflow the
/// way its retry policy is. Null on the workflow means the engine's <see cref="RunStateWriteMode"/>.</summary>
/// <remarks><c>Mode</c> names one of <see cref="RunStateWriteModeKind"/>;
/// <c>HeartbeatSeconds</c> belongs to <see cref="RunStateWriteModeKind.Heartbeat"/> and to nothing else.
/// What a policy is *allowed* to say depends on the host's settings, which no stored document can see — see
/// <see cref="NeedsTheHeartbeat"/> and <see cref="OutlivesTheCutoff"/>, which the Editor refuses a save with and
/// a run falls back on.</remarks>
public sealed class WorkflowCheckpointPolicy
{
    /// <summary>One of <see cref="RunStateWriteModeKind"/>, by name.</summary>
    public string? Mode { get; set; }

    /// <summary>How old the last write may be before the next block writes again, for
    /// <see cref="RunStateWriteModeKind.Heartbeat"/>. Required there, and meaningless anywhere else.</summary>
    public int? HeartbeatSeconds { get; set; }

    /// <summary>Reads a stored policy into the mode a run would execute under. Returns null when it read, and
    /// why it did not when it did not; <paramref name="mode"/> is null for both no policy and an unreadable one,
    /// which is the engine's mode either way.</summary>
    public static string? Read(string? policyJson, JsonSerializerOptions jsonOptions, out RunStateWriteMode? mode)
    {
        mode = null;

        if (string.IsNullOrWhiteSpace(policyJson))
            return null;

        WorkflowCheckpointPolicy? policy;
        try
        {
            policy = JsonSerializer.Deserialize<WorkflowCheckpointPolicy>(policyJson, JsonMembers.Refusing(jsonOptions));
        }
        catch (JsonException ex)
        {
            return $"CheckpointPolicyJson is not JSON this package reads: {ex.Message}";
        }

        if (policy is null)
            return "CheckpointPolicyJson is the literal null, which says nothing; leave the column empty instead.";

        if (!Enum.TryParse<RunStateWriteModeKind>(policy.Mode, ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind)
            || !string.Equals(kind.ToString(), policy.Mode, StringComparison.Ordinal))
        {
            return $"CheckpointPolicyJson names the mode '{policy.Mode}', which is none of "
                + $"{string.Join(", ", Enum.GetNames<RunStateWriteModeKind>())}.";
        }

        if (kind != RunStateWriteModeKind.Heartbeat && policy.HeartbeatSeconds is not null)
            return $"CheckpointPolicyJson names the mode {kind}, which writes on no interval, and carries HeartbeatSeconds.";

        switch (kind)
        {
            case RunStateWriteModeKind.EveryBlock:
                mode = RunStateWriteMode.EveryBlock;
                return null;

            case RunStateWriteModeKind.BeforeNonIdempotentOnly:
                mode = RunStateWriteMode.BeforeNonIdempotentOnly;
                return null;

            case RunStateWriteModeKind.Heartbeat:
                if (policy.HeartbeatSeconds is not { } seconds)
                    return "CheckpointPolicyJson names the mode Heartbeat and no HeartbeatSeconds, so there is no interval to write on.";

                if (seconds <= 0)
                    return $"CheckpointPolicyJson's HeartbeatSeconds is {seconds}; an interval must be positive.";

                mode = RunStateWriteMode.Heartbeat(TimeSpan.FromSeconds(seconds));
                return null;

            default:
                return $"CheckpointPolicyJson names the mode {kind}, which this package has no reading for.";
        }
    }

    /// <summary>Why <paramref name="mode"/> is not allowed under these settings, or null when it is. Both halves
    /// of what <c>AddWorkflowEngine</c> refuses a registration for: a per-workflow policy that cleared neither
    /// would be a way to get two replicas executing one run through a text column.</summary>
    public static string? RefusedBy(RunStateWriteMode mode, TimeSpan? runHeartbeatInterval, TimeSpan abandonedAfter) =>
        NeedsTheHeartbeat(mode, runHeartbeatInterval) ?? OutlivesTheCutoff(mode, abandonedAfter);

    /// <summary>A mode that writes the row before some blocks only, with no run heartbeat behind it: between two
    /// writes the heartbeat is what stops a replica whose row a peer took.</summary>
    public static string? NeedsTheHeartbeat(RunStateWriteMode mode, TimeSpan? runHeartbeatInterval)
    {
        ArgumentNullException.ThrowIfNull(mode);

        return mode.Kind != RunStateWriteModeKind.EveryBlock && runHeartbeatInterval is null
            ? $"RunStateWriteMode {mode.Kind} writes the row before some blocks only, and needs "
                + "RunHeartbeatInterval: between two writes the heartbeat is what stops a replica whose row a peer took. "
                + "Set RunHeartbeatInterval, or use RunStateWriteMode.EveryBlock."
            : null;
    }

    /// <summary>A heartbeat interval at or over the abandoned cutoff: the checkpoint is then older than what a
    /// peer reads, and a recovery re-enters from blocks behind where the run is.</summary>
    public static string? OutlivesTheCutoff(RunStateWriteMode mode, TimeSpan abandonedAfter)
    {
        ArgumentNullException.ThrowIfNull(mode);

        return mode.Interval is { } writeEvery && writeEvery >= abandonedAfter
            ? $"RunStateWriteMode's heartbeat interval ({writeEvery}) must be shorter than Recovery.AbandonedAfter "
                + $"({abandonedAfter}): a checkpoint older than the cutoff is one a recovery re-enters "
                + "from blocks behind where the run is."
            : null;
    }

    /// <summary>The mode a run of this workflow executes under: its own policy where that reads and these
    /// settings allow it, and the engine's where it does not. A policy the host's configuration has since put
    /// out of bounds is logged and left behind — a configuration change must not fail runs.</summary>
    public static RunStateWriteMode For(
        string? policyJson,
        RunStateWriteMode engineMode,
        TimeSpan? runHeartbeatInterval,
        TimeSpan abandonedAfter,
        JsonSerializerOptions jsonOptions,
        ILogger logger)
    {
        var refusal = Read(policyJson, jsonOptions, out var stored) ?? (stored is null ? null : RefusedBy(stored, runHeartbeatInterval, abandonedAfter));

        if (refusal is null)
            return stored ?? engineMode;

        WarnOnce(logger, refusal, engineMode);
        return engineMode;
    }

    private static readonly HashSet<string> WarnedAbout = [];

    private const int DistinctReasonsRemembered = 64;

    private static void WarnOnce(ILogger logger, string refusal, RunStateWriteMode engineMode)
    {
        lock (WarnedAbout)
        {
            if (WarnedAbout.Count < DistinctReasonsRemembered && !WarnedAbout.Add(refusal))
                return;

            if (WarnedAbout.Count >= DistinctReasonsRemembered && WarnedAbout.Contains(refusal))
                return;
        }

        logger.LogWarning(
            "A workflow's CheckpointPolicyJson is not being used, and its runs write under the engine's {Mode}: {Reason}",
            engineMode.Kind, refusal);
    }
}
