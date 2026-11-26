using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Expressions;

internal static class ExpressionNamespaceValues
{
    internal static (bool Ok, object? Value, string? Why) Hold(PortTerm term, object? value, WorkflowTypeVocabulary vocabulary)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            value = null;
        if (vocabulary.ClosedTypeOf(term) is not { } type)
            return (false, null, $"its term '{term}' has no CLR type");
        return ValueGate.Admit(type, value, out var admitted) is { } why ? (false, null, why) : (true, admitted, null);
    }

    internal static WorkflowTypeVocabulary? VocabularyOf(IServiceProvider services) =>
        services.GetService<CompilationOptions>()?.Vocabulary ?? services.GetService<IWorkflowTypeRegistry>()?.Current;
}
