using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class RethrowBlockMutationTests
{
    private static WorkflowBlockContext MakeContext(ISubCanvasExecutor? executor = null) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>(),
        SubCanvasExecutor = executor
    };

    [Fact]
    public async Task Rethrow_with_no_Message_input_throws_with_default_message()
    {
        var block = new RethrowBlock();

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?>(),
            MakeContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<SubCanvasRethrowException>()
            .WithMessage("Rethrown from workflow");
    }

    [Theory]
    [InlineData(42, "Input 'Message' cannot be read as String: the value is a Number, not text.")]
    [InlineData(null, "Input 'Message' cannot be read as String.")]
    public async Task Rethrow_with_a_present_Message_that_is_not_text_fails_naming_the_input(object? message, string expected)
    {
        var block = new RethrowBlock();

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Message"] = message },
            MakeContext(),
            CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(expected);
    }

    [Fact]
    public async Task Rethrow_uses_provided_string_Message_verbatim()
    {
        var block = new RethrowBlock();

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Message"] = "boom from sub-canvas" },
            MakeContext(),
            CancellationToken.None);

        await act.Should().ThrowAsync<SubCanvasRethrowException>()
            .WithMessage("boom from sub-canvas");
    }

    [Fact]
    public async Task Rethrow_always_throws_and_never_returns_outputs()
    {
        var block = new RethrowBlock();

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Message"] = "x", ["Other"] = "ignored" },
            MakeContext(),
            CancellationToken.None);

        var thrown = (await act.Should().ThrowExactlyAsync<SubCanvasRethrowException>()).Which;
        thrown.Should().BeAssignableTo<Exception>();
    }
}
