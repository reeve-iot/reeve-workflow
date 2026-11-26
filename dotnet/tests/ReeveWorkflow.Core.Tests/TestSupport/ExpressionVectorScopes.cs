using System.Collections;
using System.Globalization;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.TestSupport;

internal sealed class ExpressionVectorScopes
{
    private readonly Dictionary<string, (ExpressionTypingScope Typing, ExpressionEvaluationScope Evaluation)> scopes = new(StringComparer.Ordinal);

    public ExpressionVectorScopes(JsonElement scopesElement, WorkflowTypeVocabulary vocabulary)
    {
        foreach (var scope in scopesElement.EnumerateObject())
            scopes[scope.Name] = Build(scope.Value, vocabulary);
    }

    public ExpressionTypingScope Typing(string name) => scopes[name].Typing;

    public ExpressionEvaluationScope Evaluation(string name) => scopes[name].Evaluation;

    public static ExpressionVectorScopes Load(WorkflowTypeVocabulary? vocabulary = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.ExpressionTypingPath()));
        return new(document.RootElement.GetProperty("scopes").Clone(), vocabulary ?? WorkflowTypeVocabulary.Builtin);
    }

    public static object? Clr(PortTerm term, JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Null)
            return null;
        var core = term.IsNullable ? term.WithoutNullable() : term;
        if (core.IsCollection)
        {
            var element = WorkflowTypeVocabulary.Builtin.ClrTypeOf(core.Arguments[0])!;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in json.EnumerateArray())
                list.Add(Clr(core.Arguments[0], item));
            return list;
        }

        if (core.IsMap)
        {
            return WorkflowTypeVocabulary.Builtin.ClosedTypeOf(core)!.CreateMap(
                json.EnumerateObject().Select(p => new KeyValuePair<string, object?>(p.Name, Clr(core.Arguments[1], p.Value))));
        }

        return core.Name switch
        {
            "Int" => json.GetInt64(),
            "Number" => json.GetDouble(),
            "String" => json.GetString(),
            "Boolean" => json.GetBoolean(),
            "DateTime" => DateTimeOffset.Parse(json.GetString()!, CultureInfo.InvariantCulture),
            "Json" => json.Clone(),
            _ => throw new InvalidOperationException($"The vector term '{term}' has no CLR reading."),
        };
    }

    private static (ExpressionTypingScope, ExpressionEvaluationScope) Build(JsonElement scope, WorkflowTypeVocabulary vocabulary)
    {
        var typing = new ExpressionTypingScope.Builder().Vocabulary(vocabulary);
        var evaluation = new ExpressionEvaluationScope.Builder().Vocabulary(vocabulary);

        if (scope.TryGetProperty("previous", out var previous))
        {
            var outputs = Entries(previous, nested: true);
            typing.Previous((node, output) => outputs.TryGetValue(node, out var node1)
                ? node1.TryGetValue(output, out var entry)
                    ? ReadLookup.Typed(entry.Term)
                    : ReadLookup.Missing($"node '{node}' has no output '{output}'")
                : ReadLookup.Missing($"there is no node '{node}'"));
            evaluation.Previous((node, output) =>
                outputs.TryGetValue(node, out var node1) && node1.TryGetValue(output, out var entry) && !entry.Absent ? (true, entry.Value) : (false, null));
        }

        if (scope.TryGetProperty("parameters", out var parameters))
        {
            var declared = Entries(parameters, nested: false)[""];
            typing.Parameters(name => declared.TryGetValue(name, out var entry)
                ? ReadLookup.Typed(entry.Term)
                : ReadLookup.Missing("the workflow declares no such parameter"));
            evaluation.Parameters(name => declared.TryGetValue(name, out var entry) ? (true, entry.Value) : (false, null));
        }

        foreach (var ns in scope.GetProperty("namespaces").EnumerateObject())
        {
            var paths = Entries(ns.Value.GetProperty("paths"), nested: false)[""];
            var binding = new ExpressionNamespaceBinding(
                new VectorNamespace(ns.Name, paths), ns.Value.GetProperty("keySegments").GetInt32(), new VectorNamespace(ns.Name, paths));
            typing.Namespace(binding);
            evaluation.Namespace(binding);
        }

        if (scope.TryGetProperty("helpers", out var helpers))
        {
            var helper = Entries(helpers, nested: false)[""];
            typing.Helper(helper.ToDictionary(p => new HelperPath(p.Key.Split('.')), p => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, p.Value.Term.ToString())));
            evaluation.Helper(helper.ToDictionary(p => new HelperPath(p.Key.Split('.')), p => p.Value.Value));
        }

        var now = DateTimeOffset.Parse(scope.GetProperty("now").GetString()!, CultureInfo.InvariantCulture);
        var localNow = DateTimeOffset.Parse(scope.GetProperty("localNow").GetString()!, CultureInfo.InvariantCulture);
        evaluation.Now(now, localNow);
        if (!scope.TryGetProperty("variables", out var variables))
            return (typing.Build(), evaluation.Build());

        var declaredVariables = Entries(variables, nested: false)[""];
        typing.Variables(name => declaredVariables.TryGetValue(name, out var entry)
            ? ReadLookup.Typed(entry.Term)
            : ReadLookup.Missing($"the input declares no scope variable '{name}'"));
        return (typing.Build(), evaluation.Build().WithVariables(declaredVariables.ToDictionary(v => v.Key, v => v.Value.Value, StringComparer.Ordinal)));
    }

    private sealed record Entry(PortTerm Term, object? Value, bool Absent = false);

    private static Dictionary<string, Dictionary<string, Entry>> Entries(JsonElement element, bool nested)
    {
        var all = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
        if (!nested)
        {
            all[""] = element.EnumerateObject().ToDictionary(p => p.Name, p => EntryOf(p.Value), StringComparer.Ordinal);
            return all;
        }

        foreach (var node in element.EnumerateObject())
            all[node.Name] = node.Value.EnumerateObject().ToDictionary(p => p.Name, p => EntryOf(p.Value), StringComparer.Ordinal);
        return all;
    }

    private static Entry EntryOf(JsonElement element)
    {
        var term = PortTerm.Parse(element.GetProperty("term").GetString()!, PortTermGrammar.Document);
        return element.TryGetProperty("absent", out var absent) && absent.GetBoolean()
            ? new Entry(term, null, Absent: true)
            : new Entry(term, Clr(term, element.GetProperty("value")));
    }

    private sealed class VectorNamespace(string name, Dictionary<string, Entry> paths) : IExpressionNamespaceResolver, IExpressionNamespaceStaticCheck
    {
        public string Namespace => name;

        public ResolveResult Resolve(string[] segments) => paths.TryGetValue(string.Join('.', segments), out var entry)
            ? ResolveResult.Resolved(entry.Value)
            : ResolveResult.Failed();

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => paths.TryGetValue(string.Join('.', segments), out var entry)
            ? new(NamespacePathCheck.Yes, entry.Term.ToString())
            : NamespacePathCheckAnswer.No;
    }

    private sealed class VectorHelper(Dictionary<string, Entry> helpers) : IHelperResolver
    {
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.Where(p => helpers.ContainsKey(string.Join('.', p.Segments))).ToDictionary(p => p, p => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, helpers[string.Join('.', p.Segments)].Term.ToString())));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(paths.Where(p => helpers.ContainsKey(string.Join('.', p.Segments))).ToDictionary(p => p, p => helpers[string.Join('.', p.Segments)].Value));
    }
}
