using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class DeclaredVocabularyCompileTests
{
    private sealed record Crate;

    private const string Provider = "plugin:gauges";

    private static readonly WorkflowTypeRecord GaugeType = new("Gauge", typeof(Crate));

    private static WorkflowTypeRegistry Loaded()
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register(Provider, [GaugeType]).Refusals.Should().BeEmpty();
        return registry;
    }

    private static CompilationResult Compile(WorkflowTypeRegistry registry, BlockShape block) =>
        WorkflowCompiler.Compile(
            Graph([StartNode(), Node("n", block.BlockTypeId, new { Kind = "One" })], [Edge("e1", "start", "n")]),
            Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), block),
            CanvasSignature.Empty, new CompilationOptions { TypeRegistry = registry });

    private static CompilationDiagnostic[] Vocabulary(CompilationResult result) => [.. result.Diagnostics
        .Where(d => d.RuleId is CompilationRules.PortTypeAlias or CompilationRules.PortTypeUnregistered)];

    private static PortValueType Gauge(string type = "Gauge") => PortValueType.OfDeclaration(type);

    public static TheoryData<string> Shapes => new()
    {
        "input", "output", "sub-canvas input", "sub-canvas output", "Expression input",
    };

    private static BlockShape BlockOver(string where, string type)
    {
        var declared = Gauge(type);
        var branch = new[] { "Body" };
        return where switch
        {
            "input" => Shape("gadget", inputs: [("In", declared, false)]),
            "output" => Shape("gadget", outputs: [("Out", declared)]),
            "sub-canvas input" => Shape("gadget", subCanvasNames: branch,
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [("In", declared, false)] }),
            "sub-canvas output" => Shape("gadget", subCanvasNames: branch,
                subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [("Out", declared, false)] }),
            _ => Shape("gadget", inputs: [("In", Gauge($"Expression<{type}>"), false)]),
        };
    }

    private static string PortOf(string where) => where switch
    {
        "input" or "sub-canvas input" or "Expression input" => "In",
        _ => "Out",
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_block_declaring_a_type_refuses_at_its_node_once_the_type_unloads_and_is_clean_when_it_returns(string where)
    {
        var registry = Loaded();
        var block = BlockOver(where, "Gauge");
        Vocabulary(Compile(registry, block)).Should().BeEmpty("the type is live");

        registry.Unregister(Provider).Should().BeTrue();
        var refused = Vocabulary(Compile(registry, block));

        var fault = refused.Should().ContainSingle().Which;
        fault.RuleId.Should().Be(CompilationRules.PortTypeUnregistered);
        fault.NodeId.Should().Be("n");
        fault.PortName.Should().Be(PortOf(where));
        fault.Message.Should().Contain("'gadget'").And.Contain("'Gauge'");

        registry.Register(Provider, [GaugeType]).Refusals.Should().BeEmpty();
        Vocabulary(Compile(registry, block)).Should().BeEmpty("re-registering wakes the version");
    }

    [Fact]
    public void A_constructor_term_refuses_when_the_plugin_that_owns_its_constructor_and_argument_unloads()
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:crates",
        [
            new WorkflowTypeRecord("Crate", typeof(Crate)),
            new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Crate"] },
        ]).Refusals.Should().BeEmpty();
        var block = BlockOver("input", "Gauge<Crate>");
        Vocabulary(Compile(registry, block)).Should().BeEmpty();

        registry.Unregister("plugin:crates");
        var fault = Vocabulary(Compile(registry, block)).Should().ContainSingle().Which;

        fault.RuleId.Should().Be(CompilationRules.PortTypeUnregistered);
        fault.NodeId.Should().Be("n");
        fault.PortName.Should().Be("In");
        registry.Register("plugin:crates",
        [
            new WorkflowTypeRecord("Crate", typeof(Crate)),
            new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Crate"] },
        ]).Refusals.Should().BeEmpty();
        Vocabulary(Compile(registry, block)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_declared_spelling_that_became_an_alias_is_port_type_alias_at_the_node(string where)
    {
        var registry = Loaded();
        var block = BlockOver(where, "Gauge");
        registry.Unregister(Provider);
        registry.Register("plugin:meters", [new WorkflowTypeRecord("Meter", typeof(Crate)) { Aliases = ["Gauge"] }])
            .Refusals.Should().BeEmpty();

        var fault = Vocabulary(Compile(registry, block)).Should().ContainSingle().Which;

        fault.RuleId.Should().Be(CompilationRules.PortTypeAlias);
        fault.NodeId.Should().Be("n");
        fault.Message.Should().Contain("'Meter'");
    }

    [Fact]
    public void A_case_only_registered_name_change_invalidates_the_clean_fingerprint_and_refuses_the_old_spelling()
    {
        var registry = Loaded();
        var block = BlockOver("input", "Gauge");
        var options = new CompilationOptions { TypeRegistry = registry };
        var clean = Compile(registry, block);
        Vocabulary(clean).Should().BeEmpty();
        var oldFingerprint = DependencyFingerprint.Compute([block], options, NodePortEnvironment.Empty);

        registry.Unregister(Provider).Should().BeTrue();
        registry.Register(Provider, [new WorkflowTypeRecord("GAUGE", typeof(Crate))]).Refusals.Should().BeEmpty();

        DependencyFingerprint.Compute([block], options, NodePortEnvironment.Empty).Should().NotBe(oldFingerprint);
        var refused = Vocabulary(Compile(registry, block)).Should().ContainSingle().Which;
        refused.RuleId.Should().Be(CompilationRules.PortTypeAlias);
        refused.NodeId.Should().Be("n");
    }

    [Fact]
    public void Every_node_using_the_block_is_refused_and_a_node_that_does_not_is_not()
    {
        var registry = Loaded();
        var graph = Graph(
            [StartNode(), Node("a", "gadget"), Node("b", "gadget"), Node("c", "plain")],
            [Edge("e1", "start", "a"), Edge("e2", "start", "b"), Edge("e3", "start", "c")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), BlockOver("input", "Gauge"), Shape("plain", inputs: [("In", PortValueType.String, false)]));
        registry.Unregister(Provider);

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, new CompilationOptions { TypeRegistry = registry });

        Vocabulary(result).Select(d => d.NodeId).Should().Equal("a", "b");
    }

    [Fact]
    public void Any_in_any_shape_is_refused_at_the_node_naming_each_port()
    {
        var block = Shape("gadget",
            inputs: [("In", PortValueType.Of("Collection<Any>"), false), ("Rest", PortValueType.String, false)],
            outputs: [("Out", PortValueType.Of("Any"))]);

        Vocabulary(Compile(new WorkflowTypeRegistry(), block)).Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().BeEquivalentTo(
            [(CompilationRules.PortTypeAlias, "n", "In"), (CompilationRules.PortTypeAlias, "n", "Out")]);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("Collection<Any>")]
    public void Any_in_a_host_declared_shape_is_refused_as_a_reserved_name(string type)
    {
        var block = BlockShape.Snapshot(FakeFactory.For(Shape("gadget", inputs: [("In", Gauge(type), false)])));

        var fault = Vocabulary(Compile(new WorkflowTypeRegistry(), block)).Should().ContainSingle().Which;

        fault.RuleId.Should().Be(CompilationRules.PortTypeAlias);
        fault.NodeId.Should().Be("n");
        fault.PortName.Should().Be("In");
        fault.Message.Should().Contain("Any").And.Contain("not a port type");
    }

    [Fact]
    public async Task A_stored_clean_verdict_is_not_answered_after_the_type_unloads()
    {
        var registry = Loaded();
        var blocks = new CompilationServiceTests.MovableCatalogue()
            .Add(BlockOver("input", "Gauge"));
        var verdicts = new CompilationServiceTests.RecordingStore();
        var service = CompilationScopes.Service(
            blocks, new CompilationServiceTests.FakeWorkflowStore(), verdicts, new CompilationOptions { TypeRegistry = registry });
        var version = CompilationServiceTests.Version(Graph([StartNode(), Node("n", "gadget")], [Edge("e1", "start", "n")]));

        (await service.EnsureAsync(version, null, CancellationToken.None)).IsValid.Should().BeTrue();
        registry.Unregister(Provider);
        var refused = await service.EnsureAsync(version, null, CancellationToken.None);

        refused.IsValid.Should().BeFalse();
        refused.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeUnregistered).Which.NodeId.Should().Be("n");
        registry.Register(Provider, [GaugeType]);
        (await service.EnsureAsync(version, null, CancellationToken.None)).IsValid.Should().BeTrue("re-registering wakes the version");
    }
}
