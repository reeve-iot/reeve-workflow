using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class BlockCatalogueDescriptionTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Wire(IWorkflowBlockFactory factory) =>
        JsonSerializer.SerializeToElement(WorkflowBlockDescription.Describe(factory), Web);

    [Fact]
    public void SubWorkflow_names_the_input_it_runs_a_workflow_from_and_a_block_that_runs_none_names_null()
    {
        Wire(new SubWorkflowBlockFactory()).GetProperty("runsWorkflowFrom").GetString().Should().Be("WorkflowId");
        Wire(new IterateOverBlockFactory()).GetProperty("runsWorkflowFrom").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void A_generic_port_is_carried_with_the_type_parameters_its_block_declares_and_no_relation()
    {
        var iterate = Wire(new IterateOverBlockFactory());

        iterate.GetProperty("typeParameters").EnumerateArray().Select(p => p.GetProperty("name").GetString())
            .Should().Equal("T", "R");
        Port(iterate.GetProperty("subCanvasInputs").GetProperty("Body"), "Item").GetProperty("type").GetString().Should().Be("$T");
        Port(iterate.GetProperty("outputs"), "Results").GetProperty("type").GetString().Should().Be("Collection<$R>");
        Port(iterate.GetProperty("outputs"), "Results").TryGetProperty("typeFrom", out _).Should().BeFalse();
    }

    [Fact]
    public void ForLoop_is_published_as_collecting_its_declared_outputs_and_no_other_container_is()
    {
        var loop = Wire(new ForLoopBlockFactory());

        loop.GetProperty("declaredOutputsCollect").GetBoolean().Should().BeTrue();
        loop.GetProperty("supportsDeclaredOutputs").GetBoolean().Should().BeTrue();
        loop.GetProperty("outputs").EnumerateArray().Select(p => p.GetProperty("name").GetString()).Should().Equal("Iterations");
        Wire(new IfElseBlockFactory()).GetProperty("declaredOutputsCollect").GetBoolean().Should().BeFalse();
        Wire(new IterateOverBlockFactory()).GetProperty("declaredOutputsCollect").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void HttpRequest_publishes_Headers_as_secret_values_and_no_other_port_of_it_is()
    {
        var http = Wire(new HttpRequestBlockFactory());

        http.GetProperty("inputs").EnumerateArray()
            .Where(p => p.GetProperty("secretValues").GetBoolean()).Select(p => p.GetProperty("name").GetString())
            .Should().Equal("Headers");
        http.GetProperty("outputs").EnumerateArray().Should().OnlyContain(p => !p.GetProperty("secretValues").GetBoolean());
    }

    [Fact]
    public void TryCatch_and_HttpRequest_declare_no_Any_and_register_under_the_plugin_rules()
    {
        var tryCatch = () => BlockShape.FromPlugin(new TryCatchBlockFactory(), new WorkflowTypeRegistry());
        var http = () => BlockShape.FromPlugin(new HttpRequestBlockFactory(), new WorkflowTypeRegistry());

        tryCatch.Should().NotThrow();
        http.Should().NotThrow();
    }

    private static JsonElement Port(JsonElement ports, string name) =>
        ports.EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
}
