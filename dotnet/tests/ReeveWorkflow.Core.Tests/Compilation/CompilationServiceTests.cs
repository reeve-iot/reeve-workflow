using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationServiceTests
{
    internal static readonly DateTimeOffset T0 = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Helper_free_check_and_analysis_do_not_call_the_resolver()
    {
        var resolver = new ThrowingEmptyHelperCheckResolver();
        var world = new ServiceWorld(options: new CompilationOptions { HelperResolver = resolver });
        const string empty = "{\"nodes\":[],\"edges\":[]}";

        await world.Service.CheckAsync(empty, null, null, CancellationToken.None);
        await world.Service.AnalyzeAsync(empty, null, null, CancellationToken.None);

        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Unreadable_definition_is_refused_before_check_analysis_ensure_or_rewrite_catalogue_collects_paths()
    {
        const string unreadable = """{"nodes":[{"id":"\ud800"}]}""";
        var resolver = new ThrowingEmptyHelperCheckResolver();
        var world = new ServiceWorld(options: new CompilationOptions { HelperResolver = resolver });

        var check = await world.Service.CheckAsync(unreadable, null, null, CancellationToken.None);
        var analysis = await world.Service.AnalyzeAsync(unreadable, null, null, CancellationToken.None);
        var version = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, unreadable, null, null,
            Guid.NewGuid(), T0);
        var ensured = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        check.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        analysis.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        ensured.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Check_and_analysis_each_check_the_document_helper_paths_once()
    {
        var resolver = new RecordingHelperResolver();
        var world = new ServiceWorld([Shape("use", inputs: [("Value", PortValueType.Number, true)])],
            options: new CompilationOptions { HelperResolver = resolver });
        var definition = JsonSerializer.Serialize(Graph(
            [StartNode(), Node("n", "use", new { Value = "expr:${helper.x}" })], [Edge("e", "start", "n")]));

        await world.Service.CheckAsync(definition, null, null, CancellationToken.None);
        await world.Service.AnalyzeAsync(definition, null, null, CancellationToken.None);

        resolver.Calls.Should().Be(2);
        resolver.Batches.Should().HaveCount(2).And.OnlyContain(batch => batch.Select(path => string.Join('.', path.Segments)).SequenceEqual(new[] { "x" }));
    }

    [Fact]
    public async Task Ensure_does_not_store_a_refusal_when_the_helper_check_throws()
    {
        var resolver = new ThrowingHelperCheckResolver();
        var world = new ServiceWorld([Shape("use", inputs: [("Value", PortValueType.Number, true)])],
            options: new CompilationOptions { HelperResolver = resolver });
        var version = Version(Graph(
            [StartNode(), Node("n", "use", new { Value = "expr:${helper.x}" })], [Edge("e", "start", "n")]));

        var act = () => world.Service.EnsureAsync(version, null, CancellationToken.None);

        var fault = await act.Should().ThrowAsync<CompilationFaultException>();
        fault.Which.InnerException.Should().BeOfType<InvalidOperationException>();
        resolver.Calls.Should().Be(1);
        world.Store.Saves.Should().Be(0);
    }

    [Fact]
    public async Task A_version_that_reads_no_helper_asks_the_resolver_nothing_on_a_miss_a_memo_hit_or_a_stored_hit()
    {
        var resolver = new ThrowingEmptyHelperCheckResolver();
        var world = new ServiceWorld(options: new CompilationOptions { HelperResolver = resolver });
        var version = Version(NoopGraph());

        await world.Service.EnsureAsync(version, null, CancellationToken.None);
        await world.Service.EnsureAsync(version, null, CancellationToken.None);
        world.Rebuild(options: new CompilationOptions { HelperResolver = resolver });
        await world.Service.EnsureAsync(version, null, CancellationToken.None);

        world.Store.Saves.Should().Be(1);
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_version_that_reads_helpers_asks_once_per_lookup_and_a_repeated_answer_stays_a_memo_hit()
    {
        var resolver = new SwitchableHelperResolver("Int");
        var world = new ServiceWorld([Shape("use", inputs: [("Value", PortValueType.Number, true)])],
            options: new CompilationOptions { HelperResolver = resolver });
        var version = Version(HelperReadingGraph());

        var first = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        var second = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        second.Should().BeSameAs(first);
        resolver.Calls.Should().Be(2);
        world.Store.Saves.Should().Be(1);
        world.Store.Finds.Should().Be(1, "an unchanged answer is the same key, so the memo answers without the store");
    }

    [Fact]
    public async Task A_changed_helper_term_misses_the_memoised_verdict_and_compiles_against_the_new_term()
    {
        var resolver = new SwitchableHelperResolver("Int");
        var world = new ServiceWorld([Shape("use", inputs: [("Value", PortValueType.Number, true)])],
            options: new CompilationOptions { HelperResolver = resolver });
        var version = Version(HelperReadingGraph());

        var before = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        resolver.Term = "String";
        var after = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        before.IsValid.Should().BeTrue();
        after.IsValid.Should().BeFalse("a String helper no longer feeds a Number input");
        after.DependencyHash.Should().NotBe(before.DependencyHash);
        world.Store.Finds.Should().Be(2, "the memo missed, so the store was asked again");
        world.Store.Saves.Should().Be(2);
    }

    [Fact]
    public async Task A_changed_helper_term_misses_the_stored_verdict()
    {
        var resolver = new SwitchableHelperResolver("Int");
        var world = new ServiceWorld([Shape("use", inputs: [("Value", PortValueType.Number, true)])],
            options: new CompilationOptions { HelperResolver = resolver });
        var version = Version(HelperReadingGraph());
        await world.Service.EnsureAsync(version, null, CancellationToken.None);

        resolver.Term = "String";
        world.Rebuild(options: new CompilationOptions { HelperResolver = resolver });
        var after = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        resolver.Term = "Int";
        world.Rebuild(options: new CompilationOptions { HelperResolver = resolver });
        var back = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        after.IsValid.Should().BeFalse();
        back.IsValid.Should().BeTrue();
        world.Store.Saves.Should().Be(2, "the first row answers again once the term is back; only the String key compiled");
    }

    [Fact]
    public async Task A_path_the_resolver_leaves_unanswered_keys_the_version_as_a_No_and_answers_for_unasked_paths_do_not_key_it()
    {
        var shape = Shape("use", inputs: [("Value", PortValueType.Number, true)]);
        async Task<(string Hash, int Saves)> Keyed(Dictionary<string, NamespacePathCheckAnswer> answers, ServiceWorld? shared = null)
        {
            var options = new CompilationOptions { HelperResolver = new FixedAnswers(answers) };
            var world = shared ?? new ServiceWorld([shape], options: options);
            if (shared is not null)
                world.Rebuild(options: options);
            var verdict = await world.Service.EnsureAsync(Version(HelperReadingGraph(), id: FixedId), null, CancellationToken.None);
            return (verdict.DependencyHash, world.Store.Saves);
        }

        var yesInt = new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int");
        var unanswered = (await Keyed([])).Hash;
        var answeredNo = (await Keyed(new() { ["x"] = NamespacePathCheckAnswer.No })).Hash;
        var wide = new ServiceWorld([shape]);
        var withString = (await Keyed(new() { ["x"] = yesInt, ["y"] = new(NamespacePathCheck.Yes, "String") }, wide)).Hash;
        var withInt = await Keyed(new() { ["x"] = yesInt, ["y"] = yesInt }, wide);

        unanswered.Should().Be(answeredNo);
        withInt.Hash.Should().Be(withString);
        withInt.Saves.Should().Be(1, "an answer for a path the version never read is a stored hit, not a new key");
    }

    [Fact]
    public async Task A_helper_check_that_throws_fails_a_lookup_a_memoised_verdict_would_have_answered()
    {
        var resolver = new SwitchableHelperResolver("Int");
        var world = new ServiceWorld([Shape("use", inputs: [("Value", PortValueType.Number, true)])],
            options: new CompilationOptions { HelperResolver = resolver });
        var version = Version(HelperReadingGraph());
        var first = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        resolver.Throws = true;
        var act = () => world.Service.EnsureAsync(version, null, CancellationToken.None);
        await act.Should().ThrowAsync<CompilationFaultException>();
        resolver.Throws = false;
        var again = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        again.Should().BeSameAs(first);
        (world.Store.Finds, world.Store.Saves).Should().Be((1, 1), "the failed lookup read and wrote nothing");
    }

    [Fact]
    public async Task A_helper_check_that_times_out_with_the_callers_token_live_is_a_compilation_fault_not_a_cancellation()
    {
        var resolver = new SwitchableHelperResolver("Int") { Throws = true, Failure = new TaskCanceledException("helper store timed out") };
        var world = new ServiceWorld([Shape("use", inputs: [("Value", PortValueType.Number, true)])],
            options: new CompilationOptions { HelperResolver = resolver });

        var act = () => world.Service.EnsureAsync(Version(HelperReadingGraph()), null, CancellationToken.None);

        await act.Should().ThrowAsync<CompilationFaultException>();
    }

    private static readonly Guid FixedId = Guid.NewGuid();

    private sealed class FixedAnswers(Dictionary<string, NamespacePathCheckAnswer> answers) : IHelperResolver
    {
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                answers.ToDictionary(a => new HelperPath(a.Key.Split('.')), a => a.Value));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private static WorkflowDefinition HelperReadingGraph() => Graph(
        [StartNode(), Node("n", "use", new { Value = "expr:${helper.x}" })], [Edge("e", "start", "n")]);

    private sealed class SwitchableHelperResolver(string term) : IHelperResolver
    {
        public string Term { get; set; } = term;
        public bool Throws { get; set; }
        public Exception Failure { get; init; } = new InvalidOperationException("helper store is down");
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throws)
                throw Failure;
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, Term)));
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private sealed class ThrowingHelperCheckResolver : IHelperResolver
    {
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromException<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                new InvalidOperationException("helper store is down"));
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private sealed class ThrowingEmptyHelperCheckResolver : IHelperResolver
    {
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("An empty helper batch must not be requested.");
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private sealed class RecordingHelperResolver : IHelperResolver
    {
        public int Calls { get; private set; }
        public List<IReadOnlyCollection<HelperPath>> Batches { get; } = [];
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            Calls++;
            Batches.Add(paths.ToArray());
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int")));
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_duplicate_node_id_is_refused_by_the_compilation_service_with_its_canvas(bool inBody)
    {
        const string duplicate = """{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
        var definition = inBody
            ? """{"nodes":[{"id":"loop","subCanvases":{"Body":DOMAIN},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""".Replace("DOMAIN", duplicate)
            : duplicate;
        var world = new ServiceWorld();
        var version = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, definition, null, null, Guid.NewGuid(), T0);

        var result = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        var diagnostic = result.Diagnostics.Should().ContainSingle().Which;
        diagnostic.RuleId.Should().Be(CompilationRules.DuplicateNodeId);
        diagnostic.NodeId.Should().Be("twin");
        diagnostic.CanvasPath.Should().Equal(inBody ? [new CanvasPathSegment("loop", "Body")] : []);
        diagnostic.Message.Should().Contain(inBody ? "/nodes/0/subCanvases/Body/nodes/1/id" : "/nodes/1/id");
    }

    [Fact]
    public async Task A_missing_verdict_is_compiled_once_and_stored()
    {
        var world = new ServiceWorld();
        var version = Version(NoopGraph());

        var first = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        var second = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        first.IsValid.Should().BeTrue();
        second.Should().BeSameAs(first, "the second call is answered from the in-process memo, never a fresh compile");
        world.Store.Saves.Should().Be(1);
        world.Store.Finds.Should().Be(1, "the memo answers the second call before the store is ever asked again");
    }

    [Fact]
    public async Task A_stored_refusal_is_answered_without_compiling_again()
    {
        var world = new ServiceWorld();
        var version = Version(Graph([StartNode(), Node("bad", "unknown.block")], [Edge("e1", "start", "bad")]));

        var first = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        first.IsValid.Should().BeFalse();
        first.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.BlockTypeUnknown);
        world.Store.Saves.Should().Be(1);

        world.Rebuild();
        var second = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        second.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.BlockTypeUnknown);
        world.Store.Saves.Should().Be(1, "the stored row already answered; nothing recompiled it");
        world.Store.Finds.Should().Be(2);
    }

    [Fact]
    public async Task An_unreadable_declared_parameters_document_is_refused_not_answered_valid()
    {
        var world = new ServiceWorld();
        var version = Version(NoopGraph(), inputs: "not json");

        var verdict = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        verdict.IsValid.Should().BeFalse("the declared parameters could not be read at all");
        verdict.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
    }

    [Fact]
    public async Task A_verdicts_BlockSources_carries_what_the_bound_blocks_actually_declared()
    {
        var blocks = new MovableCatalogue()
            .Add(Shape("noop", source: "plugin:acme@1.0"));
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore());

        var verdict = await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        verdict.BlockSources.Should().Contain("plugin:acme@1.0");
    }

    [Fact]
    public async Task A_catalogue_generation_that_moved_is_not_answered_from_the_old_memo()
    {
        var world = new ServiceWorld();
        var version = Version(Graph([StartNode(), Node("n", "later.block")], [Edge("e1", "start", "n")]));

        var before = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        before.IsValid.Should().BeFalse();
        before.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.BlockTypeUnknown);

        world.Blocks.Move("gen-2", Shape("later.block"));
        var after = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        after.IsValid.Should().BeTrue("the catalogue that now answers this block type moved to a new generation");
    }

    [Fact]
    public void What_the_hash_was_computed_over_is_what_bind_went_on_to_resolve()
    {
        var graph = Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop"));

        var before = WorkflowCompiler.ShapesUsedBy(graph, catalogue);
        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        DependencyFingerprint.Compute(before.Values, new CompilationOptions(), NodePortEnvironment.Empty)
            .Should().Be(DependencyFingerprint.Compute(result.BoundShapes.Values, new CompilationOptions(), NodePortEnvironment.Empty));
    }

    [Fact]
    public async Task A_verdict_stored_under_the_previous_compiler_version_is_recompiled_and_now_refuses_an_unregistered_namespace()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${device.kitchen.temperature}" })],
            [Edge("e1", "start", "n1")]);
        var world = new ServiceWorld([Shape("notify", inputs: [("Message", PortValueType.String, true)])]);
        var version = Version(graph);
        var used = WorkflowCompiler.ShapesUsedBy(graph, new CatalogueBlockShapeSource(world.Blocks)).Values.ToList();
        var previousVersion = "3/" + DependencyFingerprint.CompilerVersion[2..];
        var previousKey = DependencyFingerprint.Compute(used, new CompilationOptions(), NodePortEnvironment.Empty, previousVersion);
        var storedByPrevious = new CompilationVerdict(
            version.Id, WorkflowCompilationService.ComputeDefinitionDigest(version),
            previousKey, true, [], [], previousVersion, T0);
        await world.Store.SaveAsync(storedByPrevious, CancellationToken.None);

        var verdict = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        verdict.DependencyHash.Should().Be(DependencyFingerprint.Compute(used, new CompilationOptions(), NodePortEnvironment.Empty));
        verdict.DependencyHash.Should().NotBe(previousKey);
        verdict.IsValid.Should().BeFalse("the stored row answered a question the previous compiler asked, not this one");
        verdict.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public void The_hash_still_agrees_when_bind_partially_fails()
    {
        var graph = Graph(
            [StartNode(), Node("bad", "unknown"), Node("n", "noop")],
            [Edge("e1", "start", "bad"), Edge("e2", "bad", "n")]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop"));

        var before = WorkflowCompiler.ShapesUsedBy(graph, catalogue);
        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        DependencyFingerprint.Compute(before.Values, new CompilationOptions(), NodePortEnvironment.Empty)
            .Should().Be(DependencyFingerprint.Compute(result.BoundShapes.Values, new CompilationOptions(), NodePortEnvironment.Empty));
    }

    [Fact]
    public void ShapesUsedBy_walks_every_sub_canvas_not_only_the_root()
    {
        var inner = Graph([Node("deep", "only-in-body")]);
        var container = Node("container", "container.block");
        container.SubCanvases = new() { ["Body"] = inner };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "container")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("container.block"), Shape("only-in-body"));

        var usedBy = WorkflowCompiler.ShapesUsedBy(graph, catalogue);

        usedBy.Should().ContainKey("only-in-body", "a block type reachable only inside a sub-canvas must still hash");
    }

    [Fact]
    public async Task A_draft_edited_in_place_gets_a_fresh_verdict_not_the_stale_one()
    {
        var world = new ServiceWorld();
        var versionId = Guid.NewGuid();
        var version = Version(NoopGraph(), id: versionId);

        var before = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        before.IsValid.Should().BeTrue();

        var edited = Graph([StartNode(), Node("bad", "unknown.block")], [Edge("e1", "start", "bad")]);
        version.UpdateDefinition(Stored(edited), null, null);

        var after = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        after.WorkflowVersionId.Should().Be(versionId);
        after.DefinitionDigest.Should().NotBe(before.DefinitionDigest,
            "the version id alone does not identify what was compiled once a draft is edited in place");
        after.IsValid.Should().BeFalse("the edited draft now names a block the catalogue does not have");
        world.Store.Saves.Should().Be(2, "each distinct definition digest under this version id is its own compile");
    }

    [Fact]
    public async Task A_draft_edited_in_place_with_the_same_block_types_still_gets_a_fresh_verdict()
    {
        var world = new ServiceWorld();
        var versionId = Guid.NewGuid();
        var version = Version(NoopGraph(), id: versionId);

        var before = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        var edited = Graph([StartNode(), Node("renamed", "noop")], [Edge("e1", "start", "renamed")]);
        version.UpdateDefinition(Stored(edited), null, null);

        var after = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        after.DependencyHash.Should().Be(before.DependencyHash, "the same block type (\"noop\") is still the only one used");
        after.DefinitionDigest.Should().NotBe(before.DefinitionDigest, "the node id in the definition text did change");
        world.Store.Saves.Should().Be(2, "the memo hit must be gated on BOTH digests, not the dependency hash alone");
    }

    [Fact]
    public async Task A_non_guid_configured_reference_is_refused_not_resolved()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = "not-a-guid" })],
            [Edge("e1", "start", "call")]);

        var verdict = await world.Service.EnsureAsync(Version(graph), null, CancellationToken.None);

        verdict.IsValid.Should().BeFalse();
        verdict.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.NodePortsUnresolved && d.Message.Contains("is not a workflow id"));
    }

    [Fact]
    public async Task A_configured_child_with_no_version_is_refused_not_resolved()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var missingChild = Guid.NewGuid();
        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = missingChild.ToString() })],
            [Edge("e1", "start", "call")]);

        var verdict = await world.Service.EnsureAsync(Version(graph), null, CancellationToken.None);

        verdict.IsValid.Should().BeFalse();
        verdict.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.NodePortsUnresolved && d.Message.Contains("has no version to read parameters from"));
    }

    [Fact]
    public async Task A_child_with_unreadable_declared_parameters_is_refused_not_resolved()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var childId = Guid.NewGuid();
        var childVersion = new WorkflowVersion(Guid.NewGuid(), childId, 1, "{}", "not json", "[]", Guid.NewGuid(), T0);
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), T0);
        child.SetCurrentVersion(childVersion, T0);
        world.WorkflowStore.AddPublished(child);

        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]);

        var verdict = await world.Service.EnsureAsync(Version(graph), null, CancellationToken.None);

        verdict.IsValid.Should().BeFalse();
        verdict.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.NodePortsUnresolved && d.Message.Contains("has declared parameters that could not be read"));
    }

    [Fact]
    public async Task A_child_whose_input_parameter_is_json_is_refused_in_the_parent_node()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var childId = Guid.NewGuid();
        var childVersion = new WorkflowVersion(Guid.NewGuid(), childId, 1, "{}", """[{"name":"payload","type":"Json"}]""", "[]", Guid.NewGuid(), T0);
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), T0);
        child.SetCurrentVersion(childVersion, T0);
        world.WorkflowStore.AddPublished(child);

        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]);

        var verdict = await world.Service.EnsureAsync(Version(graph), null, CancellationToken.None);

        verdict.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.PortTypeJsonPosition && d.NodeId == "call" && d.PortName == "payload");
        verdict.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved && d.NodeId == "call");
    }

    [Fact]
    public async Task Two_workflows_calling_each_other_check_clean_in_either_order_and_the_caller_is_refused_until_its_callee_exists()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape(), Shape("act")]);
        var (idA, idB) = (Guid.NewGuid(), Guid.NewGuid());
        WorkflowDefinition Calls(Guid callee) => Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = callee.ToString() }), Node("use", "act")],
            [Edge("e1", "start", "call"), Edge("e2", "call", "use")]);
        var (a, b) = (Calls(idB), Calls(idA));

        var neither = (await world.Service.CheckAsync(Stored(a), null, null, CancellationToken.None),
            await world.Service.CheckAsync(Stored(b), null, null, CancellationToken.None));
        neither.Item1.Diagnostics.Should().BeEmpty();
        neither.Item1.UnresolvedWorkflowIds.Should().Equal(idB);
        neither.Item2.Diagnostics.Should().BeEmpty();
        neither.Item2.UnresolvedWorkflowIds.Should().Equal(idA);
        var refused = await world.Service.EnsureAsync(Version(a, workflowId: idA), null, CancellationToken.None);
        refused.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved && d.NodeId == "call");

        StorePublished(world, idA, a);
        var calleeFirst = await world.Service.CheckAsync(Stored(b), null, null, CancellationToken.None);
        calleeFirst.Diagnostics.Should().BeEmpty();
        calleeFirst.UnresolvedWorkflowIds.Should().BeEmpty();

        StorePublished(world, idB, b);
        var bothStored = await world.Service.CheckAsync(Stored(a), null, null, CancellationToken.None);
        bothStored.Diagnostics.Should().BeEmpty();
        bothStored.UnresolvedWorkflowIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wire_from_a_callee_this_host_lacks_adds_no_refusal_of_its_own()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape(), Shape("act", inputs: [("In", PortValueType.String, true)])]);
        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = Guid.NewGuid().ToString() }), Node("use", "act")],
            [Edge("e1", "start", "call"), Edge("e2", "call", "use", ("Result", "In"))]);

        var check = await world.Service.CheckAsync(Stored(graph), null, null, CancellationToken.None);

        check.Diagnostics.Should().BeEmpty();
        check.UnresolvedWorkflowIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_callee_that_is_not_a_workflow_id_is_still_refused_by_an_import_check()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = "not-a-guid" })],
            [Edge("e1", "start", "call")]);

        var check = await world.Service.CheckAsync(Stored(graph), null, null, CancellationToken.None);

        check.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.NodePortsUnresolved && d.NodeId == "call");
        check.UnresolvedWorkflowIds.Should().BeEmpty();
    }

    private static void StorePublished(ServiceWorld world, Guid workflowId, WorkflowDefinition definition)
    {
        var workflow = new Workflow(workflowId, "stored", null, Guid.NewGuid(), T0);
        workflow.SetCurrentVersion(Version(definition, workflowId: workflowId), T0);
        world.WorkflowStore.AddPublished(workflow);
    }

    [Fact]
    public async Task A_called_workflows_changed_signature_recompiles_with_no_generation_bump()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);

        var childWorkflowId = Guid.NewGuid();
        var childVersion1 = new WorkflowVersion(Guid.NewGuid(), childWorkflowId, 1, "{}", null, "[]", Guid.NewGuid(), T0);
        var childWorkflow = new Workflow(childWorkflowId, "child", null, Guid.NewGuid(), T0);
        childWorkflow.SetCurrentVersion(childVersion1, T0);
        world.WorkflowStore.AddPublished(childWorkflow);

        var parentGraph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childWorkflowId.ToString() })],
            [Edge("e1", "start", "call")]);
        var parentVersion = Version(parentGraph);

        var first = await world.Service.EnsureAsync(parentVersion, null, CancellationToken.None);

        var childVersion2 = new WorkflowVersion(
            Guid.NewGuid(), childWorkflowId, 2, "{}", null,
            JsonSerializer.Serialize(new[] { new { name = "Result", type = "String" } }), Guid.NewGuid(), T0);
        childWorkflow.SetCurrentVersion(childVersion2, T0);

        var second = await world.Service.EnsureAsync(parentVersion, null, CancellationToken.None);

        second.DependencyHash.Should().NotBe(first.DependencyHash,
            "the parent's dependency hash carries the child's resolved signature, which just moved");
        world.Store.Saves.Should().Be(2, "the environment moved with no generation bump, so the memo cannot answer the second call");
    }

    [Fact]
    public async Task A_saved_memo_hit_still_reads_the_workflow_store_to_re_resolve_the_environment()
    {
        var childId = Guid.NewGuid();
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), T0);
        child.SetCurrentVersion(new WorkflowVersion(Guid.NewGuid(), childId, 1, "{}", null, "[]", Guid.NewGuid(), T0), T0);
        world.WorkflowStore.AddPublished(child);
        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]);
        var version = Version(graph);

        await world.Service.EnsureAsync(version, null, CancellationToken.None);
        var loadsBefore = world.WorkflowStore.Loads;
        await world.Service.EnsureAsync(version, null, CancellationToken.None);

        world.Store.Saves.Should().Be(1, "the second call is a saved memo hit, so no verdict-store call happens");
        (world.WorkflowStore.Loads - loadsBefore).Should().Be(1, "the workflow store is still read on re-resolution, even on a saved hit");
    }

    [Fact]
    public async Task A_graph_with_no_node_resolved_elsewhere_never_reads_the_workflow_store()
    {
        var world = new ServiceWorld();
        var version = Version(NoopGraph());

        await world.Service.EnsureAsync(version, null, CancellationToken.None);
        await world.Service.EnsureAsync(version, null, CancellationToken.None);

        world.Store.Saves.Should().Be(1, "the second call is a saved memo hit");
        world.WorkflowStore.Loads.Should().Be(0, "no node asks for a child, on the miss or on the hit");
    }

    [Fact]
    public async Task A_reference_fed_by_a_wire_is_never_asked_of_any_resolver()
    {
        var spy = new SpyResolver("Host.RunsWorkflow");
        var blocks = new MovableCatalogue()
            .Add(Shape("emitter", outputs: [("Out", PortValueType.String)]))
            .Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var store = new RecordingStore();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, resolvers: [spy]);

        var graph = Graph(
            [StartNode(), Node("emit", "emitter"), Node("call", "Host.RunsWorkflow")],
            [Edge("e1", "start", "emit"), Edge("e2", "emit", "call", ("Out", "Callee"))]);
        var version = Version(graph);

        var verdict = await service.EnsureAsync(version, null, CancellationToken.None);

        spy.CallCount.Should().Be(0, "a reference fed by a wire is refused outright, so asking a resolver would be wasted I/O");
        verdict.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void A_host_resolver_claiming_the_librarys_reserved_block_type_is_refused_at_construction()
    {
        var act = () => CompilationScopes.Service(
            new MovableCatalogue(), new FakeWorkflowStore(), verdictStore: null,
            resolvers: [new SpyResolver(WorkflowGraphHelpers.SubWorkflowBlockTypeId)]);

        act.Should().Throw<ArgumentException>().WithMessage($"*{WorkflowGraphHelpers.SubWorkflowBlockTypeId}*");
    }

    [Fact]
    public void A_host_resolver_claiming_the_reserved_id_under_different_casing_is_refused_too()
    {
        var act = () => CompilationScopes.Service(
            new MovableCatalogue(), new FakeWorkflowStore(), verdictStore: null,
            resolvers: [new SpyResolver(WorkflowGraphHelpers.SubWorkflowBlockTypeId.ToUpperInvariant())]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Two_resolvers_for_one_block_type_are_refused_at_construction()
    {
        var act = () => CompilationScopes.Service(
            new MovableCatalogue(), new FakeWorkflowStore(), verdictStore: null,
            resolvers: [new SpyResolver("Host.Thing"), new SpyResolver("Host.Thing")]);

        act.Should().Throw<ArgumentException>().WithMessage("*Host.Thing*");
    }

    [Fact]
    public async Task A_resolver_fault_propagates_and_is_never_memoised_or_saved()
    {
        var blocks = new MovableCatalogue()
            .Add(HostRunsWorkflowShape("Host.Faulty"));
        var store = new RecordingStore();
        var service = CompilationScopes.Service(
            blocks, new FakeWorkflowStore(), store, resolvers: [new ThrowingResolver("Host.Faulty")]);

        var graph = Graph(
            [StartNode(), Node("call", "Host.Faulty", new { Callee = "x" })], [Edge("e1", "start", "call")]);
        var version = Version(graph);

        var act = () => service.EnsureAsync(version, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Saves.Should().Be(0, "a fault is never a verdict, so it is never saved");
        store.Finds.Should().Be(0, "the fault happens resolving the environment, before the store is ever consulted");
    }

    [Fact]
    public async Task Concurrent_calls_for_the_same_version_never_produce_a_torn_verdict()
    {
        var world = new ServiceWorld();
        var version = Version(NoopGraph());

        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => world.Service.EnsureAsync(version, null, CancellationToken.None)));

        results.Should().AllSatisfy(v => v.DependencyHash.Should().Be(results[0].DependencyHash));
        results.Should().AllSatisfy(v => v.IsValid.Should().BeTrue());
    }

    [Fact]
    public void Null_and_empty_parameters_json_hash_differently_in_the_definition_digest()
    {
        var withNull = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{}", null, null, Guid.NewGuid(), T0);
        var withEmpty = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{}", "[]", "[]", Guid.NewGuid(), T0);

        WorkflowCompilationService.ComputeDefinitionDigest(withNull)
            .Should().NotBe(WorkflowCompilationService.ComputeDefinitionDigest(withEmpty));
    }

    [Fact]
    public void The_same_version_content_hashes_the_same_digest()
    {
        var a = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{\"nodes\":[],\"edges\":[]}", "[]", "[]", Guid.NewGuid(), T0);
        var b = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{\"nodes\":[],\"edges\":[]}", "[]", "[]", Guid.NewGuid(), T0);

        WorkflowCompilationService.ComputeDefinitionDigest(a).Should().Be(WorkflowCompilationService.ComputeDefinitionDigest(b));
    }

    [Fact]
    public async Task A_differently_cased_SubWorkflow_block_type_id_still_resolves_through_the_library()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var childId = Guid.NewGuid();
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), T0);
        child.SetCurrentVersion(new WorkflowVersion(Guid.NewGuid(), childId, 1, "{}", null, "[]", Guid.NewGuid(), T0), T0);
        world.WorkflowStore.AddPublished(child);

        var graph = Graph(
            [StartNode(), Node("call", "subWORKFLOW", new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]);

        var verdict = await world.Service.EnsureAsync(Version(graph), null, CancellationToken.None);

        verdict.IsValid.Should().BeTrue("a different case of the reserved block type id still names the same block");
    }

    [Fact]
    public async Task A_host_resolver_is_matched_case_insensitively_against_the_node_it_answers_for()
    {
        var spy = new SpyResolver("Host.RunsWorkflow");
        var blocks = new MovableCatalogue()
            .Add(HostRunsWorkflowShape("host.RUNSWORKFLOW"));
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), verdictStore: null, resolvers: [spy]);

        var graph = Graph(
            [StartNode(), Node("call", "host.RUNSWORKFLOW", new { Callee = "x" })], [Edge("e1", "start", "call")]);

        await service.EnsureAsync(Version(graph), null, CancellationToken.None);

        spy.CallCount.Should().Be(1, "the resolver's own declared id and the node's differ only in case");
    }

    [Fact]
    public async Task Under_a_pin_manifest_a_child_it_does_not_list_is_refused()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var childId = Guid.NewGuid();
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), T0);
        child.SetCurrentVersion(new WorkflowVersion(Guid.NewGuid(), childId, 1, "{}", null, "[]", Guid.NewGuid(), T0), T0);
        world.WorkflowStore.AddPublished(child);

        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]);
        var pinManifest = new Dictionary<Guid, Guid> { [Guid.NewGuid()] = Guid.NewGuid() };

        var verdict = await world.Service.EnsureAsync(Version(graph), pinManifest, CancellationToken.None);

        verdict.IsValid.Should().BeFalse("the manifest names nothing for this child, so it is never answered from its current version");
        verdict.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved);
    }

    [Fact]
    public async Task Under_a_pin_manifest_the_pinned_version_is_used_not_the_current_one()
    {
        var world = new ServiceWorld(extraShapes: [SubWorkflowShape()]);
        var childId = Guid.NewGuid();
        var currentVersion = new WorkflowVersion(Guid.NewGuid(), childId, 1, "{}", null, "[]", Guid.NewGuid(), T0);
        var pinnedVersion = new WorkflowVersion(
            Guid.NewGuid(), childId, 2, "{}", null,
            JsonSerializer.Serialize(new[] { new { name = "Result", type = "String" } }), Guid.NewGuid(), T0);
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), T0);
        child.SetCurrentVersion(currentVersion, T0);
        world.WorkflowStore.AddPublished(child);
        world.WorkflowStore.AddVersion(pinnedVersion);

        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]);
        var version = Version(graph);

        var unpinned = await world.Service.EnsureAsync(version, null, CancellationToken.None);
        var pinned = await world.Service.EnsureAsync(
            version, new Dictionary<Guid, Guid> { [childId] = pinnedVersion.Id }, CancellationToken.None);

        pinned.DependencyHash.Should().NotBe(unpinned.DependencyHash,
            "the pinned version's declared outputs differ from the current version's, and the manifest picks the pinned one");
    }

    [Fact]
    public async Task A_resolver_is_handed_the_configured_reference_value()
    {
        var spy = new SpyResolver("Host.RunsWorkflow");
        var blocks = new MovableCatalogue()
            .Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), verdictStore: null, resolvers: [spy]);

        var graph = Graph(
            [StartNode(), Node("call", "Host.RunsWorkflow", new { Callee = "configured-value" })],
            [Edge("e1", "start", "call")]);

        await service.EnsureAsync(Version(graph), null, CancellationToken.None);

        spy.LastRequest.Should().NotBeNull();
        spy.LastRequest!.EffectiveReference.Should().Be("configured-value");
    }

    [Fact]
    public async Task A_resolver_is_never_asked_when_the_reference_input_is_mapped()
    {
        var spy = new SpyResolver("Host.RunsWorkflow");
        var blocks = new MovableCatalogue()
            .Add(Shape("emitter", outputs: [("Out", PortValueType.String)]))
            .Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), verdictStore: null, resolvers: [spy]);

        var graph = Graph(
            [StartNode(), Node("emit", "emitter"), Node("call", "Host.RunsWorkflow", new { Callee = "configured-value" })],
            [Edge("e1", "start", "emit"), LiteralEdge("e2", "emit", "call", "Callee", "literal-value")]);

        var verdict = await service.EnsureAsync(Version(graph), null, CancellationToken.None);

        spy.LastRequest.Should().BeNull();
        verdict.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    [InlineData("")]
    public async Task A_resolver_is_never_asked_when_the_configured_reference_names_no_callee(object? configured)
    {
        var spy = new SpyResolver("Host.RunsWorkflow");
        var blocks = new MovableCatalogue()
            .Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), verdictStore: null, resolvers: [spy]);
        var call = configured is null
            ? Node("call", "Host.RunsWorkflow")
            : Node("call", "Host.RunsWorkflow", new { Callee = configured });

        var verdict = await service.EnsureAsync(Version(Graph([StartNode(), call], [Edge("e1", "start", "call")])), null, CancellationToken.None);

        spy.CallCount.Should().Be(0);
        verdict.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_store_read_fault_is_logged_and_treated_as_a_miss()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore { NextFindFault = () => new InvalidOperationException("read broke") };
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        var version = Version(NoopGraph());

        var verdict = await service.EnsureAsync(version, null, CancellationToken.None);

        verdict.IsValid.Should().BeTrue("a store read fault is swallowed; the service compiles fresh instead");
        store.Saves.Should().Be(1, "the freshly computed verdict is still saved");
    }

    [Fact]
    public async Task A_store_read_fault_is_actually_logged()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore { NextFindFault = () => new InvalidOperationException("read broke") };
        var logger = new CapturingLogger();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, logger: logger);

        await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        logger.Messages.Should().Contain(m => m.Contains("read broke") || m.Contains("failed"));
    }

    [Fact]
    public async Task A_store_write_fault_is_actually_logged()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore { NextSaveFault = () => new InvalidOperationException("write broke") };
        var logger = new CapturingLogger();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, logger: logger);

        await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        logger.Messages.Should().Contain(m => m.Contains("write broke") || m.Contains("failed"));
    }

    [Fact]
    public async Task A_store_that_declines_is_logged_once_without_a_stack_and_its_save_is_still_retried()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new DecliningStore();
        var logger = new CapturingLogger();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, logger: logger);

        for (var i = 0; i < 3; i++)
            (await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None)).IsValid.Should().BeTrue();

        store.Finds.Should().Be(3, "each call is a new version, so each misses the memo and asks the store");
        store.Saves.Should().Be(3);
        logger.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Warning, (Exception?)null));
        logger.Messages.Single().Should().Contain("declined").And.Contain("the store's own words");
    }

    [Fact]
    public async Task A_declined_save_is_retried_on_every_unsaved_hit_and_logged_only_the_first_time()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new DecliningStore { DeclinesFinds = false };
        var logger = new CapturingLogger();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, logger: logger);
        var version = Version(NoopGraph());

        for (var i = 0; i < 4; i++)
            await service.EnsureAsync(version, null, CancellationToken.None);

        store.Finds.Should().Be(1);
        store.Saves.Should().Be(4, "an unsaved hit retries its save, and this store declines it each time");
        logger.Entries.Should().ContainSingle().Which.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_declined_find_is_logged_once_without_a_stack_and_answered_as_a_miss()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new DecliningStore { DeclinesSaves = false };
        var logger = new CapturingLogger();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, logger: logger);

        for (var i = 0; i < 3; i++)
            (await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None)).IsValid.Should().BeTrue();

        store.Finds.Should().Be(3);
        store.Saves.Should().Be(3, "each declined find is a miss, so each call compiles and saves");
        logger.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Warning, (Exception?)null));
        logger.Messages.Single().Should().Contain("declined").And.Contain("the store's own words");
    }

    [Fact]
    public async Task Any_other_store_fault_is_still_logged_on_every_call_with_its_exception()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore();
        Func<Exception> persistentFault = null!;
        persistentFault = () => { store.NextSaveFault = persistentFault; return new InvalidOperationException("write broke"); };
        store.NextSaveFault = persistentFault;
        var logger = new CapturingLogger();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, logger: logger);
        var version = Version(NoopGraph());

        for (var i = 0; i < 3; i++)
            await service.EnsureAsync(version, null, CancellationToken.None);

        logger.Entries.Should().HaveCount(3).And.OnlyContain(entry => entry.Error is InvalidOperationException);
    }

    [Fact]
    public async Task Two_services_differing_only_in_the_widening_table_compile_different_dependency_hashes()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var plain = CompilationScopes.Service(
            blocks, new FakeWorkflowStore(), verdictStore: null,
            options: new CompilationOptions());
        var widened = CompilationScopes.Service(
            blocks, new FakeWorkflowStore(), verdictStore: null,
            options: new CompilationOptions { TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Widget", typeof(string))]) });
        var version = Version(NoopGraph());

        var plainVerdict = await plain.EnsureAsync(version, null, CancellationToken.None);
        var widenedVerdict = await widened.EnsureAsync(version, null, CancellationToken.None);

        plainVerdict.DependencyHash.Should().NotBe(widenedVerdict.DependencyHash);
    }

    [Fact]
    public async Task Two_services_differing_only_in_pinned_namespace_checks_compile_different_dependency_hashes()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var unpinned = CompilationScopes.Service(
            blocks, new FakeWorkflowStore(), verdictStore: null, options: new CompilationOptions());
        var pinned = CompilationScopes.Service(
            blocks, new FakeWorkflowStore(), verdictStore: null,
            options: new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new NsCheck("device", NamespacePathCheck.Yes))] });
        var version = Version(NoopGraph());

        var unpinnedVerdict = await unpinned.EnsureAsync(version, null, CancellationToken.None);
        var pinnedVerdict = await pinned.EnsureAsync(version, null, CancellationToken.None);

        unpinnedVerdict.DependencyHash.Should().NotBe(pinnedVerdict.DependencyHash);
    }

    [Fact]
    public async Task A_shared_namespace_registration_reaches_compilation_fingerprinting()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var unregistered = CompilationScopes.Service(blocks, new FakeWorkflowStore(), verdictStore: null);
        var registered = CompilationScopes.Service(
            blocks, new FakeWorkflowStore(), verdictStore: null,
            options: new CompilationOptions
            {
                NamespaceRegistrations =
                [
                    new WorkflowExpressionNamespace("host", 2, new NsCheck("host", NamespacePathCheck.Yes),
                        (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new FingerprintResolver()))
                ]
            });
        var version = Version(NoopGraph());

        var without = await unregistered.EnsureAsync(version, null, CancellationToken.None);
        var with = await registered.EnsureAsync(version, null, CancellationToken.None);

        with.DependencyHash.Should().NotBe(without.DependencyHash,
            "the compiler fingerprints the registration's static check namespace");
    }

    private sealed class NsCheck(string ns, NamespacePathCheck answer) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => ns;
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(answer, "String");
    }

    private sealed class FingerprintResolver : IExpressionNamespaceResolver
    {
        public string Namespace => "host";
        public ResolveResult Resolve(string[] segments) => ResolveResult.Failed();
    }

    [Fact]
    public async Task A_store_write_fault_still_answers_with_the_verdict_and_the_next_call_retries_only_the_save()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore { NextSaveFault = () => new InvalidOperationException("write broke") };
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        var version = Version(NoopGraph());

        var first = await service.EnsureAsync(version, null, CancellationToken.None);
        var second = await service.EnsureAsync(version, null, CancellationToken.None);

        first.IsValid.Should().BeTrue("the verdict is still answered even though saving it failed");
        second.IsValid.Should().BeTrue();
        store.Saves.Should().Be(2, "the first save faulted (memoised unsaved) and the second call retries just the save");
        store.Finds.Should().Be(1, "decision 4: the second call is an unsaved memo HIT, never a fresh miss that finds or recompiles");
    }

    [Fact]
    public async Task A_persistent_save_fault_never_recompiles_after_the_first_call()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore();
        Func<Exception> persistentFault = null!;
        persistentFault = () => { store.NextSaveFault = persistentFault; return new InvalidOperationException("write broke"); };
        store.NextSaveFault = persistentFault;
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        var version = Version(NoopGraph());

        for (var i = 0; i < 3; i++)
        {
            var verdict = await service.EnsureAsync(version, null, CancellationToken.None);
            verdict.IsValid.Should().BeTrue();
        }

        store.Finds.Should().Be(1, "only the very first call ever misses the memo; every later call is an unsaved hit");
        store.Saves.Should().Be(3, "every call still retries the save — that part of the doc comment's promise holds");
    }

    [Fact]
    public async Task A_store_read_cancellation_propagates_when_the_callers_own_token_actually_fired()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore { NextFindFault = () => new OperationCanceledException() };
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => service.EnsureAsync(Version(NoopGraph()), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_store_timeout_thrown_as_an_OperationCanceledException_with_a_live_token_is_an_ordinary_fault()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var findStore = new FaultyStore { NextFindFault = () => new TaskCanceledException("store timed out") };
        var findService = CompilationScopes.Service(blocks, new FakeWorkflowStore(), findStore);

        var findVerdict = await findService.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        findVerdict.IsValid.Should().BeTrue("a live-token cancellation exception from a read is an ordinary fault, not a real cancellation");
        findStore.Saves.Should().Be(1, "the freshly compiled verdict is still saved");

        var saveStore = new FaultyStore { NextSaveFault = () => new TaskCanceledException("store timed out") };
        var saveService = CompilationScopes.Service(blocks, new FakeWorkflowStore(), saveStore);

        var saveVerdict = await saveService.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        saveVerdict.IsValid.Should().BeTrue("the verdict is still answered even though the save's fault looked like a cancellation");
    }

    [Fact]
    public async Task A_store_save_cancellation_propagates_when_the_callers_own_token_actually_fired()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new FaultyStore { NextSaveFault = () => new OperationCanceledException() };
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => service.EnsureAsync(Version(NoopGraph()), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_draft_mutated_while_a_resolver_is_still_being_awaited_is_compiled_as_it_was_when_the_call_started()
    {
        var childId = Guid.NewGuid();
        var blocks = new MovableCatalogue()
            .Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var version = Version(Graph(
            [StartNode(), Node("call", "Host.RunsWorkflow", new { Callee = childId.ToString() })],
            [Edge("e1", "start", "call")]));
        var originalDigest = WorkflowCompilationService.ComputeDefinitionDigest(version);

        var mutatingResolver = new MutatingResolver("Host.RunsWorkflow", () =>
            version.UpdateDefinition(
                Stored(NoopGraph()), null, null));
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), verdictStore: null, resolvers: [mutatingResolver]);

        var verdict = await service.EnsureAsync(version, null, CancellationToken.None);

        verdict.DefinitionDigest.Should().Be(originalDigest,
            "the definition read at the top of the call, not whatever the draft holds by the time it returns");
    }

    [Fact]
    public async Task A_drafts_parameters_mutated_while_a_resolver_awaits_do_not_change_what_is_compiled()
    {
        var childId = Guid.NewGuid();
        var blocks = new MovableCatalogue()
            .Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var validInputs = JsonSerializer.Serialize(new[] { new { name = "X", type = "String" } });
        var version = Version(
            Graph([StartNode(), Node("call", "Host.RunsWorkflow", new { Callee = childId.ToString() })], [Edge("e1", "start", "call")]),
            inputs: validInputs);

        var mutatingResolver = new MutatingResolver("Host.RunsWorkflow", () =>
            version.UpdateDefinition(version.DefinitionJson, "not json", null));
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), verdictStore: null, resolvers: [mutatingResolver]);

        var verdict = await service.EnsureAsync(version, null, CancellationToken.None);

        verdict.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.SignatureUnreadable,
            "the parameters read at the top of the call were valid; the mutated ones must never be re-read");
    }

    [Fact]
    public async Task Concurrent_calls_for_a_never_before_seen_version_genuinely_overlap_in_the_store()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new GatedStore();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        var version = Version(NoopGraph());

        var calls = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.EnsureAsync(version, null, CancellationToken.None)));
        for (var i = 0; i < 50 && Volatile.Read(ref store.ConcurrentFinds) < 8; i++)
            await Task.Delay(10);
        store.Release();
        var results = await calls;

        store.MaxConcurrentFinds.Should().BeGreaterThan(1, "the calls must have genuinely overlapped inside the store");
        results.Should().AllSatisfy(v => v.IsValid.Should().BeTrue());
    }

    [Fact]
    public async Task An_unsaved_retry_that_finishes_after_a_newer_entry_was_installed_drops_its_upgrade()
    {
        var store = new ScriptedStore();
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = call => call switch
        {
            1 => throw new InvalidOperationException("D1's first save fails, so D1 is memoised unsaved"),
            2 => Enter(retryEntered, releaseRetry),
            3 => throw new InvalidOperationException("D2's save fails, so D2 is memoised unsaved"),
            _ => Task.CompletedTask,
        };
        var (service, v1, v2) = TwoDraftsOfOneVersion(store);

        await service.EnsureAsync(v1, null, CancellationToken.None);
        var retry = service.EnsureAsync(v1, null, CancellationToken.None);
        await retryEntered.Task.WaitAsync(Deadline);
        await service.EnsureAsync(v2, null, CancellationToken.None);
        releaseRetry.SetResult();
        await retry.WaitAsync(Deadline);
        var findsBefore = store.Finds;
        var savesBefore = store.Saves;

        await service.EnsureAsync(v2, null, CancellationToken.None);

        store.Finds.Should().Be(findsBefore, "D2's unsaved entry is still installed, so this call is a hit");
        store.Saves.Should().Be(savesBefore + 1, "and a hit on an unsaved entry retries its save");
    }

    [Fact]
    public async Task A_compile_whose_save_finishes_after_a_newer_entry_was_installed_drops_its_install()
    {
        var store = new ScriptedStore();
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = call => call == 1 ? Enter(saveEntered, releaseSave) : Task.CompletedTask;
        var (service, v1, v2) = TwoDraftsOfOneVersion(store);

        var first = service.EnsureAsync(v1, null, CancellationToken.None);
        await saveEntered.Task.WaitAsync(Deadline);
        await service.EnsureAsync(v2, null, CancellationToken.None);
        releaseSave.SetResult();
        (await first.WaitAsync(Deadline)).DefinitionDigest.Should().Be(WorkflowCompilationService.ComputeDefinitionDigest(v1));
        var findsBefore = store.Finds;

        await service.EnsureAsync(v2, null, CancellationToken.None);

        store.Finds.Should().Be(findsBefore, "D1's late install must not have displaced D2's entry");
    }

    [Fact]
    public async Task A_store_hit_that_finishes_after_a_newer_entry_was_installed_drops_its_install()
    {
        var store = new ScriptedStore();
        var (seeding, v1, v2) = TwoDraftsOfOneVersion(store);
        await seeding.EnsureAsync(v1, null, CancellationToken.None);
        var findEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedFind = store.Finds + 1;
        store.BeforeFind = call => call == gatedFind ? Enter(findEntered, releaseFind) : Task.CompletedTask;
        var service = CompilationScopes.Service(TwoDraftsCatalogue(), new FakeWorkflowStore(), store);

        var first = service.EnsureAsync(v1, null, CancellationToken.None);
        await findEntered.Task.WaitAsync(Deadline);
        await service.EnsureAsync(v2, null, CancellationToken.None);
        releaseFind.SetResult();
        (await first.WaitAsync(Deadline)).DefinitionDigest.Should().Be(WorkflowCompilationService.ComputeDefinitionDigest(v1));
        var findsBefore = store.Finds;

        await service.EnsureAsync(v2, null, CancellationToken.None);

        store.Finds.Should().Be(findsBefore, "D1's late store hit must not have displaced D2's entry");
    }

    [Fact]
    public async Task A_call_that_began_before_the_generation_moved_installs_into_the_old_memo_only()
    {
        var store = new ScriptedStore();
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = call => call == 1 ? Enter(saveEntered, releaseSave) : Task.CompletedTask;
        var blocks = TwoDraftsCatalogue();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        var version = Version(NoopGraph());

        var first = service.EnsureAsync(version, null, CancellationToken.None);
        await saveEntered.Task.WaitAsync(Deadline);
        blocks.Move("gen-2", Shape("unrelated.block"));
        releaseSave.SetResult();
        var compiled = await first.WaitAsync(Deadline);
        var findsBefore = store.Finds;

        var next = await service.EnsureAsync(version, null, CancellationToken.None);

        next.DependencyHash.Should().Be(compiled.DependencyHash, "the moved generation added nothing this graph uses");
        store.Finds.Should().Be(findsBefore + 1, "the new generation's memo is empty, so this call reads the store");
    }

    [Fact]
    public async Task A_store_hit_that_began_before_the_generation_moved_installs_into_the_old_memo_only()
    {
        var store = new ScriptedStore();
        var blocks = TwoDraftsCatalogue();
        var version = Version(NoopGraph());
        await CompilationScopes.Service(blocks, new FakeWorkflowStore(), store)
            .EnsureAsync(version, null, CancellationToken.None);

        var findEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFind = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedFind = store.Finds + 1;
        store.BeforeFind = call => call == gatedFind ? Enter(findEntered, releaseFind) : Task.CompletedTask;
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);

        var first = service.EnsureAsync(version, null, CancellationToken.None);
        await findEntered.Task.WaitAsync(Deadline);
        blocks.Move("gen-2", Shape("unrelated.block"));
        releaseFind.SetResult();
        await first.WaitAsync(Deadline);
        var findsBefore = store.Finds;

        var next = await service.EnsureAsync(version, null, CancellationToken.None);

        store.Finds.Should().Be(findsBefore + 1, "the new generation's memo is empty, so the store-hit install must not have leaked into it");
    }

    [Fact]
    public async Task An_unsaved_retry_that_began_before_the_generation_moved_installs_into_the_old_memo_only()
    {
        var store = new ScriptedStore
        {
            BeforeSave = call => call == 1 ? throw new InvalidOperationException("the first save fails") : Task.CompletedTask,
        };
        var blocks = TwoDraftsCatalogue();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        var version = Version(NoopGraph());
        await service.EnsureAsync(version, null, CancellationToken.None);

        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = call => call == 2 ? Enter(saveEntered, releaseSave) : Task.CompletedTask;

        var retry = service.EnsureAsync(version, null, CancellationToken.None);
        await saveEntered.Task.WaitAsync(Deadline);
        blocks.Move("gen-2", Shape("unrelated.block"));
        releaseSave.SetResult();
        await retry.WaitAsync(Deadline);
        var findsBefore = store.Finds;

        var next = await service.EnsureAsync(version, null, CancellationToken.None);

        store.Finds.Should().Be(findsBefore + 1, "the new generation's memo is empty, so the retry's AsSaved install must not have leaked into it");
    }

    [Fact]
    public async Task A_call_gated_inside_the_resolver_sees_a_fresh_entry_installed_meanwhile_without_an_extra_store_call()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new FirstCallGatedResolver("Host.RunsWorkflow", entered, release);
        var blocks = new MovableCatalogue()
            .Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var store = new RecordingStore();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store, resolvers: [resolver]);
        var graph = Graph(
            [StartNode(), Node("call", "Host.RunsWorkflow", new { Callee = "configured-value" })],
            [Edge("e1", "start", "call")]);
        var version = Version(graph);

        var first = service.EnsureAsync(version, null, CancellationToken.None);
        await entered.Task.WaitAsync(Deadline);
        var second = await service.EnsureAsync(version, null, CancellationToken.None);
        release.SetResult();
        await first.WaitAsync(Deadline);

        second.IsValid.Should().BeTrue();
        store.Finds.Should().Be(1, "the first call's late memo read sees the second call's fresh install directly, with no redundant store visit");
    }

    [Fact]
    public async Task The_memo_evicts_past_its_capacity_instead_of_growing_without_bound()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var store = new RecordingStore();
        var service = CompilationScopes.Service(blocks, new FakeWorkflowStore(), store);
        var first = Version(NoopGraph());
        await service.EnsureAsync(first, null, CancellationToken.None);

        for (var i = 0; i < 10_050; i++)
            await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        var findsBefore = store.Finds;
        await service.EnsureAsync(first, null, CancellationToken.None);

        store.Finds.Should().BeGreaterThan(findsBefore,
            "past capacity the memo evicts, so the first version's entry is eventually gone and this call must consult the store again");
    }

    [Fact]
    public async Task Ten_thousand_versions_are_all_answered_from_the_memo()
    {
        var world = new ServiceWorld();
        var versions = Enumerable.Range(0, 10_000).Select(_ => Version(NoopGraph())).ToArray();
        foreach (var version in versions)
            await world.Service.EnsureAsync(version, null, CancellationToken.None);
        var findsBefore = world.Store.Finds;

        foreach (var version in versions)
            await world.Service.EnsureAsync(version, null, CancellationToken.None);

        world.Store.Finds.Should().Be(findsBefore, "no version was evicted, so every second call is a memo hit");
    }

    [Fact]
    public async Task A_store_hit_for_an_edited_draft_replaces_the_previous_drafts_entry()
    {
        var world = new ServiceWorld();
        var id = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var v1 = Version(Graph([StartNode(), Node("a", "noop")], [Edge("e1", "start", "a")]), id, workflowId);
        var v2 = Version(Graph([StartNode(), Node("b", "noop")], [Edge("e1", "start", "b")]), id, workflowId);
        await world.Service.EnsureAsync(v1, null, CancellationToken.None);
        await world.Service.EnsureAsync(v2, null, CancellationToken.None);
        world.Rebuild();

        await world.Service.EnsureAsync(v1, null, CancellationToken.None);
        await world.Service.EnsureAsync(v2, null, CancellationToken.None);
        var findsBefore = world.Store.Finds;
        await world.Service.EnsureAsync(v2, null, CancellationToken.None);

        world.Store.Finds.Should().Be(findsBefore, "v2's store hit replaced v1's entry, so this call is a memo hit");
    }

    [Fact]
    public async Task A_successful_retry_marks_the_entry_saved_so_later_hits_do_not_write()
    {
        var store = new ScriptedStore
        {
            BeforeSave = call => call == 1 ? throw new InvalidOperationException("the first save fails") : Task.CompletedTask,
        };
        var (service, v1, _) = TwoDraftsOfOneVersion(store);

        for (var i = 0; i < 4; i++)
            (await service.EnsureAsync(v1, null, CancellationToken.None)).IsValid.Should().BeTrue();

        store.Saves.Should().Be(2, "the failed save, then the one retry that succeeded; later hits are saved hits");
        store.Finds.Should().Be(1);
    }

    [Fact]
    public async Task A_store_hit_is_remembered_as_saved_so_the_next_call_neither_finds_nor_saves()
    {
        var store = new ScriptedStore();
        var (seeding, v1, _) = TwoDraftsOfOneVersion(store);
        await seeding.EnsureAsync(v1, null, CancellationToken.None);
        var service = CompilationScopes.Service(TwoDraftsCatalogue(), new FakeWorkflowStore(), store);
        var (findsBefore, savesBefore) = (store.Finds, store.Saves);

        await service.EnsureAsync(v1, null, CancellationToken.None);
        await service.EnsureAsync(v1, null, CancellationToken.None);

        store.Finds.Should().Be(findsBefore + 1, "only the first call reads the store");
        store.Saves.Should().Be(savesBefore, "a row the store already holds is never written back");
    }

    [Fact]
    public async Task A_run_scope_that_fails_while_the_call_reads_it_is_still_disposed()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop"));
        var scopes = new StoreThrowingScopes();
        var service = new WorkflowCompilationService(blocks, scopes);

        var ensuring = () => service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        (await ensuring.Should().ThrowAsync<InvalidOperationException>()).WithMessage("the store cannot be built");
        scopes.Opened.Should().Be(2, "the constructor opens one and the call one");
        scopes.Disposed.Should().Be(2, "the call's scope is disposed although reading its store threw");
    }

    [Fact]
    public async Task A_miss_hands_the_callers_own_token_to_the_find_and_to_the_save()
    {
        var store = new ScriptedStore();
        var (service, v1, _) = TwoDraftsOfOneVersion(store);
        using var caller = new CancellationTokenSource();

        await service.EnsureAsync(v1, null, caller.Token);

        store.FindTokenQueue.Should().Equal(caller.Token);
        store.SaveTokens.Should().Equal(caller.Token);
    }

    [Fact]
    public async Task An_unsaved_retry_is_handed_the_callers_own_token()
    {
        var store = new ScriptedStore
        {
            BeforeSave = call => call == 1 ? throw new InvalidOperationException("the first save fails") : Task.CompletedTask,
        };
        var (service, v1, _) = TwoDraftsOfOneVersion(store);
        using var caller = new CancellationTokenSource();

        await service.EnsureAsync(v1, null, CancellationToken.None);
        await service.EnsureAsync(v1, null, caller.Token);

        store.SaveTokens.Should().HaveCount(2);
        store.SaveTokens[1].Should().Be(caller.Token);
    }

    [Fact]
    public async Task An_unsaved_retry_whose_caller_cancelled_propagates_the_cancellation()
    {
        var store = new ScriptedStore
        {
            BeforeSave = call => call == 1 ? throw new InvalidOperationException("the first save fails") : Task.CompletedTask,
        };
        var (service, v1, _) = TwoDraftsOfOneVersion(store);
        await service.EnsureAsync(v1, null, CancellationToken.None);
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        var act = () => service.EnsureAsync(v1, null, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Saves.Should().Be(2, "the retry reached the store and the store saw the caller's cancelled token");
    }

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static async Task Enter(TaskCompletionSource entered, TaskCompletionSource release)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(Deadline);
    }

    private static MovableCatalogue TwoDraftsCatalogue() =>
        new MovableCatalogue().Add(Shape("noop"));

    private static (WorkflowCompilationService Service, WorkflowVersion V1, WorkflowVersion V2) TwoDraftsOfOneVersion(
        ScriptedStore store)
    {
        var id = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var v1 = Version(Graph([StartNode(), Node("a", "noop")], [Edge("e1", "start", "a")]), id, workflowId);
        var v2 = Version(Graph([StartNode(), Node("b", "noop")], [Edge("e1", "start", "b")]), id, workflowId);
        return (CompilationScopes.Service(TwoDraftsCatalogue(), new FakeWorkflowStore(), store), v1, v2);
    }

    [Fact]
    public async Task A_DI_registration_with_no_verdict_store_registered_still_resolves_and_compiles()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowBlocks>(new MovableCatalogue().Add(Shape("noop")));
        services.AddSingleton<IWorkflowRunScopeFactory>(new CompilationScopes(new FakeWorkflowStore(), verdicts: null, [new SpyResolver("Host.Unused")]));
        services.AddSingleton<WorkflowCompilationService>();
        using var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<WorkflowCompilationService>();
        var verdict = await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);

        verdict.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_call_opens_one_run_scope_only_when_it_reads_from_one_and_disposes_it_before_it_returns_even_when_it_throws()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop")).Add(HostRunsWorkflowShape("Host.Faulty"));
        var scopes = new CompilationScopes(new FakeWorkflowStore(), new RecordingStore(), [new ThrowingResolver("Host.Faulty")]);
        var service = new WorkflowCompilationService(blocks, scopes);
        var noop = Version(NoopGraph());
        var faulting = Version(Graph(
            [StartNode(), Node("call", "Host.Faulty", new { Callee = "x" })], [Edge("e1", "start", "call")]));
        scopes.Opened.Should().Be(1, "the constructor opens one to check the resolvers");

        await service.EnsureAsync(noop, null, CancellationToken.None);
        await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);
        var afterTwoMisses = scopes.Opened;
        await service.EnsureAsync(noop, null, CancellationToken.None);
        var afterASavedHit = scopes.Opened;
        var act = () => service.EnsureAsync(faulting, null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        afterTwoMisses.Should().Be(3, "a miss reads the verdict store");
        afterASavedHit.Should().Be(3, "a saved hit over no node resolved elsewhere reads nothing from a scope");
        scopes.Opened.Should().Be(4, "a resolver is read from the scope");
        scopes.Disposed.Should().Be(4);
    }

    [Fact]
    public async Task The_verdict_store_and_the_resolvers_are_the_ones_each_calls_scope_answers()
    {
        var blocks = new MovableCatalogue().Add(Shape("noop")).Add(HostRunsWorkflowShape("Host.RunsWorkflow"));
        var first = new RecordingStore();
        var second = new RecordingStore();
        var spy = new SpyResolver("Host.RunsWorkflow");
        var scopes = new SwitchingScopes(new CompilationScopes(new FakeWorkflowStore(), first));
        var service = new WorkflowCompilationService(blocks, scopes);

        await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);
        scopes.Inner = new CompilationScopes(new FakeWorkflowStore(), second, [spy]);
        await service.EnsureAsync(Version(Graph(
            [StartNode(), Node("call", "Host.RunsWorkflow", new { Callee = "x" })], [Edge("e1", "start", "call")])), null, CancellationToken.None);

        (first.Saves, second.Saves).Should().Be((1, 1));
        spy.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_verdict_is_stamped_with_the_clock_the_service_was_given()
    {
        var clock = new FixedClock(T0.AddDays(3));
        var service = CompilationScopes.Service(DefaultCatalogue(), new FakeWorkflowStore(), timeProvider: clock);

        var valid = await service.EnsureAsync(Version(NoopGraph()), null, CancellationToken.None);
        var unreadable = await service.EnsureAsync(Version(NoopGraph(), inputs: "not json"), null, CancellationToken.None);

        valid.CompiledAtUtc.Should().Be(T0.AddDays(3));
        unreadable.CompiledAtUtc.Should().Be(T0.AddDays(3), "a refusal of an unreadable document is stamped by the same clock");
    }

    [Fact]
    public void A_duplicate_ids_first_node_becoming_known_moves_the_dependency_hash_even_though_bind_never_sees_it()
    {
        var graph = Graph(
            [StartNode(), Node("n", "later.block"), Node("n", "noop")],
            [Edge("e1", "start", "n")]);
        var before = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop"));
        var after = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop"), Shape("later.block"));

        var usedByBefore = WorkflowCompiler.ShapesUsedBy(graph, before);
        var usedByAfter = WorkflowCompiler.ShapesUsedBy(graph, after);

        DependencyFingerprint.Compute(usedByBefore.Values, new CompilationOptions(), NodePortEnvironment.Empty)
            .Should().NotBe(DependencyFingerprint.Compute(usedByAfter.Values, new CompilationOptions(), NodePortEnvironment.Empty),
                "the first node's block type just became known, and the hash must reflect it");
        usedByAfter.Should().ContainKey("later.block");

        var result = WorkflowCompiler.Compile(graph, after, CanvasSignature.Empty);
        result.BoundShapes.Should().NotContainKey("later.block");
        result.BoundShapes.Values.Select(s => s.BlockTypeId).Should().BeSubsetOf(usedByAfter.Values.Select(s => s.BlockTypeId));
    }



    internal static WorkflowVersion Version(
        WorkflowDefinition definition, Guid? id = null, Guid? workflowId = null, string? inputs = null, string? outputs = null) =>
        new(id ?? Guid.NewGuid(), workflowId ?? Guid.NewGuid(), 1,
            Stored(definition), inputs, outputs, Guid.NewGuid(), T0);

    private static WorkflowDefinition NoopGraph() =>
        Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]);

    internal static BlockShape SubWorkflowShape() => Shape(
        WorkflowGraphHelpers.SubWorkflowBlockTypeId,
        inputs: [("WorkflowId", PortValueType.String, true)],
        portsAreResolvedElsewhere: true,
        runsWorkflowFrom: "WorkflowId");

    private static BlockShape HostRunsWorkflowShape(string blockTypeId) => Shape(
        blockTypeId,
        inputs: [("Callee", PortValueType.String, true)],
        portsAreResolvedElsewhere: true,
        runsWorkflowFrom: "Callee");

    internal sealed class ServiceWorld
    {
        public MovableCatalogue Blocks { get; }
        public FakeWorkflowStore WorkflowStore { get; } = new();
        public RecordingStore Store { get; } = new();
        public WorkflowCompilationService Service { get; private set; }

        public ServiceWorld(IEnumerable<BlockShape>? extraShapes = null, IReadOnlyList<INodePortResolver>? resolvers = null, CompilationOptions? options = null)
        {
            Blocks = new MovableCatalogue()
                .Add(Shape("noop"));
            foreach (var shape in extraShapes ?? [])
                Blocks.Add(shape);

            Service = CompilationScopes.Service(Blocks, WorkflowStore, Store, options: options, resolvers: resolvers);
        }

        public void Rebuild(IReadOnlyList<INodePortResolver>? resolvers = null, CompilationOptions? options = null) =>
            Service = CompilationScopes.Service(Blocks, WorkflowStore, Store, options: options, resolvers: resolvers);
    }

    private static MovableCatalogue DefaultCatalogue() =>
        new MovableCatalogue().Add(Shape("noop"));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SwitchingScopes(IWorkflowRunScopeFactory inner) : IWorkflowRunScopeFactory
    {
        public IWorkflowRunScopeFactory Inner { get; set; } = inner;

        public IWorkflowRunScope CreateScope() => Inner.CreateScope();
    }

    internal sealed class MovableCatalogue : IWorkflowBlocks
    {
        private readonly Dictionary<string, IWorkflowBlockFactory> byId = new(StringComparer.OrdinalIgnoreCase);

        public string Generation { get; private set; } = "gen-1";

        public MovableCatalogue Add(BlockShape shape)
        {
            byId[shape.BlockTypeId] = FakeFactory.For(shape);
            return this;
        }

        public void Move(string generation, BlockShape added)
        {
            byId[added.BlockTypeId] = FakeFactory.For(added);
            Generation = generation;
        }

        public IWorkflowBlockFactory? Find(string blockTypeId) => byId.TryGetValue(blockTypeId, out var f) ? f : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [.. byId.Values];
    }

    internal sealed class RecordingStore : IWorkflowCompilationStore
    {
        private readonly Dictionary<(Guid VersionId, string DefinitionDigest, string DependencyHash), CompilationVerdict> rows = [];

        public int Saves { get; private set; }
        public int Finds { get; private set; }

        public Task<CompilationVerdict?> FindAsync(
            Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
        {
            Finds++;
            return Task.FromResult(rows.TryGetValue((workflowVersionId, definitionDigest, dependencyHash), out var v) ? v : null);
        }

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
        {
            Saves++;
            rows[(verdict.WorkflowVersionId, verdict.DefinitionDigest, verdict.DependencyHash)] = verdict;
            return Task.CompletedTask;
        }

        public List<(IReadOnlyCollection<CompilationKey> Current, TimeSpan OlderThan)> Deletes { get; } = [];

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken)
        {
            Deletes.Add((current, olderThan));
            return Task.FromResult(7);
        }
    }

    internal sealed class FakeWorkflowStore : IWorkflowStore
    {
        private readonly Dictionary<Guid, Workflow> workflows = [];
        private readonly Dictionary<Guid, WorkflowVersion> versionsById = [];

        public int Loads { get; private set; }

        public void AddPublished(Workflow workflow)
        {
            workflows[workflow.Id] = workflow;
            if (workflow.CurrentVersion is { } version)
                versionsById[version.Id] = version;
        }

        public void AddVersion(WorkflowVersion version) => versionsById[version.Id] = version;

        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(workflows.TryGetValue(workflowId, out var workflow) ? workflow : null);
        }

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(versionsById.TryGetValue(versionId, out var version) ? version : null);
        }

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
            Guid workflowId, CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(workflows.TryGetValue(workflowId, out var workflow) && workflow.CurrentVersion is { } version
                ? ((Guid VersionId, string DefinitionJson)?)(version.Id, version.DefinitionJson)
                : null);
        }
    }

    private sealed class SpyResolver(string blockTypeId, string? reference = "spy-ref") : INodePortResolver
    {
        public int CallCount { get; private set; }
        public NodePortResolutionRequest? LastRequest { get; private set; }
        public string BlockTypeId => blockTypeId;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(reference is null
                ? NodePortResolution.Failed("a spy answers nothing meaningful")
                : NodePortResolution.Resolved(reference, CanvasSignature.Empty));
        }
    }

    private sealed class MutatingResolver(string blockTypeId, Action mutate) : INodePortResolver
    {
        public string BlockTypeId => blockTypeId;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken)
        {
            mutate();
            return Task.FromResult(NodePortResolution.Resolved(request.EffectiveReference, CanvasSignature.Empty));
        }
    }

    private sealed class ThrowingResolver(string blockTypeId) : INodePortResolver
    {
        public string BlockTypeId => blockTypeId;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A resolver's own fault, never turned into a diagnostic.");
    }

    private sealed class FirstCallGatedResolver(
        string blockTypeId, TaskCompletionSource entered, TaskCompletionSource release) : INodePortResolver
    {
        private int calls;
        public string BlockTypeId => blockTypeId;

        public async Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Deadline);
            }

            return NodePortResolution.Resolved(request.EffectiveReference, CanvasSignature.Empty);
        }
    }

    private sealed class FaultyStore : IWorkflowCompilationStore
    {
        private readonly Dictionary<(Guid, string, string), CompilationVerdict> rows = [];
        public int Saves { get; private set; }
        public int Finds { get; private set; }
        public Func<Exception>? NextFindFault { get; set; }
        public Func<Exception>? NextSaveFault { get; set; }

        public Task<CompilationVerdict?> FindAsync(
            Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
        {
            Finds++;
            if (NextFindFault is { } fault)
            {
                NextFindFault = null;
                throw fault();
            }

            return Task.FromResult(rows.TryGetValue((workflowVersionId, definitionDigest, dependencyHash), out var v) ? v : null);
        }

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
        {
            Saves++;
            if (NextSaveFault is { } fault)
            {
                NextSaveFault = null;
                throw fault();
            }

            rows[(verdict.WorkflowVersionId, verdict.DefinitionDigest, verdict.DependencyHash)] = verdict;
            return Task.CompletedTask;
        }

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class DecliningStore : IWorkflowCompilationStore
    {
        public bool DeclinesFinds { get; init; } = true;
        public bool DeclinesSaves { get; init; } = true;
        public int Saves { get; private set; }
        public int Finds { get; private set; }

        public Task<CompilationVerdict?> FindAsync(
            Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
        {
            Finds++;
            return DeclinesFinds
                ? throw new CompilationStoreUnavailableException("the store's own words.")
                : Task.FromResult<CompilationVerdict?>(null);
        }

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
        {
            Saves++;
            return DeclinesSaves
                ? throw new CompilationStoreUnavailableException("the store's own words.")
                : Task.CompletedTask;
        }

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            throw new CompilationStoreUnavailableException("the store's own words.");
    }

    private sealed class StoreThrowingScopes : IWorkflowRunScopeFactory
    {
        private int opened;
        private int disposed;

        public int Opened => Volatile.Read(ref opened);

        public int Disposed => Volatile.Read(ref disposed);

        public IWorkflowRunScope CreateScope()
        {
            Interlocked.Increment(ref opened);
            return new Scope(this);
        }

        private sealed class Scope(StoreThrowingScopes owner) : IWorkflowRunScope, IServiceProvider
        {
            public IServiceProvider Services => this;

            public IWorkflowStore Store => throw new InvalidOperationException("the store cannot be built");

            public IWorkflowRunJournal Journal => throw new NotSupportedException();

            public IWorkflowAuditSink Audit => throw new NotSupportedException();

            public IWorkflowAuthorization Authorization => throw new NotSupportedException();

            public object? GetService(Type serviceType) => null;

            public void Dispose() => Interlocked.Increment(ref owner.disposed);
        }
    }

    private sealed class ScriptedStore : IWorkflowCompilationStore
    {
        private readonly ConcurrentDictionary<(Guid, string, string), CompilationVerdict> rows = new();
        private int saves;
        private int finds;
        public Func<int, Task>? BeforeFind { get; set; }
        public Func<int, Task>? BeforeSave { get; set; }
        public ConcurrentQueue<CancellationToken> SaveTokenQueue { get; } = new();
        public IReadOnlyList<CancellationToken> SaveTokens => [.. SaveTokenQueue];
        public ConcurrentQueue<CancellationToken> FindTokenQueue { get; } = new();
        public int Saves => Volatile.Read(ref saves);
        public int Finds => Volatile.Read(ref finds);

        public async Task<CompilationVerdict?> FindAsync(
            Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref finds);
            FindTokenQueue.Enqueue(cancellationToken);
            if (BeforeFind is { } hook)
                await hook(call);
            return rows.TryGetValue((workflowVersionId, definitionDigest, dependencyHash), out var v) ? v : null;
        }

        public async Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref saves);
            SaveTokenQueue.Enqueue(cancellationToken);
            if (BeforeSave is { } hook)
                await hook(call);
            cancellationToken.ThrowIfCancellationRequested();
            rows[(verdict.WorkflowVersionId, verdict.DefinitionDigest, verdict.DependencyHash)] = verdict;
        }

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class CapturingLogger : ILogger<WorkflowCompilationService>
    {
        public List<string> Messages { get; } = [];

        public List<(LogLevel Level, Exception? Error)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Entries.Add((logLevel, exception));
        }
    }

    private sealed class GatedStore : IWorkflowCompilationStore
    {
        private readonly Dictionary<(Guid, string, string), CompilationVerdict> rows = [];
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConcurrentFinds;
        public int MaxConcurrentFinds;

        public void Release() => gate.TrySetResult();

        public async Task<CompilationVerdict?> FindAsync(
            Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref ConcurrentFinds);
            InterlockedMax(ref MaxConcurrentFinds, concurrent);
            await gate.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref ConcurrentFinds);
            return rows.TryGetValue((workflowVersionId, definitionDigest, dependencyHash), out var v) ? v : null;
        }

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
        {
            rows[(verdict.WorkflowVersionId, verdict.DefinitionDigest, verdict.DependencyHash)] = verdict;
            return Task.CompletedTask;
        }

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref target);
                if (value <= current) return;
            } while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }
}
