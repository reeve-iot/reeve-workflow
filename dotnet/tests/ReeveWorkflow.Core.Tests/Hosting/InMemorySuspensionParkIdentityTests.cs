using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Execution;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class InMemorySuspensionParkIdentityTests : SuspensionParkIdentityContract
{
    private readonly MemoryJournal journal = new();
    private readonly MemorySuspensions suspensions;

    public InMemorySuspensionParkIdentityTests() => suspensions = new MemorySuspensions(journal);

    protected override IWorkflowSuspensionStore Store() => suspensions;

    protected override IWorkflowSuspensionResumeStore Resume() => suspensions;

    protected override IWorkflowParkedSleepStore ParkedSleeps() => suspensions;

    protected override Task<(Guid WorkflowId, Guid ExecutionLogId)> RunningRunAsync()
    {
        var log = new WorkflowExecutionLog(
            Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        journal.Runs[log.Id] = log;
        return Task.FromResult((log.WorkflowId, log.Id));
    }

    protected override Task<IReadOnlyList<ParkRow>> RowsAsync(Guid executionLogId) =>
        Task.FromResult<IReadOnlyList<ParkRow>>(suspensions.Rows
            .Where(row => row.ExecutionLogId == executionLogId)
            .OrderBy(row => row.SuspendedAtUtc)
            .Select(row => new ParkRow(row.Id, row.BlockId, row.Status))
            .ToList());
}
