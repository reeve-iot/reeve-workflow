using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Blocks;

public sealed class StringToIntBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var str = BlockInputCoercion.String(inputs, "Value")
            ?? throw new ArgumentException("Input 'Value' is required and must be a string.");

        if (!BlockValueReading.TryInt(str, out var result))
            throw new InvalidOperationException($"Cannot convert '{str}' to Int.");

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = result });
    }
}

public sealed class StringToIntBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "StringToInt";
    public string DisplayName => "String → Int";
    public string Description => "Converts a string value to an integer.";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.String, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.Int, true)
    ];

    public IWorkflowBlockPlugin Create() => new StringToIntBlock();
}

public sealed class IntToStringBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue("Value", out var raw) || raw is null)
            throw new ArgumentException("Input 'Value' is required.");

        var result = raw switch
        {
            int i => i.ToString(),
            long l => l.ToString(),
            double d when d == Math.Floor(d) => ((long)d).ToString(),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Cannot convert '{raw.GetType().Name}' to string as integer.")
        };

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = result });
    }
}

public sealed class IntToStringBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "IntToString";
    public string DisplayName => "Int → String";
    public string Description => "Converts an integer value to a string.";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.Int, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.String, true)
    ];

    public IWorkflowBlockPlugin Create() => new IntToStringBlock();
}

public sealed class StringToBoolBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var str = BlockInputCoercion.String(inputs, "Value")
            ?? throw new ArgumentException("Input 'Value' is required and must be a string.");

        if (!BlockValueReading.TryBoolean(str, out var result))
            throw new InvalidOperationException($"Cannot convert '{str}' to Boolean.");

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = result });
    }
}

public sealed class StringToBoolBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "StringToBool";
    public string DisplayName => "String → Bool";
    public string Description => "Converts a string value ('true'/'false') to a boolean.";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.String, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.Boolean, true)
    ];

    public IWorkflowBlockPlugin Create() => new StringToBoolBlock();
}

public sealed class BoolToStringBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue("Value", out var raw) || raw is not bool boolVal)
            throw new ArgumentException("Input 'Value' is required and must be a boolean.");

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = boolVal.ToString().ToLowerInvariant() });
    }
}

public sealed class BoolToStringBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "BoolToString";
    public string DisplayName => "Bool → String";
    public string Description => "Converts a boolean value to a string ('true'/'false').";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.Boolean, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.String, true)
    ];

    public IWorkflowBlockPlugin Create() => new BoolToStringBlock();
}

public sealed class NumberToStringBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue("Value", out var raw) || raw is null)
            throw new ArgumentException("Input 'Value' is required.");

        var result = raw switch
        {
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int i => i.ToString(),
            long l => l.ToString(),
            decimal dec => dec.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Cannot convert '{raw.GetType().Name}' to string as number.")
        };

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = result });
    }
}

public sealed class NumberToStringBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "NumberToString";
    public string DisplayName => "Number → String";
    public string Description => "Converts a numeric value to a string.";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.Number, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.String, true)
    ];

    public IWorkflowBlockPlugin Create() => new NumberToStringBlock();
}

public sealed class StringToNumberBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var str = BlockInputCoercion.String(inputs, "Value")
            ?? throw new ArgumentException("Input 'Value' is required and must be a string.");

        if (!BlockValueReading.TryNumber(str, out var result))
            throw new InvalidOperationException($"Cannot convert '{str}' to Number.");

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Result"] = result });
    }
}

public sealed class StringToNumberBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "StringToNumber";
    public string DisplayName => "String → Number";
    public string Description => "Converts a string value to a number (double).";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.String, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Result", "Result", PortValueType.Number, true)
    ];

    public IWorkflowBlockPlugin Create() => new StringToNumberBlock();
}

/// <summary>Writes a typed value as canonical JSON text, as <c>toJsonText</c> does. The closed type of <c>Value</c> comes from the node's compile.</summary>
public sealed class JsonToTextBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue("Value", out var value) || value is null)
            throw new ArgumentException("Input 'Value' is required and cannot be null.");
        var type = BlockValues.TypeOf(context, "Value");
        var text = JsonText.Write(type, value)
            ?? throw new InvalidOperationException($"A value of '{type.Term}' writes JSON null, so its JsonSerializable claim does not hold.");
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Text"] = text });
    }
}

public sealed class JsonToTextBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "JsonToText";
    public string DisplayName => "Value → JSON Text";
    public string Description => "Writes a typed value as JSON text.";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    /// <summary><c>$T</c> is the type of <c>Value</c>, bound by what feeds it; a <c>Json</c> value cannot bind it.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T", [TypeParameterConstraints.JsonSerializable])];

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.OfDeclaration("$T"), true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Text", "Text", PortValueType.String, true)
    ];

    public IWorkflowBlockPlugin Create() => new JsonToTextBlock();
}

/// <summary>Reads JSON text as a <c>Json</c> value, as <c>parseJson</c> does: the text <c>null</c> gives a null <c>Value</c>, and text that is not JSON fails the node.</summary>
public sealed class TextToJsonBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var text = BlockInputCoercion.String(inputs, "Text")
            ?? throw new ArgumentException("Input 'Text' is required and must be a string.");
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Value"] = JsonText.Parse(text) });
    }
}

public sealed class TextToJsonBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "TextToJson";
    public string DisplayName => "JSON Text → JSON";
    public string Description => "Reads JSON text as a Json value; the text null gives a null Value.";
    public string Category => "Conversion";
    /// <summary>A pure conversion of its input.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Text", "Text", PortValueType.String, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.OfDeclaration("Json?"), false)
    ];

    public IWorkflowBlockPlugin Create() => new TextToJsonBlock();
}
