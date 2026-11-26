using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class EfSuspensionParkIdentityTests : SuspensionParkIdentityContract, IDisposable
{
    private readonly SqliteWorkflowDatabase database = new();

    public void Dispose() => database.Dispose();

    protected override IWorkflowSuspensionStore Store() =>
        new EfWorkflowSuspensionStore(database.Services.GetRequiredService<IServiceScopeFactory>(), []);

    protected override IWorkflowSuspensionResumeStore Resume() =>
        new EfWorkflowSuspensionResumeStore(database.Services.GetRequiredService<IServiceScopeFactory>(), []);

    protected override IWorkflowParkedSleepStore ParkedSleeps() =>
        new EfWorkflowSuspensionStore(database.Services.GetRequiredService<IServiceScopeFactory>(), []);

    protected override async Task<(Guid WorkflowId, Guid ExecutionLogId)> RunningRunAsync()
    {
        var (workflowId, versionId) = database.SeedPublishedWorkflow();
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var log = new WorkflowExecutionLog(
            Guid.NewGuid(), null, workflowId, versionId, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        db.WorkflowExecutionLogs.Add(log);
        await db.SaveChangesAsync();
        return (workflowId, log.Id);
    }

    protected override async Task<IReadOnlyList<ParkRow>> RowsAsync(Guid executionLogId)
    {
        using var scope = database.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
            .AsNoTracking()
            .Where(row => row.ExecutionLogId == executionLogId)
            .ToListAsync();
        return rows.OrderBy(row => row.SuspendedAtUtc).Select(row => new ParkRow(row.Id, row.BlockId, row.Status)).ToList();
    }
}
