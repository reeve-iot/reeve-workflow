namespace ReeveWorkflow.Abstractions;

/// <summary>A persisted value that does not read as its closed term, thrown by <see cref="ClosedPortType.FromJson"/>. A resume that
/// meets one abandons the run with <c>resume_value_invalid</c>; a block building its resumed outputs lets it propagate.</summary>
public sealed class PersistedValueReadException : Exception
{
    /// <summary>Raised for a value of <paramref name="term"/> that does not read; <paramref name="reason"/> says what was found.</summary>
    public PersistedValueReadException(PortTerm term, string reason)
        : base($"A persisted value does not read as '{term}': {reason}.")
    {
        Term = term;
        Reason = reason;
    }

    /// <summary>The closed term the value was read as.</summary>
    public PortTerm Term { get; }

    /// <summary>What was found instead of a value of <see cref="Term"/>; never the value itself.</summary>
    public string Reason { get; }
}
