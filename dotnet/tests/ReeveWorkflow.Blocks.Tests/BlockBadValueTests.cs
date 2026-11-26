using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class BlockBadValueTests
{
    private static WorkflowBlockContext Context(ISubCanvasExecutor? executor = null) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>(),
        SubCanvasExecutor = executor ?? Substitute.For<ISubCanvasExecutor>(),
    };

    private static Task<IReadOnlyDictionary<string, object?>> Run(IWorkflowBlockPlugin block, string port, object? value)
    {
        var inputs = new Dictionary<string, object?>
        {
            ["To"] = 1.0, ["Collection"] = new List<object?> { 1 }, ["Url"] = "https://example.com/", ["DurationMs"] = 0L,
        };
        inputs[port] = value;
        return block.ExecuteAsync(inputs, Context(), CancellationToken.None);
    }

    public static TheoryData<string, string> BadValues => new()
    {
        { "WhileLoop", "MaxIterations" },
        { "ForLoop", "MaxIterations" },
        { "ForLoop", "From" },
        { "ForLoop", "To" },
        { "ForLoop", "Step" },
        { "IterateOver", "MaxItems" },
        { "WaitForDuration", "DurationMs" },
        { "HttpRequest", "TimeoutSeconds" },
    };

    private static IWorkflowBlockPlugin Create(string blockTypeId) => blockTypeId switch
    {
        "WhileLoop" => new WhileLoopBlock(),
        "ForLoop" => new ForLoopBlock(),
        "IterateOver" => new IterateOverBlock(),
        "WaitForDuration" => new WaitForDurationBlock(),
        "HttpRequest" => new HttpRequestBlock(),
        _ => throw new ArgumentOutOfRangeException(nameof(blockTypeId)),
    };

    [Theory]
    [MemberData(nameof(BadValues))]
    public async Task A_value_the_port_cannot_read_fails_the_block_naming_the_port(string blockTypeId, string port)
    {
        var act = async () => await Run(Create(blockTypeId), port, true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(port);
    }

    [Theory]
    [MemberData(nameof(BadValues))]
    public async Task A_null_that_is_present_is_not_an_absent_input_and_fails_the_block_naming_the_port(string blockTypeId, string port)
    {
        foreach (var nothing in new object?[] { null, JsonDocument.Parse("null").RootElement.Clone() })
        {
            var act = async () => await Run(Create(blockTypeId), port, nothing);

            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain($"Input '{port}' cannot be read as");
        }
    }

    public static TheoryData<string, string> StringPorts => new()
    {
        { "HttpRequest", "Url" },
        { "HttpRequest", "Method" },
        { "HttpRequest", "Body" },
    };

    [Theory]
    [MemberData(nameof(StringPorts))]
    public async Task A_string_input_that_is_present_and_null_fails_the_block_naming_the_port(string blockTypeId, string port)
    {
        foreach (var value in new object?[] { null, JsonDocument.Parse("null").RootElement.Clone() })
        {
            var act = async () => await Run(Create(blockTypeId), port, value);

            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be($"Input '{port}' cannot be read as String.");
        }
    }

    public static TheoryData<string, string, string> NonTextKinds => new()
    {
        { "5", "a Number", "number" },
        { "5.5", "a Number", "double" },
        { "true", "a Boolean", "bool" },
        { "{}", "an Object", "map" },
        { "[1]", "an Array", "array" },
    };

    [Theory]
    [MemberData(nameof(NonTextKinds))]
    public async Task A_string_input_that_is_present_and_not_text_fails_the_block_naming_the_port_and_the_kind(
        string json, string kind, string clr)
    {
        object? Clr() => clr switch
        {
            "number" => 5, "double" => 5.5, "bool" => true, "map" => new Dictionary<string, object?>(), _ => new List<object?> { 1 },
        };

        foreach (var (blockTypeId, port) in new[] { ("HttpRequest", "Url"), ("HttpRequest", "Method"), ("HttpRequest", "Body") })
        {
            var parsed = JsonDocument.Parse(json).RootElement.Clone();
            foreach (var (value, valueKind) in new (object?, string)[] { (parsed, kind), (Clr(), kind) })
            {
                var act = async () => await Run(Create(blockTypeId), port, value);

                (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(
                    $"Input '{port}' cannot be read as String: the value is {valueKind}, not text.");
            }
        }
    }

    private static JsonElement JsonText(string text) => JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone();

    [Fact]
    public async Task A_json_string_is_text_for_every_standard_block_that_reads_one()
    {
        async Task<object?> Convert(IWorkflowBlockPlugin block, string text) =>
            (await block.ExecuteAsync(new Dictionary<string, object?> { ["Value"] = JsonText(text) }, Context(), CancellationToken.None))["Result"];

        (await Convert(new StringToIntBlock(), "42")).Should().Be(42L);
        (await Convert(new StringToNumberBlock(), "1.5")).Should().Be(1.5);
        (await Convert(new StringToBoolBlock(), "true")).Should().Be(true);

        var rethrow = async () => await new RethrowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Message"] = JsonText("boom") }, Context(), CancellationToken.None);
        await rethrow.Should().ThrowAsync<SubCanvasRethrowException>().WithMessage("boom");

        var notAGuid = async () => await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = JsonText("not-a-guid") }, Context(), CancellationToken.None);
        (await notAGuid.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("valid Guid string");
    }

    [Theory]
    [InlineData("StringToInt")]
    [InlineData("StringToNumber")]
    [InlineData("StringToBool")]
    public async Task A_conversion_block_refuses_a_value_that_is_not_text_naming_the_input_and_the_kind(string blockTypeId)
    {
        IWorkflowBlockPlugin block = blockTypeId switch
        {
            "StringToInt" => new StringToIntBlock(),
            "StringToNumber" => new StringToNumberBlock(),
            _ => new StringToBoolBlock(),
        };

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = 5 }, Context(), CancellationToken.None);
        var absent = async () => await block.ExecuteAsync(new Dictionary<string, object?>(), Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(
            "Input 'Value' cannot be read as String: the value is a Number, not text.");
        (await absent.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Be(
            "Input 'Value' is required and must be a string.");
    }

    [Fact]
    public async Task An_undefined_json_value_is_named_as_such_and_a_read_only_map_is_an_object()
    {
        var undefined = async () => await Run(new HttpRequestBlock(), "Method", default(JsonElement));
        var map = async () => await Run(new HttpRequestBlock(), "Method", new Dictionary<string, object?>().AsReadOnly());

        (await undefined.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().EndWith("the value is an undefined value, not text.");
        (await map.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().EndWith("the value is an Object, not text.");
    }

    [Fact]
    public async Task An_absent_scalar_input_takes_the_blocks_documented_default()
    {
        var inputs = new Dictionary<string, object?> { ["To"] = 2.0, ["Url"] = "https://example.com/" };
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync("Body", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, object?>());

        var result = await new ForLoopBlock().ExecuteAsync(inputs, Context(executor), CancellationToken.None);

        result["Iterations"].Should().Be(2, "From defaults to 0 and Step to 1 when they are not supplied");
    }

    [Theory]
    [InlineData("StringToInt", "Value", "12abc")]
    [InlineData("StringToNumber", "Value", "1,5")]
    [InlineData("StringToBool", "Value", "yes")]
    [InlineData("StringToInt", "Value", "5\0")]
    [InlineData("StringToInt", "Value", " 5")]
    [InlineData("StringToInt", "Value", "0x10")]
    [InlineData("StringToInt", "Value", "1e3")]
    [InlineData("StringToInt", "Value", "5.5")]
    [InlineData("StringToInt", "Value", "9223372036854775808")]
    [InlineData("StringToNumber", "Value", "5\0")]
    [InlineData("StringToNumber", "Value", " 5")]
    [InlineData("StringToNumber", "Value", "Infinity")]
    [InlineData("StringToNumber", "Value", "NaN")]
    [InlineData("StringToNumber", "Value", "1e500")]
    [InlineData("StringToBool", "Value", " true ")]
    public async Task A_conversion_block_refuses_text_the_shared_coercion_refuses(string blockTypeId, string port, string text)
    {
        IWorkflowBlockPlugin block = blockTypeId switch
        {
            "StringToInt" => new StringToIntBlock(),
            "StringToNumber" => new StringToNumberBlock(),
            _ => new StringToBoolBlock(),
        };

        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { [port] = text }, Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("Cannot convert");
    }

    [Theory]
    [InlineData("ForLoop", "To", "5")]
    [InlineData("WaitForDuration", "DurationMs", "5")]
    [InlineData("ForLoop", "To", "2.5")]
    [InlineData("HttpRequest", "ThrowOnFailure", "true")]
    public async Task A_standard_scalar_input_does_not_parse_text_at_run_time(string blockTypeId, string port, string text)
    {
        var value = (object)text;
        var block = blockTypeId switch
        {
            "ForLoop" => (IWorkflowBlockPlugin)new ForLoopBlock(),
            "WaitForDuration" => new WaitForDurationBlock(),
            _ => new HttpRequestBlock(),
        };
        var act = async () => await Run(block, port, value);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain($"Input '{port}' cannot be read as");
    }

    [Fact]
    public async Task An_Int_beyond_int32_that_the_compiler_accepts_runs_in_a_block()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var wait = async () => await new WaitForDurationBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["DurationMs"] = 3_000_000_000L }, Context(), cancelled.Token);
        await wait.Should().ThrowAsync<OperationCanceledException>();

        var inputs = new Dictionary<string, object?> { ["MaxIterations"] = 3_000_000_000L, ["To"] = 1.0 };
        var loop = await new ForLoopBlock().ExecuteAsync(inputs, Context(), CancellationToken.None);
        loop["Iterations"].Should().Be(1);
    }

    [Theory]
    [InlineData("WaitForDuration", "DurationMs", 4_294_967_295L)]
    [InlineData("HttpRequest", "TimeoutSeconds", 4_294_968L)]
    public async Task An_Int_beyond_what_the_framework_takes_fails_the_node_as_an_overflow(string block, string port, long value)
    {
        IWorkflowBlockPlugin plugin = block == "WaitForDuration" ? new WaitForDurationBlock() : new HttpRequestBlock();
        var act = async () => await Run(plugin, port, value);

        (await act.Should().ThrowAsync<OverflowException>()).Which.Message.Should().Contain(port);
    }

}
