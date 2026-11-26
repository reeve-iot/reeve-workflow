using System.Text.Json;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What a resuming run decides before it re-enters the graph: why it woke up, and which versions it
/// is still pinned to. Both were reachable only through a suspension row, so neither had a test of its own.</summary>
public static class WorkflowResumeDecisions
{
    /// <summary>The key a dispatcher writes into the resumed block's outputs to say why it woke the run. It
    /// beats every inference below, because the dispatcher knows and the outputs can only be read.</summary>
    public const string ResumeReasonKey = "_resumeReason";

    /// <summary>The key the wait block itself writes. Its one interesting value is the timeout.</summary>
    public const string OutcomeKey = "outcome";

    /// <summary>The outcome value that means the wait expired rather than being satisfied.</summary>
    public const string TimedOutOutcome = "timed_out";

    internal const string LastEvaluationKey = "lastEvaluation";

    internal const string TriggerReason = "trigger";
    internal const string HelperChangeReason = "helper_change";
    internal const string PollReason = "poll";
    internal const string TimeoutReason = "timeout";
    internal const string SleepReason = "sleep";

    /// <summary>Why this run woke up, as an audit kind. Three sources in increasing authority: the kind of
    /// wait it was, whether the wait timed out, and what the dispatcher said outright.</summary>
    public static string ResolveResumeAuditKind(
        WorkflowSuspensionKind suspensionKind, IReadOnlyDictionary<string, object?> blockOutputs)
    {
        ArgumentNullException.ThrowIfNull(blockOutputs);

        var timedOut = ReadString(blockOutputs, OutcomeKey) == TimedOutOutcome;

        var kind = suspensionKind switch
        {
            WorkflowSuspensionKind.WaitForTrigger => WorkflowAuditEventKinds.ResumedByTrigger,
            WorkflowSuspensionKind.WaitTemplate => WorkflowAuditEventKinds.ResumedByPoll,
            WorkflowSuspensionKind.Sleep => WorkflowAuditEventKinds.ResumedBySleep,
            WorkflowSuspensionKind.SubWorkflow => WorkflowAuditEventKinds.ResumedBySubWorkflow,
            WorkflowSuspensionKind.Predicate => WorkflowAuditEventKinds.ResumedByPoll,
            _ => WorkflowAuditEventKinds.ResumedByTrigger
        };

        if (timedOut)
            kind = WorkflowAuditEventKinds.ResumedByTimeout;

        return ReadString(blockOutputs, ResumeReasonKey) switch
        {
            TriggerReason => WorkflowAuditEventKinds.ResumedByTrigger,
            HelperChangeReason => WorkflowAuditEventKinds.ResumedByHelperChange,
            PollReason => WorkflowAuditEventKinds.ResumedByPoll,
            TimeoutReason => WorkflowAuditEventKinds.ResumedByTimeout,
            SleepReason => WorkflowAuditEventKinds.ResumedBySleep,
            _ => kind
        };
    }

    /// <summary>The version manifest a resumed run re-enters under. A run that recorded no manifest is still
    /// pinned to the version its own log names — resuming on current would run code the caller never chose.</summary>
    public static IReadOnlyDictionary<Guid, Guid> ResolveResumeManifest(
        string? manifestJson, Guid workflowId, Guid pinnedVersionId, JsonSerializerOptions jsonOptions)
    {
        if (!string.IsNullOrWhiteSpace(manifestJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(manifestJson, jsonOptions);
                if (parsed != null)
                    return parsed;
            }
            catch (JsonException)
            {
            }
        }

        return new Dictionary<Guid, Guid> { [workflowId] = pinnedVersionId };
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> outputs, string key) =>
        outputs.TryGetValue(key, out var value) ? value?.ToString() : null;
}
