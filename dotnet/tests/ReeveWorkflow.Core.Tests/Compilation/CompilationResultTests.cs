using FluentAssertions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationResultTests
{
    [Fact]
    public void A_refusal_carries_the_rule_and_where_it_points_not_only_a_sentence()
    {
        var diagnostic = new CompilationDiagnostic(
            CompilationRules.BlockTypeUnknown, "Block type 'x' is not registered.", NodeId: "n1");

        var result = CompilationResult.Refused([diagnostic]);

        result.Succeeded.Should().BeFalse();
        result.Program.Should().BeNull();
        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(
            d => d.RuleId == "block-type-unknown" && d.NodeId == "n1");
    }

    [Fact]
    public void A_result_that_succeeded_carries_no_diagnostics_to_be_ignored()
    {
        var result = CompilationResult.Compiled(new WorkflowProgram([]));

        result.Succeeded.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_refusal_that_names_no_rule_is_refused_itself()
    {
        var act = () => CompilationResult.Refused([]);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("diagnostics");
    }

    [Fact]
    public void Compiled_refuses_to_wrap_a_program_that_does_not_exist()
    {
        var act = () => CompilationResult.Compiled(null!);

        act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("program");
    }

    [Fact]
    public void Refused_copies_its_diagnostics_so_a_callers_later_edit_cannot_reach_the_result()
    {
        var diagnostics = new List<CompilationDiagnostic> { new(CompilationRules.Cycle, "cycle found") };

        var result = CompilationResult.Refused(diagnostics);
        diagnostics.Clear();

        result.Diagnostics.Should().ContainSingle();
    }
}
