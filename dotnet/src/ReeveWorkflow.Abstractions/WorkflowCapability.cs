namespace ReeveWorkflow.Abstractions;

/// <summary>A capability a port term can implement. The vocabulary is the library's and is closed: a host
/// claims <see cref="Comparable"/>, <see cref="Equatable"/> and <see cref="JsonSerializable"/> for the types it
/// registers; <see cref="Iterable"/> and <see cref="Numeric"/> are the library's alone.</summary>
public enum WorkflowCapability
{
    /// <summary>Has items: <c>Iterable&lt;Item&gt;</c>. Only <c>Collection&lt;T&gt;</c> implements it, built in.</summary>
    Iterable,

    /// <summary>Arithmetic: only <c>Int</c> and <c>Number</c>; the evaluator has no host arithmetic.</summary>
    Numeric,

    /// <summary>Values order: the CLR type implements <see cref="IComparable{T}"/>.</summary>
    Comparable,

    /// <summary>Values compare for equality: the CLR type implements <see cref="IEquatable{T}"/> or is a record.</summary>
    Equatable,

    /// <summary>Values are written as JSON and read back: the record has a <c>JsonTypeInfo</c> whose <c>Type</c> is its CLR
    /// type. A term with this capability feeds a <c>Json</c> output (rule A4 of docs/reference/port-types.md, Widening and assignability).</summary>
    JsonSerializable,
}

/// <summary>The capability vocabulary as data: the names <c>GET port-types</c> publishes, the one capability that
/// carries an associated type, and which a host may claim.</summary>
public static class PortCapabilities
{
    /// <summary>Every capability, in the order the vocabulary is published.</summary>
    public static IReadOnlyList<WorkflowCapability> All { get; } = Array.AsReadOnly(Enum.GetValues<WorkflowCapability>());

    /// <summary>The capability's name as it is written in a declaration and published: its identifier.</summary>
    public static string NameOf(WorkflowCapability capability) => capability.ToString();

    /// <summary>The capability a published or declared name stands for, compared exactly; false for any other text.</summary>
    public static bool TryParse(string? name, out WorkflowCapability capability)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
            {
                capability = candidate;
                return true;
            }
        }

        capability = default;
        return false;
    }

    /// <summary>The associated types a capability names: <c>Item</c> for <see cref="WorkflowCapability.Iterable"/>,
    /// none for the rest.</summary>
    public static IReadOnlyList<string> AssociatedTypes(WorkflowCapability capability) =>
        capability == WorkflowCapability.Iterable ? ["Item"] : [];

    /// <summary>Whether a host may claim <paramref name="capability"/> for a type it registers.</summary>
    public static bool HostMayClaim(WorkflowCapability capability) =>
        capability is WorkflowCapability.Comparable or WorkflowCapability.Equatable or WorkflowCapability.JsonSerializable;
}
