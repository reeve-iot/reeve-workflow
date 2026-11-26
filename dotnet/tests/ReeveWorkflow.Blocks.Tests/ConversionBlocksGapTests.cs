using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Blocks;
using Xunit;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ConversionBlocksGapTests
{
    private sealed class EmptyProvider : IServiceProvider { public object? GetService(Type t) => null; }
    private static WorkflowBlockContext Ctx() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = NullLogger.Instance,
        Services = new EmptyProvider(),
    };

    [Fact]
    public async Task IntToString_null_value_throws_required_not_nre()
    {
        var act = () => new IntToStringBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = null }, Ctx(), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*'Value' is required*");
    }

    [Fact]
    public async Task IntToString_unconvertible_type_reports_cannot_convert()
    {
        var act = () => new IntToStringBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "not-an-int" }, Ctx(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cannot convert*");
    }

    [Fact]
    public async Task NumberToString_null_value_throws_required_not_nre()
    {
        var act = () => new NumberToStringBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = null }, Ctx(), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*'Value' is required*");
    }

    [Fact]
    public async Task NumberToString_unconvertible_type_reports_cannot_convert()
    {
        var act = () => new NumberToStringBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = true }, Ctx(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Cannot convert*");
    }
}
