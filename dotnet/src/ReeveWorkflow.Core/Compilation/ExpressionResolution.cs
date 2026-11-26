using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Compilation;

internal static class ExpressionResolution
{
    public static TypedExpression? Type(
        string nodeId,
        string nodeName,
        IReadOnlyList<CanvasPathSegment> canvasPath,
        string source,
        CompilationScope scope,
        IReadOnlyList<EffectivePort>? parameters,
        CompilationOptions options,
        List<CompilationDiagnostic> diagnostics,
        Func<string, ReadLookup>? variables = null)
    {
        var typing = Scope(nodeId, canvasPath, scope, parameters, options, variables);
        var result = ExpressionTyper.Type(source, typing);
        foreach (var refusal in result.Refusals)
            diagnostics.Add(Diagnostic(nodeId, nodeName, canvasPath, refusal));
        return result.Refusals.Count == 0 ? result.Tree : null;
    }

    private static ExpressionTypingScope Scope(
        string nodeId,
        IReadOnlyList<CanvasPathSegment> canvasPath,
        CompilationScope scope,
        IReadOnlyList<EffectivePort>? parameters,
        CompilationOptions options,
        Func<string, ReadLookup>? variables)
    {
        var vocabulary = options.Vocabulary;
        var builder = new ExpressionTypingScope.Builder()
            .Vocabulary(vocabulary)
            .Helper(options.HelperChecks)
            .Previous((publisher, output) => Previous(nodeId, scope, publisher, output, vocabulary))
            .Parameters(name => Parameter(parameters, name, vocabulary));
        if (variables is not null)
            builder.Variables(variables);
        foreach (var registration in options.NamespaceRegistrations)
        {
            var check = options.PinnedNamespaceChecks.FirstOrDefault(p => string.Equals(p.Namespace, registration.Namespace, StringComparison.Ordinal)).Check;
            builder.Namespace(
                registration.Namespace,
                registration.KeySegments,
                key => CompilationFault.AtNode(
                    canvasPath, nodeId, () => check is null ? NamespacePathCheckAnswer.No : check.CheckWithKeyType(key)));
        }

        return builder.Build();
    }

    private static ReadLookup Previous(string nodeId, CompilationScope scope, string publisherId, string outputName, WorkflowTypeVocabulary vocabulary)
    {
        switch (scope.TryResolve(nodeId, publisherId, out var outputs))
        {
            case ScopeVisibility.NotInScope:
                return ReadLookup.Missing($"node '{publisherId}' does not run before this one on this canvas");
            case ScopeVisibility.Unresolved:
                return ReadLookup.Unresolved;
        }

        return outputs.FirstOrDefault(p => p.Name == outputName) is { } port
            ? Port(port, optional: false, vocabulary, $"output '{outputName}' of node '{publisherId}'")
            : ReadLookup.Missing($"node '{publisherId}' publishes no output '{outputName}'");
    }

    private static ReadLookup Parameter(IReadOnlyList<EffectivePort>? parameters, string name, WorkflowTypeVocabulary vocabulary)
    {
        if (parameters is null)
            return ReadLookup.Unresolved;
        return parameters.FirstOrDefault(p => p.Name == name) is { } port
            ? Port(port, !port.IsRequired, vocabulary, $"parameter '{name}'")
            : ReadLookup.Missing("the workflow declares no such parameter");
    }

    internal static ReadLookup Port(EffectivePort port, bool optional, WorkflowTypeVocabulary vocabulary, string what) => port.State switch
    {
        PortState.Typed => Lookup(port.Type, optional, vocabulary),
        PortState.Unbound => ReadLookup.Unbound(
            $"the type of {what} names the type parameter '{port.UnboundParameters[0]}', which nothing binds"),
        _ => ReadLookup.Unresolved,
    };

    internal static ReadLookup Lookup(PortValueType type, bool optional, WorkflowTypeVocabulary vocabulary)
    {
        if (type.IsDefault)
            return ReadLookup.Unresolved;
        if (!PortTerm.TryParse(type.Name, PortTermGrammar.Document, out var term, out _)
            || PortTypeNameSites.Unspelled(type.Name, term, vocabulary.Table) is not null)
            return ReadLookup.Unresolved;
        return ReadLookup.Typed(optional && !term.IsNullable ? term.AsNullable() : term);
    }

    private static CompilationDiagnostic Diagnostic(string nodeId, string nodeName, IReadOnlyList<CanvasPathSegment> canvasPath, ExpressionRefusal refusal)
    {
        var message = refusal switch
        {
            { RuleId: CompilationRules.ExpressionSyntax } =>
                $"Expression on node {nodeName} does not parse at position {refusal.Position}: {refusal.Reason}",
            { RuleId: CompilationRules.ExpressionUnknownNamespace, Path: { } path } =>
                $"Expression on node {nodeName} references '{path}', which does not exist.",
            { Path: { } path } => $"Expression on node {nodeName} references '{path}': {refusal.Reason}.",
            _ => $"Expression on node {nodeName} at position {refusal.Position}: {refusal.Reason}.",
        };
        return new CompilationDiagnostic(refusal.RuleId, message, nodeId, CanvasPath: canvasPath);
    }
}
