using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationDiagnosticTests
{
    [Fact]
    public void A_diagnostic_with_every_field_set_round_trips_field_by_field()
    {
        var original = new CompilationDiagnostic(
            CompilationRules.PortTypeMismatch,
            "Output 'Result' is Number but input 'Target' expects Shipment.",
            NodeId: "n1",
            PortName: "Target",
            CanvasPath: [new CanvasPathSegment("branch", "Body"), new CanvasPathSegment("attempt", "Retry")],
            BlockSource: "reeve-plugin-bt@2.1.0");

        var json = JsonSerializer.Serialize(original, WorkflowGraphHelpers.JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<CompilationDiagnostic>(json, WorkflowGraphHelpers.JsonOptions);

        json.Should().Contain("\"RuleId\"", "the wire casing is a decision the HTTP endpoint must match, not a default nobody chose");
        roundTripped.Should().NotBeNull();
        roundTripped!.RuleId.Should().Be(original.RuleId);
        roundTripped.Message.Should().Be(original.Message);
        roundTripped.NodeId.Should().Be(original.NodeId);
        roundTripped.PortName.Should().Be(original.PortName);
        roundTripped.CanvasPath.Should().Equal(original.CanvasPath);
        roundTripped.BlockSource.Should().Be(original.BlockSource);
    }

    [Fact]
    public void A_diagnostic_built_with_no_canvas_path_never_carries_a_null_one()
    {
        var diagnostic = new CompilationDiagnostic(CompilationRules.Cycle, "cycle found");

        diagnostic.CanvasPath.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void A_diagnostic_deserialised_with_no_canvas_path_never_carries_a_null_one()
    {
        var diagnostic = JsonSerializer.Deserialize<CompilationDiagnostic>(
            """{"RuleId":"r","Message":"m","CanvasPath":null}""", WorkflowGraphHelpers.JsonOptions);

        diagnostic!.CanvasPath.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void A_diagnostic_with_its_canvas_path_cleared_still_carries_no_null()
    {
        var diagnostic = new CompilationDiagnostic(
            CompilationRules.Cycle, "cycle found", CanvasPath: [new CanvasPathSegment("branch", "Body")]);

        var cleared = diagnostic with { CanvasPath = null! };

        cleared.CanvasPath.Should().NotBeNull().And.BeEmpty();
    }
}
