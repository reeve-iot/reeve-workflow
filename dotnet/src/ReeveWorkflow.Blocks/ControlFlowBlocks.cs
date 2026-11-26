using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

public sealed class IfElseBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (context.SubCanvasExecutor is null)
            throw new InvalidOperationException("IfElse block requires a SubCanvasExecutor.");

        if (!inputs.TryGetValue("Condition", out var condObj) || condObj is not bool condition)
            throw new ArgumentException("Input 'Condition' is required and must be a boolean.");

        var branchName = condition ? "Then" : "Else";
        return await context.SubCanvasExecutor.ExecuteAsync(branchName, inputs, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class IfElseBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "IfElse";
    public string DisplayName => "If / Else";
    public string Description => "Executes the 'Then' sub-canvas when Condition is true, otherwise the 'Else' sub-canvas.";
    public string Category => "Control Flow";
    /// <summary>A retry re-runs the whole chosen branch, and the branch is where the side effects are.</summary>
    public bool IsIdempotent => false;
    /// <summary>The branch is chosen from Condition alone, so the same inputs choose it again.</summary>
    public bool OnlyDrivesItsSubCanvases => true;
    public bool SupportsDeclaredOutputs => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Condition", "Condition", PortValueType.Boolean, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("Condition", "Condition", ConfigFieldType.Boolean,
            Required: false, DefaultValue: "false",
            Description: "A constant, or an expression such as expr: ${previous.read.Value} > 3.")
    ];

    public IReadOnlyCollection<string> SubCanvasNames => ["Then", "Else"];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) => [];

    public IWorkflowBlockPlugin Create() => new IfElseBlock();
}

public sealed class WhileLoopBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (context.SubCanvasExecutor is null)
            throw new InvalidOperationException("WhileLoop block requires a SubCanvasExecutor.");

        var maxIterations = BlockInputCoercion.Int(inputs, "MaxIterations") ?? 100;
        if (maxIterations <= 0)
            throw new ArgumentException("'MaxIterations' must be greater than 0.");

        var iteration = 0;
        while (true)
        {
            if (iteration >= maxIterations)
                throw new InvalidOperationException(
                    $"WhileLoop exceeded the maximum iteration limit of {maxIterations}.");

            var outputs = await context.SubCanvasExecutor.ExecuteAsync("Body", inputs, cancellationToken).ConfigureAwait(false);
            iteration++;

            if (!outputs.TryGetValue("Continue", out var continueObj)) break;
            if (continueObj is not bool continueLoop)
                throw new ArgumentException("Output 'Continue' cannot be read as Boolean.");
            if (!continueLoop) break;
        }

        return new Dictionary<string, object?> { ["Iterations"] = iteration };
    }

    private static long ResolveInt(IReadOnlyDictionary<string, object?> inputs, string key, long defaultValue) =>
        BlockInputCoercion.Int(inputs, key) ?? defaultValue;
}

public sealed class WhileLoopBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "WhileLoop";
    public string DisplayName => "While Loop";
    public string Description => "Repeats the 'Body' sub-canvas while it outputs Continue = true.";
    public string Category => "Control Flow";
    /// <summary>A retry re-runs the body from iteration one, however far the first attempt got.</summary>
    public bool IsIdempotent => false;
    /// <summary>Whether to go round again is the body's own Continue output, so replaying the bodies that finished
    /// takes the loop back to the iteration it was in.</summary>
    public bool OnlyDrivesItsSubCanvases => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs => [];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Iterations", "Iterations", PortValueType.Int, false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("MaxIterations", "Max Iterations", ConfigFieldType.Int,
            Required: false, DefaultValue: "100",
            Description: "Maximum number of iterations before the loop is aborted.")
    ];

    public IReadOnlyCollection<string> SubCanvasNames => ["Body"];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) =>
        subCanvasName == "Body" ? [new BlockPortDefinition("Continue", "Continue Loop", PortValueType.Boolean, false)] : [];

    public IWorkflowBlockPlugin Create() => new WhileLoopBlock();
}

public sealed class ForLoopBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (context.SubCanvasExecutor is null)
            throw new InvalidOperationException("ForLoop block requires a SubCanvasExecutor.");

        var from = ResolveDouble(inputs, "From") ?? 0;
        var to = ResolveDouble(inputs, "To") ?? throw new ArgumentException("'To' is required.");
        var step = ResolveDouble(inputs, "Step") ?? 1;

        if (step == 0)
            throw new ArgumentException("'Step' must not be zero.");

        var maxIterations = ResolveInt(inputs, "MaxIterations", DefaultMaxIterations);
        if (maxIterations <= 0)
            throw new ArgumentException("'MaxIterations' must be greater than 0.");

        var collected = context.DeclaredOutputs.Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, _ => new List<object?>(), StringComparer.Ordinal);
        foreach (var name in collected.Keys)
            BlockValues.Writes(context, name);
        var index = 0;
        var current = from;

        while ((step > 0 && current < to) || (step < 0 && current > to))
        {
            if (index >= maxIterations)
                throw new InvalidOperationException(
                    $"ForLoop exceeded the maximum iteration limit of {maxIterations}.");

            var iterInputs = new Dictionary<string, object?>(inputs)
            {
                ["Index"] = index,
                ["Current"] = current
            };

            var output = await context.SubCanvasExecutor.ExecuteAsync("Body", iterInputs, cancellationToken).ConfigureAwait(false);
            foreach (var (name, values) in collected)
                values.Add(output.GetValueOrDefault(name));

            current += step;
            index++;
        }

        var written = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Iterations"] = index };
        foreach (var (name, values) in collected)
        {
            written[name] = BlockValues.Collection(context, name, values);
        }

        return written;
    }

    internal const int DefaultMaxIterations = 10_000;

    private static long ResolveInt(IReadOnlyDictionary<string, object?> inputs, string key, long defaultValue)
        => BlockInputCoercion.Int(inputs, key) ?? defaultValue;

    private static double? ResolveDouble(IReadOnlyDictionary<string, object?> inputs, string key)
    {
        return BlockInputCoercion.Number(inputs, key);
    }
}

public sealed class ForLoopBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "ForLoop";
    public string DisplayName => "For Loop";
    public string Description => "Iterates from From to To (exclusive) with the given Step, passing Index and Current to the 'Body' sub-canvas and collecting each declared output across the iterations.";
    public string Category => "Control Flow";
    /// <summary>A retry re-runs the body from the first index, however far the first attempt got.</summary>
    public bool IsIdempotent => false;
    /// <summary>From, To and Step are inputs, so the same inputs walk the same indexes.</summary>
    public bool OnlyDrivesItsSubCanvases => true;
    public bool SupportsDeclaredOutputs => true;
    public bool DeclaredOutputsCollect => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("From", "From", PortValueType.Number, false),
        new BlockPortDefinition("To", "To", PortValueType.Number, true),
        new BlockPortDefinition("Step", "Step", PortValueType.Number, false)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Iterations", "Iterations", PortValueType.Int, false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("From", "From", ConfigFieldType.Number,
            Required: false, DefaultValue: "0", Description: "Starting value (inclusive)."),
        new BlockConfigFieldDefinition("To", "To", ConfigFieldType.Number,
            Required: false, Description: "End value (exclusive)."),
        new BlockConfigFieldDefinition("Step", "Step", ConfigFieldType.Number,
            Required: false, DefaultValue: "1", Description: "Increment per iteration."),
        new BlockConfigFieldDefinition("MaxIterations", "Max Iterations", ConfigFieldType.Int,
            Required: false, DefaultValue: "10000",
            Description: "Maximum number of iterations before the loop is aborted.")
    ];

    public IReadOnlyCollection<string> SubCanvasNames => ["Body"];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) =>
        subCanvasName == "Body"
            ?
            [
                new BlockPortDefinition("Index", "Index", PortValueType.Int, false),
                new BlockPortDefinition("Current", "Current Value", PortValueType.Number, false)
            ]
            : [];

    public IWorkflowBlockPlugin Create() => new ForLoopBlock();
}

public sealed class IterateOverBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (context.SubCanvasExecutor is null)
            throw new InvalidOperationException("IterateOver block requires a SubCanvasExecutor.");

        var collection = BlockValues.ReadCollection(inputs, "Collection");

        var maxItems = BlockInputCoercion.Int(inputs, "MaxItems") ?? DefaultMaxItems;
        if (maxItems <= 0)
            throw new ArgumentException("'MaxItems' must be greater than 0.");

        var writesResults = BlockValues.Writes(context, "Results", "$R");
        var results = new List<object?>();
        var index = 0;

        foreach (var item in collection)
        {
            if (index >= maxItems)
                throw new InvalidOperationException(
                    $"IterateOver exceeded the maximum item limit of {maxItems}.");

            var iterInputs = new Dictionary<string, object?>(inputs)
            {
                ["Item"] = item,
                ["Index"] = index
            };

            var output = await context.SubCanvasExecutor.ExecuteAsync("Body", iterInputs, cancellationToken).ConfigureAwait(false);

            if (output.TryGetValue("Result", out var result))
                results.Add(result);

            index++;
        }

        var written = new Dictionary<string, object?>();
        if (writesResults)
            written["Results"] = BlockValues.Collection(context, "Results", results);
        return written;
    }

    internal const int DefaultMaxItems = 10_000;

    private static long ResolveInt(IReadOnlyDictionary<string, object?> inputs, string key, long defaultValue) =>
        BlockInputCoercion.Int(inputs, key) ?? defaultValue;
}

public sealed class IterateOverBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "IterateOver";
    public string DisplayName => "Iterate Over";
    public string Description => "Iterates over a collection, passing each Item and Index to the 'Body' sub-canvas and collecting Result outputs.";
    public string Category => "Control Flow";
    /// <summary>A retry re-runs the body for every item, including the items the first attempt already handled.</summary>
    public bool IsIdempotent => false;
    /// <summary>The collection is an input, so the same inputs walk the same items in the same order.</summary>
    public bool OnlyDrivesItsSubCanvases => true;

    /// <summary><c>$T</c> is the item type, bound by what feeds <c>Collection</c>; <c>$R</c> is the body's result
    /// type, bound by what the body feeds its <c>Result</c>.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T"), new("R")];

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Collection", "Collection", PortValueType.OfDeclaration("Collection<$T>"), true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Results", "Results", PortValueType.OfDeclaration("Collection<$R>"), false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("MaxItems", "Max Items", ConfigFieldType.Int,
            Required: false, DefaultValue: "10000",
            Description: "Maximum number of items processed before iteration is aborted.")
    ];

    public IReadOnlyCollection<string> SubCanvasNames => ["Body"];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) =>
        subCanvasName == "Body"
            ?
            [
                new BlockPortDefinition("Index", "Index", PortValueType.Int, false),
                new BlockPortDefinition("Item", "Item", PortValueType.OfDeclaration("$T"), false)
            ]
            : [];

    public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) =>
        subCanvasName == "Body" ? [new BlockPortDefinition("Result", "Result", PortValueType.OfDeclaration("$R"), false)] : [];

    public IWorkflowBlockPlugin Create() => new IterateOverBlock();
}
