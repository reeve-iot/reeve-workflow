using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class WaitForDurationBlockTests
{
    private static WorkflowBlockContext Context() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>()
    };

    [Fact]
    public async Task WaitForDuration_returns_completed_after_delay()
    {
        var block = new WaitForDurationBlock();

        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["DurationMs"] = 1L },
            Context(),
            CancellationToken.None);

        result["Completed"].Should().Be(true);
        result["DurationMs"].Should().Be(1L);
    }

    [Fact]
    public async Task WaitForDuration_honors_cancellation()
    {
        var block = new WaitForDurationBlock();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["DurationMs"] = 1000L },
            Context(),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitForDuration_rejects_negative_duration()
    {
        var block = new WaitForDurationBlock();

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["DurationMs"] = -1L },
            Context(),
            CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*DurationMs*");
    }
}
