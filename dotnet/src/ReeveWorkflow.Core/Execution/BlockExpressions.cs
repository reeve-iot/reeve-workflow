using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Execution;

internal sealed class BlockExpressions(CompiledNode node, Func<ExpressionEvaluationScope> scope) : IWorkflowBlockExpressions
{
    private ExpressionEvaluationScope? built;

    public object? Evaluate(string input, IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(variables);
        var port = node.Shape.Inputs.FirstOrDefault(p => string.Equals(p.Name, input, StringComparison.Ordinal));
        if (port is null || !port.Type.IsExpression)
            throw new ArgumentException($"'{input}' is not an Expression<T> input of block '{node.Shape.BlockTypeId}'.", nameof(input));
        if (!node.Expressions.TryGetValue(input, out var expression))
            throw new ArgumentException($"Input '{input}' of node '{node.NodeId}' holds no expression to evaluate.", nameof(input));

        var admitted = Admit(node, port, variables);
        var value = TypedExpressionEvaluator.Evaluate(expression.Tree, (built ??= scope()).WithVariables(admitted));
        return expression.Conversion is { } conversion ? conversion.Apply(value) : value;
    }

    private static Dictionary<string, object?> Admit(CompiledNode node, PortShape port, IReadOnlyDictionary<string, object?> variables) =>
        Admitted(node, port, variables, out var admitted) is { } reason ? throw new ArgumentException(reason, nameof(variables)) : admitted;

    internal static string? Admitted(
        CompiledNode node, PortShape port, IReadOnlyDictionary<string, object?> variables, out Dictionary<string, object?> admitted)
    {
        var types = node.ScopeVariables.GetValueOrDefault(port.Name);
        admitted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in variables.Keys)
        {
            if (port.ScopeVariables.All(v => !string.Equals(v.Name, name, StringComparison.Ordinal)))
                return $"Input '{port.Name}' of node '{node.NodeId}' declares no scope variable '{name}'.";
        }

        foreach (var declared in port.ScopeVariables)
        {
            if (!variables.TryGetValue(declared.Name, out var value))
                return $"Scope variable '{declared.Name}' of input '{port.Name}' of node '{node.NodeId}' was not given a value.";
            if (types is null || !types.TryGetValue(declared.Name, out var type))
                return $"Scope variable '{declared.Name}' of input '{port.Name}' of node '{node.NodeId}' has the type '{declared.Type}', which names a parameter nothing binds here.";
            if (ValueGate.Admit(type, value, out var checkedValue) is { } reason)
                return $"Scope variable '{declared.Name}' of input '{port.Name}' of node '{node.NodeId}': {reason}.";
            admitted[declared.Name] = checkedValue;
        }

        return null;
    }
}
