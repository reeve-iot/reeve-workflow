using System.Text.Json;

namespace ReeveWorkflow.Abstractions;

/// <summary>Implemented by the plugin of a block that parks: the resumer asks it for the node's outputs from the wait's outcome
/// instead of building them itself. A block that parks a <c>Predicate</c> wait must implement it.</summary>
public interface IWorkflowResumedOutputs
{
    /// <summary>The node's outputs for an outcome <c>satisfied</c> or <c>timed_out</c>, each read through <see cref="WorkflowBlockContext.Types"/>;
    /// a <see cref="PersistedValueReadException"/> abandons the run, any other throw or a null fails the node with <c>block-failed</c>.</summary>
    IReadOnlyDictionary<string, object?> ResumedOutputs(JsonElement outcome, WorkflowBlockContext context);
}
