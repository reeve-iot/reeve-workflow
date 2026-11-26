using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class MathAndWaitDurationMutationTests
{
    private static WorkflowBlockContext Context() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>()
    };

    [Fact]
    public async Task WaitForDuration_zero_duration_completes_immediately_without_throwing()
    {
        var block = new WaitForDurationBlock();

        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["DurationMs"] = 0L },
            Context(), CancellationToken.None);

        result["Completed"].Should().Be(true);
        result["DurationMs"].Should().Be(0L);
    }

    [Fact]
    public async Task WaitForDuration_small_positive_duration_completes()
    {
        var block = new WaitForDurationBlock();

        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["DurationMs"] = 1L },
            Context(), CancellationToken.None);

        result["Completed"].Should().Be(true);
        result["DurationMs"].Should().Be(1L);
    }

    [Fact]
    public async Task WaitForDuration_refuses_to_run_when_DurationMs_is_absent()
    {
        var block = new WaitForDurationBlockFactory().Create();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?>(),
            Context(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DurationMs*");
    }
}
