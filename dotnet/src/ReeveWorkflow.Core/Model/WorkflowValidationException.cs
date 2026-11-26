namespace ReeveWorkflow.Core.Model;

/// <summary>Refusal to run a definition whose shape is wrong. It lives with the rules that raise it, so a
/// host outside this repository can catch what its validation throws without depending on Reeve.</summary>
public sealed class WorkflowValidationException(string message) : Exception(message);
