using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RunTimeValueReadingTests
{
    [Fact]
    public void The_reader_accepts_bound_Int_values_and_refuses_text_and_other_numeric_types()
    {
        RunTimeValueReading.TryInt(7L, out var integer).Should().BeTrue();
        integer.Should().Be(7L);
        RunTimeValueReading.TryInt(7, out _).Should().BeFalse();
        RunTimeValueReading.TryInt(7.0, out _).Should().BeFalse();
    }

    [Fact]
    public void The_durable_sleep_refuses_text_instead_of_parsing_it()
    {
        var read = () => WorkflowExecutionHelpers.ResolveSleepDurationMs(new Dictionary<string, object?> { ["DurationMs"] = "7" });
        read.Should().Throw<ArgumentException>().WithMessage("DurationMs must be an integer.");
    }

    [Theory]
    [InlineData(3_000_000_000L)]
    [InlineData(long.MaxValue)]
    public void The_durable_sleep_keeps_the_long_it_is_given(long milliseconds) =>
        WorkflowExecutionHelpers.ResolveSleepDurationMs(new Dictionary<string, object?> { ["DurationMs"] = milliseconds })
            .Should().Be(milliseconds);

    [Fact]
    public void A_sleep_no_clock_can_name_is_an_overflow()
    {
        var act = () => WorkflowExecutionHelpers.SleepEnd(DateTimeOffset.UtcNow, long.MaxValue);

        act.Should().Throw<OverflowException>();
    }
}
