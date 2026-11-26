using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class EfWorkflowSuspensionStoreTests : IDisposable
{
    private readonly SqliteWorkflowDatabase database = new();

    public void Dispose() => database.Dispose();

    [Fact]
    public async Task Two_replicas_that_both_take_one_parked_run_move_it_exactly_once()
    {
        var suspension = await SuspendAsync();
        var resolvedAt = DateTimeOffset.UtcNow;

        var both = await Task.WhenAll(
            Resume().TryResolveAsync(
                suspension.Id, WorkflowSuspensionStatus.TimedOut, "{\"outcome\":\"timed_out\"}", resolvedAt, default),
            Resume().TryResolveAsync(
                suspension.Id, WorkflowSuspensionStatus.TimedOut, "{\"outcome\":\"timed_out\"}", resolvedAt, default));

        both.Count(context => context is not null).Should().Be(1);
    }

    [Fact]
    public async Task A_run_that_was_taken_is_not_offered_to_the_next_pass()
    {
        var suspension = await SuspendAsync();

        await Resume().TryResolveAsync(
            suspension.Id, WorkflowSuspensionStatus.TimedOut, "{}", DateTimeOffset.UtcNow, default);

        (await RowAsync(suspension.Id)).Status.Should().NotBe(WorkflowSuspensionStatus.Suspended,
            "the pass only looks at rows still parked");
    }

    [Fact]
    public async Task A_cancelled_run_leaves_nothing_behind_that_could_resume_it()
    {
        var suspension = await SuspendAsync();

        await Store().CancelByExecutionAsync(suspension.ExecutionLogId, default);

        (await RowAsync(suspension.Id)).Status.Should().Be(WorkflowSuspensionStatus.Cancelled,
            "a row no longer parked is a row no pass will offer to a resume");
    }

    [Fact]
    public async Task Cancelling_a_run_does_not_rewrite_what_already_happened_to_it()
    {
        var suspension = await SuspendAsync();
        await Resume().TryResolveAsync(
            suspension.Id, WorkflowSuspensionStatus.Resumed, "{\"outcome\":\"satisfied\"}", DateTimeOffset.UtcNow, default);

        await Store().CancelByExecutionAsync(suspension.ExecutionLogId, default);

        (await RowAsync(suspension.Id)).Status.Should().Be(WorkflowSuspensionStatus.Resumed);
    }

    [Fact]
    public async Task A_wait_that_was_taken_and_not_used_goes_back_where_it_was()
    {
        var suspension = await SuspendAsync();
        await Resume().TryResolveAsync(
            suspension.Id, WorkflowSuspensionStatus.TimedOut, "{}", DateTimeOffset.UtcNow, default);

        var released = await Resume().ReleaseAsync(suspension.Id, WorkflowSuspensionStatus.TimedOut, default);

        released.Should().BeTrue();
        var row = await RowAsync(suspension.Id);
        row.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        row.ResolvedAtUtc.Should().BeNull("a row put back has not been resolved");
        row.OutcomeJson.Should().BeNull();
    }

    [Fact]
    public async Task A_wait_is_not_handed_back_to_a_run_that_has_already_ended()
    {
        var suspension = await SuspendAsync();
        await Resume().TryResolveAsync(
            suspension.Id, WorkflowSuspensionStatus.TimedOut, "{}", DateTimeOffset.UtcNow, default);
        await EndTheRunAsync(suspension.ExecutionLogId, WorkflowExecutionStatus.Cancelled);

        var released = await Resume().ReleaseAsync(suspension.Id, WorkflowSuspensionStatus.TimedOut, default);

        released.Should().BeFalse("there is no run left to hand it back to");
        (await RowAsync(suspension.Id)).Status.Should().Be(WorkflowSuspensionStatus.TimedOut);
    }

    [Fact]
    public async Task A_wait_taken_with_one_status_is_not_handed_back_by_a_caller_holding_another()
    {
        var suspension = await SuspendAsync();
        await Resume().TryResolveAsync(
            suspension.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default);

        var released = await Resume().ReleaseAsync(suspension.Id, WorkflowSuspensionStatus.TimedOut, default);

        released.Should().BeFalse("the status this caller wrote is what it may give back");
        (await RowAsync(suspension.Id)).Status.Should().Be(WorkflowSuspensionStatus.Resumed);
    }

    [Fact]
    public async Task A_run_too_big_to_park_fails_at_the_wait_rather_than_in_the_middle()
    {
        var store = new EfWorkflowSuspensionStore(
            database.Services.GetRequiredService<IServiceScopeFactory>(), [],
            new WorkflowSuspensionOptions { MaxStatePayloadBytes = 16 });

        var park = () => SuspendAsync(store, payload: new string('x', 64));

        await park.Should().ThrowAsync<SuspendPayloadTooLargeException>();
    }

    [Fact]
    public async Task A_workflow_with_a_sleep_still_parked_says_so_and_one_parked_on_a_template_does_not()
    {
        var sleeping = database.SeedPublishedWorkflow();
        var templating = database.SeedPublishedWorkflow();
        await SuspendAsync(WorkflowSuspensionKind.Sleep, into: sleeping);
        await SuspendAsync(WorkflowSuspensionKind.WaitTemplate, into: templating);

        (await ParkedSleeps().HasParkedSleepAsync(sleeping.WorkflowId, default)).Should().BeTrue();
        (await ParkedSleeps().HasParkedSleepAsync(templating.WorkflowId, default)).Should().BeFalse(
            "Single's promise is about a run asleep; a run waiting on a condition held no lease before either");
    }

    [Fact]
    public async Task A_sleep_a_waker_just_took_still_counts_and_a_restart_can_cancel_it()
    {
        var workflow = database.SeedPublishedWorkflow();
        var sleep = await SuspendAsync(WorkflowSuspensionKind.Sleep, into: workflow);
        await Resume().TryResolveAsync(sleep.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default);

        (await ParkedSleeps().HasParkedSleepAsync(workflow.WorkflowId, default)).Should().BeTrue();
        (await ParkedSleeps().ListParkedSleepsAsync(workflow.WorkflowId, default)).Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        (await ParkedSleeps().CancelParkedSleepAsync(sleep.Id, default)).Should().BeTrue();
        (await RowAsync(sleep.Id)).Status.Should().Be(WorkflowSuspensionStatus.Cancelled);
    }

    [Fact]
    public async Task A_sleep_taken_however_long_ago_is_listed_while_its_run_is_running()
    {
        var workflow = database.SeedPublishedWorkflow();
        var sleep = await SuspendAsync(WorkflowSuspensionKind.Sleep, into: workflow);
        await Resume().TryResolveAsync(sleep.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow.AddDays(-3), default);

        (await ParkedSleeps().HasParkedSleepAsync(workflow.WorkflowId, default)).Should().BeTrue();
        (await ParkedSleeps().ListParkedSleepsAsync(workflow.WorkflowId, default)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sleep_whose_run_has_ended_does_not_count(bool taken)
    {
        var workflow = database.SeedPublishedWorkflow();
        var sleep = await SuspendAsync(WorkflowSuspensionKind.Sleep, into: workflow);
        if (taken)
            await Resume().TryResolveAsync(sleep.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default);
        await EndTheRunAsync(sleep.ExecutionLogId, WorkflowExecutionStatus.Cancelled);

        (await ParkedSleeps().HasParkedSleepAsync(workflow.WorkflowId, default)).Should().BeFalse();
        (await ParkedSleeps().ListParkedSleepsAsync(workflow.WorkflowId, default)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowSuspensionKind.WaitTemplate)]
    [InlineData(WorkflowSuspensionKind.Sleep)]
    public async Task A_taken_sleep_whose_run_parked_again_counts_only_through_its_newer_row(WorkflowSuspensionKind next)
    {
        var workflow = database.SeedPublishedWorkflow();
        var sleep = await SuspendAsync(Store(), WorkflowSuspensionKind.Sleep, into: workflow,
            suspendedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-2));
        await Resume().TryResolveAsync(sleep.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default);
        var again = await SuspendAsync(Store(), next, into: workflow, run: sleep.ExecutionLogId, blockId: "wait-2");

        var listed = await ParkedSleeps().ListParkedSleepsAsync(workflow.WorkflowId, default);

        if (next == WorkflowSuspensionKind.Sleep)
            listed.Should().ContainSingle().Which.SuspensionId.Should().Be(again.Id);
        else
            listed.Should().BeEmpty("a run waiting on a template held no lease before either");
    }

    [Fact]
    public async Task Parked_sleeps_are_listed_and_cancelled_for_that_workflow_and_nothing_else()
    {
        var workflow = database.SeedPublishedWorkflow();
        var sleep = await SuspendAsync(WorkflowSuspensionKind.Sleep, into: workflow);
        var template = await SuspendAsync(WorkflowSuspensionKind.WaitTemplate, into: workflow);
        var elsewhere = await SuspendAsync(WorkflowSuspensionKind.Sleep);

        var listed = await ParkedSleeps().ListParkedSleepsAsync(workflow.WorkflowId, default);
        (await ParkedSleeps().CancelParkedSleepAsync(template.Id, default)).Should().BeFalse("a template is not a sleep");
        (await ParkedSleeps().CancelParkedSleepAsync(sleep.Id, default)).Should().BeTrue();

        listed.Should().ContainSingle().Which.Should().Match<WorkflowParkedSleep>(row =>
            row.SuspensionId == sleep.Id && row.ExecutionLogId == sleep.ExecutionLogId && row.StatePayloadJson == "{}"
            && row.BlockId == sleep.BlockId && row.Status == WorkflowSuspensionStatus.Suspended);
        (await ParkedSleeps().CancelParkedSleepAsync(sleep.Id, default)).Should().BeFalse("it has already moved on");
        (await RowAsync(sleep.Id)).Status.Should().Be(WorkflowSuspensionStatus.Cancelled);
        (await RowAsync(template.Id)).Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        (await RowAsync(elsewhere.Id)).Status.Should().Be(WorkflowSuspensionStatus.Suspended);
    }

    [Fact]
    public async Task The_parked_row_a_wake_names_is_read_by_either_id()
    {
        var row = await SuspendAsync();

        var bySuspension = await Resume().GetParkedAsync(row.Id, null, default);
        var byExecution = await Resume().GetParkedAsync(null, row.ExecutionLogId, default);

        bySuspension!.Row!.SuspensionId.Should().Be(row.Id);
        bySuspension.NoLongerWaiting.Should().BeFalse();
        bySuspension.AsOfUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        byExecution!.Row!.SuspensionId.Should().Be(row.Id);
        byExecution.Row.Kind.Should().Be(row.Kind);
        byExecution.Row.StatePayloadJson.Should().Be(row.StatePayloadJson);
    }

    [Fact]
    public async Task A_read_whose_two_ids_do_not_belong_together_answers_with_no_row()
    {
        var row = await SuspendAsync();
        var other = await SuspendAsync();

        var read = await Resume().GetParkedAsync(row.Id, other.ExecutionLogId, default);

        read.Should().NotBeNull("the store answers the read; it is the row that is not there");
        read!.Row.Should().BeNull();
        read.NoLongerWaiting.Should().BeFalse("a pair that names no row names no wait that was answered either");
    }

    [Fact]
    public async Task A_read_whose_two_ids_do_not_belong_together_does_not_say_the_wait_was_answered()
    {
        var row = await SuspendAsync();
        var other = await SuspendAsync();
        await Resume().TryResolveAsync(
            row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default);

        var read = await Resume().GetParkedAsync(row.Id, other.ExecutionLogId, default);

        read!.Row.Should().BeNull();
        read.NoLongerWaiting.Should().BeFalse();
    }

    [Fact]
    public async Task A_row_that_has_been_resolved_is_not_read_back_as_parked()
    {
        var row = await SuspendAsync();
        await Resume().TryResolveAsync(
            row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default);

        var read = await Resume().GetParkedAsync(row.Id, null, default);

        read!.Row.Should().BeNull();
        read.NoLongerWaiting.Should().BeTrue("a wake that finds it so has nothing left to hold for");
    }

    [Fact]
    public async Task A_row_whose_run_has_ended_is_not_read_back_as_parked()
    {
        var row = await SuspendAsync();
        await EndTheRunAsync(row.ExecutionLogId, WorkflowExecutionStatus.Failed);

        var read = await Resume().GetParkedAsync(row.Id, null, default);

        read!.Row.Should().BeNull();
        read.NoLongerWaiting.Should().BeTrue();
    }

    [Fact]
    public async Task A_read_of_a_wait_that_is_not_there_does_not_say_it_was_answered()
    {
        var read = await Resume().GetParkedAsync(Guid.NewGuid(), null, default);

        read!.Row.Should().BeNull();
        read.NoLongerWaiting.Should().BeFalse();
    }

    [Fact]
    public async Task A_read_by_execution_alone_says_answered_only_for_a_run_that_has_ended()
    {
        var resumed = await SuspendAsync();
        await Resume().TryResolveAsync(
            resumed.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default);
        var ended = await SuspendAsync();
        await EndTheRunAsync(ended.ExecutionLogId, WorkflowExecutionStatus.Cancelled);

        var running = await Resume().GetParkedAsync(null, resumed.ExecutionLogId, default);
        var over = await Resume().GetParkedAsync(null, ended.ExecutionLogId, default);

        running!.Row.Should().BeNull();
        running.NoLongerWaiting.Should().BeFalse("the run may be parking on its next wait");
        over!.Row.Should().BeNull();
        over.NoLongerWaiting.Should().BeTrue();
    }

    [Fact]
    public async Task A_read_by_execution_answers_with_the_wait_the_run_is_parked_on_now()
    {
        var first = await SuspendAsync();
        await Resume().TryResolveAsync(
            first.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default);
        var second = await SuspendAsync(
            Store(), run: first.ExecutionLogId, blockId: "wait-2",
            suspendedAtUtc: DateTimeOffset.UtcNow.AddSeconds(1));

        var read = await Resume().GetParkedAsync(null, first.ExecutionLogId, default);

        read!.Row!.SuspensionId.Should().Be(second.Id);
        read.Row.BlockId.Should().Be("wait-2");
    }

    [Fact]
    public async Task A_read_by_execution_alone_answers_with_no_row_where_a_run_is_parked_on_two_waits()
    {
        var first = await SuspendAsync();
        var second = await SuspendAsync(
            Store(), run: first.ExecutionLogId, blockId: "wait-2",
            suspendedAtUtc: DateTimeOffset.UtcNow.AddSeconds(1));

        var read = await Resume().GetParkedAsync(null, first.ExecutionLogId, default);

        read!.Row.Should().BeNull();
        read.NoLongerWaiting.Should().BeFalse("both waits are still parked");

        (await Resume().GetParkedAsync(second.Id, first.ExecutionLogId, default))!.Row!.BlockId
            .Should().Be("wait-2");
    }

    [Fact]
    public async Task A_read_that_names_neither_id_is_refused()
    {
        var reading = () => Resume().GetParkedAsync(null, null, default);

        await reading.Should().ThrowAsync<ArgumentException>();
    }

    private IWorkflowParkedSleepStore ParkedSleeps() =>
        (IWorkflowParkedSleepStore)Store();

    private async Task EndTheRunAsync(Guid executionLogId, WorkflowExecutionStatus status)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var log = await db.WorkflowExecutionLogs.SingleAsync(row => row.Id == executionLogId);
        log.Complete(status, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private IWorkflowSuspensionStore Store() =>
        new EfWorkflowSuspensionStore(database.Services.GetRequiredService<IServiceScopeFactory>(), []);

    private IWorkflowSuspensionResumeStore Resume() =>
        new EfWorkflowSuspensionResumeStore(database.Services.GetRequiredService<IServiceScopeFactory>(), []);

    private async Task<WorkflowSuspension> RowAsync(Guid id)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return await db.WorkflowSuspensions.AsNoTracking().SingleAsync(row => row.Id == id);
    }

    private Task<WorkflowSuspension> SuspendAsync(
        WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitForTrigger,
        (Guid WorkflowId, Guid VersionId)? into = null) => SuspendAsync(Store(), kind: kind, into: into);

    private async Task<WorkflowSuspension> SuspendAsync(
        IWorkflowSuspensionStore store,
        WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitForTrigger,
        string payload = "{}",
        (Guid WorkflowId, Guid VersionId)? into = null,
        DateTimeOffset? suspendedAtUtc = null,
        Guid? run = null,
        string blockId = "wait-1")
    {
        var (workflowId, versionId) = into ?? database.SeedPublishedWorkflow();
        var now = suspendedAtUtc ?? DateTimeOffset.UtcNow;

        if (run is null)
        {
            using var scope = database.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var log = new WorkflowExecutionLog(
                Guid.NewGuid(), null, workflowId, versionId, null, WorkflowExecutionStatus.Running, now);
            db.WorkflowExecutionLogs.Add(log);
            await db.SaveChangesAsync();
            run = log.Id;
        }

        return await store.SuspendAsync(new SuspendRequest
        {
            ExecutionLogId = run.Value,
            WorkflowId = workflowId,
            BlockId = blockId,
            Kind = kind,
            ConfigJson = "{}",
            StatePayloadJson = payload,
            SuspendedAtUtc = now,
            TimeoutAtUtc = now.AddMinutes(5)
        }, default);
    }
}
