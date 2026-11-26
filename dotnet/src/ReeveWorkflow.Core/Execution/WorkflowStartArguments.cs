using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed class AdmittedArguments(Dictionary<string, object?> values) : ReadOnlyDictionary<string, object?>(values);

internal static class WorkflowStartArguments
{
    internal sealed record Outcome(
        AdmittedArguments Values, IReadOnlyList<WorkflowValidationFinding> Findings, IReadOnlyList<WorkflowStartArgument> HostArguments);

    internal static Outcome Check(
        CanvasSignature signature, IReadOnlyDictionary<string, object?>? supplied, WorkflowTypeVocabulary vocabulary)
    {
        supplied ??= ReadOnlyDictionary<string, object?>.Empty;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var findings = new List<WorkflowValidationFinding>();
        var host = new List<WorkflowStartArgument>();
        var declared = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in signature.Inputs)
        {
            declared.Add(parameter.Name);
            var found = supplied.TryGetValue(parameter.Name, out var argument);
            var term = PortTerm.TryParse(parameter.Type.Name, PortTermGrammar.Document, out var parsed, out _) ? parsed : null;
            if (!found)
            {
                if (parameter.IsRequired)
                {
                    findings.Add(Finding(WorkflowStartArgumentCodes.Missing, parameter.Name,
                        $"Parameter '{parameter.Name}' is required and no argument was supplied (expected {Show(term, parameter.Type.Name)})."));
                }

                continue;
            }

            if (term is null || vocabulary.ClosedTypeOf(term) is not { } type)
                throw new InvalidOperationException($"Parameter '{parameter.Name}' of a compiled program has no closed type '{parameter.Type.Name}'.");

            if (Read(argument, type, vocabulary, out var value, out var received))
            {
                values[parameter.Name] = value;
                if (vocabulary.HoldsHostName(type.Term))
                    host.Add(new WorkflowStartArgument(parameter.Name, type.Term.ToString(), value));
            }
            else
            {
                findings.Add(Finding(WorkflowStartArgumentCodes.TypeMismatch, parameter.Name,
                    $"Parameter '{parameter.Name}' expects {type.Term}, received {received}."));
            }
        }

        foreach (var name in supplied.Keys)
        {
            if (!declared.Contains(name))
            {
                findings.Add(Finding(WorkflowStartArgumentCodes.Unknown, name,
                    $"Argument '{name}' is not a parameter of this workflow."));
            }
        }

        return new Outcome(new AdmittedArguments(values), findings, host);
    }

    internal static IReadOnlyDictionary<string, object?> ReadPayload(
        IReadOnlyDictionary<string, object?> payload, WorkflowExpressionNamespace? trigger, WorkflowTypeVocabulary vocabulary,
        List<WorkflowValidationFinding> findings, Dictionary<string, ClosedPortType>? terms = null)
    {
        var read = new Dictionary<string, object?>(payload.Count, StringComparer.Ordinal);
        foreach (var (key, field) in payload)
        {
            if (PayloadTermOf(key, trigger, vocabulary) is not { } type)
                continue;

            if (ReadField(field, type, vocabulary, out var value, out var received))
            {
                read[key] = value;
                if (terms is not null)
                    terms[key] = type;
            }
            else
            {
                findings.Add(Finding(WorkflowStartArgumentCodes.TypeMismatch, $"trigger.{key}",
                    $"Payload field 'trigger.{key}' expects {type.Term}, received {received}."));
            }
        }

        return new ReadOnlyDictionary<string, object?>(read);
    }

    internal static WorkflowExpressionNamespace? TriggerNamespace(CompilationOptions options) =>
        options.NamespaceRegistrations.FirstOrDefault(r => r.Namespace == "trigger");

    internal static Func<string, ClosedPortType?> PayloadTerms(CompilationOptions options)
    {
        var trigger = TriggerNamespace(options);
        var vocabulary = options.Vocabulary;
        return key => PayloadTermOf(key, trigger, vocabulary);
    }

    internal static ClosedPortType? PayloadTermOf(string key, WorkflowExpressionNamespace? trigger, WorkflowTypeVocabulary vocabulary)
    {
        if (trigger is not { KeySegments: 1 })
            return null;
        if (key == TriggerEnvelope.IdKey)
            return vocabulary.ClosedTypeOf(PortTerm.Atom("String", isNullable: true));
        if (key == TriggerEnvelope.FiredAtKey)
            return vocabulary.ClosedTypeOf(PortTerm.Atom("DateTime", isNullable: true));
        var answer = trigger.StaticCheck.CheckWithKeyType([key]);
        return answer.Verdict == NamespacePathCheck.Yes
            && PortTerm.TryParse(answer.KeyPortType, PortTermGrammar.Document, out var term, out _)
                ? vocabulary.ClosedTypeOf(term)
                : null;
    }

    private static WorkflowValidationFinding Finding(string code, string parameter, string message) =>
        WorkflowValidationFinding.Of(code, message) with { Parameter = parameter };

    private static string Show(PortTerm? term, string written) => term is null ? written : term.ToString();

    private static bool ReadField(
        object? field, ClosedPortType type, WorkflowTypeVocabulary vocabulary, out object? value, out string received)
    {
        if (field is not null and not JsonElement && type.ElementType is null
            && type.Term is { Kind: PortTermKind.Atom, Name: "Json" })
        {
            try
            {
                return Read(JsonSerializer.SerializeToElement(field, field.GetType(), WorkflowGraphHelpers.JsonOptions), type, vocabulary,
                    out value, out received);
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
            {
                value = null;
                received = $"a {field.GetType().Name} that cannot be written as JSON";
                return false;
            }
        }

        return Read(field, type, vocabulary, out value, out received);
    }

    private static bool Read(
        object? argument, ClosedPortType type, WorkflowTypeVocabulary vocabulary, out object? value, out string received)
    {
        value = null;
        if (argument is JsonElement element)
        {
            return ReadJson(element, type, out value, out received);
        }

        received = argument is null ? "null" : $"a {argument.GetType().Name}";
        var held = ExpressionNamespaceValues.Hold(type.Term, argument, vocabulary);
        value = held.Value;
        return held.Ok;
    }

    private static bool ReadJson(JsonElement element, ClosedPortType type, out object? value, out string received)
    {
        var ok = type.TryFromJson(element, out value, out var reason);
        received = ok ? Describe(element) : reason ?? Describe(element);
        return ok;
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "a JSON string",
        JsonValueKind.Number => "a JSON number",
        JsonValueKind.True or JsonValueKind.False => "a JSON boolean",
        JsonValueKind.Object => "a JSON object",
        JsonValueKind.Array => "a JSON array",
        _ => "null",
    };
}
