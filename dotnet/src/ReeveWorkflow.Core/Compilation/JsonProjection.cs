using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.JsonPath;

namespace ReeveWorkflow.Core.Compilation;

internal static class ProjectionTerms
{
    private static readonly HashSet<string> Scalars = new(["Boolean", "Int", "Number", "String", "DateTime"], StringComparer.Ordinal);

    public static bool IsScalar(PortTerm term) => term.Kind == PortTermKind.Atom && Scalars.Contains(term.Name);

    public static bool Delivers(PortTerm term, bool intoWorkflowOutput, WorkflowTypeVocabulary vocabulary)
    {
        var canonical = vocabulary.Table.Canonicalize(term);
        if (intoWorkflowOutput && IsJson(canonical))
            return true;
        if (canonical.IsMap)
            return IsScalar(canonical.Arguments[1]);
        var leaf = Leaf(canonical);
        return leaf.Kind == PortTermKind.Atom && Scalars.Contains(leaf.Name);
    }

    public static string? Refusal(PortTerm term, JsonPathQuery query, bool jsonText, bool intoWorkflowOutput, WorkflowTypeVocabulary vocabulary)
    {
        if (!Delivers(term, intoWorkflowOutput, vocabulary))
            return $"delivers {term}; a projection delivers Boolean, Int, Number, String or DateTime, collections of them, or a Map<String,V> with V one of them.";
        if (!term.IsCollection && !query.IsSingular)
            return $"has the path '{query.Text}', which is not a singular query, and {term} is not a collection.";
        if (jsonText && !IsText(term, vocabulary))
            return $"is in jsonText mode, which delivers text, and {term} is not String.";
        return null;
    }

    public static bool IsText(PortTerm term, WorkflowTypeVocabulary vocabulary) =>
        Leaf(vocabulary.Table.Canonicalize(term)) is { Kind: PortTermKind.Atom, Name: "String" };

    public static bool IsJson(PortTerm term) =>
        term.Kind == PortTermKind.Atom && string.Equals(term.Name, "Json", StringComparison.OrdinalIgnoreCase);

    private static PortTerm Leaf(PortTerm term)
    {
        while (term.IsCollection)
            term = term.Arguments[0];
        return term;
    }
}

internal sealed class CompiledProjection
{
    private readonly WorkflowTypeVocabulary vocabulary;

    public CompiledProjection(JsonPathQuery query, bool jsonText, ClosedPortType term, PortConversion? conversion, WorkflowTypeVocabulary vocabulary)
    {
        Query = query;
        JsonText = jsonText;
        Term = term;
        Conversion = conversion;
        this.vocabulary = vocabulary;
    }

    public JsonPathQuery Query { get; }

    public bool JsonText { get; }

    public ClosedPortType Term { get; }

    public PortConversion? Conversion { get; }

    public bool TryBind(object? source, string wire, out object? value)
    {
        var nodes = Nodes(source, wire);
        object? projected;
        if (Term.ElementType is { } element)
        {
            projected = Term.CreateList(nodes.Select(node => Grade(node, element, wire)));
        }
        else if (nodes.Count == 0)
        {
            if (!Term.Term.IsNullable)
                throw new JsonProjectionException($"{wire}: the path '{Query.Text}' found nothing, and {Term.Term} is not nullable.", JsonProjectionException.ValueAbsent);
            value = null;
            return false;
        }
        else
        {
            projected = Term.ValueType is null ? Grade(nodes[0], Term, wire) : GradeMap(nodes[0], Term, wire);
        }

        value = Conversion is null ? projected : Conversion.Apply(projected);
        return true;
    }

    private IReadOnlyList<JsonElement> Nodes(object? source, string wire)
    {
        if (source is null)
            return [];
        JsonElement root;
        try
        {
            root = ExpressionJsonValue.Recorded(source);
        }
        catch (Exception ex) when (ExpressionJsonValue.Unwritable(ex))
        {
            throw new JsonProjectionException($"{wire}: the source's value has no JSON form: {ex.Message}", JsonProjectionException.Narrowing);
        }

        if (JsonMembers.FindRepeat(root) is { } repeat)
            throw new JsonProjectionException($"{wire}: the source holds an ambiguous object: {repeat}.", JsonProjectionException.Narrowing);

        if (root.ValueKind == JsonValueKind.Null)
            return [];
        try
        {
            return Query.Evaluate(root);
        }
        catch (JsonPathBudgetException budget)
        {
            throw new JsonProjectionException($"{wire}: the path '{Query.Text}' {budget.Reason}.", JsonProjectionException.TooLarge);
        }
    }

    private object? GradeMap(JsonElement node, ClosedPortType map, string wire)
    {
        if (node.ValueKind == JsonValueKind.Null)
            return Grade(node, map, wire);
        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new JsonProjectionException(
                $"{wire}: the path '{Query.Text}' found a JSON {node.ValueKind.ToString().ToLowerInvariant()}, which is not an object, and {map.Term} takes one.",
                JsonProjectionException.Narrowing);
        }

        var entries = new List<KeyValuePair<string, object?>>();
        foreach (var member in node.EnumerateObject())
            entries.Add(new(member.Name, Grade(member.Value, map.ValueType!, wire, member.Name)));

        return map.CreateMap(entries);
    }

    private string Found(string? member, string what) =>
        member is null ? $"the path '{Query.Text}' found {what}" : $"member '{member}' of the object at '{Query.Text}' is {what}";

    private object? Grade(JsonElement node, ClosedPortType type, string wire, string? member = null)
    {
        if (node.ValueKind == JsonValueKind.Null)
        {
            return type.Term.IsNullable
                ? null
                : throw new JsonProjectionException($"{wire}: {Found(member, "null")}, and {type.Term} is not nullable.", JsonProjectionException.Narrowing);
        }

        if (JsonText)
        {
            return new ExpressionJsonValue(node).TryText(out var text, out var why)
                ? text
                : throw new JsonProjectionException($"{wire}: a node has no JSON text: {why}", JsonProjectionException.Narrowing);
        }

        if (ProjectionTerms.IsJson(type.Term))
            return node.Clone();
        if (!vocabulary.IsLiteralOf(node, PortValueType.Of(type.Term.ToString())))
        {
            throw new JsonProjectionException(
                $"{wire}: {Found(member, "a JSON " + node.ValueKind.ToString().ToLowerInvariant())}, which is not a value of {type.Term}.",
                JsonProjectionException.Narrowing);
        }

        return WorkflowCompiler.Coerce(node, type, vocabulary);
    }
}

internal sealed class JsonProjectionException : Exception, IWorkflowCodedException
{
    public const string ValueAbsent = "value-absent";
    public const string Narrowing = "narrowing";
    public const string TooLarge = "too-large";

    public JsonProjectionException(string message, string category)
        : base(message)
    {
        Category = category;
    }

    public string Code => WorkflowErrorCodes.JsonProjectionFailed;

    public string Category { get; }
}
