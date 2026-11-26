using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

/// <summary>Gives <c>Value</c> when it holds a value that is not null, otherwise <c>Default</c>.</summary>
public sealed class CoalesceBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (inputs.TryGetValue("Value", out var value) && value is not null)
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Result"] = value });

        if (!inputs.TryGetValue("Default", out var fallback))
            throw new ArgumentException("Input 'Default' is required.");
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Result"] = fallback });
    }
}

public sealed class CoalesceBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "Coalesce";
    public string DisplayName => "Coalesce";
    public string Description => "Gives Value when it is present and not null, otherwise Default.";
    public string Category => "Control Flow";
    /// <summary>Chooses between two values it was handed and touches nothing outside its own outputs.</summary>
    public bool IsIdempotent => true;

    /// <summary><c>$T</c> is the term of <c>Default</c> and of <c>Result</c>, bound by what feeds <c>Value</c> and <c>Default</c>.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T")];

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.OfDeclaration("$T?"), false),
        new BlockPortDefinition("Default", "Default", PortValueType.OfDeclaration("$T"), true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.OfDeclaration("$T"), false)
    ];

    public IWorkflowBlockPlugin Create() => new CoalesceBlock();
}

/// <summary>Runs <c>Present</c> with the value when <c>Value</c> holds one that is not null, otherwise <c>Absent</c>.</summary>
public sealed class IfPresentBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (context.SubCanvasExecutor is null)
            throw new InvalidOperationException("IfPresent block requires a SubCanvasExecutor.");

        var branchName = inputs.TryGetValue("Value", out var value) && value is not null ? "Present" : "Absent";
        return await context.SubCanvasExecutor.ExecuteAsync(branchName, inputs, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class IfPresentBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "IfPresent";
    public string DisplayName => "If Present";
    public string Description => "Executes the 'Present' sub-canvas with Value when it is present and not null, otherwise the 'Absent' sub-canvas.";
    public string Category => "Control Flow";
    /// <summary>A retry re-runs the whole chosen branch, and the branch is where the side effects are.</summary>
    public bool IsIdempotent => false;
    /// <summary>The branch is chosen from Value alone, so the same inputs choose it again.</summary>
    public bool OnlyDrivesItsSubCanvases => true;
    public bool SupportsDeclaredOutputs => true;

    /// <summary><c>$T</c> is the term of <c>Value</c> with its nullability taken off, bound by what feeds <c>Value</c>.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T")];

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.OfDeclaration("$T?"), false)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

    public IReadOnlyCollection<string> SubCanvasNames => ["Present", "Absent"];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) =>
        subCanvasName == "Present" ? [new BlockPortDefinition("Value", "Value", PortValueType.OfDeclaration("$T"), false)] : [];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) => [];

    public IWorkflowBlockPlugin Create() => new IfPresentBlock();
}
