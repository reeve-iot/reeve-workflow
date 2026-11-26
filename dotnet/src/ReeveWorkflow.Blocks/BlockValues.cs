using System.Collections;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks;

internal static class BlockValues
{
    public static IEnumerable ReadCollection(IReadOnlyDictionary<string, object?> inputs, string input)
    {
        inputs.TryGetValue(input, out var raw);
        return raw switch
        {
            string or JsonElement { ValueKind: JsonValueKind.String } =>
                throw new ArgumentException($"Input '{input}' must be a collection, not text."),
            JsonElement { ValueKind: JsonValueKind.Array } array => (IEnumerable)WorkflowGraphHelpers.JsonToValue(array)!,
            JsonElement { ValueKind: JsonValueKind.Object } or IDictionary =>
                throw new ArgumentException($"Input '{input}' must be a collection, not an object."),
            not null when IsStringKeyedMap(raw) =>
                throw new ArgumentException($"Input '{input}' must be a collection, not an object."),
            IEnumerable collection => collection,
            _ => throw new ArgumentException($"Input '{input}' is required and must be an IEnumerable."),
        };
    }

    public static void RequireCompiled(WorkflowBlockContext context)
    {
        if (context.Types.Count == 0)
            throw new InvalidOperationException("The types of the node are not known: the node was not compiled.");
    }

    public static ClosedPortType TypeOf(WorkflowBlockContext context, string name)
    {
        RequireCompiled(context);
        return context.Types.TryGetValue(name, out var type)
            ? type
            : throw new InvalidOperationException($"The type of '{name}' is not known: the context of the compiled node holds none.");
    }

    public static bool Writes(WorkflowBlockContext context, string output, string? boundBy = null)
    {
        RequireCompiled(context);
        if (context.Types.ContainsKey(output))
            return true;
        if (boundBy is not null && !context.Types.ContainsKey(boundBy))
            return false;
        throw new InvalidOperationException($"The type of output '{output}' is not known: the context of the compiled node holds none.");
    }

    public static IList Collection(WorkflowBlockContext context, string output, IEnumerable<object?> items) =>
        TypeOf(context, output).CreateList(items);

    private static bool IsStringKeyedMap(object value) =>
        value.GetType().GetInterfaces().Any(i => i.IsGenericType
            && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            && i.GetGenericArguments()[0] == typeof(string));
}
