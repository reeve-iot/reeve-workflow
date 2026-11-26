using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class EfWorkflowRunRecoveryClaimTests
{
    private static readonly TimeSpan Cutoff = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task A_row_a_stopping_replica_handed_over_is_claimed_without_waiting_out_the_cutoff()
    {
        using var desk = new Desk();
        var executionId = await desk.StartAsync(owner: "replica-stopping");
        await desk.HandOverAsync(executionId, "replica-stopping");

        var claimed = await desk.ClaimAsync("replica-staying");

        claimed.Should().ContainSingle().Which.ExecutionId.Should().Be(executionId,
            "a row that names no replica is one nobody is running, and a deploy should not be as slow as a crash");
        claimed[0].OwnedByReplicaId.Should().Be("replica-staying");
        (await desk.State.GetAsync(executionId, CancellationToken.None))!.OwnedByReplicaId
            .Should().Be("replica-staying");
    }

    [Fact]
    public async Task A_row_its_replica_still_names_is_not_claimed_before_the_cutoff()
    {
        using var desk = new Desk();
        await desk.StartAsync(owner: "replica-live");

        (await desk.ClaimAsync("replica-staying")).Should().BeEmpty(
            "the row was written a moment ago by a replica that is running it");
    }

    [Fact]
    public async Task A_claim_in_no_name_reads_a_handed_over_row_by_its_age_alone()
    {
        using var desk = new Desk();
        var executionId = await desk.StartAsync(owner: "replica-stopping");
        await desk.HandOverAsync(executionId, "replica-stopping");

        (await desk.ClaimAsync(owner: null)).Should().BeEmpty(
            "on a host that names no replica every live row names nobody, so nobody is not a handover there");
    }

    [Fact]
    public async Task A_handed_over_row_one_sweep_took_is_not_there_for_the_next()
    {
        using var desk = new Desk();
        var executionId = await desk.StartAsync(owner: "replica-stopping");
        await desk.HandOverAsync(executionId, "replica-stopping");

        var first = await desk.ClaimAsync("replica-a");
        var second = await desk.ClaimAsync("replica-b");

        first.Should().ContainSingle().Which.ExecutionId.Should().Be(executionId);
        second.Should().BeEmpty("the first claim wrote its name, so the row is held again");
    }

    [Fact]
    public async Task A_handed_over_sleep_is_left_alone_until_it_is_due()
    {
        using var desk = new Desk();
        var executionId = await desk.StartAsync(owner: "replica-stopping");
        await desk.SleepAsync(executionId, "replica-stopping", DateTimeOffset.UtcNow.AddHours(1));
        await desk.HandOverAsync(executionId, "replica-stopping");

        (await desk.ClaimAsync("replica-staying")).Should().BeEmpty(
            "taken early, the sleep would be re-entered and slept out inside the sweep");
    }

    [Fact]
    public async Task A_handed_over_sleep_past_its_wake_up_time_is_claimed_at_once()
    {
        using var desk = new Desk();
        var executionId = await desk.StartAsync(owner: "replica-stopping");
        await desk.SleepAsync(executionId, "replica-stopping", DateTimeOffset.UtcNow.AddMinutes(-1));
        await desk.HandOverAsync(executionId, "replica-stopping");

        (await desk.ClaimAsync("replica-staying")).Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowRunStateStatus.Sleeping);
    }

    private sealed class Desk : IDisposable
    {
        private readonly SqliteWorkflowDatabase database =
            new(context: builder => builder.ReplaceService<IModelCustomizer, InstantsAsNumbers>());
        private readonly Guid workflowId;

        public Desk()
        {
            workflowId = database.SeedWorkflow();
            State = database.Services.GetRequiredService<IWorkflowRunStateStore>();
            Recovery = new EfWorkflowRunRecoveryStore(database.Services.GetRequiredService<IServiceScopeFactory>());
        }

        public IWorkflowRunStateStore State { get; }

        private IWorkflowRunRecoveryStore Recovery { get; }

        public Task<Guid> StartAsync(string owner) => State.BeginAsync(new WorkflowRunStateBegin
        {
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            VariablesJson = "{}",
            TriggerSource = "test",
            OwnedByReplicaId = owner
        }, CancellationToken.None);

        public async Task SleepAsync(Guid executionId, string owner, DateTimeOffset wakeAt) =>
            (await State.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = executionId,
                FromStatus = WorkflowRunStateStatus.Running,
                ToStatus = WorkflowRunStateStatus.Sleeping,
                CurrentBlockPath = "sleep",
                VariablesJson = "{}",
                WaitUntilUtc = wakeAt,
                OwnedByReplicaId = owner,
                ExpectedOwnedByReplicaId = owner
            }, CancellationToken.None)).Should().BeTrue();

        public async Task HandOverAsync(Guid executionId, string owner)
        {
            var row = await State.GetAsync(executionId, CancellationToken.None);
            (await State.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = executionId,
                FromStatus = row!.Status,
                ToStatus = row.Status,
                CurrentBlockPath = row.CurrentBlockPath,
                VariablesJson = row.VariablesJson,
                WaitUntilUtc = row.WaitUntilUtc,
                OwnedByReplicaId = null,
                ExpectedOwnedByReplicaId = owner
            }, CancellationToken.None)).Should().BeTrue();
        }

        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAsync(string? owner) =>
            Recovery.ClaimAbandonedRunsAsync(new WorkflowRunRecoveryClaim
            {
                AbandonedAfter = Cutoff,
                BatchSize = 20,
                ClaimedByReplicaId = owner
            }, CancellationToken.None);

        public void Dispose() => database.Dispose();
    }

    private sealed class InstantsAsNumbers(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                if (property.GetValueConverter() is null)
                    property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }
}
