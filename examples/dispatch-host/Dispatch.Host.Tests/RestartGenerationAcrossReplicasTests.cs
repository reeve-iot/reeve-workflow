using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;
using ReeveWorkflow.Npgsql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class RestartGenerationAcrossReplicasTests(ClusterBackends backends)
{
    private const int StatusCodeClientClosedRequest = 499;

    [Fact]
    public async Task A_run_whose_replica_never_hears_the_newer_trigger_is_stopped_by_its_own_look_at_the_generations()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_restart_unheard");
        await using var holding = await DispatchReplica.StartAsync(
            Settings("desk-holding", postgres) with { RunHeartbeatInterval = TimeSpan.FromSeconds(1) });
        await using var unheard = await DispatchReplica.StartAsync(
            Settings("desk-unheard", postgres) with { RestartWordUnheard = true });
        await FreightDesk.WarmAsync(postgres, holding, unheard);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Restart, maxConcurrentRuns: 1,
            shipment: "SHP-6160", carrier: "Nordfrakt", dwellSeconds: 20);

        var interrupted = holding.ExecuteAsync(workflowId);
        await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            states => states.Any(state => state.OwnedByReplicaId == holding.Id
                                          && state.Status == nameof(WorkflowRunStateStatus.Running)),
            TimeSpan.FromSeconds(30), "the first replica to be holding the bay", holding, unheard);

        var sinceTakeover = System.Diagnostics.Stopwatch.StartNew();
        var takeover = unheard.ExecuteAsync(workflowId);
        var superseded = await interrupted.WaitAsync(TimeSpan.FromSeconds(60));

        ((int)superseded.Status).Should().Be(StatusCodeClientClosedRequest,
            "the holding run found the newer generation waiting on its own, long before its dwell was over");
        sinceTakeover.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8),
            "the run looks as often as its host's heartbeat, not on the ten seconds a host that set none gets");
        var took = await takeover.WaitAsync(TimeSpan.FromSeconds(90));
        took.Outputs.Should().ContainKey("waybill");
        (await FreightDesk.RestartGenerationsAsync(postgres, workflowId)).Should().Be((2L, 2L));
    }

    [Fact]
    public async Task An_older_trigger_runs_when_the_newer_one_died_waiting_for_the_lock()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_restart_newest_died");
        await using var older = await DispatchReplica.StartAsync(Settings("desk-older", postgres));
        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await FreightDesk.WarmAsync(postgres, older, dying);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Restart, maxConcurrentRuns: 1,
            shipment: "SHP-7170", carrier: "Baltlink", dwellSeconds: 0);

        await using var lockHolder = new NpgsqlConnection(postgres);
        await lockHolder.OpenAsync();
        var lockId = NpgsqlWorkflowLockOptions.DeriveLockId($"WorkflowRunBudget_{workflowId:N}");
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", lockHolder))
        {
            take.Parameters.AddWithValue("key", lockId);
            await take.ExecuteScalarAsync();
        }

        var olderTrigger = older.ExecuteAsync(workflowId);
        await FreightDesk.EventuallyAsync(
            () => FreightDesk.RestartGenerationsAsync(postgres, workflowId), generations => generations.Issued == 1,
            TimeSpan.FromSeconds(30), "the older trigger's generation", older, dying);

        var newerTrigger = dying.ExecuteAsync(workflowId);
        await FreightDesk.EventuallyAsync(
            () => FreightDesk.RestartGenerationsAsync(postgres, workflowId), generations => generations.Issued == 2,
            TimeSpan.FromSeconds(30), "the newer trigger's generation", older, dying);
        dying.Kill();
        await newerTrigger.Awaiting(trigger => trigger).Should().ThrowAsync<HttpRequestException>();

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lockHolder))
        {
            release.Parameters.AddWithValue("key", lockId);
            ((bool)(await release.ExecuteScalarAsync())!).Should().BeTrue();
        }

        var ran = await olderTrigger.WaitAsync(TimeSpan.FromSeconds(90));
        ran.Outputs.Should().ContainKey("waybill").WhoseValue!.ToString().Should().Be("WB-BALTLINK-SHP7170");
        (await FreightDesk.RestartGenerationsAsync(postgres, workflowId)).Should().Be((2L, 1L),
            "the older trigger ran, and the dead one never did");
    }

    [Fact]
    public async Task Replicas_issuing_at_once_on_postgres_never_issue_one_generation_twice()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_restart_generations_race");
        var replicas = new List<ServiceProvider>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var services = new ServiceCollection();
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
                services.AddWorkflows(workflows =>
                {
                    workflows.ConfigureDbContext(context => context.UseNpgsql(postgres));
                }).AddPostgreSqlSchema();
                replicas.Add(services.BuildServiceProvider());
            }

            using (var scope = replicas[0].CreateScope())
                await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();

            var workflowId = Guid.NewGuid();
            using var start = new ManualResetEventSlim();
            var issues = replicas
                .Select(replica => replica.GetRequiredService<IWorkflowRestartGenerations>())
                .SelectMany(store => Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
                {
                    start.Wait();
                    return await store.IssueAsync(workflowId, TimeSpan.FromSeconds(15), CancellationToken.None);
                })))
                .ToList();
            start.Set();
            var issued = await Task.WhenAll(issues).WaitAsync(TimeSpan.FromSeconds(60));

            issued.Should().OnlyHaveUniqueItems().And.HaveCount(40);
            issued.Order().Should().Equal(Enumerable.Range(1, 40).Select(n => (long)n), "no generation is skipped either");
            var state = await replicas[0].GetRequiredService<IWorkflowRestartGenerations>()
                .ReadAsync(workflowId, CancellationToken.None);
            state.Should().Be(new WorkflowRestartGenerationState(40, 0, 40),
                "every waiter was written on the database's clock and is live on it");
        }
        finally
        {
            foreach (var replica in replicas)
                await replica.DisposeAsync();
        }
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString
    };
}
