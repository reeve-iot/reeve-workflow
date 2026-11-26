using System.Collections.Frozen;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Expressions;

internal enum ExpressionSignatureKind
{
    Operator,
    Function,
}

internal sealed record ExpressionSignature(
    string Id, ExpressionSignatureKind Kind, string Name, IReadOnlyList<string> Parameters, string Result,
    IReadOnlyList<string> Failures, bool Variadic = false, string? Capability = null)
{
    public bool IsGeneric => Parameters.Any(p => p.StartsWith('$'));

    public string Render() => Kind switch
    {
        ExpressionSignatureKind.Function => $"{Name}({string.Join(", ", Parameters)}{(Variadic ? "…" : "")}) -> {Result}",
        _ when Parameters.Count == 1 => $"{Name}{Parameters[0]} -> {Result}",
        _ => $"{string.Join($" {Name} ", Parameters)} -> {Result}",
    };

    public bool Takes(IReadOnlyList<PortTerm> arguments)
    {
        if (IsGeneric || (Variadic ? arguments.Count < Parameters.Count : arguments.Count != Parameters.Count))
            return false;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (!string.Equals(Parameters[Math.Min(i, Parameters.Count - 1)], arguments[i].ToString(), StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

internal static class ExpressionSignatures
{
    public static IReadOnlyList<ExpressionSignature> All { get; } =
    [
        new("int-add", ExpressionSignatureKind.Operator, "+", ["Int", "Int"], "Int", ["overflow"]),
        new("number-add", ExpressionSignatureKind.Operator, "+", ["Number", "Number"], "Number", ["overflow"]),
        new("string-concat", ExpressionSignatureKind.Operator, "+", ["String", "String"], "String", []),
        new("int-sub", ExpressionSignatureKind.Operator, "-", ["Int", "Int"], "Int", ["overflow"]),
        new("number-sub", ExpressionSignatureKind.Operator, "-", ["Number", "Number"], "Number", ["overflow"]),
        new("int-mul", ExpressionSignatureKind.Operator, "*", ["Int", "Int"], "Int", ["overflow"]),
        new("number-mul", ExpressionSignatureKind.Operator, "*", ["Number", "Number"], "Number", ["overflow"]),
        new("int-rem", ExpressionSignatureKind.Operator, "%", ["Int", "Int"], "Int", ["division-by-zero"]),
        new("number-rem", ExpressionSignatureKind.Operator, "%", ["Number", "Number"], "Number", ["division-by-zero"]),
        new("number-div", ExpressionSignatureKind.Operator, "/", ["Number", "Number"], "Number", ["division-by-zero", "overflow"]),
        new("int-neg", ExpressionSignatureKind.Operator, "-", ["Int"], "Int", ["overflow"]),
        new("number-neg", ExpressionSignatureKind.Operator, "-", ["Number"], "Number", []),
        new("lt-int", ExpressionSignatureKind.Operator, "<", ["Int", "Int"], "Boolean", []),
        new("lt-number", ExpressionSignatureKind.Operator, "<", ["Number", "Number"], "Boolean", []),
        new("lt-string", ExpressionSignatureKind.Operator, "<", ["String", "String"], "Boolean", []),
        new("lt-datetime", ExpressionSignatureKind.Operator, "<", ["DateTime", "DateTime"], "Boolean", []),
        new("le-int", ExpressionSignatureKind.Operator, "<=", ["Int", "Int"], "Boolean", []),
        new("le-number", ExpressionSignatureKind.Operator, "<=", ["Number", "Number"], "Boolean", []),
        new("le-string", ExpressionSignatureKind.Operator, "<=", ["String", "String"], "Boolean", []),
        new("le-datetime", ExpressionSignatureKind.Operator, "<=", ["DateTime", "DateTime"], "Boolean", []),
        new("gt-int", ExpressionSignatureKind.Operator, ">", ["Int", "Int"], "Boolean", []),
        new("gt-number", ExpressionSignatureKind.Operator, ">", ["Number", "Number"], "Boolean", []),
        new("gt-string", ExpressionSignatureKind.Operator, ">", ["String", "String"], "Boolean", []),
        new("gt-datetime", ExpressionSignatureKind.Operator, ">", ["DateTime", "DateTime"], "Boolean", []),
        new("ge-int", ExpressionSignatureKind.Operator, ">=", ["Int", "Int"], "Boolean", []),
        new("ge-number", ExpressionSignatureKind.Operator, ">=", ["Number", "Number"], "Boolean", []),
        new("ge-string", ExpressionSignatureKind.Operator, ">=", ["String", "String"], "Boolean", []),
        new("ge-datetime", ExpressionSignatureKind.Operator, ">=", ["DateTime", "DateTime"], "Boolean", []),
        new("equal", ExpressionSignatureKind.Operator, "==", ["$T", "$T"], "Boolean", [], Capability: "Equatable"),
        new("not-equal", ExpressionSignatureKind.Operator, "!=", ["$T", "$T"], "Boolean", [], Capability: "Equatable"),
        new("and", ExpressionSignatureKind.Operator, "&&", ["Boolean", "Boolean"], "Boolean", []),
        new("or", ExpressionSignatureKind.Operator, "||", ["Boolean", "Boolean"], "Boolean", []),
        new("not", ExpressionSignatureKind.Operator, "!", ["Boolean"], "Boolean", []),
        new("conditional", ExpressionSignatureKind.Operator, "?:", ["Boolean", "$T", "$T"], "LUB($T)", []),
        new("coalesce", ExpressionSignatureKind.Operator, "??", ["$T?", "$T"], "LUB($T)", []),
        new("min-int", ExpressionSignatureKind.Function, "min", ["Int"], "Int", [], Variadic: true),
        new("min-number", ExpressionSignatureKind.Function, "min", ["Number"], "Number", [], Variadic: true),
        new("max-int", ExpressionSignatureKind.Function, "max", ["Int"], "Int", [], Variadic: true),
        new("max-number", ExpressionSignatureKind.Function, "max", ["Number"], "Number", [], Variadic: true),
        new("abs-int", ExpressionSignatureKind.Function, "abs", ["Int"], "Int", ["overflow"]),
        new("abs-number", ExpressionSignatureKind.Function, "abs", ["Number"], "Number", []),
        new("round-number", ExpressionSignatureKind.Function, "round", ["Number"], "Int", ["overflow"]),
        new("round-int", ExpressionSignatureKind.Function, "round", ["Int"], "Int", []),
        new("floor-number", ExpressionSignatureKind.Function, "floor", ["Number"], "Int", ["overflow"]),
        new("floor-int", ExpressionSignatureKind.Function, "floor", ["Int"], "Int", []),
        new("ceil-number", ExpressionSignatureKind.Function, "ceil", ["Number"], "Int", ["overflow"]),
        new("ceil-int", ExpressionSignatureKind.Function, "ceil", ["Int"], "Int", []),
        new("len", ExpressionSignatureKind.Function, "len", ["String"], "Int", []),
        new("contains", ExpressionSignatureKind.Function, "contains", ["String", "String"], "Boolean", []),
        new("starts-with", ExpressionSignatureKind.Function, "startsWith", ["String", "String"], "Boolean", []),
        new("ends-with", ExpressionSignatureKind.Function, "endsWith", ["String", "String"], "Boolean", []),
        new("to-number-int", ExpressionSignatureKind.Function, "toNumber", ["Int"], "Number", []),
        new("to-number-string", ExpressionSignatureKind.Function, "toNumber", ["String"], "Number?", []),
        new("to-string-boolean", ExpressionSignatureKind.Function, "toString", ["Boolean"], "String", []),
        new("to-string-int", ExpressionSignatureKind.Function, "toString", ["Int"], "String", []),
        new("to-string-number", ExpressionSignatureKind.Function, "toString", ["Number"], "String", []),
        new("to-string-string", ExpressionSignatureKind.Function, "toString", ["String"], "String", []),
        new("to-string-datetime", ExpressionSignatureKind.Function, "toString", ["DateTime"], "String", []),
        new("to-json-text", ExpressionSignatureKind.Function, "toJsonText", ["$T"], "String", [], Capability: "JsonSerializable"),
        new("parse-json", ExpressionSignatureKind.Function, "parseJson", ["String"], "Json?", ["json-parse"]),
        new("json-path", ExpressionSignatureKind.Function, "jsonPath", ["Json?", "String", "String"], "$type", ["value-absent", "narrowing", "too-large"]),
        new("json-path-text", ExpressionSignatureKind.Function, "jsonPathText", ["Json?", "String", "String"], "$type", ["value-absent", "narrowing", "too-large"]),
    ];

    private static readonly FrozenDictionary<string, ExpressionSignature> ById = All.ToFrozenDictionary(s => s.Id, StringComparer.Ordinal);

    public static ExpressionSignature Get(string id) => ById[id];

    public static ExpressionSignature? Bind(ExpressionSignatureKind kind, string name, IReadOnlyList<PortTerm> arguments) =>
        All.FirstOrDefault(s => s.Kind == kind && s.Name == name && s.Takes(arguments));

    public static IReadOnlyList<ExpressionSignature> Candidates(ExpressionSignatureKind kind, string name, int arity)
    {
        var named = All.Where(s => s.Kind == kind && s.Name == name).ToList();
        var fitting = named.Where(s => s.Variadic ? arity >= s.Parameters.Count : arity == s.Parameters.Count).ToList();
        return fitting.Count > 0 ? fitting : named;
    }

    public static bool IsFunction(string name) => All.Any(s => s.Kind == ExpressionSignatureKind.Function && s.Name == name);

    public static PortTerm ResultOf(ExpressionSignature signature) =>
        PortTerm.Parse(signature.Result, PortTermGrammar.Document);

    internal static string Describe(IEnumerable<PortTerm> terms) =>
        string.Join(", ", terms.Select(t => t.ToString()));
}
