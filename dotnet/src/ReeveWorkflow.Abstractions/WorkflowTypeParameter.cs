namespace ReeveWorkflow.Abstractions;

/// <summary>A type parameter a block declares: <c>$Name</c> in the port types of its declaration stands for one
/// term, which the compiler binds per node. The constraints are capability names a bound term must implement.</summary>
/// <param name="Name">An identifier. Compared exactly, so <c>$T</c> and <c>$t</c> are two parameters.</param>
/// <param name="Constraints">Names from <see cref="TypeParameterConstraints.Known"/>, compared exactly; empty when
/// the parameter admits any term.</param>
public sealed record WorkflowTypeParameter(string Name, IReadOnlyList<string> Constraints)
{
    /// <summary>A parameter that admits any term.</summary>
    public WorkflowTypeParameter(string name) : this(name, [])
    {
    }
}

/// <summary>The capability names a type parameter may be constrained by: <see cref="PortCapabilities.All"/> but
/// <c>Iterable</c>. Whether a given term implements one is the compiler's answer, not the declaration's. <c>Iterable</c>
/// is not here: a bare one says no more than <c>Collection&lt;$U&gt;</c>, and the form with an <c>Item</c> is not supported.</summary>
public static class TypeParameterConstraints
{
    public const string Numeric = "Numeric";
    public const string Comparable = "Comparable";
    public const string Equatable = "Equatable";
    public const string JsonSerializable = "JsonSerializable";

    /// <summary>Every name a declaration may constrain a parameter by.</summary>
    public static IReadOnlyList<string> Known { get; } =
        [.. PortCapabilities.All.Where(c => c != WorkflowCapability.Iterable).Select(PortCapabilities.NameOf)];
}
