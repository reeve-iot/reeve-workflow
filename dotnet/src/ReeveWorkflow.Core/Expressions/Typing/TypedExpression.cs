using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using System.Runtime.CompilerServices;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>The text of an <c>expr:</c> value: a value is an expression when it starts with <see cref="Prefix"/>, and
/// the typer and the evaluator take the text after it.</summary>
public static class ExpressionSource
{
    /// <summary>The sentinel that marks a stored value as an expression.</summary>
    public const string Prefix = "expr:";

    /// <summary>Whether <paramref name="value"/> starts with <see cref="Prefix"/>.</summary>
    public static bool IsExpression(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The expression text of an <c>expr:</c> value, which is the value without its prefix.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not start with <see cref="Prefix"/>.</exception>
    public static string Strip(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsExpression(value) ? value[Prefix.Length..] : throw new ArgumentException("value does not start with 'expr:'", nameof(value));
    }
}

/// <summary>A node of the typed tree <see cref="ExpressionTyper"/> answers: an expression node with its term, every
/// operator and function bound to one overload, every conversion explicit. Only the typer builds one; a host hands it
/// to <see cref="TypedExpressionEvaluator"/>.</summary>
public abstract record TypedExpression
{
    private static readonly ConditionalWeakTable<TypedExpression, HelperPathCache> HelperPathCaches = new();
    private protected TypedExpression(PortTerm term, int position)
    {
        Term = term;
        Position = position;
    }

    /// <summary>The closed term of the value this node evaluates to; <see cref="PortTerm.Null"/> for the literal <c>null</c>.
    /// A run answers a value in the CLR type the vocabulary gives the term.</summary>
    public PortTerm Term { get; }

    /// <summary>Where the node starts in the expression text, as the parser reports it.</summary>
    public int Position { get; }

    /// <summary>Distinct helper paths read by this typed tree.</summary>
    public IReadOnlyCollection<HelperPath> HelperPaths
    {
        get => HelperPathCaches.GetValue(this, static expression => new HelperPathCache(Collect(expression).Distinct().ToArray())).Paths;
    }

    private sealed record HelperPathCache(IReadOnlyCollection<HelperPath> Paths);

    private static IEnumerable<HelperPath> Collect(TypedExpression node)
    {
        switch (node)
        {
            case TypedRead { Namespace: "helper" } read: yield return new HelperPath(read.Key); break;
            case TypedConversion conversion: foreach (var path in Collect(conversion.Operand)) yield return path; break;
            case TypedOperation operation: foreach (var child in operation.Operands) foreach (var path in Collect(child)) yield return path; break;
            case TypedProjection projection: foreach (var path in Collect(projection.Subject)) yield return path; break;
        }
    }
}

/// <summary>A literal: an <c>Int</c> (<see cref="long"/>), a <c>Number</c> (<see cref="double"/>), a <c>String</c>, a
/// <c>Boolean</c>, or the literal <c>null</c>.</summary>
public sealed record TypedLiteral : TypedExpression
{
    internal TypedLiteral(PortTerm term, object? value, int position) : base(term, position) => Value = value;

    /// <summary>The value, in the CLR type of <see cref="TypedExpression.Term"/>; null for the literal <c>null</c>.</summary>
    public object? Value { get; }
}

/// <summary>A <c>${…}</c> read. Its term is the one the read's source answered; a field after the key never reaches a
/// typed tree, since a member or an index is refused.</summary>
public sealed record TypedRead : TypedExpression
{
    internal TypedRead(PortTerm term, string @namespace, IReadOnlyList<string> key, int position) : base(term, position)
    {
        Namespace = @namespace;
        Key = key;
    }

    /// <summary>The namespace: <c>previous</c>, <c>parameter</c>, <c>now</c>, <c>helper</c> or a host's.</summary>
    public string Namespace { get; }

    /// <summary>The key segments after the namespace: the node and the output of a <c>previous</c> read, the name of a parameter.</summary>
    public IReadOnlyList<string> Key { get; }

    /// <summary>The read as an author wrote it inside <c>${…}</c>.</summary>
    public string Path => string.Join('.', Key.Prepend(Namespace));
}

/// <summary>What a conversion node does to the value of its operand.</summary>
public enum TypedConversionKind
{
    /// <summary>An <c>Int</c> (or <c>Int?</c>) becomes a <c>Number</c> (or <c>Number?</c>).</summary>
    Widen,

    /// <summary>A <c>String</c>, <c>Int</c>, <c>Number</c>, <c>Boolean</c> or <c>DateTime</c> becomes its invariant text.</summary>
    ToText,

    /// <summary>A value is brought to the least upper bound of the branches of a <c>?:</c>: a collection is rebuilt
    /// element by element.</summary>
    Convert,
}

/// <summary>An explicit conversion of one operand.</summary>
public sealed record TypedConversion : TypedExpression
{
    internal TypedConversion(TypedConversionKind kind, TypedExpression operand, PortTerm term) : base(term, operand.Position)
    {
        Kind = kind;
        Operand = operand;
    }

    /// <summary>What the conversion does.</summary>
    public TypedConversionKind Kind { get; }

    /// <summary>The converted node.</summary>
    public TypedExpression Operand { get; }
}

/// <summary>An operator or function bound to one overload of <c>expression-functions.json</c>.</summary>
public sealed record TypedOperation : TypedExpression
{
    internal TypedOperation(string overload, IReadOnlyList<TypedExpression> operands, PortTerm term, int position)
        : base(term, position)
    {
        Overload = overload;
        Operands = operands;
    }

    /// <summary>The id of the bound row of <c>expression-functions.json</c>: <c>int-add</c>, <c>number-div</c>, <c>conditional</c>.</summary>
    public string Overload { get; }

    /// <summary>The operands or arguments, in order, each already converted to its parameter's term.</summary>
    public IReadOnlyList<TypedExpression> Operands { get; }
}

/// <summary>A <c>jsonPath</c> or <c>jsonPathText</c> call: the projection of its subject, a <c>Json</c> or <c>Json?</c> value, by a
/// query the typer parsed once, delivered in the term the call's type names, which is its <see cref="TypedExpression.Term"/>.</summary>
public sealed record TypedProjection : TypedExpression
{
    internal TypedProjection(string overload, TypedExpression subject, CompiledProjection projection, int position)
        : base(projection.Term.Term, position)
    {
        Overload = overload;
        Subject = subject;
        Projection = projection;
    }

    /// <summary>The id of the bound row of <c>expression-functions.json</c>: <c>json-path</c> or <c>json-path-text</c>.</summary>
    public string Overload { get; }

    /// <summary>The value the query reads.</summary>
    public TypedExpression Subject { get; }

    /// <summary>The query, as the author wrote it in the call.</summary>
    public string Path => Projection.Query.Text;

    /// <summary>Whether each node is delivered as its JSON text, as <c>jsonPathText</c> does.</summary>
    public bool JsonText => Projection.JsonText;

    internal CompiledProjection Projection { get; }
}
