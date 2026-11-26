using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Model;

/// <summary>A workflow row setting the run reads as something other than what is written: its reason code and the words
/// a caller is refused with.</summary>
/// <param name="Reason">One of the <see cref="WorkflowRowSettings"/> reason codes; a refusal names it.</param>
/// <param name="Message">What is wrong and what to write instead.</param>
public sealed record WorkflowRowFault(string Reason, string Message);

/// <summary>What a create, an update and an import may store in the workflow row's execution settings: a stored form
/// has one reading. The run reads a timeout of zero or less as none and a policy it cannot parse as none, so each is refused
/// where an author can still say what they meant.</summary>
public static class WorkflowRowSettings
{
    /// <summary>The longest execution timeout a timer can hold: 4,294,967,295 milliseconds, in whole seconds.</summary>
    public const int LongestTimeoutSeconds = 4_294_967;

    /// <summary>The timeout is zero or negative: the run reads it as none, a second spelling of null.</summary>
    public const string TimeoutNotPositive = "execution-timeout-not-positive";

    /// <summary>The timeout is above <see cref="LongestTimeoutSeconds"/>.</summary>
    public const string TimeoutAboveTimerLimit = "execution-timeout-above-timer-limit";

    /// <summary>The retry policy is not the text of a JSON object that deserialises into <see cref="WorkflowRetryPolicy"/>.</summary>
    public const string RetryPolicyMalformed = "retry-policy-malformed";

    /// <summary>The backoff strategy is neither <c>fixed</c> nor <c>exponential</c>: the run reads every other as fixed.</summary>
    public const string RetryBackoffOther = "retry-backoff-other";

    /// <summary>A written <c>maxAttempts</c> below 1, a second spelling of 1.</summary>
    public const string RetryMaxAttemptsBelowOne = "retry-max-attempts-below-1";

    /// <summary>A written <c>initialDelayMs</c> of 0 or below, a second spelling of no delay.</summary>
    public const string RetryInitialDelayBelowOne = "retry-initial-delay-below-1";

    /// <summary>The first refusal of the two settings, or null when both are stored as they read.</summary>
    public static string? Refusal(int? executionTimeoutSeconds, string? retryPolicyJson) =>
        (TimeoutFault(executionTimeoutSeconds) ?? RetryPolicyFault(retryPolicyJson))?.Message;

    /// <summary>Throws <see cref="ArgumentException"/> with the refusal when either setting is refused. The one
    /// check behind the <see cref="Workflow"/> constructor and <see cref="Workflow.Update"/>, so every door that
    /// stores the row, a host constructing the entity included, refuses what the editor and import refuse.</summary>
    public static void Require(int? executionTimeoutSeconds, string? retryPolicyJson)
    {
        if (Refusal(executionTimeoutSeconds, retryPolicyJson) is { } refusal)
            throw new ArgumentException(refusal);
    }

    /// <summary>Why a version's parameter list is refused as a stored value, or null: blank text and the JSON <c>null</c>
    /// are two more spellings of no parameters (rule L7), which is an absent list or <c>[]</c>. The finer shape rules of
    /// the list belong to the stored document reader that every editor door runs.</summary>
    public static string? ParameterListRefusal(string? text, bool input) =>
        StoredWorkflowDocument.ReadParameters(
                text, input ? PortTypeNameSite.InputParameter : PortTypeNameSite.OutputParameter, out var fault) is null
        && fault!.Rule == StoredDocumentRules.L7 && fault.Detail is StoredWorkflowDocument.BlankList or StoredWorkflowDocument.NullList
            ? $"The workflow's declared {(input ? "input" : "output")} parameters cannot be read: {fault.Describe()}."
            : null;

    /// <summary>Why <paramref name="executionTimeoutSeconds"/> is refused, or null. Absent is no timeout.</summary>
    public static WorkflowRowFault? TimeoutFault(int? executionTimeoutSeconds) => executionTimeoutSeconds switch
    {
        null => null,
        <= 0 => new(TimeoutNotPositive,
            $"ExecutionTimeoutSeconds is {executionTimeoutSeconds}; the run reads it as no timeout. Leave it out for none, or write a positive number."),
        > LongestTimeoutSeconds => new(TimeoutAboveTimerLimit,
            $"ExecutionTimeoutSeconds is {executionTimeoutSeconds}; a timer holds at most {LongestTimeoutSeconds} seconds."),
        _ => null,
    };

    /// <summary>Why <paramref name="retryPolicyJson"/> is refused, or null. Absent and blank are no policy. The count and
    /// the delay are read as key presence in the text, not as the bound value: an absent count still makes one attempt.</summary>
    public static WorkflowRowFault? RetryPolicyFault(string? retryPolicyJson)
    {
        if (string.IsNullOrWhiteSpace(retryPolicyJson))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(retryPolicyJson);
        }
        catch (JsonException exception)
        {
            return new(RetryPolicyMalformed, $"RetryPolicyJson is not a retry policy: {exception.Message}");
        }

        using (document)
        {
            if (JsonMembers.FindRepeat(document.RootElement) is { } repeat)
                return new(RetryPolicyMalformed, $"RetryPolicyJson is not a retry policy: {repeat}.");
            WorkflowRetryPolicy? policy;
            try
            {
                policy = document.RootElement.Deserialize<WorkflowRetryPolicy>(JsonMembers.Refusing(WorkflowGraphHelpers.JsonOptions));
            }
            catch (JsonException exception)
            {
                return new(RetryPolicyMalformed, $"RetryPolicyJson is not a retry policy: {exception.Message}");
            }

            if (policy is null || document.RootElement.ValueKind != JsonValueKind.Object)
                return new(RetryPolicyMalformed, "RetryPolicyJson is not a retry policy: it must be a JSON object.");

            if (policy.BackoffStrategy is not ("fixed" or "exponential"))
            {
                return new(RetryBackoffOther,
                    $"RetryPolicyJson names the backoff strategy '{policy.BackoffStrategy}'; the run reads every strategy but exponential as fixed. Write 'fixed' or 'exponential'.");
            }

            if (Written(document.RootElement, "maxAttempts") && policy.MaxAttempts < 1)
            {
                return new(RetryMaxAttemptsBelowOne,
                    $"RetryPolicyJson writes maxAttempts {policy.MaxAttempts}; the run makes one attempt. Write 1 or more, or leave it out.");
            }

            if (Written(document.RootElement, "initialDelayMs") && policy.InitialDelayMs <= 0)
            {
                return new(RetryInitialDelayBelowOne,
                    $"RetryPolicyJson writes initialDelayMs {policy.InitialDelayMs}; the run waits no time. Write above 0, or leave it out.");
            }
        }

        return null;
    }

    private static bool Written(JsonElement policy, string key) =>
        policy.EnumerateObject().Any(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
}
