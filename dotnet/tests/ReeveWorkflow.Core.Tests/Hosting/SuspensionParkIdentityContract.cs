using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed record ParkRow(Guid Id, string BlockId, WorkflowSuspensionStatus Status);

public abstract class SuspensionParkIdentityContract
{
    private const string Node = "wait";
    private static readonly DateTimeOffset FirstPark = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    protected abstract IWorkflowSuspensionStore Store();

    protected abstract IWorkflowSuspensionResumeStore Resume();

    protected abstract IWorkflowParkedSleepStore ParkedSleeps();

    protected abstract Task<(Guid WorkflowId, Guid ExecutionLogId)> RunningRunAsync();

    protected abstract Task<IReadOnlyList<ParkRow>> RowsAsync(Guid executionLogId);

    public static TheoryData<WorkflowSuspensionKind, int> KindsAndPasses()
    {
        var data = new TheoryData<WorkflowSuspensionKind, int>();
        foreach (var kind in Enum.GetValues<WorkflowSuspensionKind>())
            foreach (var passes in new[] { 1, 2, 3 })
                data.Add(kind, passes);
        return data;
    }

    [Theory]
    [MemberData(nameof(KindsAndPasses))]
    public async Task A_run_parked_at_one_node_once_per_pass_has_a_row_per_pass_each_taken_by_its_own_id(
        WorkflowSuspensionKind kind, int passes)
    {
        var run = await RunningRunAsync();
        var parks = new List<WorkflowSuspension>();

        for (var pass = 0; pass < passes; pass++)
        {
            var park = await ParkAsync(run, kind, pass);
            parks.Add(park);
            var taken = await Resume().TryResolveAsync(
                park.Id, WorkflowSuspensionStatus.Resumed, "{}", FirstPark.AddSeconds(pass), default);
            taken.Should().NotBeNull($"pass {pass + 1} parked a wait of its own");
            (taken!.SuspensionId, taken.BlockId).Should().Be((park.Id, Node));
        }

        parks.Select(park => park.Id).Should().OnlyHaveUniqueItems();
        var rows = await RowsAsync(run.ExecutionLogId);
        rows.Select(row => row.Id).Should().Equal(parks.Select(park => park.Id));
        rows.Should().OnlyContain(row => row.BlockId == Node && row.Status == WorkflowSuspensionStatus.Resumed);
    }

    [Fact]
    public async Task Taking_or_cancelling_a_runs_newest_park_at_a_node_leaves_its_older_park_as_it_was()
    {
        var run = await RunningRunAsync();
        var first = await ParkAsync(run, WorkflowSuspensionKind.WaitTemplate, 0);
        (await Resume().TryResolveAsync(first.Id, WorkflowSuspensionStatus.TimedOut, "{}", FirstPark, default))
            .Should().NotBeNull();
        var second = await ParkAsync(run, WorkflowSuspensionKind.WaitTemplate, 1);

        (await Resume().TryResolveAsync(first.Id, WorkflowSuspensionStatus.Resumed, "{}", FirstPark, default))
            .Should().BeNull("the older park was taken once already, and the newer one is not named by its id");
        await Store().CancelByExecutionAsync(run.ExecutionLogId, default);

        (await RowsAsync(run.ExecutionLogId)).Should().Equal(
            new ParkRow(first.Id, Node, WorkflowSuspensionStatus.TimedOut),
            new ParkRow(second.Id, Node, WorkflowSuspensionStatus.Cancelled));
    }

    [Fact]
    public async Task A_sleep_parked_at_one_node_again_is_listed_and_cancelled_through_its_newest_park_only()
    {
        var run = await RunningRunAsync();
        var first = await ParkAsync(run, WorkflowSuspensionKind.Sleep, 0);
        await Resume().TryResolveAsync(first.Id, WorkflowSuspensionStatus.Resumed, "{}", FirstPark, default);
        var second = await ParkAsync(run, WorkflowSuspensionKind.Sleep, 1);

        (await ParkedSleeps().ListParkedSleepsAsync(run.WorkflowId, default))
            .Should().ContainSingle().Which.SuspensionId.Should().Be(second.Id);
        (await ParkedSleeps().CancelParkedSleepAsync(second.Id, default)).Should().BeTrue();

        (await RowsAsync(run.ExecutionLogId)).Should().Equal(
            new ParkRow(first.Id, Node, WorkflowSuspensionStatus.Resumed),
            new ParkRow(second.Id, Node, WorkflowSuspensionStatus.Cancelled));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_lost_park_superseded_at_re_entry_leaves_the_runs_next_park_at_the_node_its_one_parked_wait(
        WorkflowSuspensionKind kind)
    {
        var run = await RunningRunAsync();
        var lost = await ParkAsync(run, kind, 0);

        await Store().CancelByExecutionAsync(run.ExecutionLogId, default);
        var next = await ParkAsync(run, kind, 1);

        (await RowsAsync(run.ExecutionLogId)).Should().Equal(
            new ParkRow(lost.Id, Node, WorkflowSuspensionStatus.Cancelled),
            new ParkRow(next.Id, Node, WorkflowSuspensionStatus.Suspended));
        if (await Resume().GetParkedAsync(null, run.ExecutionLogId, default) is { } byRun)
            byRun.Row.Should().NotBeNull().And.Match<WorkflowSuspensionCandidate>(row => row.SuspensionId == next.Id);
    }

    public static TheoryData<WorkflowSuspensionKind> Kinds()
    {
        var data = new TheoryData<WorkflowSuspensionKind>();
        foreach (var kind in Enum.GetValues<WorkflowSuspensionKind>())
            data.Add(kind);
        return data;
    }

    private async Task<WorkflowSuspension> ParkAsync(
        (Guid WorkflowId, Guid ExecutionLogId) run, WorkflowSuspensionKind kind, int pass)
    {
        var at = FirstPark.AddMinutes(pass);
        return await Store().SuspendAsync(new SuspendRequest
        {
            ExecutionLogId = run.ExecutionLogId,
            WorkflowId = run.WorkflowId,
            BlockId = Node,
            Kind = kind,
            ConfigJson = "{}",
            StatePayloadJson = "{}",
            SuspendedAtUtc = at,
            TimeoutAtUtc = at.AddHours(1)
        }, default);
    }
}
