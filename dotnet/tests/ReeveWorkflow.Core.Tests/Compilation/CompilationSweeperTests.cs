using System.Runtime.CompilerServices;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilationServiceTests;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationSweeperTests
{
    [Fact]
    public async Task The_sweep_compiles_every_current_version_and_reports_the_ones_that_fail()
    {
        var world = new ServiceWorld();
        var sweeper = new WorkflowCompilationSweeper(world.Service, world.Store);
        var good = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));
        var bad = Version(Graph([StartNode(), Node("n1", "gone.block")], [Edge("e1", "start", "n1")]));

        var report = await sweeper.SweepAsync(Listed(good, bad), CancellationToken.None);

        report.Current.Select(k => k.WorkflowVersionId).Should().Equal(good.Id, bad.Id);
        report.Invalid.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(bad.Id);
        report.Invalid[0].Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.BlockTypeUnknown);
        report.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_run_twice_writes_nothing_the_second_time_nor_on_another_replica()
    {
        var world = new ServiceWorld();
        var version = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));

        await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(version), CancellationToken.None);
        await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(version), CancellationToken.None);
        world.Rebuild();
        var elsewhere = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(version), CancellationToken.None);

        world.Store.Saves.Should().Be(1);
        elsewhere.Current.Should().ContainSingle();
        elsewhere.Unsaved.Should().BeEmpty("the other replica's answer came from the stored row");
    }

    [Fact]
    public async Task Each_version_is_reported_under_the_key_another_service_computes_for_it_and_two_workflows_can_have_two_current_hashes()
    {
        var world = new ServiceWorld([Shape("other")]);
        var elsewhere = CompilationScopes.Service(world.Blocks, world.WorkflowStore);
        var a = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));
        var b = Version(Graph([StartNode(), Node("n", "other")], [Edge("e1", "start", "n")]));

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(a, b), CancellationToken.None);

        report.Current.Should().Equal(
            CompilationKey.Of(await elsewhere.EnsureAsync(a, null, CancellationToken.None)),
            CompilationKey.Of(await elsewhere.EnsureAsync(b, null, CancellationToken.None)));
        report.Current[0].DependencyHash.Should().NotBe(report.Current[1].DependencyHash);
    }

    [Fact]
    public async Task A_version_whose_definition_cannot_be_read_is_reported_invalid_with_that_rule()
    {
        var world = new ServiceWorld();
        var unreadable = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "not json", null, null, Guid.NewGuid(), T0);

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(unreadable), CancellationToken.None);

        report.Invalid.Should().ContainSingle().Which.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
    }

    [Fact]
    public async Task A_version_the_service_throws_on_is_reported_failed_and_the_sweep_goes_on()
    {
        var world = new ServiceWorld([Shape("boom", portsAreResolvedElsewhere: true)],
            [new ThrowingResolver("boom", () => new InvalidOperationException("the resolver's own fault"))]);
        var failing = Version(Graph([StartNode(), Node("n", "boom")], [Edge("e1", "start", "n")]));
        var good = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(failing, good), CancellationToken.None);

        report.Failed.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(failing.Id);
        report.Failed[0].Error.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("the resolver's own fault");
        report.Current.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(good.Id);
    }

    [Fact]
    public async Task A_cancellation_the_callers_token_did_not_ask_for_is_a_failure_of_that_version()
    {
        var world = new ServiceWorld([Shape("boom", portsAreResolvedElsewhere: true)],
            [new ThrowingResolver("boom", () => new TaskCanceledException("the resolver's own timeout"))]);
        var failing = Version(Graph([StartNode(), Node("n", "boom")], [Edge("e1", "start", "n")]));

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(failing), CancellationToken.None);

        report.Failed.Should().ContainSingle().Which.Error.Should().BeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task A_called_workflow_is_read_at_its_current_version_as_no_pin_manifest_reads_it()
    {
        var world = new ServiceWorld([SubWorkflowShape()]);
        var childId = Guid.NewGuid();
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), T0);
        child.SetCurrentVersion(new WorkflowVersion(Guid.NewGuid(), childId, 1, "{}", null, "[]", Guid.NewGuid(), T0), T0);
        world.WorkflowStore.AddPublished(child);
        var parent = Version(Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]));

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(parent), CancellationToken.None);

        report.Current.Should().ContainSingle();
        report.Invalid.Should().BeEmpty("under any manifest, a child it does not list is refused");
    }

    [Fact]
    public async Task A_cancelled_sweep_throws_rather_than_report_what_it_reached()
    {
        var world = new ServiceWorld();
        using var cancel = new CancellationTokenSource();
        var first = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));
        var second = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));

        var sweeping = () => new WorkflowCompilationSweeper(world.Service, world.Store)
            .SweepAsync(Listed([first, second], afterFirst: cancel.Cancel), cancel.Token);

        await sweeping.Should().ThrowAsync<OperationCanceledException>();
        world.Store.Saves.Should().Be(1, "the first version was answered before the token was cancelled");
    }

    [Fact]
    public async Task A_cancellation_the_callers_token_asked_for_inside_a_version_ends_the_sweep()
    {
        using var cancel = new CancellationTokenSource();
        var world = new ServiceWorld([Shape("boom", portsAreResolvedElsewhere: true)],
            [new ThrowingResolver("boom", () =>
            {
                cancel.Cancel();
                return new OperationCanceledException(cancel.Token);
            })]);
        var cancelling = Version(Graph([StartNode(), Node("n", "boom")], [Edge("e1", "start", "n")]));

        var sweeping = () => new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(cancelling), cancel.Token);

        await sweeping.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_source_that_fails_fails_the_sweep()
    {
        var world = new ServiceWorld();

        var sweeping = () => new WorkflowCompilationSweeper(world.Service, world.Store)
            .SweepAsync(Failing(new InvalidOperationException("the source's own fault")), CancellationToken.None);

        (await sweeping.Should().ThrowAsync<InvalidOperationException>()).WithMessage("the source's own fault");
    }

    [Fact]
    public async Task A_version_given_for_two_workflows_is_answered_once_and_reported_as_shared()
    {
        var world = new ServiceWorld();
        var version = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));
        var other = Version(Graph([StartNode(), Node("m", "noop")], [Edge("e1", "start", "m")]));
        var borrower = Guid.NewGuid();

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(
            Entries(new(version.WorkflowId, version), new(other.WorkflowId, other), new(borrower, version)),
            CancellationToken.None);

        report.Current.Select(k => k.WorkflowVersionId).Should().Equal(version.Id, other.Id);
        report.Shared.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CompilationSharedVersion(version.Id, [version.WorkflowId, borrower]));
        world.Store.Saves.Should().Be(2);
    }

    [Fact]
    public async Task A_version_that_failed_given_again_for_another_workflow_is_not_answered_again()
    {
        var asked = 0;
        var world = new ServiceWorld([Shape("boom", portsAreResolvedElsewhere: true)],
            [new ThrowingResolver("boom", () => new InvalidOperationException($"fault {Interlocked.Increment(ref asked)}"))]);
        var failing = Version(Graph([StartNode(), Node("n", "boom")], [Edge("e1", "start", "n")]));
        var borrower = Guid.NewGuid();

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(
            Entries(new CurrentWorkflowVersion(failing.WorkflowId, failing), new CurrentWorkflowVersion(borrower, failing)),
            CancellationToken.None);

        asked.Should().Be(1);
        report.Failed.Should().ContainSingle().Which.Error.Message.Should().Be("fault 1");
        report.Current.Should().BeEmpty();
        report.Shared.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CompilationSharedVersion(failing.Id, [failing.WorkflowId, borrower]));
    }

    [Fact]
    public async Task A_null_entry_or_a_null_version_is_refused()
    {
        var world = new ServiceWorld();
        var sweeper = new WorkflowCompilationSweeper(world.Service, world.Store);

        var nulled = () => sweeper.SweepAsync(Entries([null!], afterFirst: null), CancellationToken.None);
        var noVersion = () => sweeper.SweepAsync(Entries(new CurrentWorkflowVersion(Guid.NewGuid(), null!)), CancellationToken.None);
        var none = () => sweeper.SweepAsync(null!, CancellationToken.None);

        (await nulled.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("currentVersions");
        (await noVersion.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("currentVersions");
        (await none.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("currentVersions");
    }

    [Fact]
    public async Task A_version_whose_verdict_the_store_did_not_save_is_reported_unsaved_until_a_save_lands()
    {
        var world = new ServiceWorld();
        var store = new FlakySaves();
        var sweeper = new WorkflowCompilationSweeper(CompilationScopes.Service(world.Blocks, world.WorkflowStore, store), store);
        var faulted = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));
        var declined = Version(Graph([StartNode(), Node("m", "noop")], [Edge("e1", "start", "m")]));
        var kept = Version(Graph([StartNode(), Node("k", "noop")], [Edge("e1", "start", "k")]));
        store.Refuse[faulted.Id] = () => new InvalidOperationException("the database's own fault");
        store.Refuse[declined.Id] = () => new CompilationStoreUnavailableException("declined.");

        var first = await sweeper.SweepAsync(Listed(faulted, declined, kept), CancellationToken.None);
        var memoHits = await sweeper.SweepAsync(Listed(faulted, declined, kept), CancellationToken.None);
        store.Refuse.Remove(faulted.Id);
        var retried = await sweeper.SweepAsync(Listed(faulted, declined, kept), CancellationToken.None);

        first.Unsaved.Should().Equal(faulted.Id, declined.Id);
        first.Current.Select(k => k.WorkflowVersionId).Should().Equal(faulted.Id, declined.Id, kept.Id);
        memoHits.Unsaved.Should().Equal(faulted.Id, declined.Id);
        retried.Unsaved.Should().Equal(declined.Id);
        store.Saves.Should().Be(7, "three first saves, then a retry of each unsaved verdict on each later sweep");
    }

    [Fact]
    public async Task The_callers_token_reaches_the_listing_and_each_answer()
    {
        using var cancel = new CancellationTokenSource();
        var answered = new List<CancellationToken>();
        var world = new ServiceWorld([Shape("probe", portsAreResolvedElsewhere: true)], [new RecordingResolver("probe", answered)]);
        var version = Version(Graph([StartNode(), Node("n", "probe")], [Edge("e1", "start", "n")]));
        var listed = new List<CancellationToken>();

        await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Observed(version, listed), cancel.Token);

        listed.Should().Equal(cancel.Token);
        answered.Should().Equal(cancel.Token);
    }

    [Fact]
    public async Task The_report_counts_malformed_port_type_names_by_where_they_are_written()
    {
        var world = new ServiceWorld([Shape("dyn.source", hasDynamicPorts: true)]);
        var inParameters = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]),
            inputs: """[{"name":"a","type":"CollectionInt"},{"name":"b","type":"Collection"}]""");
        var onANode = Version(Graph(
            [StartNode(), Node("n", "dyn.source", new { OutputPorts = """[{"name":"Items","type":"CollectionInt"}]""" })],
            [Edge("e1", "start", "n")]));
        var otherwiseInvalid = Version(Graph([StartNode(), Node("n1", "gone.block")], [Edge("e1", "start", "n1")]));

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store)
            .SweepAsync(Listed(inParameters, onANode, otherwiseInvalid), CancellationToken.None);

        report.MalformedPortTypeNamesInParameters.Should().Be(2);
        report.MalformedPortTypeNamesOnNodes.Should().Be(1);
    }

    [Fact]
    public async Task The_report_counts_malformed_type_arguments_separately_from_node_sites_and_ignores_unknown_keys()
    {
        var world = new ServiceWorld([Shape("generic", typeParameters: [new WorkflowTypeParameter("T", [])])]);
        var node = Node("n", "generic");
        node.TypeArguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["T"] = "Int<" };
        var version = Version(Graph([StartNode(), node], [Edge("e1", "start", "n")]));

        var report = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(version), CancellationToken.None);

        report.MalformedPortTypeNamesInTypeArguments.Should().Be(1);
        report.MalformedPortTypeNamesOnNodes.Should().Be(0);

        var unknownNode = Node("unknown", "generic");
        unknownNode.TypeArguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["Unknown"] = "Int" };
        var unknownVersion = Version(Graph([StartNode(), unknownNode], [Edge("e2", "start", "unknown")]));
        var unknown = await new WorkflowCompilationSweeper(world.Service, world.Store).SweepAsync(Listed(unknownVersion), CancellationToken.None);
        unknown.MalformedPortTypeNamesInTypeArguments.Should().Be(0);
    }

    [Fact]
    public async Task The_store_is_handed_each_answered_versions_key_and_no_failed_version_with_the_retention()
    {
        var world = new ServiceWorld([Shape("boom", portsAreResolvedElsewhere: true)],
            [new ThrowingResolver("boom", () => new InvalidOperationException("no"))]);
        var sweeper = new WorkflowCompilationSweeper(world.Service, world.Store) { Retention = TimeSpan.FromDays(3) };
        var first = Version(Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]));
        var failing = Version(Graph([StartNode(), Node("n", "boom")], [Edge("e1", "start", "n")]));
        var second = Version(Graph([StartNode(), Node("m", "noop")], [Edge("e1", "start", "m")]));
        var alsoFailing = Version(Graph([StartNode(), Node("m", "boom")], [Edge("e1", "start", "m")]));
        var report = await sweeper.SweepAsync(Listed(first, failing, second, alsoFailing), CancellationToken.None);

        var deleted = await sweeper.DeleteStaleAsync(report, CancellationToken.None);

        deleted.Should().Be(7, "the store's own count is what the sweeper answers");
        report.Failed.Select(f => f.WorkflowVersionId).Should().Equal(failing.Id, alsoFailing.Id);
        var call = world.Store.Deletes.Should().ContainSingle().Subject;
        call.Current.Select(k => k.WorkflowVersionId).Should().Equal(first.Id, second.Id);
        call.Current.Should().Equal(report.Current);
        call.OlderThan.Should().Be(TimeSpan.FromDays(3));
    }

    [Fact]
    public async Task An_unset_retention_hands_the_store_thirty_days()
    {
        var world = new ServiceWorld();
        var sweeper = new WorkflowCompilationSweeper(world.Service, world.Store);
        var report = await sweeper.SweepAsync(Listed(), CancellationToken.None);

        await sweeper.DeleteStaleAsync(report, CancellationToken.None);

        world.Store.Deletes.Should().ContainSingle().Which.OlderThan.Should().Be(TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task Without_a_store_the_sweep_still_reports_and_the_retention_has_nothing_to_delete()
    {
        var world = new ServiceWorld();
        var sweeper = new WorkflowCompilationSweeper(world.Service, store: null);
        var bad = Version(Graph([StartNode(), Node("n1", "gone.block")], [Edge("e1", "start", "n1")]));

        var report = await sweeper.SweepAsync(Listed(bad), CancellationToken.None);

        report.Invalid.Should().ContainSingle();
        (await sweeper.DeleteStaleAsync(report, CancellationToken.None)).Should().Be(0);
        world.Store.Deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_that_declines_the_retention_is_heard_by_the_caller()
    {
        var world = new ServiceWorld();
        var sweeper = new WorkflowCompilationSweeper(world.Service, new DecliningRetention());
        var report = await sweeper.SweepAsync(Listed(), CancellationToken.None);

        var deleting = () => sweeper.DeleteStaleAsync(report, CancellationToken.None);

        await deleting.Should().ThrowAsync<CompilationStoreUnavailableException>();
    }

    [Fact]
    public void A_retention_shorter_than_an_hour_is_refused_and_an_hour_is_taken()
    {
        var world = new ServiceWorld();
        var hour = TimeSpan.FromHours(1);

        foreach (var retention in new[] { TimeSpan.Zero, -TimeSpan.FromTicks(1), hour - TimeSpan.FromTicks(1) })
        {
            var building = () => new WorkflowCompilationSweeper(world.Service, world.Store) { Retention = retention };

            building.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("value");
        }

        new WorkflowCompilationSweeper(world.Service, world.Store) { Retention = hour }.Retention.Should().Be(hour);
    }

    [Fact]
    public void The_sweeper_refuses_a_missing_service()
    {
        var building = () => new WorkflowCompilationSweeper(null!, null);

        building.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("service");
    }

    private static IAsyncEnumerable<CurrentWorkflowVersion> Listed(params WorkflowVersion[] versions) => Listed(versions, afterFirst: null);

    private static IAsyncEnumerable<CurrentWorkflowVersion> Listed(IReadOnlyList<WorkflowVersion> versions, Action? afterFirst) =>
        Entries([.. versions.Select(v => new CurrentWorkflowVersion(v.WorkflowId, v))], afterFirst);

    private static IAsyncEnumerable<CurrentWorkflowVersion> Entries(params CurrentWorkflowVersion[] entries) => Entries(entries, afterFirst: null);

    private static async IAsyncEnumerable<CurrentWorkflowVersion> Entries(IReadOnlyList<CurrentWorkflowVersion> entries, Action? afterFirst)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            await Task.Yield();
            if (i == 1)
                afterFirst?.Invoke();
            yield return entries[i];
        }
    }

    private static async IAsyncEnumerable<CurrentWorkflowVersion> Observed(
        WorkflowVersion version, List<CancellationToken> seen, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        seen.Add(cancellationToken);
        await Task.Yield();
        yield return new CurrentWorkflowVersion(version.WorkflowId, version);
    }

    private static async IAsyncEnumerable<CurrentWorkflowVersion> Failing(Exception fault)
    {
        await Task.Yield();
        throw fault;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private sealed class RecordingResolver(string blockTypeId, List<CancellationToken> seen) : INodePortResolver
    {
        public string BlockTypeId => blockTypeId;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken)
        {
            seen.Add(cancellationToken);
            return Task.FromResult(NodePortResolution.Failed("recorded"));
        }
    }

    private sealed class FlakySaves : IWorkflowCompilationStore
    {
        public Dictionary<Guid, Func<Exception>> Refuse { get; } = [];
        public int Saves { get; private set; }

        public Task<CompilationVerdict?> FindAsync(Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken) =>
            Task.FromResult<CompilationVerdict?>(null);

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
        {
            Saves++;
            return Refuse.TryGetValue(verdict.WorkflowVersionId, out var fault) ? throw fault() : Task.CompletedTask;
        }

        public Task<int> DeleteStaleAsync(IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class ThrowingResolver(string blockTypeId, Func<Exception> fault) : INodePortResolver
    {
        public string BlockTypeId => blockTypeId;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken) =>
            throw fault();
    }

    private sealed class DecliningRetention : IWorkflowCompilationStore
    {
        public Task<CompilationVerdict?> FindAsync(Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken) =>
            Task.FromResult<CompilationVerdict?>(null);

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            throw new CompilationStoreUnavailableException("declined.");
    }
}
