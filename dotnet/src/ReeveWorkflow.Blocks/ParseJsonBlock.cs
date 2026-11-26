using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Blocks;

/// <summary>Reads JSON text as one <c>Json?</c> value, read downstream only through projections. Text that is not JSON gives
/// <c>Success</c> false and no <c>Value</c>, and the node does not fail; the text <c>null</c> gives a null <c>Value</c>. Text that repeats
/// a member name at any depth is ambiguous and no value, so it answers <c>Success</c> false too.</summary>
public sealed class ParseJsonBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var text = BlockInputCoercion.String(inputs, "Json")
            ?? throw new ArgumentException("Input 'Json' is required.");

        JsonElement? value;
        try
        {
            value = JsonText.Parse(text);
        }
        catch (JsonException)
        {
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Success"] = false });
        }

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Value"] = value, ["Success"] = true });
    }
}

public sealed class ParseJsonBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "ParseJson";
    public string DisplayName => "Parse JSON";
    public string Description => "Reads JSON text as a Json value; read its fields with projection wires. Success is false when the text is not JSON or repeats a member name.";
    public string Category => "Data";
    /// <summary>Reads its text; nothing observable survives the call but its outputs.</summary>
    public bool IsIdempotent => true;

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new BlockPortDefinition("Json", "JSON Text", PortValueType.String, true)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new BlockPortDefinition("Value", "Value", PortValueType.OfDeclaration("Json?"), false),
        new BlockPortDefinition("Success", "Success", PortValueType.Boolean, false)
    ];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("Json", "JSON", ConfigFieldType.String,
            Required: false, Description: "The JSON text to read — a literal, or an expression.")
    ];

    public IWorkflowBlockPlugin Create() => new ParseJsonBlock();
}
