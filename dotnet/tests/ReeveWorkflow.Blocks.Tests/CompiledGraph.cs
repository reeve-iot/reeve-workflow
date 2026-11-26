using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks.Tests;

internal static class CompiledGraph
{
    public static RegisteredWorkflowBlocks Blocks(params IWorkflowBlockFactory[] extra)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        foreach (var factory in extra)
            services.AddSingleton(factory);
        return new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
    }

    public static WorkflowDefinition Read(string json) =>
        StoredWorkflowDocument.Read(json, out var fault)?.Definition ?? throw new InvalidOperationException(fault!.Describe());

    public static CompilationResult Compile(WorkflowDefinition definition, IWorkflowBlocks blocks) =>
        WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);

    public static string[] Refusals(WorkflowDefinition definition, IWorkflowBlocks blocks) =>
        [.. Compile(definition, blocks).Diagnostics.Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}")];

    public static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
        WorkflowDefinition definition, IWorkflowBlocks blocks)
    {
        var result = Compile(definition, blocks);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var start = definition.Nodes.Single(n => n.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId).Id;
        var parameters = new Dictionary<string, object?>();
        return await new WorkflowGraphExecutor(blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
                NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance, new WorkflowGraphExecutorOptions())
            .ExecuteGraphAsync(new Dictionary<string, IReadOnlyDictionary<string, object?>> { [start] = parameters },
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NoServices.Instance, [], [], WorkflowTraceMode.Full,
                debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
                inputParametersForRun: parameters, triggerIdForRun: null, triggerEnvelopeForRun: null,
                cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
                mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(result.Program!));
    }

    public static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
