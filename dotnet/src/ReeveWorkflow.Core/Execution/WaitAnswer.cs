using System.Text.Json;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed record WaitAnswer(IReadOnlyDictionary<string, object?> Audited, WorkflowResumedFaultException? Fault, string? Unevaluable)
{
    private static readonly IReadOnlySet<string> UnevaluableReasons = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkflowAbandonmentReasons.PinnedVersionUncompilable, WorkflowAbandonmentReasons.ResumeValueInvalid,
    };

    public static WaitAnswer Read(string outcomeJson)
    {
        using var document = JsonDocument.Parse(outcomeJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The wait's outcome is not a JSON object.");

        var audited = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in new[] { WorkflowResumeDecisions.OutcomeKey, WorkflowResumeDecisions.ResumeReasonKey })
        {
            if (root.TryGetProperty(key, out var value))
                audited[key] = value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException($"The wait's {key} is not text.");
        }

        var outcome = audited.GetValueOrDefault(WorkflowResumeDecisions.OutcomeKey) as string;
        if (outcome == WorkflowWaitOutcomes.Unevaluable)
        {
            return root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                && UnevaluableReasons.Contains(reason.GetString()!)
                    ? new WaitAnswer(audited, null, reason.GetString())
                    : throw new JsonException("An unevaluable outcome names no reason the resumer knows.");
        }

        if (outcome != WorkflowWaitOutcomes.Faulted)
            return new WaitAnswer(audited, null, null);
        if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String
            || !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
            throw new JsonException("A faulted outcome carries no error with a code and a message.");
        var category = error.TryGetProperty("category", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : null;
        return new WaitAnswer(audited, new WorkflowResumedFaultException(code.GetString()!, category, message.GetString()!), null);
    }
}
