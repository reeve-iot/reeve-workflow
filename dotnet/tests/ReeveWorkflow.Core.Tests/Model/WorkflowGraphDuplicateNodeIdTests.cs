using FluentAssertions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowGraphDuplicateNodeIdTests
{
    [Theory]
    [InlineData(nameof(WorkflowGraphHelpers.ValidateStartAndReachability))]
    [InlineData(nameof(WorkflowGraphHelpers.TopologicalSort))]
    public void Model_helpers_refuse_duplicate_ids_with_the_id(string method)
    {
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "twin", BlockTypeId = "WorkflowInput" },
                new WorkflowNodeDefinition { Id = "twin", BlockTypeId = "step" },
            ],
        };

        Action invoke = method == nameof(WorkflowGraphHelpers.ValidateStartAndReachability)
            ? () => WorkflowGraphHelpers.ValidateStartAndReachability(definition)
            : () => WorkflowGraphHelpers.TopologicalSort(definition);

        invoke.Should().Throw<WorkflowValidationException>().WithMessage("*duplicate node id 'twin' (rule duplicate-node-id)*");
    }
}
