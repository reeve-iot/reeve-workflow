using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>An Enum field held a value outside its names when the node was about to run; the node fails under its
/// error policy and the block never sees the value.</summary>
public sealed class WorkflowConfigValueNotAllowedException : Exception, IWorkflowCodedException
{
    public WorkflowConfigValueNotAllowedException(string nodeId, string fieldName, IReadOnlyList<string> allowed, object? value)
        : base($"config_value_not_allowed: field '{fieldName}' of node '{nodeId}' must be one of "
            + $"{string.Join(", ", allowed.Select(name => "'" + name + "'"))}, but the value is {Describe(value)}.")
    {
        NodeId = nodeId;
        FieldName = fieldName;
        AllowedValues = allowed;
    }

    /// <summary>Always <see cref="WorkflowErrorCodes.ConfigValueNotAllowed"/>, the code the message leads with.</summary>
    public string Code => WorkflowErrorCodes.ConfigValueNotAllowed;

    public string NodeId { get; }

    public string FieldName { get; }

    public IReadOnlyList<string> AllowedValues { get; }

    private static string Describe(object? value) => value switch
    {
        string text => $"'{text}'",
        bool => "a Boolean",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "a Number",
        _ => $"a {value?.GetType().Name ?? "null"}",
    };
}
