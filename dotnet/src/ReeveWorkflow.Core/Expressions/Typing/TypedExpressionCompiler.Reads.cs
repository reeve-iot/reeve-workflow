using System.Globalization;

namespace ReeveWorkflow.Core.Expressions;

internal static partial class TypedExpressionCompiler
{
    private const string Narrowing = "narrowing";
    private const string Absent = "value-absent";

    private static ClosureNode Read(TypedRead node)
    {
        var path = node.Path;
        var raw = node.Namespace == "now" ? Now(node) : Resolved(node, path);
        return ClosureNode.Of<object?>(scope => Held(node, path, raw(scope), scope)).In(ClosureNode.ShapeOf(node.Term));
    }

    private static object? Held(TypedRead node, string path, object? value, ExpressionEvaluationScope scope)
    {
        if (scope.Vocabulary.ClrTypeOf(node.Term) is null)
            throw new ExpressionEvaluationException($"the term '{node.Term}' of '${{{path}}}' has no CLR type", node.Position);
        var held = ExpressionNamespaceValues.Hold(node.Term, value, scope.Vocabulary);
        if (!held.Ok)
        {
            throw new ExpressionEvaluationException(
                $"the value of '${{{path}}}' is off its term: {held.Why}", node.Position, missingPath: path, category: Narrowing);
        }

        return held.Value;
    }

    private static Func<ExpressionEvaluationScope, object?> Resolved(TypedRead node, string path)
    {
        var lookup = Lookup(node);
        return scope =>
        {
            (bool Found, object? Value) answer;
            try
            {
                answer = lookup(scope);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ExpressionEvaluationException(
                    $"reading '${{{path}}}' threw {ex.GetType().Name}: {ex.Message}", node.Position, ex, null);
            }

            return answer.Found
                ? answer.Value
                : throw new ExpressionEvaluationException(
                    $"variable '${{{path}}}' did not resolve", node.Position, missingPath: path, category: Absent);
        };
    }

    private static Func<ExpressionEvaluationScope, (bool Found, object? Value)> Lookup(TypedRead node)
    {
        var name = node.Namespace;
        var key = node.Key;
        if (WorkflowExpressionNamespaces.IsScopeVariableName(name))
            return scope => scope.Variables is { } bound && bound.TryGetValue(name, out var variable) ? (true, variable) : (false, null);
        switch (name)
        {
            case "previous":
                return scope => scope.Previous?.Invoke(key[0], key[1]) is { Found: true } found ? (true, found.Value) : (false, null);
            case "parameter":
                return scope => scope.Parameter?.Invoke(key[0]) is { Found: true } found ? (true, found.Value) : (false, null);
            case "helper":
                var helperPath = new HelperPath(key);
                return scope =>
                {
                    return scope.Helper.TryGetValue(helperPath, out var value) ? (true, value) : (false, null);
                };
            default:
                return scope =>
                {
                    if (!scope.Hosts.TryGetValue(name, out var binding))
                        return (false, null);
                    var result = binding.Resolver.Resolve([.. key]);
                    return result.Found ? (true, result.Value) : (false, null);
                };
        }
    }

    private static Func<ExpressionEvaluationScope, object?> Now(TypedRead node)
    {
        if (node.Key.Count == 0)
            return scope => scope.Now;
        var field = node.Key[0].ToLowerInvariant();
        Func<ExpressionEvaluationScope, DateTimeOffset> clock =
            field.StartsWith("local", StringComparison.Ordinal) ? scope => scope.LocalNow : scope => scope.Now;
        switch (field)
        {
            case "iso" or "localiso":
                return scope => clock(scope).ToString("O", CultureInfo.InvariantCulture);
            case "epoch":
                return scope => clock(scope).ToUnixTimeSeconds();
            case "year" or "localyear":
                return scope => (long)clock(scope).Year;
            case "month" or "localmonth":
                return scope => (long)clock(scope).Month;
            case "day" or "localday":
                return scope => (long)clock(scope).Day;
            case "hour" or "localhour":
                return scope => (long)clock(scope).Hour;
            case "minute" or "localminute":
                return scope => (long)clock(scope).Minute;
            case "second" or "localsecond":
                return scope => (long)clock(scope).Second;
            case "dayofweek" or "localdayofweek":
                return scope => (long)(int)clock(scope).DayOfWeek;
            default:
                var position = node.Position;
                return _ => throw new ExpressionEvaluationException($"now has no field '{field}'", position);
        }
    }
}
