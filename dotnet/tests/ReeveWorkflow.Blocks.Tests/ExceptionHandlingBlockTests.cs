using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ExceptionHandlingBlockTests
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
    public async Task TryCatch_calls_Try_and_returns_success_when_no_error()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?> { ["X"] = 1 }, null));

        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));

        var block = new TryCatchBlock();
        var result = await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        result["CaughtError"].Should().Be(false);
        result["ErrorMessage"].Should().Be("");

        await executor.Received(1).TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
        await executor.DidNotReceive().TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
        await executor.Received(1).TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryCatch_calls_Catch_and_Finally_when_Try_fails()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var error = new SubCanvasError("kaboom", "InvalidOperationException", "node1", "block1");

        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), error));

        executor.TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));

        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));

        var block = new TryCatchBlock();
        var result = await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        result["CaughtError"].Should().Be(true);
        result["ErrorMessage"].Should().Be("kaboom");

        await executor.Received(1).TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
        await executor.Received(1).TryExecuteAsync("Catch", Arg.Is<IReadOnlyDictionary<string, object?>>(d =>
            d["Message"]!.Equals("kaboom") && d["ExceptionType"]!.Equals("InvalidOperationException")), Arg.Any<CancellationToken>());
        await executor.Received(1).TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryCatch_surfaces_AggregateException_with_original_first_when_Catch_also_fails()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var tryError = new SubCanvasError("original-try-boom", "InvalidOperationException", "tryNode", "tryBlock");
        var catchError = new SubCanvasError("catch-boom", "ArgumentException", "catchNode", "catchBlock");

        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), tryError));
        executor.TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), catchError));
        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));

        var block = new TryCatchBlock();
        var act = async () => await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions.Should().HaveCount(2);
        thrown.InnerExceptions[0].Message.Should().Be("original-try-boom");
        thrown.InnerExceptions[1].Message.Should().Be("catch-boom");

        await executor.Received(1).TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryCatch_surfaces_Finally_error_when_Try_and_Catch_both_succeeded()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var finallyError = new SubCanvasError("finally-boom", "InvalidOperationException", "finNode", "finBlock");

        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));
        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), finallyError));

        var block = new TryCatchBlock();
        var act = async () => await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions[0].Message.Should().Be("finally-boom");

        await executor.DidNotReceive().TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryCatch_keeps_original_first_when_Try_handled_but_Finally_fails()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var tryError = new SubCanvasError("original-try-boom", "InvalidOperationException", "tryNode", "tryBlock");
        var finallyError = new SubCanvasError("finally-boom", "ArgumentException", "finNode", "finBlock");

        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), tryError));
        executor.TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));
        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), finallyError));

        var block = new TryCatchBlock();
        var act = async () => await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions.Should().HaveCount(2);
        thrown.InnerExceptions[0].Message.Should().Be("original-try-boom");
        thrown.InnerExceptions[1].Message.Should().Be("finally-boom");
    }

    [Fact]
    public async Task TryCatch_Catch_rethrows_and_Finally_fails_surfaces_both_original_first()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var tryError = new SubCanvasError("original-try-boom", "InvalidOperationException", "tryNode", "tryBlock");
        var finallyError = new SubCanvasError("finally-boom", "ArgumentException", "finNode", "finBlock");

        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), tryError));
        executor.TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<SubCanvasResult>(_ => throw new SubCanvasRethrowException("rethrown-from-catch"));
        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), finallyError));

        var block = new TryCatchBlock();
        var act = async () => await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<AggregateException>()).Which;
        thrown.InnerExceptions.Should().HaveCount(2);
        thrown.InnerExceptions[0].Message.Should().Be("original-try-boom");
        thrown.InnerExceptions[1].Message.Should().Be("finally-boom");

        await executor.Received(1).TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryCatch_Catch_rethrows_and_Finally_succeeds_rethrow_propagates()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var tryError = new SubCanvasError("original-try-boom", "InvalidOperationException", "tryNode", "tryBlock");

        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), tryError));
        executor.TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<SubCanvasResult>(_ => throw new SubCanvasRethrowException("rethrown-from-catch"));
        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));

        var block = new TryCatchBlock();
        var act = async () => await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<SubCanvasRethrowException>().WithMessage("rethrown-from-catch");

        await executor.Received(1).TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryCatch_Finally_alone_throws_it_surfaces()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();

        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));
        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<SubCanvasResult>(_ => throw new SubCanvasRethrowException("finally-rethrow-boom"));

        var block = new TryCatchBlock();
        var act = async () => await block.ExecuteAsync(new Dictionary<string, object?>(), MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<SubCanvasRethrowException>().WithMessage("finally-rethrow-boom");

        await executor.DidNotReceive().TryExecuteAsync("Catch", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rethrow_throws_SubCanvasRethrowException()
    {
        var block = new RethrowBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Message"] = "custom message" },
            MakeContext(), CancellationToken.None);

        await act.Should().ThrowAsync<SubCanvasRethrowException>()
            .WithMessage("custom message");
    }

    [Theory]
    [InlineData(5, "Input 'Message' cannot be read as String: the value is a Number, not text.")]
    [InlineData(null, "Input 'Message' cannot be read as String.")]
    public async Task Rethrow_fails_on_a_present_message_that_is_not_text(object? message, string expected)
    {
        var act = async () => await new RethrowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Message"] = message }, MakeContext(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(expected);
    }

    [Fact]
    public async Task Rethrow_without_a_message_uses_the_default_text()
    {
        var act = async () => await new RethrowBlock().ExecuteAsync(
            new Dictionary<string, object?>(), MakeContext(), CancellationToken.None);

        await act.Should().ThrowAsync<SubCanvasRethrowException>().WithMessage("Rethrown from workflow");
    }
}
