using FluentAssertions;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Tests.Hosting;

public abstract class RestartGenerationsContract
{
    private static readonly TimeSpan LiveFor = TimeSpan.FromSeconds(15);

    protected ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));

    protected abstract IWorkflowRestartGenerations Store();

    [Fact]
    public async Task A_workflow_nothing_was_issued_for_reads_as_nothing()
    {
        var state = await Store().ReadAsync(Guid.NewGuid(), CancellationToken.None);

        state.Should().Be(new WorkflowRestartGenerationState(0, 0, null));
    }

    [Fact]
    public async Task Each_generation_issued_is_larger_than_the_last_and_the_newest_is_the_one_waiting()
    {
        var store = Store();
        var workflowId = Guid.NewGuid();

        var first = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        var second = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);

        first.Should().BeGreaterThan(0);
        second.Should().BeGreaterThan(first);
        (await store.ReadAsync(workflowId, CancellationToken.None))
            .Should().Be(new WorkflowRestartGenerationState(second, 0, second));
    }

    [Fact]
    public async Task Two_stores_over_one_place_never_issue_the_same_generation()
    {
        var workflowId = Guid.NewGuid();
        var stores = Enumerable.Range(0, 4).Select(_ => Store()).ToList();

        var issued = await Task.WhenAll(stores.SelectMany(store => Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => store.IssueAsync(workflowId, LiveFor, CancellationToken.None)))));

        issued.Should().OnlyHaveUniqueItems();
        (await stores[0].ReadAsync(workflowId, CancellationToken.None)).Issued.Should().Be(issued.Max());
    }

    [Fact]
    public async Task A_generation_nobody_touches_stops_being_live_when_its_time_is_up()
    {
        var store = Store();
        var workflowId = Guid.NewGuid();
        var older = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        Clock.Advance(TimeSpan.FromSeconds(10));
        var newer = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);

        Clock.Advance(TimeSpan.FromSeconds(6));
        (await store.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting.Should().Be(newer);

        Clock.Advance(TimeSpan.FromSeconds(10));
        var state = await store.ReadAsync(workflowId, CancellationToken.None);
        state.NewestWaiting.Should().BeNull();
        state.Issued.Should().Be(newer, "a generation that lapsed was still issued");
        older.Should().BeLessThan(newer);
    }

    [Fact]
    public async Task A_touched_generation_stays_live()
    {
        var store = Store();
        var workflowId = Guid.NewGuid();
        var generation = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);

        Clock.Advance(TimeSpan.FromSeconds(10));
        await store.TouchAsync(workflowId, generation, LiveFor, CancellationToken.None);
        Clock.Advance(TimeSpan.FromSeconds(10));

        (await store.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting.Should().Be(generation);
    }

    [Fact]
    public async Task A_generation_that_lapsed_is_live_again_when_its_trigger_touches_it()
    {
        var store = Store();
        var workflowId = Guid.NewGuid();
        var generation = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        Clock.Advance(LiveFor + TimeSpan.FromSeconds(5));

        await store.TouchAsync(workflowId, generation, LiveFor, CancellationToken.None);

        (await store.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting.Should().Be(generation);
    }

    [Fact]
    public async Task A_retired_generation_stops_waiting_and_the_newest_one_left_is_the_one_waiting()
    {
        var store = Store();
        var workflowId = Guid.NewGuid();
        var older = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        var newer = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);

        await store.RetireAsync(workflowId, newer, CancellationToken.None);

        (await store.ReadAsync(workflowId, CancellationToken.None))
            .Should().Be(new WorkflowRestartGenerationState(newer, 0, older));
    }

    [Fact]
    public async Task A_touch_does_not_bring_back_a_generation_that_was_retired_or_started()
    {
        var store = Store();
        var workflowId = Guid.NewGuid();
        var retired = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        var started = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        await store.RetireAsync(workflowId, retired, CancellationToken.None);
        await store.StartAsync(workflowId, started, CancellationToken.None);

        await store.TouchAsync(workflowId, retired, LiveFor, CancellationToken.None);
        await store.TouchAsync(workflowId, started, LiveFor, CancellationToken.None);

        (await store.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting.Should().BeNull();
    }

    [Fact]
    public async Task A_started_generation_stops_waiting_and_the_started_mark_only_rises()
    {
        var store = Store();
        var workflowId = Guid.NewGuid();
        var older = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);
        var newer = await store.IssueAsync(workflowId, LiveFor, CancellationToken.None);

        await store.StartAsync(workflowId, newer, CancellationToken.None);
        await store.StartAsync(workflowId, older, CancellationToken.None);

        (await store.ReadAsync(workflowId, CancellationToken.None))
            .Should().Be(new WorkflowRestartGenerationState(newer, newer, null));
    }

    [Fact]
    public async Task One_workflows_generations_are_not_anothers()
    {
        var store = Store();
        var one = Guid.NewGuid();
        var other = Guid.NewGuid();
        var generation = await store.IssueAsync(one, LiveFor, CancellationToken.None);
        await store.StartAsync(one, generation, CancellationToken.None);
        await store.IssueAsync(one, LiveFor, CancellationToken.None);

        (await store.ReadAsync(other, CancellationToken.None)).Should().Be(new WorkflowRestartGenerationState(0, 0, null));
    }

    protected sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long ticks = now.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}
