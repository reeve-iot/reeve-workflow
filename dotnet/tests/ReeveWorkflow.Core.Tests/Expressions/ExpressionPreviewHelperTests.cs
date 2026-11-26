using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionPreviewHelperTests
{
    [Fact]
    public async Task Preview_does_not_check_or_read_when_expression_has_no_helper_path()
    {
        var resolver = new Resolver(throwCheck: true, throwRead: true);
        var result = await new ExpressionPreviewService(resolver)
            .PreviewAsync("1 + 2", "global", new ServiceCollection().BuildServiceProvider());

        result.Ok.Should().BeTrue();
        result.Value.Should().Be(3L);
        resolver.CheckCalls.Should().Be(0);
        resolver.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Preview_reads_its_helper_snapshot_once()
    {
        var resolver = new Resolver();
        var result = await new ExpressionPreviewService(resolver)
            .PreviewAsync("${helper.x} + ${helper.x}", "global", new ServiceCollection().BuildServiceProvider());

        result.Ok.Should().BeTrue();
        result.Value.Should().Be(8L);
        resolver.CheckCalls.Should().Be(1);
        resolver.ReadCalls.Should().Be(1);
        resolver.ReadPaths.Should().ContainSingle();
    }

    [Fact]
    public async Task Preview_keeps_syntax_error_position_and_rule_before_helper_resolution()
    {
        var resolver = new Resolver();
        var result = await new ExpressionPreviewService(resolver)
            .PreviewAsync("1 + )", "global", new ServiceCollection().BuildServiceProvider());

        result.Ok.Should().BeFalse();
        result.Error!.RuleId.Should().Be("expression-syntax");
        result.Error.Position.Should().Be(4);
        resolver.CheckCalls.Should().Be(0);
    }

    [Fact]
    public async Task Preview_treats_a_malformed_expression_as_syntax_before_collecting_helpers()
    {
        var resolver = new Resolver(throwCheck: true);
        var result = await new ExpressionPreviewService(resolver)
            .PreviewAsync("${helper.x} + \ud800", "global", new ServiceCollection().BuildServiceProvider());

        result.Ok.Should().BeFalse();
        result.Error!.RuleId.Should().Be("expression-syntax");
        resolver.CheckCalls.Should().Be(0);
        resolver.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_throwing_preview_helper_read_is_expression_runtime_error()
    {
        var resolver = new Resolver(throwRead: true);
        var result = await new ExpressionPreviewService(resolver)
            .PreviewAsync("${helper.x}", "global", new ServiceCollection().BuildServiceProvider());

        result.Ok.Should().BeFalse();
        result.Error!.RuleId.Should().Be("expression-runtime-error");
    }

    [Fact]
    public async Task Preview_reports_a_check_failure_without_reading_helpers()
    {
        var resolver = new Resolver(throwCheck: true);
        var result = await new ExpressionPreviewService(resolver)
            .PreviewAsync("${helper.x}", "global", new ServiceCollection().BuildServiceProvider());

        result.Ok.Should().BeFalse();
        result.Error!.Message.Should().Contain("Could not resolve helper values: check failed");
        resolver.CheckCalls.Should().Be(1);
        resolver.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Preview_preserves_a_present_null_helper_value()
    {
        var resolver = new Resolver(term: "Int?", presentNull: true);
        var result = await new ExpressionPreviewService(resolver)
            .PreviewAsync("${helper.x}", "global", new ServiceCollection().BuildServiceProvider());

        result.Ok.Should().BeTrue();
        result.Value.Should().BeNull();
        result.Value.Should().BeNull();
        resolver.CheckCalls.Should().Be(1);
        resolver.ReadCalls.Should().Be(1);
    }

    private sealed class Resolver(bool throwRead = false, bool throwCheck = false, string term = "Int", bool presentNull = false) : IHelperResolver
    {
        public int CheckCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public IReadOnlyCollection<HelperPath> ReadPaths { get; private set; } = [];

        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            CheckCalls++;
            if (throwCheck) return ValueTask.FromException<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(new InvalidOperationException("check failed"));
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, term)));
        }

        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            ReadCalls++;
            ReadPaths = paths.ToArray();
            if (throwRead) return ValueTask.FromException<IReadOnlyDictionary<HelperPath, object?>>(new InvalidOperationException("read failed"));
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(
                paths.ToDictionary(path => path, _ => (object?)(presentNull ? null : 4L)));
        }
    }
}
