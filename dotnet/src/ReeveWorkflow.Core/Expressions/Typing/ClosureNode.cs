using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Expressions;

internal enum ClosureShape
{
    Object,
    Int,
    Number,
    Boolean,
    Text,
}

internal readonly struct ClosureNode
{
    private readonly Delegate evaluate;

    private ClosureNode(ClosureShape shape, Delegate evaluate)
    {
        Shape = shape;
        this.evaluate = evaluate;
    }

    public ClosureShape Shape { get; }

    public static ClosureShape ShapeOf(PortTerm term)
    {
        if (term.Kind != PortTermKind.Atom)
            return ClosureShape.Object;
        if (term.Equals(Terms.Int))
            return ClosureShape.Int;
        if (term.Equals(Terms.Number))
            return ClosureShape.Number;
        if (term.Equals(Terms.Boolean))
            return ClosureShape.Boolean;
        return term.Equals(Terms.String) ? ClosureShape.Text : ClosureShape.Object;
    }

    public static ClosureNode Of<T>(Func<ExpressionEvaluationScope, T> evaluate)
    {
        if (typeof(T) == typeof(long))
            return new(ClosureShape.Int, evaluate);
        if (typeof(T) == typeof(double))
            return new(ClosureShape.Number, evaluate);
        if (typeof(T) == typeof(bool))
            return new(ClosureShape.Boolean, evaluate);
        if (typeof(T) == typeof(string))
            return new(ClosureShape.Text, evaluate);
        return typeof(T) == typeof(object)
            ? new(ClosureShape.Object, evaluate)
            : throw new InvalidOperationException($"A closure cannot answer a {typeof(T).Name}.");
    }

    public Func<ExpressionEvaluationScope, T> As<T>()
    {
        if (evaluate is Func<ExpressionEvaluationScope, T> same)
            return same;
        if (typeof(T) == typeof(object))
            return (Func<ExpressionEvaluationScope, T>)(object)Boxed();
        if (Shape != ClosureShape.Object)
            throw new InvalidOperationException($"A {Shape} closure cannot feed a {typeof(T).Name} operand.");
        var boxed = Boxed();
        return scope => (T)boxed(scope)!;
    }

    public ClosureNode In(ClosureShape shape) => shape switch
    {
        ClosureShape.Int => Of(As<long>()),
        ClosureShape.Number => Of(As<double>()),
        ClosureShape.Boolean => Of(As<bool>()),
        ClosureShape.Text => Of(As<string>()),
        _ => Of(As<object?>()),
    };

    private Func<ExpressionEvaluationScope, object?> Boxed() => evaluate switch
    {
        Func<ExpressionEvaluationScope, object?> answer => answer,
        Func<ExpressionEvaluationScope, long> answer => scope => answer(scope),
        Func<ExpressionEvaluationScope, double> answer => scope => answer(scope),
        Func<ExpressionEvaluationScope, bool> answer => scope => answer(scope),
        _ => throw new InvalidOperationException("A closure answers a type it has no shape for."),
    };
}
