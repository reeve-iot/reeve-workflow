using FluentAssertions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowVariablesEncoderDateTimeTests
{
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void A_DateTime_that_is_not_UTC_is_refused(DateTimeKind kind)
    {
        var variables = new Dictionary<string, object?> { ["at"] = new DateTime(2026, 1, 2, 3, 4, 5, kind) };

        FluentActions.Invoking(() => WorkflowVariablesEncoder.Encode(variables)).Should().Throw<ArgumentException>().Which.Message.Should().Contain(kind.ToString());
    }

    [Fact]
    public void A_UTC_DateTime_and_a_DateTimeOffset_are_written_as_the_same_UTC_instant()
    {
        var utc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        WorkflowVariablesEncoder.Encode(new Dictionary<string, object?> { ["at"] = utc })
            .Should().Be(WorkflowVariablesEncoder.Encode(new Dictionary<string, object?> { ["at"] = new DateTimeOffset(utc) }));
    }
}
