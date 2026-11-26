using FluentAssertions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class WorkflowDoesNotCompileExceptionTests
{
    [Fact]
    public void It_carries_the_diagnostics_and_names_the_first_in_its_message()
    {
        CompilationDiagnostic[] diagnostics =
        [
            new(CompilationRules.BlockTypeUnknown, "Block type 'gone' is not registered.", "n1"),
            new(CompilationRules.NoStartNode, "Workflow has no start node."),
        ];

        var exception = new WorkflowDoesNotCompileException(diagnostics);

        exception.Diagnostics.Should().Equal(diagnostics);
        exception.Message.Should().Be(
            "The workflow does not compile: 2 problem(s), the first is block-type-unknown: Block type 'gone' is not registered.");
    }

    [Fact]
    public void It_refuses_an_empty_list_since_an_invalid_verdict_names_at_least_one_problem()
    {
        var empty = () => new WorkflowDoesNotCompileException([]);

        empty.Should().Throw<ArgumentException>().WithParameterName("diagnostics");
    }
}
