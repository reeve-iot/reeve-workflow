using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Hosting;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name)]
[Trait("Category", "Docker")]
public sealed class SuspensionParkIdentityOnPostgresTests(ClusterBackends backends)
    : SuspensionParkIdentityContract, IAsyncLifetime
{
    private static Task<string>? migrated;
    private ServiceProvider? container;

    private IServiceScopeFactory Scopes => container!.GetRequiredService<IServiceScopeFactory>();

    public async Task InitializeAsync() => container = FreightDesk.Container(await (migrated ??= MigrateAsync(backends)));

    public async Task DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }

    protected override IWorkflowSuspensionStore Store() => new EfWorkflowSuspensionStore(Scopes, []);

    protected override IWorkflowSuspensionResumeStore Resume() => new EfWorkflowSuspensionResumeStore(Scopes, []);

    protected override IWorkflowParkedSleepStore ParkedSleeps() => new EfWorkflowSuspensionStore(Scopes, []);

    protected override async Task<(Guid WorkflowId, Guid ExecutionLogId)> RunningRunAsync()
    {
        using var scope = Scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(Guid.NewGuid(), "parks", null, FreightDesk.Operator, now);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var version = new WorkflowVersion(
            Guid.NewGuid(), workflow.Id, 1, "{\"nodes\":[],\"edges\":[]}", null, null, FreightDesk.Operator, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();
        var log = new WorkflowExecutionLog(
            Guid.NewGuid(), null, workflow.Id, version.Id, null, WorkflowExecutionStatus.Running, now);
        db.WorkflowExecutionLogs.Add(log);
        await db.SaveChangesAsync();
        return (workflow.Id, log.Id);
    }

    protected override async Task<IReadOnlyList<ParkRow>> RowsAsync(Guid executionLogId)
    {
        using var scope = Scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
            .AsNoTracking()
            .Where(row => row.ExecutionLogId == executionLogId)
            .OrderBy(row => row.SuspendedAtUtc)
            .Select(row => new ParkRow(row.Id, row.BlockId, row.Status))
            .ToListAsync();
    }

    private static async Task<string> MigrateAsync(ClusterBackends backends)
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_park_identity", dropAfterTest: false);
        await using var db = new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(postgres).UseWorkflowPostgreSqlSchema()
            .Options);
        await db.Database.MigrateAsync();
        return postgres;
    }
}
