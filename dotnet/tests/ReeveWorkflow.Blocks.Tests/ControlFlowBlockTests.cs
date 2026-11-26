using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ControlFlowBlockTests
{
    private static WorkflowBlockContext MakeContext(ISubCanvasExecutor? executor = null) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>(),
        SubCanvasExecutor = executor
    };

    [Theory]
    [InlineData("IfElse")]
    [InlineData("WhileLoop")]
    public async Task A_block_awaiting_its_branch_does_not_wait_on_the_callers_synchronization_context(string block)
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.Run<IReadOnlyDictionary<string, object?>>(async () =>
            {
                await Task.Delay(20).ConfigureAwait(false);
                return new Dictionary<string, object?> { ["Continue"] = false };
            }));
        IWorkflowBlockPlugin plugin = block == "IfElse" ? new IfElseBlock() : new WhileLoopBlock();
        var inputs = new Dictionary<string, object?> { ["Condition"] = true, ["MaxIterations"] = 10L };

        Task<IReadOnlyDictionary<string, object?>> run;
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DeadContext());
        try { run = plugin.ExecuteAsync(inputs, MakeContext(executor), CancellationToken.None); }
        finally { SynchronizationContext.SetSynchronizationContext(before); }

        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class DeadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    [Fact]
    public async Task IfElse_executes_then_branch_when_condition_true()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Then", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Output"] = "then-result" });

        var block = new IfElseBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Condition"] = true },
            MakeContext(executor), CancellationToken.None);

        result["Output"].Should().Be("then-result");
        await executor.Received(1).ExecuteAsync("Then", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
        await executor.DidNotReceive().ExecuteAsync("Else", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IfElse_executes_else_branch_when_condition_false()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Else", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Output"] = "else-result" });

        var block = new IfElseBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Condition"] = false },
            MakeContext(executor), CancellationToken.None);

        result["Output"].Should().Be("else-result");
        await executor.DidNotReceive().ExecuteAsync("Then", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
        await executor.Received(1).ExecuteAsync("Else", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhileLoop_exits_when_continue_is_false()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Continue"] = false });

        var block = new WhileLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = 10L },
            MakeContext(executor), CancellationToken.None);

        result["Iterations"].Should().Be(1);
        await executor.Received(1).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("true")]
    [InlineData(1)]
    [InlineData(null)]
    public async Task WhileLoop_fails_on_a_present_continue_that_is_not_a_boolean(object? continueValue)
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Continue"] = continueValue });

        var act = async () => await new WhileLoopBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = 10L }, MakeContext(executor), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be("Output 'Continue' cannot be read as Boolean.");
    }

    [Fact]
    public async Task WhileLoop_stops_when_the_body_produces_no_continue()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?>());

        var result = await new WhileLoopBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = 10L }, MakeContext(executor), CancellationToken.None);

        result["Iterations"].Should().Be(1);
    }

    [Fact]
    public async Task WhileLoop_throws_when_max_iterations_exceeded()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Continue"] = true });

        var block = new WhileLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["MaxIterations"] = 3L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded*");

        await executor.Received(3).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_executes_correct_number_of_iterations()
    {
        var capturedIndices = new List<int>();

        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IReadOnlyDictionary<string, object?>>();
                capturedIndices.Add((int)inputs["Index"]!);
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["Value"] = inputs["Index"] });
            });

        var block = new ForLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 3.0, ["Step"] = 1.0 },
            MakeContext(executor), CancellationToken.None);

        result["Iterations"].Should().Be(3);
        capturedIndices.Should().Equal(0, 1, 2);
        result.Keys.Should().Equal(["Iterations"], "a loop that declares no outputs writes the count alone");
    }

    private static WorkflowBlockContext CollectingContext(ISubCanvasExecutor executor, params (string Name, string Term)[] declared) =>
        new()
        {
            ExecutionId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Logger = Substitute.For<ILogger>(),
            Services = Substitute.For<IServiceProvider>(),
            SubCanvasExecutor = executor,
            DeclaredOutputs = [.. declared.Select(d => d.Name)],
            Types = declared.ToDictionary(
                d => d.Name,
                d => WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse(d.Term, PortTermGrammar.Document))!),
        };

    private static WorkflowBlockContext TypedContext(ISubCanvasExecutor executor, params (string Name, string Term)[] ports) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>(),
        SubCanvasExecutor = executor,
        Types = ports.ToDictionary(
            p => p.Name, p => WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse(p.Term, PortTermGrammar.Document))!),
    };

    private static WorkflowBlockContext ResultsContext(ISubCanvasExecutor executor) =>
        TypedContext(executor, ("Results", "Collection<String>"));

    [Fact]
    public async Task IterateOver_in_a_context_built_outside_a_compiled_run_is_refused_before_the_body_runs()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();

        var act = () => new IterateOverBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = new List<object?> { "a" } }, MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not compiled*");
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task IterateOver_whose_context_lacks_the_Results_type_of_a_bound_R_is_refused_before_the_body_runs()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var context = TypedContext(executor, ("Collection", "Collection<String>"), ("$T", "String"), ("$R", "Int"));

        var act = () => new IterateOverBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = new List<string> { "a" } }, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'Results'*");
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task ForLoop_collecting_in_a_context_built_outside_a_compiled_run_is_refused_before_the_body_runs()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = Substitute.For<ILogger>(),
            Services = Substitute.For<IServiceProvider>(), SubCanvasExecutor = executor, DeclaredOutputs = ["Sum"],
        };

        var act = () => new ForLoopBlock().ExecuteAsync(new Dictionary<string, object?> { ["To"] = 3.0 }, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not compiled*");
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task ForLoop_whose_context_lacks_the_type_of_a_declared_output_is_refused_before_the_body_runs()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = Substitute.For<ILogger>(),
            Services = Substitute.For<IServiceProvider>(), SubCanvasExecutor = executor, DeclaredOutputs = ["Sum"],
            Types = CollectingContext(executor, ("Other", "Collection<Number?>")).Types,
        };

        var act = () => new ForLoopBlock().ExecuteAsync(new Dictionary<string, object?> { ["To"] = 3.0 }, context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*'Sum'*");
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task IterateOver_writes_no_Results_when_its_compile_dropped_them()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>()));
        var context = TypedContext(executor, ("Collection", "Collection<String>"), ("$T", "String"));

        var result = await new IterateOverBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = new List<string> { "a" } }, context, CancellationToken.None);

        result.Should().BeEmpty();
    }

    private static ISubCanvasExecutor BodyAnswering(Func<int, Dictionary<string, object?>> answer)
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyDictionary<string, object?>>(answer(Convert.ToInt32(call.Arg<IReadOnlyDictionary<string, object?>>()["Index"]))));
        return executor;
    }

    [Fact]
    public async Task ForLoop_collects_a_declared_output_one_element_per_iteration_in_order_and_null_where_one_left_it_absent()
    {
        var executor = BodyAnswering(i => i == 1 ? new() : new() { ["Sum"] = i * 1.5 });

        var result = await new ForLoopBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 3.0, ["Step"] = 1.0 },
            CollectingContext(executor, ("Sum", "Collection<Number?>")), CancellationToken.None);

        result["Sum"].Should().BeOfType<List<double?>>().Which.Should().Equal(0.0, null, 3.0);
        result["Iterations"].Should().Be(3);
    }

    [Fact]
    public async Task ForLoop_collects_each_declared_output_into_its_own_collection_in_the_CLR_type_of_its_term()
    {
        var executor = BodyAnswering(i => new() { ["Count"] = i, ["Label"] = $"n{i}" });

        var result = await new ForLoopBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["To"] = 2.0 },
            CollectingContext(executor, ("Count", "Collection<Int?>"), ("Label", "Collection<String?>")), CancellationToken.None);

        result["Count"].Should().BeOfType<List<long?>>().Which.Should().Equal(0L, 1L);
        result["Label"].Should().BeOfType<List<string?>>().Which.Should().Equal("n0", "n1");
    }

    [Fact]
    public async Task ForLoop_with_no_iteration_writes_an_empty_collection_for_each_declared_output()
    {
        var executor = BodyAnswering(_ => new() { ["Sum"] = 1.0 });

        var result = await new ForLoopBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 5.0, ["To"] = 5.0 },
            CollectingContext(executor, ("Sum", "Collection<Number?>")), CancellationToken.None);

        result["Sum"].Should().BeOfType<List<double?>>().Which.Should().BeEmpty();
        result["Iterations"].Should().Be(0);
    }

    [Fact]
    public async Task ForLoop_refuses_an_element_off_the_declared_term_rather_than_writing_it()
    {
        var executor = BodyAnswering(_ => new() { ["Sum"] = "not a number" });

        var act = async () => await new ForLoopBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["To"] = 1.0 },
            CollectingContext(executor, ("Sum", "Collection<Number?>")), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Element 0 is a String*Number?*");
    }

    [Fact]
    public void ForLoop_declares_declared_outputs_collecting_and_no_Results_output()
    {
        var factory = new ForLoopBlockFactory();

        factory.SupportsDeclaredOutputs.Should().BeTrue();
        factory.DeclaredOutputsCollect.Should().BeTrue();
        factory.Outputs.Select(p => p.Name).Should().Equal("Iterations");
    }

    [Fact]
    public async Task IterateOver_collects_results_from_body()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IReadOnlyDictionary<string, object?>>();
                var item = inputs["Item"];
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["Result"] = $"processed-{item}" });
            });

        var block = new IterateOverBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = new List<object?> { "a", "b", "c" } },
            ResultsContext(executor), CancellationToken.None);

        var results = result["Results"].Should().BeOfType<List<string>>().Subject;
        results.Should().Equal("processed-a", "processed-b", "processed-c");
        await executor.Received(3).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task ForLoop_throws_when_max_iterations_exceeded()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Value"] = 1 });

        var block = new ForLoopBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 100.0, ["Step"] = 1.0, ["MaxIterations"] = 5L },
            MakeContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded the maximum iteration limit of 5*");

        await executor.Received(5).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForLoop_within_cap_completes_normally()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Value"] = 1 });

        var block = new ForLoopBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["From"] = 0.0, ["To"] = 3.0, ["Step"] = 1.0, ["MaxIterations"] = 100L },
            MakeContext(executor), CancellationToken.None);

        result["Iterations"].Should().Be(3);
    }

    [Fact]
    public async Task IterateOver_throws_when_collection_exceeds_max_items()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?> { ["Result"] = "ok" });

        var oversized = Enumerable.Range(0, 50).Cast<object?>().ToList();
        var block = new IterateOverBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = oversized, ["MaxItems"] = 4L },
            ResultsContext(executor), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeded the maximum item limit of 4*");

        await executor.Received(4).ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IterateOver_within_cap_completes_normally()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var inputs = callInfo.Arg<IReadOnlyDictionary<string, object?>>();
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["Result"] = inputs["Item"] });
            });

        var block = new IterateOverBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Collection"] = new List<object?> { "a", "b" }, ["MaxItems"] = 10L },
            ResultsContext(executor), CancellationToken.None);

        var results = result["Results"].Should().BeOfType<List<string>>().Subject;
        results.Should().Equal("a", "b");
    }

    [Fact]
    public void IterateOver_declares_its_ports_with_its_two_type_parameters()
    {
        var factory = new IterateOverBlockFactory();

        factory.TypeParameters.Select(p => p.Name).Should().Equal("T", "R");
        factory.Inputs.Single(p => p.Name == "Collection").Type.Name.Should().Be("Collection<$T>");
        factory.Outputs.Single(p => p.Name == "Results").Type.Name.Should().Be("Collection<$R>");
        factory.GetSubCanvasInputs("Body").Single(p => p.Name == "Item").Type.Name.Should().Be("$T");
        factory.GetSubCanvasOutputs("Body").Single(p => p.Name == "Result").Type.Name.Should().Be("$R");
    }
}
