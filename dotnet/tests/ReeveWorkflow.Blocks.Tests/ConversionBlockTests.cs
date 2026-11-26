using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ConversionBlockTests
{
    private readonly WorkflowBlockContext _context = new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>()
    };

    [Fact]
    public async Task StringToInt_converts_valid_string()
    {
        var block = new StringToIntBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "42" }, _context, CancellationToken.None);

        result["Result"].Should().Be(42);
    }

    [Fact]
    public async Task StringToInt_throws_for_non_numeric_string()
    {
        var block = new StringToIntBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "abc" }, _context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task IntToString_converts_int_value()
    {
        var block = new IntToStringBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = 7 }, _context, CancellationToken.None);

        result["Result"].Should().Be("7");
    }

    [Fact]
    public async Task IntToString_throws_for_non_int_type()
    {
        var block = new IntToStringBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "not-an-int" }, _context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StringToBool_converts_true_string()
    {
        var block = new StringToBoolBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "True" }, _context, CancellationToken.None);

        result["Result"].Should().Be(true);
    }

    [Fact]
    public async Task StringToBool_throws_for_invalid_string()
    {
        var block = new StringToBoolBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "yes" }, _context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task BoolToString_converts_false_to_string()
    {
        var block = new BoolToStringBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = false }, _context, CancellationToken.None);

        result["Result"].Should().Be("false");
    }

    [Fact]
    public async Task BoolToString_throws_for_non_bool()
    {
        var block = new BoolToStringBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "true" }, _context, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task NumberToString_converts_double_to_string()
    {
        var block = new NumberToStringBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = 3.14 }, _context, CancellationToken.None);

        result["Result"].Should().Be("3.14");
    }

    [Fact]
    public async Task NumberToString_throws_for_non_numeric_type()
    {
        var block = new NumberToStringBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "3.14" }, _context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StringToNumber_converts_valid_string()
    {
        var block = new StringToNumberBlock();
        var result = await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "2.718" }, _context, CancellationToken.None);

        result["Result"].Should().Be(2.718);
    }

    [Fact]
    public async Task StringToNumber_throws_for_non_numeric_string()
    {
        var block = new StringToNumberBlock();
        var act = async () => await block.ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "not-a-number" }, _context, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
