using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

internal sealed partial class TypingRun
{
    private const string Previous = "previous";
    private const string Parameter = "parameter";
    private const string Now = "now";
    private const string Helper = "helper";

    private ResolvedRead? ResolveRead(string path, int position)
    {
        if (!ExpressionPath.TrySplit(path, scope.KeyShapes, out var parts, out var why))
        {
            Refuse(CompilationRules.ExpressionOutOfScope, why, position, path);
            return null;
        }

        if (!keys.TryGetValue(parts.DependencyKey, out var term))
        {
            term = parts.Namespace switch
            {
                Previous => ReadPrevious(path, parts, position),
                Parameter => ReadParameter(path, parts, position),
                Now => ReadNow(path, parts, position),
                Helper => Ask(path, parts, scope.Helper, position),
                _ => ReadHost(path, parts, position),
            };
            keys[parts.DependencyKey] = term;
        }

        if (term is null)
            return null;

        if (parts.Fields.Count > 0)
        {
            Refuse(CompilationRules.ExpressionFieldAccessInvalid, FieldReason(term, parts.Fields[0]), position, path);
            return null;
        }

        return new ResolvedRead(Terms.IsJson(term) ? Terms.Json.AsNullable() : term, parts.Namespace, parts.Key);
    }

    private PortTerm? ReadPrevious(string path, ExpressionPathParts parts, int position)
    {
        if (scope.Previous is null)
            return Missing(path, "an expression outside a workflow has no node outputs", position);
        return Looked(scope.Previous(parts.Key[0], parts.Key[1]), path, position);
    }

    private PortTerm? ReadParameter(string path, ExpressionPathParts parts, int position)
    {
        if (scope.Parameter is null)
            return Missing(path, "an expression outside a workflow has no parameters", position);
        return Looked(scope.Parameter(parts.Key[0]), path, position);
    }

    private PortTerm? Looked(ReadLookup lookup, string path, int position) => lookup.Kind switch
    {
        ReadLookupKind.Typed => lookup.Term,
        ReadLookupKind.Missing => Missing(path, lookup.Why!, position),
        ReadLookupKind.Unbound => Unbound(path, lookup.Why!, position),
        _ => null,
    };

    private PortTerm? Unbound(string path, string why, int position)
    {
        Refuse(CompilationRules.TypeParameterUnbound, why, position, path);
        return null;
    }

    private PortTerm? Missing(string path, string why, int position)
    {
        Refuse(CompilationRules.ExpressionOutOfScope, why, position, path);
        return null;
    }

    private PortTerm? ReadVariable(string path, ExpressionPathParts parts, int position) =>
        Looked(scope.Variable?.Invoke(parts.Namespace)
            ?? ReadLookup.Missing($"'{parts.Namespace}' is a scope variable, read only inside the Expression input that declares it"), path, position);

    private PortTerm? ReadNow(string path, ExpressionPathParts parts, int position)
    {
        if (parts.Key.Count == 0)
            return Terms.DateTime;
        var field = parts.Key[0];
        if (!NowFields.Names.Contains(field))
            return Missing(path, "now has no such field", position);
        return field.Equals("iso", StringComparison.OrdinalIgnoreCase) || field.Equals("localiso", StringComparison.OrdinalIgnoreCase)
            ? Terms.String
            : Terms.Int;
    }

    private PortTerm? ReadHost(string path, ExpressionPathParts parts, int position)
    {
        if (WorkflowExpressionNamespaces.IsScopeVariableName(parts.Namespace))
            return ReadVariable(path, parts, position);
        if (scope.Checks.TryGetValue(parts.Namespace, out var check))
            return Ask(path, parts, check, position);

        Refuse(CompilationRules.ExpressionUnknownNamespace, "it does not exist", position, path);
        return null;
    }

    private PortTerm? Ask(string path, ExpressionPathParts parts, Func<IReadOnlyList<string>, NamespacePathCheckAnswer> check, int position)
    {
        var answer = check(parts.Key);
        if (answer.Verdict != NamespacePathCheck.Yes)
        {
            Refuse(CompilationRules.ExpressionUnknownNamespace, "it does not exist", position, path);
            return null;
        }

        var key = $"'{parts.Namespace}.{string.Join('.', parts.Key)}'";
        var graded = PortTermGrading.Grade(answer.KeyPortType, TermSeam.Answer, TermPosition.Output, vocabulary.Table, out var refusal);
        if (refusal is null)
            return graded!.Term;
        Refuse(refusal!.Value.Rule, $"the check of {key} {refusal.Value.What}", position, path);
        return null;
    }

    private static string FieldReason(PortTerm baseTerm, ExpressionFieldStep step)
    {
        if (Terms.IsJson(baseTerm))
            return "a Json value is opaque: it has no member or index and is read only through a projection";
        var term = Terms.Strip(baseTerm);
        if (term.IsCollection && step.IsIndex)
            return $"an index into a collection is not allowed; a {term} is read whole";
        if (term.IsMap)
            return $"a {term} is read whole; it has no member or index";
        return step.IsIndex ? $"a {term} has no index" : $"a {term} has no member '{step.Name}'";
    }
}
