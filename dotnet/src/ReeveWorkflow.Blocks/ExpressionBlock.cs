using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

/// <summary>Names an expression result so downstream blocks can wire it: the engine has already evaluated the
/// <c>expr:</c> value by the time this runs, so the block only surfaces it on <c>Result</c>, in the term the compiler gave it.</summary>
public sealed class ExpressionBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue("Expression", out var value))
            throw new ArgumentException("Input 'Expression' is required.");

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = value });
    }
}

public sealed class ExpressionBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "Expression";
    public string DisplayName => "Evaluate Expression";
    public string Description => "Evaluates an expression and exposes the result on a port — feed it to If / Else, or to any typed input.";
    public string Category => "Control Flow";
    /// <summary>Hands back a value the engine already evaluated; the block itself computes nothing.</summary>
    public bool IsIdempotent => true;

    /// <summary><c>$T</c> is the result's term, bound by what feeds <c>Expression</c>: an <c>expr:</c> value's result term.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T")];

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Expression", "Expression", PortValueType.OfDeclaration("$T"), true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.OfDeclaration("$T"), false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("Expression", "Expression", ConfigFieldType.String,
            Required: false, DefaultValue: "expr: ",
            Description: "For example ${previous.read.Value} > 3, or ${parameter.Mode} == \"away\".")
    ];

    public IWorkflowBlockPlugin Create() => new ExpressionBlock();
}
