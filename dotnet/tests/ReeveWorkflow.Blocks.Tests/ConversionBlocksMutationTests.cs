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

public sealed class ConversionBlocksMutationTests
{
    private sealed class EmptyProvider : IServiceProvider
    {
        public object? GetService(Type t) => null;
    }

    private static WorkflowBlockContext Ctx() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = NullLogger.Instance,
        Services = new EmptyProvider(),
    };

    private static Dictionary<string, object?> In(object? value) => new() { ["Value"] = value };

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData("-17", -17)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("9999999999999", 9999999999999L)]
    public async Task StringToInt_parses_to_exact_int(string input, long expected)
    {
        var result = await new StringToIntBlock().ExecuteAsync(In(input), Ctx(), CancellationToken.None);

        result["Result"].Should().BeOfType<long>().Which.Should().Be(expected);
    }

    [Fact]
    public async Task StringToInt_trims_surrounding_whitespace()
    {
        var result = await new StringToIntBlock().ExecuteAsync(In("  123  "), Ctx(), CancellationToken.None);

        result["Result"].Should().Be(123);
    }

    [Fact]
    public async Task StringToInt_missing_key_throws_argument_exception()
    {
        var act = () => new StringToIntBlock().ExecuteAsync(
            new Dictionary<string, object?>(), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StringToInt_non_string_value_throws_argument_exception()
    {
        var act = () => new StringToIntBlock().ExecuteAsync(In(42), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("3.5")]
    [InlineData("9223372036854775808")]
    public async Task StringToInt_unparseable_throws_invalid_operation(string input)
    {
        var act = () => new StringToIntBlock().ExecuteAsync(In(input), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task IntToString_int_arm_renders_decimal_string()
    {
        var result = await new IntToStringBlock().ExecuteAsync(In(255), Ctx(), CancellationToken.None);

        result["Result"].Should().BeOfType<string>().Which.Should().Be("255");
    }

    [Fact]
    public async Task IntToString_negative_int_keeps_sign()
    {
        var result = await new IntToStringBlock().ExecuteAsync(In(-7), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("-7");
    }

    [Fact]
    public async Task IntToString_long_arm_renders_full_value()
    {
        var result = await new IntToStringBlock().ExecuteAsync(In(9_999_999_999L), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("9999999999");
    }

    [Fact]
    public async Task IntToString_whole_double_arm_drops_fraction_and_uses_long_form()
    {
        var result = await new IntToStringBlock().ExecuteAsync(In(12.0d), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("12");
    }

    [Fact]
    public async Task IntToString_fractional_double_arm_renders_invariant_decimal()
    {
        var result = await new IntToStringBlock().ExecuteAsync(In(3.25d), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("3.25");
    }

    [Fact]
    public async Task IntToString_negative_whole_double_uses_long_form()
    {
        var result = await new IntToStringBlock().ExecuteAsync(In(-4.0d), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("-4");
    }

    [Fact]
    public async Task IntToString_missing_key_throws_argument_exception()
    {
        var act = () => new IntToStringBlock().ExecuteAsync(
            new Dictionary<string, object?>(), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    [InlineData("  true  ", true)]
    public async Task StringToBool_parses_to_exact_bool(string input, bool expected)
    {
        var result = await new StringToBoolBlock().ExecuteAsync(In(input), Ctx(), CancellationToken.None);

        result["Result"].Should().BeOfType<bool>().Which.Should().Be(expected);
    }

    [Fact]
    public async Task StringToBool_missing_key_throws_argument_exception()
    {
        var act = () => new StringToBoolBlock().ExecuteAsync(
            new Dictionary<string, object?>(), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StringToBool_non_string_value_throws_argument_exception()
    {
        var act = () => new StringToBoolBlock().ExecuteAsync(In(true), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("tru")]
    public async Task StringToBool_unparseable_throws_invalid_operation(string input)
    {
        var act = () => new StringToBoolBlock().ExecuteAsync(In(input), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public async Task BoolToString_renders_lowercase_literal(bool input, string expected)
    {
        var result = await new BoolToStringBlock().ExecuteAsync(In(input), Ctx(), CancellationToken.None);

        result["Result"].Should().BeOfType<string>().Which.Should().Be(expected);
    }

    [Fact]
    public async Task BoolToString_missing_key_throws_argument_exception()
    {
        var act = () => new BoolToStringBlock().ExecuteAsync(
            new Dictionary<string, object?>(), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task BoolToString_non_bool_value_throws_argument_exception()
    {
        var act = () => new BoolToStringBlock().ExecuteAsync(In("true"), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task BoolToString_null_value_throws_argument_exception()
    {
        var act = () => new BoolToStringBlock().ExecuteAsync(In(null), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task NumberToString_double_arm_renders_invariant_decimal()
    {
        var result = await new NumberToStringBlock().ExecuteAsync(In(1.5d), Ctx(), CancellationToken.None);

        result["Result"].Should().BeOfType<string>().Which.Should().Be("1.5");
    }

    [Fact]
    public async Task NumberToString_float_arm_renders_invariant_decimal()
    {
        var result = await new NumberToStringBlock().ExecuteAsync(In(2.5f), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("2.5");
    }

    [Fact]
    public async Task NumberToString_int_arm_renders_decimal_string()
    {
        var result = await new NumberToStringBlock().ExecuteAsync(In(7), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("7");
    }

    [Fact]
    public async Task NumberToString_long_arm_renders_full_value()
    {
        var result = await new NumberToStringBlock().ExecuteAsync(In(5_000_000_000L), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("5000000000");
    }

    [Fact]
    public async Task NumberToString_decimal_arm_renders_invariant_decimal()
    {
        var result = await new NumberToStringBlock().ExecuteAsync(In(3.14m), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("3.14");
    }

    [Fact]
    public async Task NumberToString_negative_double_keeps_sign()
    {
        var result = await new NumberToStringBlock().ExecuteAsync(In(-0.25d), Ctx(), CancellationToken.None);

        result["Result"].Should().Be("-0.25");
    }

    [Fact]
    public async Task NumberToString_missing_key_throws_argument_exception()
    {
        var act = () => new NumberToStringBlock().ExecuteAsync(
            new Dictionary<string, object?>(), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task NumberToString_unconvertible_string_throws_invalid_operation()
    {
        var act = () => new NumberToStringBlock().ExecuteAsync(In("nope"), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("0", 0d)]
    [InlineData("42", 42d)]
    [InlineData("3.14", 3.14d)]
    [InlineData("-2.5", -2.5d)]
    [InlineData("1e3", 1000d)]
    [InlineData("  12.5  ", 12.5d)]
    public async Task StringToNumber_parses_to_exact_double(string input, double expected)
    {
        var result = await new StringToNumberBlock().ExecuteAsync(In(input), Ctx(), CancellationToken.None);

        result["Result"].Should().BeOfType<double>().Which.Should().Be(expected);
    }

    [Fact]
    public async Task StringToNumber_uses_invariant_culture_dot_separator()
    {
        var result = await new StringToNumberBlock().ExecuteAsync(In("1.5"), Ctx(), CancellationToken.None);

        result["Result"].Should().Be(1.5d);
    }

    [Fact]
    public async Task StringToNumber_missing_key_throws_argument_exception()
    {
        var act = () => new StringToNumberBlock().ExecuteAsync(
            new Dictionary<string, object?>(), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StringToNumber_non_string_value_throws_argument_exception()
    {
        var act = () => new StringToNumberBlock().ExecuteAsync(In(1.5d), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.2.3")]
    public async Task StringToNumber_unparseable_throws_invalid_operation(string input)
    {
        var act = () => new StringToNumberBlock().ExecuteAsync(In(input), Ctx(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StringToInt_then_IntToString_round_trips()
    {
        var ctx = Ctx();
        var parsed = await new StringToIntBlock().ExecuteAsync(In("-128"), ctx, CancellationToken.None);
        var back = await new IntToStringBlock().ExecuteAsync(In(parsed["Result"]), ctx, CancellationToken.None);

        parsed["Result"].Should().Be(-128);
        back["Result"].Should().Be("-128");
    }

    [Fact]
    public async Task StringToNumber_then_NumberToString_round_trips()
    {
        var ctx = Ctx();
        var parsed = await new StringToNumberBlock().ExecuteAsync(In("2.75"), ctx, CancellationToken.None);
        var back = await new NumberToStringBlock().ExecuteAsync(In(parsed["Result"]), ctx, CancellationToken.None);

        parsed["Result"].Should().Be(2.75d);
        back["Result"].Should().Be("2.75");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task StringToBool_then_BoolToString_round_trips(string input, bool parsedValue)
    {
        var ctx = Ctx();
        var parsed = await new StringToBoolBlock().ExecuteAsync(In(input), ctx, CancellationToken.None);
        var back = await new BoolToStringBlock().ExecuteAsync(In(parsed["Result"]), ctx, CancellationToken.None);

        parsed["Result"].Should().Be(parsedValue);
        back["Result"].Should().Be(input);
    }
}
