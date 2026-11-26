using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ControlFlowBlocksMutationTests
{
    private static WorkflowBlockContext MakeContext(ISubCanvasExecutor? executor = null, params (string Name, string Term)[] types) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>(),
        SubCanvasExecutor = executor,
        Types = types.ToDictionary(
            t => t.Name, t => WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse(t.Term, PortTermGrammar.Document))!),
    };

    private static ISubCanvasExecutor BodyReturning(IReadOnlyDictionary<string, object?> output)
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(output);
        return executor;
    }

    [Fact]
    public async Task WhileLoop_MaxIterations_zero_is_rejected_up_front_as_ArgumentException()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Continue"] = true });

        var block = new WhileLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = 0L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await act.Should().NotThrowAsync<InvalidOperationException>();
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhileLoop_negative_MaxIterations_is_rejected_as_ArgumentException()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Continue"] = true });

        var block = new WhileLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = -1L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhileLoop_cap_of_one_stops_at_exactly_one_body_when_continue_stays_true()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Continue"] = true });

        var block = new WhileLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = 1L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await executor.Received(1).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_MaxIterations_zero_is_rejected_up_front_as_ArgumentException()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Value"] = 1 });

        var block = new ForLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 5.0, ["Step"] = 1.0, ["MaxIterations"] = 0L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await act.Should().NotThrowAsync<InvalidOperationException>();
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_negative_MaxIterations_is_rejected_as_ArgumentException()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Value"] = 1 });

        var block = new ForLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 5.0, ["Step"] = 1.0, ["MaxIterations"] = -3L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IterateOver_MaxItems_zero_is_rejected_up_front_as_ArgumentException()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Result"] = "ok" });
        var collection = Enumerable.Range(0, 5).Cast<object?>().ToList();

        var block = new IterateOverBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = collection, ["MaxItems"] = 0L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await act.Should().NotThrowAsync<InvalidOperationException>();
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    private static (ISubCanvasExecutor executor, List<double> currents) BodyCapturingCurrent()
    {
        var currents = new List<double>();
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IReadOnlyDictionary<string, object?>>();
                currents.Add(Convert.ToDouble(inputs["Current"]!));
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["Value"] = inputs["Current"] });
            });
        return (executor, currents);
    }

    [Fact]
    public async Task ForLoop_positive_step_counts_up_and_stops_at_to_exclusive()
    {
        var (executor, currents) = BodyCapturingCurrent();

        var block = new ForLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 5.0, ["Step"] = 2.0 },
            MakeContext(executor), CancellationToken.None);

        currents.Should().Equal(0.0, 2.0, 4.0);
        result["Iterations"].Should().Be(3);
        await executor.Received(3).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_negative_step_counts_down_and_stops_at_to_exclusive()
    {
        var (executor, currents) = BodyCapturingCurrent();

        var block = new ForLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 5.0, ["To"] = 1.0, ["Step"] = -2.0 },
            MakeContext(executor), CancellationToken.None);

        currents.Should().Equal(5.0, 3.0);
        result["Iterations"].Should().Be(2);
        await executor.Received(2).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_negative_step_with_from_below_to_runs_zero_iterations()
    {
        var (executor, currents) = BodyCapturingCurrent();

        var block = new ForLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 5.0, ["Step"] = -1.0 },
            MakeContext(executor), CancellationToken.None);

        currents.Should().BeEmpty();
        result["Iterations"].Should().Be(0);
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_positive_step_with_from_above_to_runs_zero_iterations()
    {
        var (executor, currents) = BodyCapturingCurrent();

        var block = new ForLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 5.0, ["To"] = 5.0, ["Step"] = 1.0 },
            MakeContext(executor), CancellationToken.None);

        currents.Should().BeEmpty();
        result["Iterations"].Should().Be(0);
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_step_zero_is_rejected_as_ArgumentException()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Value"] = 1 });

        var block = new ForLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 5.0, ["Step"] = 0.0 },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_uses_From_default_0_and_Step_default_1_when_absent()
    {
        var (executor, currents) = BodyCapturingCurrent();

        var block = new ForLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["To"] = 3.0 },
            MakeContext(executor), CancellationToken.None);

        currents.Should().Equal(0.0, 1.0, 2.0);
        result["Iterations"].Should().Be(3);
    }

    [Fact]
    public async Task ForLoop_missing_To_throws_ArgumentException()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Value"] = 1 });

        var block = new ForLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["Step"] = 1.0 },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        await executor.DidNotReceive().ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhileLoop_defaults_to_100_iterations_when_MaxIterations_absent()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Continue"] = true });

        var block = new WhileLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?>(),
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await executor.Received(100).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhileLoop_stops_after_one_iteration_when_continue_absent()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Other"] = 1 });

        var block = new WhileLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = 50L },
            MakeContext(executor), CancellationToken.None);

        result["Iterations"].Should().Be(1);
        await executor.Received(1).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IterateOver_collection_exactly_at_MaxItems_completes_without_throwing()
    {
        var executor = BodyReturning(new Dictionary<string, object?> { ["Result"] = "ok" });
        var exactly = Enumerable.Range(0, 4).Cast<object?>().ToList();

        var block = new IterateOverBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = exactly, ["MaxItems"] = 4L },
            MakeContext(executor, ("Results", "Collection<String>")), CancellationToken.None);

        var results = result["Results"].Should().BeOfType<List<string>>().Subject;
        results.Should().HaveCount(4);
        await executor.Received(4).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }
}
