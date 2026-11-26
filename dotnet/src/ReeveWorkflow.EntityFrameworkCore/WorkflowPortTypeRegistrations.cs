using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The port types a host registers at startup, in <c>Seams.PortTypes</c>: one record per atom
/// (<see cref="WorkflowTypeRecord"/>) and one per constructor (<see cref="WorkflowTypeConstructorRecord"/>). They
/// are applied once, in the order added, onto the library's built-ins as generation 0 of the
/// <c>IWorkflowTypeRegistry</c>; a plugin registers its own through that registry while the process runs.</summary>
/// <remarks><c>AddWorkflows</c> applies them when it runs, so a record the vocabulary refuses stops startup there, with
/// a <see cref="WorkflowTypeRegistrationException"/> naming the record, not at the first compile. It replaces
/// <c>Seams.PortTypeWidenings</c> and its <c>Add</c>, <c>AddAlias</c>, <c>Declare</c> and <c>AddConstructor</c>.</remarks>
public sealed class WorkflowPortTypeRegistrations
{
    private readonly List<WorkflowTypeRegistrationRecord> records = [];

    internal WorkflowPortTypeRegistrations()
    {
    }

    /// <summary>Registers <paramref name="record"/>. A record may name a type added after it, so the order does not
    /// matter to what is accepted.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public WorkflowPortTypeRegistrations Add(WorkflowTypeRegistrationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        records.Add(record);
        return this;
    }

    internal IReadOnlyList<WorkflowTypeRegistrationRecord> Records => [.. records];
}
