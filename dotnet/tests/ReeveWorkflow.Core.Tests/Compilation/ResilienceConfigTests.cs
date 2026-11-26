using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class ResilienceConfigTests
{
    [Fact]
    public void A_retryEnabled_that_is_not_a_boolean_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryEnabled = "yes" })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop", source: "test://noop")),
            CanvasSignature.Empty);

        var diagnostic = result.Diagnostics.Should().ContainSingle().Which;
        diagnostic.RuleId.Should().Be(CompilationRules.ResilienceConfigInvalid);
        diagnostic.NodeId.Should().Be("a");
        diagnostic.PortName.Should().Be("retryEnabled");
        diagnostic.CanvasPath.Should().BeEmpty();
        diagnostic.BlockSource.Should().Be("test://noop");
        diagnostic.Message.Should().Contain("retryEnabled").And.Contain("String").And.Contain("not a boolean");
    }

    [Fact]
    public void A_timeoutSeconds_that_is_not_a_number_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { timeoutSeconds = "soon" })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop", source: "test://noop")),
            CanvasSignature.Empty);

        var diagnostic = result.Diagnostics.Should().ContainSingle().Which;
        diagnostic.RuleId.Should().Be(CompilationRules.ResilienceConfigInvalid);
        diagnostic.NodeId.Should().Be("a");
        diagnostic.PortName.Should().Be("timeoutSeconds");
        diagnostic.CanvasPath.Should().BeEmpty();
        diagnostic.BlockSource.Should().Be("test://noop");
        diagnostic.Message.Should().Contain("String is not a number");
    }

    [Fact]
    public void A_retryCount_that_is_not_a_number_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryCount = false })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.ResilienceConfigInvalid && d.NodeId == "a" && d.PortName == "retryCount");
    }

    [Fact]
    public void A_fractional_retryCount_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryCount = 3.5 })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ResilienceConfigInvalid);
    }

    [Fact]
    public void A_fractional_timeoutSeconds_is_refused_by_compilation()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { timeoutSeconds = 1.5 })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.ResilienceConfigInvalid && d.NodeId == "a" && d.PortName == "timeoutSeconds");
    }

    [Fact]
    public void A_retryBackoffSeconds_that_is_not_a_number_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryBackoffSeconds = new[] { 1, 2 } })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.ResilienceConfigInvalid && d.NodeId == "a" && d.PortName == "retryBackoffSeconds");
    }

    [Fact]
    public void A_retryEnabled_stored_as_a_number_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryEnabled = 1 })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ResilienceConfigInvalid);
    }

    [Fact]
    public void A_null_resilience_value_is_an_unreadable_document_not_an_unset_field()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryEnabled = (bool?)null, timeoutSeconds = (int?)null })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
    }

    [Fact]
    public void A_malformed_resilience_config_on_the_start_node_is_not_refused()
    {
        var start = Node("start", WorkflowGraphHelpers.WorkflowInputBlockTypeId, new
        {
            timeoutSeconds = "soon",
            retryEnabled = "yes",
            retryCount = "lots",
            retryBackoffSeconds = new[] { 1, 2 },
        });
        var graph = Graph([start]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_malformed_resilience_config_on_a_WorkflowOutput_node_is_not_refused()
    {
        var end = Node("end", WorkflowGraphHelpers.WorkflowOutputBlockTypeId, new { retryEnabled = "nope" });
        var graph = Graph([StartNode(), end], [Edge("e1", "start", "end")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId)), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_timeoutSeconds_longer_than_the_engine_can_apply_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { timeoutSeconds = 604800 })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.ResilienceConfigInvalid && d.NodeId == "a" && d.PortName == "timeoutSeconds");
    }

    [Fact]
    public void A_timeoutSeconds_of_exactly_one_day_compiles_clean()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { timeoutSeconds = 86400 })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Every_Polly_config_key_gets_a_door_check()
    {
        foreach (var key in WorkflowGraphExecutor.PollyConfigKeys)
        {
            var badValue = key == WorkflowGraphExecutor.ConfigRetryEnabled
                ? JsonSerializer.SerializeToElement("not-a-boolean")
                : JsonSerializer.SerializeToElement("not-a-number");

            var graph = Graph(
                [StartNode(), Node("a", "noop", new Dictionary<string, JsonElement> { [key] = badValue })],
                [Edge("e1", "start", "a")]);

            var result = WorkflowCompiler.Compile(
                graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

            result.Diagnostics.Should().Contain(
                d => d.RuleId == CompilationRules.ResilienceConfigInvalid && d.PortName == key,
                $"'{key}' is in PollyConfigKeys and must be checked at the door");
        }
    }

    [Fact]
    public void A_resilience_value_inside_a_sub_canvas_is_refused_with_the_sub_canvas_path()
    {
        var inner = Node("inner", "noop", new { retryEnabled = "yes" });
        var body = Graph([StartNode("innerStart"), inner], [Edge("be1", "innerStart", "inner")]);
        var container = Node("branch", "if-else");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Then"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "branch")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("if-else", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasNames: ["Then"]),
            Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.ResilienceConfigInvalid
            && d.NodeId == "inner"
            && d.PortName == "retryEnabled"
            && d.CanvasPath.SequenceEqual(new[] { new CanvasPathSegment("branch", "Then") }));
    }

    [Fact]
    public void A_resilience_check_still_fires_when_the_sub_canvas_signature_never_resolved()
    {
        var inner = Node("inner", "noop", new { retryEnabled = "yes" });
        var body = Graph([StartNode("innerStart"), inner], [Edge("be1", "innerStart", "inner")]);
        var container = Node("branch", "if-else");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Else"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "branch")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("if-else", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasNames: ["Then"]),
            Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.SubCanvasNameUnknown);
        result.Diagnostics.Should().Contain(
            d => d.RuleId == CompilationRules.ResilienceConfigInvalid && d.NodeId == "inner",
            "the door check must not depend on this canvas's own signature having resolved");
    }

    [Fact]
    public void A_well_formed_resilience_config_compiles_clean()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryEnabled = true, retryCount = 5, retryBackoffSeconds = 2.5, timeoutSeconds = 10 })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }
}
