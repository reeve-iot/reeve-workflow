using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationCancellationTests
{
    private static readonly CanvasSignature CollectionOfInt =
        Signature(inputs: [("c", PortValueType.CollectionOf(PortValueType.Int), true)]);

    private sealed class CancellingShapes(IBlockShapeSource inner, int cancelAt, CancellationTokenSource source) : IBlockShapeSource
    {
        public int Lookups { get; private set; }

        public BlockShape? Find(string blockTypeId)
        {
            if (++Lookups == cancelAt)
                source.Cancel();
            return inner.Find(blockTypeId);
        }
    }

    [Fact]
    public void A_token_cancelled_before_the_compile_is_thrown_and_nothing_is_answered()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        FluentActions.Invoking(() => WorkflowCompiler.Compile(
                GenericShapes.FilterChain(3), Catalogue(GenericShapes.Filter), CollectionOfInt, cancellationToken: source.Token))
            .Should().Throw<OperationCanceledException>();
        FluentActions.Invoking(() => WorkflowCompiler.Compile(Graph([]), Catalogue(), CanvasSignature.Empty, cancellationToken: source.Token))
            .Should().Throw<OperationCanceledException>("a canvas with no node is still checked once per phase");
    }

    [Fact]
    public void A_token_cancelled_by_the_catalogue_while_binding_ends_the_compile_before_the_next_node_is_bound()
    {
        using var source = new CancellationTokenSource();
        var shapes = new CancellingShapes(Catalogue(GenericShapes.Pass), 1, source);

        FluentActions.Invoking(() => WorkflowCompiler.Compile(GenericShapes.PlainChain(200), shapes, CollectionOfInt, cancellationToken: source.Token))
            .Should().Throw<OperationCanceledException>();

        shapes.Lookups.Should().Be(1);
    }

    [Fact]
    public void A_token_cancelled_by_host_code_at_one_node_ends_the_compile_before_the_next()
    {
        using var source = new CancellationTokenSource();
        var graph = CompilationBackstopTests.AskingTheHost();
        graph.Nodes = [.. graph.Nodes, Node("y", "a", new { v = "expr:${host.r}" })];
        graph.Edges = [.. graph.Edges!, Edge("e2", "x", "y")];
        var asked = 0;
        var options = new CompilationOptions
        {
            NamespaceRegistrations = [HostNamespaceRegistration.For(new CompilationBackstopTests.HostCheck(() => { asked++; source.Cancel(); return null; }))],
        };

        FluentActions.Invoking(() => WorkflowCompiler.Compile(graph, Catalogue(Shape("a")), CanvasSignature.Empty, options, cancellationToken: source.Token))
            .Should().Throw<OperationCanceledException>();

        asked.Should().Be(1);
    }

    [Fact]
    public async Task A_call_whose_token_is_cancelled_during_the_compile_throws_it_logs_nothing_and_records_nothing()
    {
        using var source = new CancellationTokenSource();
        var cancel = true;
        var store = new CompilationServiceTests.RecordingStore();
        var log = new CompilationFaultServiceTests.ErrorLog();
        var service = CompilationScopes.Service(
            new CompilationServiceTests.MovableCatalogue().Add(Shape("a")), new CompilationServiceTests.FakeWorkflowStore(), store,
            new CompilationOptions
            {
                NamespaceRegistrations = [HostNamespaceRegistration.For(new CompilationBackstopTests.HostCheck(() => { if (cancel) source.Cancel(); return null; }))],
            },
            logger: log);
        var version = CompilationServiceTests.Version(CompilationBackstopTests.AskingTheHost());

        await FluentActions.Awaiting(() => service.EnsureAsync(version, null, source.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        store.Saves.Should().Be(0);
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        cancel = false;
        (await service.EnsureAsync(version, null, CancellationToken.None)).IsValid.Should().BeTrue();
        store.Saves.Should().Be(1);
    }

    [Fact]
    public async Task A_chain_through_the_service_cancelled_at_its_first_nodes_check_stops_before_the_next_nodes()
    {
        var chain = GenericShapes.FilterChain(18);
        foreach (var node in chain.Nodes.Skip(1))
            node.Config = new() { ["Predicate"] = System.Text.Json.JsonSerializer.SerializeToElement("expr:${host.q}") };
        using var source = new CancellationTokenSource();
        var asked = 0;
        var service = CompilationScopes.Service(
            new CompilationServiceTests.MovableCatalogue().Add(GenericShapes.Filter),
            new CompilationServiceTests.FakeWorkflowStore(), new CompilationServiceTests.RecordingStore(),
            new CompilationOptions
            {
                NamespaceRegistrations = [HostNamespaceRegistration.For(new CompilationBackstopTests.HostCheck(() => { asked++; source.Cancel(); return null; }))],
            });
        var version = CompilationServiceTests.Version(chain,
            inputs: "[{\"name\":\"c\",\"type\":\"Collection<Int>\",\"isRequired\":true}]", outputs: "[]");

        await FluentActions.Awaiting(() => service.EnsureAsync(version, null, source.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        asked.Should().Be(1);
    }
}
