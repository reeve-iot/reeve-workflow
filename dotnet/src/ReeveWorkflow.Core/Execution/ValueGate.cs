using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Execution;

internal static class ValueGate
{
    internal static string? Admit(ClosedPortType type, object? value, out object? admitted)
    {
        admitted = value;
        if (value is null)
            return type.Term.IsNullable ? null : $"null is not a value of '{type.Term}'";

        var normalised = type.Normalised(value);
        if (!type.ClrType.IsInstanceOfType(normalised))
            return $"a {value.GetType().Name} is not a value of '{type.Term}'";

        if (type.IsNonFiniteNumber(normalised))
            return $"a non-finite number is not a value of '{type.Term}'";

        if (normalised is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } element && IsJson(type.Term))
            return $"a JSON {(element.ValueKind == JsonValueKind.Null ? "null" : "undefined")} is not a value of '{type.Term}', and a Json value is never one";

        if (type.FirstRepeat(normalised) is { } repeat)
            return $"a Json value holding {repeat.Reason}";

        admitted = normalised;
        return null;
    }

    internal static Dictionary<string, object?> Outputs(CompiledNode node, IReadOnlyDictionary<string, object?> written)
    {
        var admitted = new Dictionary<string, object?>(written.Count);
        foreach (var (name, value) in written)
        {
            if (!node.OutputNames.Contains(name))
                continue;
            if (node.OutputTypes.TryGetValue(name, out var type))
            {
                if (Admit(type, value, out var checkedValue) is { } reason)
                    throw new WorkflowOutputTypeMismatchException(node.NodeId, null, name, reason);
                admitted[name] = checkedValue;
            }
            else
            {
                admitted[name] = value;
            }
        }

        return admitted;
    }

    internal static Dictionary<string, object?> SubCanvasInputs(
        string nodeId, string subCanvas, CompiledCanvas canvas, IReadOnlyDictionary<string, object?> written)
    {
        var admitted = new Dictionary<string, object?>(written.Count);
        foreach (var (name, value) in written)
        {
            if (!canvas.InputTypes.TryGetValue(name, out var type))
                continue;
            if (type is null)
            {
                admitted[name] = value;
                continue;
            }

            if (Admit(type, value, out var checkedValue) is { } reason)
                throw new WorkflowOutputTypeMismatchException(nodeId, subCanvas, name, reason);
            admitted[name] = checkedValue;
        }

        return admitted;
    }

    private static bool IsJson(PortTerm term) =>
        term.Kind == PortTermKind.Atom && string.Equals(term.Name, "Json", StringComparison.Ordinal);
}
