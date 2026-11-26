using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class HostJsonLiteralTests
{
    public sealed record Reading(string Shipment, string Key);

    private static CompilationOptions Options() => new()
    {
        TypeRegistry = new WorkflowTypeRegistry([
            new WorkflowTypeRecord("Reading", typeof(Reading))
            {
                Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Json),
                JsonTypeInfo = StrictJson.Info(typeof(Reading)),
            },
            new WorkflowTypeRecord("Counter", typeof(int))
            {
                Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Int),
                JsonTypeInfo = StrictJson.Info(typeof(int)),
            },
            new WorkflowTypeConstructorRecord("Cell", typeof(Reading))
            {
                AllowedArguments = ["Boolean"],
                Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Json),
                JsonTypeInfo = StrictJson.Info(typeof(Reading)),
            },
        ]),
    };

    private const string Good = """{"Shipment":"s-1","Key":"temp"}""";
    private const string Bad = """{"Shipment":5,"Key":"temp"}""";

    private static BlockShape Receiver(string type) => Shape("receiver", inputs: [("Value", PortValueType.Of(type), false)]);

    private static WorkflowDefinition Literal(string json, bool asConfiguration)
    {
        var value = JsonDocument.Parse(json).RootElement.Clone();
        return asConfiguration
            ? Graph([StartNode(), Node("n", "receiver", new Dictionary<string, object?> { ["Value"] = value })], [Edge("e", "start", "n")])
            : Graph([StartNode(), Node("n", "receiver")], [LiteralEdge("e", "start", "n", "Value", value)]);
    }

    public static TheoryData<string, string, bool> Unreadable()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var asConfiguration in new[] { false, true })
        {
            data.Add("Reading", Bad, asConfiguration);
            data.Add("Cell<Boolean>", Bad, asConfiguration);
            data.Add("Collection<Reading>", $"[{Good},{Bad}]", asConfiguration);
            data.Add("Counter", "3000000000", asConfiguration);
        }

        return data;
    }

    public static TheoryData<string, string, bool> Readable()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var asConfiguration in new[] { false, true })
        {
            data.Add("Reading", Good, asConfiguration);
            data.Add("Cell<Boolean>", Good, asConfiguration);
            data.Add("Collection<Reading>", $"[{Good},{Good}]", asConfiguration);
            data.Add("Counter", "300", asConfiguration);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void A_literal_the_record_does_not_read_is_refused_at_its_node_and_port_and_is_no_fault(string type, string json, bool asConfiguration)
    {
        var result = WorkflowCompiler.Compile(
            Literal(json, asConfiguration),
            Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Receiver(type)), CanvasSignature.Empty, Options());

        result.Fault.Should().BeNull();
        var refusal = result.Diagnostics.Should().ContainSingle().Which;
        (refusal.RuleId, refusal.NodeId, refusal.PortName).Should().Be((CompilationRules.LiteralPortValueInvalid, "n", "Value"));
        refusal.Message.Should().EndWith($"of 'receiver' cannot be read as {type}.");
    }

    [Theory]
    [MemberData(nameof(Readable))]
    public void A_literal_the_record_reads_compiles_and_lowers(string type, string json, bool asConfiguration)
    {
        var result = WorkflowCompiler.Compile(
            Literal(json, asConfiguration),
            Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Receiver(type)), CanvasSignature.Empty, Options());

        result.Fault.Should().BeNull();
        result.Diagnostics.Should().BeEmpty();
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Check_and_draft_analysis_answer_the_refusal_instead_of_throwing()
    {
        var blocks = new CompilationServiceTests.MovableCatalogue().Add(Receiver("Reading"));
        var service = CompilationScopes.Service(blocks, new CompilationServiceTests.FakeWorkflowStore(), verdictStore: null, options: Options());
        var definition = Stored(Literal(Bad, asConfiguration: false));

        var check = await service.CheckAsync(definition, null, null, CancellationToken.None);
        var analysis = await service.AnalyzeAsync(definition, null, null, CancellationToken.None);

        foreach (var diagnostics in new[] { check.Diagnostics, analysis.Diagnostics })
        {
            var refusal = diagnostics.Should().ContainSingle().Which;
            (refusal.RuleId, refusal.NodeId, refusal.PortName).Should().Be((CompilationRules.LiteralPortValueInvalid, "n", "Value"));
        }
    }
}
