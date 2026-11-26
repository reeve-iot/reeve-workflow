using FluentAssertions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class WorkflowExpressionNamespaceTests
{
    [Fact]
    public async Task Preview_resolves_the_same_registered_namespace()
    {
        var registration = Registration("host", new TestResolver("host", segments =>
            segments is ["value"]
                ? ResolveResult.Resolved(42d)
                : ResolveResult.Failed()));
        var preview = new ExpressionPreviewService(null, [registration]);

        var result = await preview.PreviewAsync("${host.value}", "global", new TestServices());

        result.Ok.Should().BeTrue();
        result.Value.Should().Be(42d);
    }

    [Fact]
    public async Task Two_runtime_resolvers_for_one_namespace_are_refused()
    {
        var first = Registration("host", new TestResolver("host", _ => ResolveResult.Resolved(null)));
        var second = Registration("host", new TestResolver("host", _ => ResolveResult.Resolved(null)));

        var act = () => WorkflowExpressionNamespaces.MergeResolversAsync(
            [first, second], new ExpressionNamespaceScope(new TestServices(), null, null, null), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("Duplicate namespace registration 'host'.*");
    }

    [Theory]
    [InlineData("previous")]
    [InlineData("parameter")]
    [InlineData("now")]
    [InlineData("helper")]
    [InlineData("item")]
    [InlineData("value")]
    public void Library_names_are_refused_when_a_host_registration_is_created(string name)
    {
        var act = () => Registration(name, new TestResolver(name, _ => ResolveResult.Resolved(null)));

        act.Should().Throw<ArgumentException>().WithMessage("*reserved by the library*");
    }

    [Fact]
    public async Task Preview_does_not_run_a_factory_for_an_unreferenced_namespace()
    {
        var registration = new WorkflowExpressionNamespace("host", 1, new TestCheck("host", NamespacePathCheck.Yes),
            (_, _) => throw new InvalidOperationException("unused namespace must not be opened"));
        var preview = new ExpressionPreviewService(null, [registration]);

        var result = await preview.PreviewAsync("1 + 1", "global", new TestServices());

        result.Ok.Should().BeTrue();
        result.Value.Should().Be(2L);
        result.Type.Should().Be("Int");
    }

    [Fact]
    public async Task Preview_reports_a_throwing_factory_for_a_referenced_namespace_as_an_expression_error()
    {
        var registration = new WorkflowExpressionNamespace("host", 1, new TestCheck("host", NamespacePathCheck.Yes),
            (_, _) => throw new InvalidOperationException("factory unavailable"));
        var preview = new ExpressionPreviewService(null, [registration]);

        var result = await preview.PreviewAsync("${host.value}", "global", new TestServices());

        result.Ok.Should().BeFalse();
        result.Error!.Message.Should().Contain("host").And.Contain("factory unavailable");
    }

    [Fact]
    public async Task Preview_does_not_run_a_factory_for_a_namespace_that_is_only_a_prefix()
    {
        var registration = new WorkflowExpressionNamespace("host", 1, new TestCheck("host", NamespacePathCheck.Yes),
            (_, _) => throw new InvalidOperationException("host is only a prefix of hostile"));
        var preview = new ExpressionPreviewService(null, [registration]);

        var result = await preview.PreviewAsync("${hostile.value}", "global", new TestServices());

        result.Ok.Should().BeFalse();
        result.Error!.RuleId.Should().Be("expression-unknown-namespace");
    }

    [Fact]
    public async Task Preview_does_not_run_a_factory_for_reference_text_inside_a_string_literal()
    {
        var registration = new WorkflowExpressionNamespace("host", 1, new TestCheck("host", NamespacePathCheck.Yes),
            (_, _) => throw new InvalidOperationException("string contents are not variable references"));
        var preview = new ExpressionPreviewService(null, [registration]);

        var result = await preview.PreviewAsync("\"${host.value}\"", "global", new TestServices());

        result.Ok.Should().BeTrue();
        result.Value.Should().Be("${host.value}");
    }

    [Fact]
    public async Task Preview_retains_namespaces_from_its_cached_context_when_registrations_exist()
    {
        var cached = new TestResolver("cached", segments => segments is ["value"]
            ? ResolveResult.Resolved(7d)
            : ResolveResult.Failed());
        var preview = new ExpressionPreviewService(null,
            [Registration("host", new TestResolver("host", _ => ResolveResult.Failed()))]);
        preview.SetContext("workflow", new ExpressionContext(namespaces: [new ExpressionNamespaceBinding(cached, 1, new ContextTestNamespaces.InferredCheck(cached))]));

        var result = await preview.PreviewAsync("${cached.value}", "workflow", new TestServices());

        result.Ok.Should().BeTrue();
        result.Value.Should().Be(7d);
    }

    [Fact]
    public async Task Preview_refuses_a_namespace_set_on_the_scope_context_and_also_registered()
    {
        var pinned = new TestResolver("host", _ => ResolveResult.Resolved(1d));
        var preview = new ExpressionPreviewService(null,
            [Registration("host", new TestResolver("host", _ => ResolveResult.Resolved(2d)))]);
        preview.SetContext("workflow", new ExpressionContext(namespaces: [new ExpressionNamespaceBinding(pinned, 1, new ContextTestNamespaces.InferredCheck(pinned))]));

        var conflicting = await preview.PreviewAsync("${host.value}", "workflow", new TestServices());
        var unrelated = await preview.PreviewAsync("1 + 1", "workflow", new TestServices());

        conflicting.Ok.Should().BeFalse();
        conflicting.Error!.Message.Should().Contain("'host'").And.Contain("registered by the host");
        unrelated.Ok.Should().BeTrue("a preview that never reads the namespace has nothing to choose between");
    }

    [Fact]
    public void A_context_lists_only_the_names_of_the_host_namespaces()
    {
        var depot = new TestResolver("depot", _ => ResolveResult.Resolved(1d));
        var context = new ExpressionContext.Builder()
            .Namespace(depot, 1, new ContextTestNamespaces.InferredCheck(depot))
            .Build();

        context.Namespaces.Should().Equal("depot");
        new ExpressionContext().Namespaces.Should().BeEmpty();
    }

    [Fact]
    public void Existing_static_checks_can_additionally_report_a_key_port_type()
    {
        var check = new TypedCheck();

        check.CheckWithKeyType(["id"]).Should().Be(new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Collection<Device>"));
        _ = new ExpressionPreviewService();
    }

    private static WorkflowExpressionNamespace Registration(
        string name, IExpressionNamespaceResolver resolver, IExpressionNamespaceStaticCheck? check = null, int keySegments = 1) =>
        new(name, keySegments, check ?? new TestCheck(name, NamespacePathCheck.Yes),
            (_, _) => Task.FromResult(resolver));

    private sealed class TestServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed record TestCheck(string Namespace, NamespacePathCheck Answer) : IExpressionNamespaceStaticCheck
    {
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(Answer, "Number");
    }

    private sealed class TypedCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "host";
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            new(NamespacePathCheck.Yes, "Collection<Device>");
    }

    private sealed record TestResolver(
        string Namespace, Func<string[], ResolveResult> ResolvePath) : IExpressionNamespaceResolver
    {
        public ResolveResult Resolve(string[] segments) => ResolvePath(segments);
    }
}
