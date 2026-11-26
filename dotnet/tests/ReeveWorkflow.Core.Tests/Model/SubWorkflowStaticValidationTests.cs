using System.Text.Json;
using FluentAssertions;
using Xunit;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class SubWorkflowStaticValidationTests
{
    private static WorkflowDefinition Parse(string json) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowGraphHelpers.JsonOptions)!;

    [Fact]
    public void Accepts_subworkflow_with_literal_guid_config()
    {
        var json = JsonSerializer.Serialize(new
        {
            Nodes = new object[]
            {
                new { Id = "in", BlockTypeId = "WorkflowInput" },
                new { Id = "sub", BlockTypeId = "SubWorkflow", Configuration = new { WorkflowId = Guid.NewGuid().ToString() } }
            }
        });

        var act = () => WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(Parse(json));

        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_subworkflow_with_edge_into_workflowid_port()
    {
        var json = JsonSerializer.Serialize(new
        {
            Nodes = new object[]
            {
                new { Id = "in", BlockTypeId = "WorkflowInput" },
                new { Id = "src", BlockTypeId = "some-block" },
                new { Id = "sub", BlockTypeId = "SubWorkflow", Configuration = new { WorkflowId = Guid.NewGuid().ToString() } }
            },
            Edges = new[]
            {
                new { Id = "c1", SourceNodeId = "src", TargetNodeId = "sub",
                      Mappings = new Dictionary<string, object> { ["WorkflowId"] = new { type = "direct", sourceOutputName = "out" } } }
            }
        });

        var act = () => WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(Parse(json));

        act.Should().Throw<WorkflowValidationException>().WithMessage("*WorkflowId*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("${input.targetWorkflowId}")]
    [InlineData("not-a-guid")]
    public void Rejects_subworkflow_with_non_literal_workflowid(string value)
    {
        var json = JsonSerializer.Serialize(new
        {
            Nodes = new object[]
            {
                new { Id = "in", BlockTypeId = "WorkflowInput" },
                new { Id = "sub", BlockTypeId = "SubWorkflow", Configuration = new { WorkflowId = value } }
            }
        });

        var act = () => WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(Parse(json));

        act.Should().Throw<WorkflowValidationException>();
    }

    [Fact]
    public void Accepts_subworkflow_with_edge_into_unrelated_port()
    {
        var json = JsonSerializer.Serialize(new
        {
            Nodes = new object[]
            {
                new { Id = "in", BlockTypeId = "WorkflowInput" },
                new { Id = "src", BlockTypeId = "some-block" },
                new { Id = "sub", BlockTypeId = "SubWorkflow", Configuration = new { WorkflowId = Guid.NewGuid().ToString() } }
            },
            Edges = new[]
            {
                new { Id = "c1", SourceNodeId = "src", TargetNodeId = "sub",
                      Mappings = new Dictionary<string, object> { ["SomeOtherInput"] = new { type = "direct", sourceOutputName = "out" } } }
            }
        });

        var act = () => WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(Parse(json));

        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_subworkflow_with_noncanonical_casing_and_invalid_workflowid()
    {
        var json = JsonSerializer.Serialize(new
        {
            Nodes = new object[]
            {
                new { Id = "in", BlockTypeId = "WorkflowInput" },
                new { Id = "sub", BlockTypeId = "subworkflow", Configuration = new { WorkflowId = "${input.x}" } }
            }
        });
        var act = () => WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(Parse(json));
        act.Should().Throw<WorkflowValidationException>();
    }

    [Fact]
    public void Rejects_subworkflow_nested_in_subcanvas()
    {
        var json = JsonSerializer.Serialize(new
        {
            Nodes = new object[]
            {
                new { Id = "in", BlockTypeId = "WorkflowInput" },
                new
                {
                    Id = "ifelse", BlockTypeId = "IfElse",
                    SubCanvases = new Dictionary<string, object>
                    {
                        ["then"] = new
                        {
                            Nodes = new object[]
                            {
                                new { Id = "sub", BlockTypeId = "SubWorkflow" }
                            }
                        }
                    }
                }
            }
        });

        var act = () => WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(Parse(json));

        act.Should().Throw<WorkflowValidationException>();
    }
}
