using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.JsonPath;

namespace ReeveWorkflow.Core.Expressions;

internal sealed partial class TypingRun
{
    private TypedExpression? Projection(CallNode node, IReadOnlyList<TypedExpression> arguments)
    {
        if (arguments.Count != 3 || !Terms.IsJson(arguments[0].Term) || !arguments[1].Term.Equals(Terms.String) || !arguments[2].Term.Equals(Terms.String))
            return Mismatch(ExpressionSignatureKind.Function, node.Name, arguments, node.Position);
        if (arguments[1] is not TypedLiteral { Value: string path } || arguments[2] is not TypedLiteral { Value: string type })
        {
            return Refuse(CompilationRules.ExpressionTypeMismatch,
                $"the path and the type of {node.Name} are string literals, which the typer reads before the expression runs", node.Position);
        }

        if (!JsonPathQuery.TryParse(path, out var query, out var error))
        {
            return Refuse(CompilationRules.JsonProjectionInvalid,
                error!.IsProjectionRestriction
                    ? error.Message
                    : $"the path '{path}' of {node.Name} is not an RFC 9535 query: {error.Message}", node.Position);
        }

        var graded = PortTermGrading.Grade(type, TermSeam.Stored, TermPosition.Input, vocabulary.Table, out var typeRefusal);
        if (typeRefusal is { } bad)
            return Refuse(bad.Rule, $"the type argument of {node.Name} {bad.What}", node.Position);

        var term = graded!.Term;
        var jsonText = node.Name == "jsonPathText";
        if (ProjectionTerms.Refusal(term, query, jsonText, intoWorkflowOutput: false, vocabulary) is { } reason)
            return Refuse(CompilationRules.JsonProjectionInvalid, $"{node.Name} with the type '{type}' {reason}", node.Position);
        if (vocabulary.ClosedTypeOf(term) is not { } closed)
            return Refuse(CompilationRules.JsonProjectionInvalid, $"{node.Name} with the type '{type}' delivers a term with no closed type", node.Position);

        var projection = new CompiledProjection(query, jsonText, closed, conversion: null, vocabulary);
        return new TypedProjection(jsonText ? "json-path-text" : "json-path", arguments[0], projection, node.Position);
    }
}
