using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class EfParkedRowReadRaceTests : IDisposable
{
    private readonly SecondSuspensionReadInterceptor interceptor = new();
    private readonly SqliteWorkflowDatabase database;

    public EfParkedRowReadRaceTests() =>
        database = new SqliteWorkflowDatabase(onFile: true, context: options => options.AddInterceptors(interceptor));

    [Fact]
    public async Task A_wait_put_back_between_the_two_statements_is_not_said_to_be_answered()
    {
        var row = await SuspendAsync();
        await Resume().TryResolveAsync(
            row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default);
        var putBack = false;
        interceptor.BeforeSecondRead = async () =>
            putBack = await Resume().ReleaseAsync(row.Id, WorkflowSuspensionStatus.Resumed, default);

        var read = await Resume().GetParkedAsync(row.Id, null, default);

        putBack.Should().BeTrue("the race this test is about has to have happened");
        read!.Row.Should().BeNull("the first statement read the row while it was taken");
        read.NoLongerWaiting.Should().BeFalse("the row is parked again, and the wake that names it is owed");
    }

    public void Dispose() => database.Dispose();

    private IWorkflowSuspensionResumeStore Resume() =>
        new EfWorkflowSuspensionResumeStore(database.Services.GetRequiredService<IServiceScopeFactory>(), []);

    private async Task<WorkflowSuspension> SuspendAsync()
    {
        var (workflowId, versionId) = database.SeedPublishedWorkflow();
        var now = DateTimeOffset.UtcNow;
        using (var scope = database.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var log = new WorkflowExecutionLog(
                Guid.NewGuid(), null, workflowId, versionId, null, WorkflowExecutionStatus.Running, now);
            db.WorkflowExecutionLogs.Add(log);
            await db.SaveChangesAsync();

            return await new EfWorkflowSuspensionStore(database.Services.GetRequiredService<IServiceScopeFactory>(), [])
                .SuspendAsync(new SuspendRequest
                {
                    ExecutionLogId = log.Id,
                    WorkflowId = workflowId,
                    BlockId = "wait-1",
                    Kind = WorkflowSuspensionKind.WaitForTrigger,
                    ConfigJson = "{}",
                    StatePayloadJson = "{}",
                    SuspendedAtUtc = now,
                    TimeoutAtUtc = now.AddMinutes(5)
                }, default);
        }
    }

    private sealed class SecondSuspensionReadInterceptor : DbCommandInterceptor
    {
        private Func<Task>? arrangement;
        private int reads;

        public Func<Task> BeforeSecondRead
        {
            set { reads = 0; arrangement = value; }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref arrangement) is not null
                && command.CommandText.Contains("workflow_suspensions", StringComparison.Ordinal)
                && Interlocked.Increment(ref reads) == 2
                && Interlocked.Exchange(ref arrangement, null) is { } arrange)
            {
                await arrange();
            }

            return result;
        }
    }
}
