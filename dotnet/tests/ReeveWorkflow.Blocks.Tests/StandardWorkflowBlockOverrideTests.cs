using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Hosting.Defaults;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class StandardWorkflowBlockOverrideTests
{
    [Fact]
    public async Task AnOverriddenIdRunsTheReplacementAndTheStandardBlockIsGone()
    {
        var blocks = Catalogue(o => o.Override<CountingHttpRequestBlockFactory>("HttpRequest"));

        var factory = blocks.Find("HttpRequest");
        var outputs = await factory!.Create().ExecuteAsync(
            new Dictionary<string, object?> { ["Url"] = "https://example.invalid" },
            Context(), CancellationToken.None);

        outputs["Body"].Should().Be("substitute",
            "the replacement is what the engine executes for that id; if the standard block also reached the "
            + "container the catalogue would have refused to build at all");
    }

    [Fact]
    public void RegisteringTheStandardBlocksTwiceOverOneIdIsWhatTheOverrideExistsToAvoid()
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new CountingHttpRequestBlockFactory());

        var build = () => new RegisteredWorkflowBlocks(
            services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());

        build.Should().Throw<InvalidOperationException>().WithMessage("*HttpRequest*");
    }

    [Fact]
    public void AnOverrideNamingAnIdThePackageDoesNotCarryIsRefusedWhereItIsWritten()
    {
        var services = new ServiceCollection();

        var register = () => services.AddStandardWorkflowBlocks(o =>
            o.Override<CountingHttpRequestBlockFactory>("HttpRequst"));

        register.Should().Throw<ArgumentException>()
            .WithMessage("*HttpRequst*", "a typo has to name itself, which is the whole reason this is not a list of exclusions");
    }

    [Fact]
    public void AReplacementWhoseOwnIdDisagreesWithTheIdItReplacesIsRefused()
    {
        var blocks = () => Catalogue(o => o.Override<CountingHttpRequestBlockFactory>("ParseJson"));

        blocks.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("ParseJson").And.Contain("HttpRequest",
                "silently taking the replacement's own id would create a new block and drop the standard one");
    }

    [Fact]
    public async Task AReplacementIsBuiltThroughTheContainerSoItMayTakeConstructorDependencies()
    {
        var services = new ServiceCollection();
        var stamp = new Stamp("from-di");
        services.AddSingleton(stamp);
        services.AddStandardWorkflowBlocks(o => o.Override<StampedHttpRequestBlockFactory>("HttpRequest"));

        var blocks = new RegisteredWorkflowBlocks(
            services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());

        var outputs = await blocks.Find("HttpRequest")!.Create().ExecuteAsync(
            new Dictionary<string, object?>(), Context(), CancellationToken.None);

        outputs["Body"].Should().Be("from-di");
    }

    [Fact]
    public async Task WithNoOverridesTheStandardBlocksAreTheOnesThatRun()
    {
        var blocks = Catalogue(null);

        var outputs = await blocks.Find("StringToInt")!.Create().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "41" }, Context(), CancellationToken.None);

        outputs["Result"].Should().Be(41);
    }

    private static RegisteredWorkflowBlocks Catalogue(Action<StandardWorkflowBlockOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks(configure);
        return new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
    }

    private static WorkflowBlockContext Context() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = NullLogger.Instance,
        Services = new EmptyServices()
    };

    private sealed record Stamp(string Value);

    private sealed class CountingHttpRequestBlockFactory : HttpRequestBlockFactory
    {
        public override IWorkflowBlockPlugin Create() => new FixedBodyBlock("substitute");
    }

    private sealed class StampedHttpRequestBlockFactory(Stamp stamp) : HttpRequestBlockFactory
    {
        public override IWorkflowBlockPlugin Create() => new FixedBodyBlock(stamp.Value);
    }

    private sealed class FixedBodyBlock(string body) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["Body"] = body });
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
