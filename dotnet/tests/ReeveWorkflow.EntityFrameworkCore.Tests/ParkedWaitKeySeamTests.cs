using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class ParkedWaitKeySeamTests : IDisposable
{
    private const string Condition = """{"Expression":"${depot.north.gate7.state.open} == true"}""";

    private readonly SqliteWorkflowDatabase database = new(
        workflows: options => options.Defaults.ExpressionNamespaces.Add(Depot()));

    public void Dispose() => database.Dispose();

    [Fact]
    public async Task A_wait_is_written_with_the_key_its_namespace_registration_declares()
    {
        var id = await ParkAsync(database.Services.GetRequiredService<IWorkflowSuspensionStore>());

        (await KeysAsync(id)).Should().Equal("depot:north:gate7");
    }

    [Fact]
    public async Task A_wait_put_back_is_given_the_key_it_was_written_with()
    {
        var resume = database.Services.GetRequiredService<IWorkflowSuspensionResumeStore>();
        var id = await ParkAsync(database.Services.GetRequiredService<IWorkflowSuspensionStore>());
        var written = await KeysAsync(id);
        await resume.TryResolveAsync(id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default);
        (await KeysAsync(id)).Should().BeEmpty("a taken wait holds no keys");

        (await resume.ReleaseAsync(id, WorkflowSuspensionStatus.Resumed, default)).Should().BeTrue();

        (await KeysAsync(id)).Should().Equal(written);
    }

    [Fact]
    public async Task A_predicate_wait_is_written_with_its_config_keys_behind_the_predicate_prefix()
    {
        var config = System.Text.Json.JsonSerializer.Serialize(new WorkflowPredicateWaitConfig("Predicate", ["ship-1/temp", "ship-1/door"], null));

        var id = await ParkAsync(database.Services.GetRequiredService<IWorkflowSuspensionStore>(), WorkflowSuspensionKind.Predicate, config);

        (await KeysAsync(id)).Should().Equal("predicate-key:ship-1/door", "predicate-key:ship-1/temp");
    }

    [Fact]
    public async Task A_store_given_no_registrations_would_write_the_longer_key()
    {
        var id = await ParkAsync(new EfWorkflowSuspensionStore(
            database.Services.GetRequiredService<IServiceScopeFactory>(), []));

        (await KeysAsync(id)).Should().Equal("depot:north:gate7:state:open");
    }

    private async Task<Guid> ParkAsync(
        IWorkflowSuspensionStore store, WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitTemplate, string configJson = Condition)
    {
        var (workflowId, versionId) = database.SeedPublishedWorkflow();
        var now = DateTimeOffset.UtcNow;
        Guid logId;
        using (var scope = database.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var log = new WorkflowExecutionLog(
                Guid.NewGuid(), null, workflowId, versionId, null, WorkflowExecutionStatus.Running, now);
            db.WorkflowExecutionLogs.Add(log);
            await db.SaveChangesAsync();
            logId = log.Id;
        }

        var suspension = await store.SuspendAsync(new SuspendRequest
        {
            ExecutionLogId = logId,
            WorkflowId = workflowId,
            BlockId = "wait-1",
            Kind = kind,
            ConfigJson = configJson,
            StatePayloadJson = "{}",
            SuspendedAtUtc = now,
            TimeoutAtUtc = now.AddMinutes(5)
        }, default);
        return suspension.Id;
    }

    private async Task<string[]> KeysAsync(Guid suspensionId)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return await db.WorkflowSuspensionDependencyKeys.AsNoTracking()
            .Where(key => key.SuspensionId == suspensionId).Select(key => key.DependencyKey).OrderBy(key => key).ToArrayAsync();
    }

    private static WorkflowExpressionNamespace Depot() =>
        new("depot", 2, new DepotCheck(), (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new DepotResolver()));

    private sealed class DepotCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "depot";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "String");
    }

    private sealed class DepotResolver : IExpressionNamespaceResolver
    {
        public string Namespace => "depot";

        public ResolveResult Resolve(string[] segments) => ResolveResult.Failed();
    }
}
