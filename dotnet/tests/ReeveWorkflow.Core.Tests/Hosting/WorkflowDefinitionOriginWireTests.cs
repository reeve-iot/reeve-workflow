using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class WorkflowDefinitionOriginWireTests
{
    [Theory]
    [InlineData("\"Edited\"", WorkflowDefinitionOrigin.Edited)]
    [InlineData("\"Imported\"", WorkflowDefinitionOrigin.Imported)]
    public void An_exact_name_is_read_and_written_back_as_the_same_text(string json, WorkflowDefinitionOrigin origin)
    {
        JsonSerializer.Deserialize<WorkflowDefinitionOrigin>(json).Should().Be(origin);
        JsonSerializer.Serialize(origin).Should().Be(json);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"1\"")]
    [InlineData("\"imported\"")]
    [InlineData("\"Edited, Imported\"")]
    [InlineData("\" Edited\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Any_other_spelling_is_refused(string json)
    {
        FluentActions.Invoking(() => JsonSerializer.Deserialize<WorkflowDefinitionOrigin>(json)).Should().Throw<JsonException>();
    }

    [Fact]
    public void A_value_that_is_no_member_is_not_written()
    {
        FluentActions.Invoking(() => JsonSerializer.Serialize((WorkflowDefinitionOrigin)7)).Should().Throw<JsonException>();
    }
}
