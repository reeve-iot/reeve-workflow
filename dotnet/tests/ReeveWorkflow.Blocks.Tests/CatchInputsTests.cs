using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class CatchInputsTests
{
    [Fact]
    public void Catch_declares_the_six_fields_of_the_error_and_no_Error()
    {
        var inputs = new TryCatchBlockFactory().GetSubCanvasInputs("Catch");

        inputs.Select(p => (p.Name, p.Type.Name, p.IsRequired)).Should().Equal(
            ("Code", "String", false), ("Category", "String?", false), ("Message", "String", false),
            ("ExceptionType", "String", false), ("FailedNodeId", "String?", false), ("FailedBlockTypeId", "String?", false));
    }

    public static TheoryData<SubCanvasError, Dictionary<string, object?>> Errors => new()
    {
        {
            new SubCanvasError("division by zero", "ExpressionEvaluationException", "calc", "Math", "expression-runtime-error", "division-by-zero"),
            new()
            {
                ["Code"] = "expression-runtime-error", ["Category"] = "division-by-zero", ["Message"] = "division by zero",
                ["ExceptionType"] = "ExpressionEvaluationException", ["FailedNodeId"] = "calc", ["FailedBlockTypeId"] = "Math",
            }
        },
        {
            new SubCanvasError("gone", "TimeoutException", null, null),
            new()
            {
                ["Code"] = "block-failed", ["Category"] = null, ["Message"] = "gone",
                ["ExceptionType"] = "TimeoutException", ["FailedNodeId"] = null, ["FailedBlockTypeId"] = null,
            }
        },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Catch_is_handed_each_field_of_the_error_Try_ended_with(SubCanvasError error, Dictionary<string, object?> expected)
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        IReadOnlyDictionary<string, object?>? handed = null;
        executor.TryExecuteAsync("Try", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), error));
        executor.TryExecuteAsync("Catch", Arg.Do<IReadOnlyDictionary<string, object?>>(d => handed = d), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));
        executor.TryExecuteAsync("Finally", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new SubCanvasResult(new Dictionary<string, object?>(), null));

        await new TryCatchBlock().ExecuteAsync(new Dictionary<string, object?>(), Context(executor), CancellationToken.None);

        handed.Should().Equal(expected);
    }

    [Fact]
    public async Task A_Catch_body_reads_the_fields_of_a_block_failure_by_wire_and_by_expression()
    {
        var seen = new Dictionary<string, object?>();

        await RunAsync(Thrower.Id, seen);

        seen.Should().Equal(new Dictionary<string, object?>
        {
            ["Code"] = "block-failed", ["Category"] = null, ["Message"] = seen["Message"],
            ["ExceptionType"] = nameof(InvalidOperationException), ["FailedNodeId"] = "bad", ["FailedBlockTypeId"] = Thrower.Id,
            ["CodeText"] = "block-failed",
        });
        ((string)seen["Message"]!).Should().Contain("boom");
    }

    [Fact]
    public async Task A_Catch_body_reads_the_category_of_an_expression_failure()
    {
        var seen = new Dictionary<string, object?>();

        await RunAsync(Divider.Id, seen);

        (seen["Code"], seen["Category"], seen["CodeText"], seen["FailedNodeId"], seen["FailedBlockTypeId"])
            .Should().Be(("expression-runtime-error", "division-by-zero", "expression-runtime-error", "bad", Divider.Id));
    }

    private static async Task RunAsync(string failing, Dictionary<string, object?> seen)
    {
        var tryBody = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "tin", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "zero", BlockTypeId = Zero.Id },
                new WorkflowNodeDefinition
                {
                    Id = "bad", BlockTypeId = failing,
                    Config = failing == Divider.Id ? new() { ["X"] = JsonSerializer.SerializeToElement("expr:10 / ${previous.zero.V}") } : new(),
                },
            ],
            Edges =
            [
                new() { Id = "t0", SourceNodeId = "tin", TargetNodeId = "zero", Mappings = NoMappings },
                new() { Id = "t1", SourceNodeId = "zero", TargetNodeId = "bad", Mappings = NoMappings },
            ],
        };
        var catchBody = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "cin", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition
                {
                    Id = "rec", BlockTypeId = Recorder.Id,
                    Config = new() { ["CodeText"] = JsonSerializer.SerializeToElement("expr:${previous.cin.Code}") },
                },
            ],
            Edges =
            [
                new()
                {
                    Id = "c0", SourceNodeId = "cin", TargetNodeId = "rec",
                    Mappings = JsonSerializer.SerializeToElement(Recorder.Fields.ToDictionary(
                        f => f, f => new { type = "direct", sourceOutputName = f })),
                },
            ],
        };
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition
                {
                    Id = "tc", BlockTypeId = "TryCatch",
                    SubCanvases = new() { ["Try"] = tryBody, ["Catch"] = catchBody },
                },
            ],
            Edges =
            [
                new() { Id = "e0", SourceNodeId = "start", TargetNodeId = "tc", Mappings = NoMappings },
            ],
        };

        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new Thrower());
        services.AddSingleton<IWorkflowBlockFactory>(new Divider());
        services.AddSingleton<IWorkflowBlockFactory>(new Zero());
        services.AddSingleton<IWorkflowBlockFactory>(new Recorder(seen));
        var blocks = new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));

        var parameters = new Dictionary<string, object?>();
        await new WorkflowGraphExecutor(blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
                NullHelperResolver.Instance, new WorkflowGraphExecutorOptions())
            .ExecuteGraphAsync(new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = parameters },
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NoServices(), [], [], WorkflowTraceMode.Full,
                debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
                inputParametersForRun: parameters, triggerIdForRun: null, triggerEnvelopeForRun: null,
                cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
                mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(result.Program!));
    }

    private static readonly JsonElement NoMappings = JsonSerializer.SerializeToElement(new { });

    private static WorkflowBlockContext Context(ISubCanvasExecutor executor) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>(),
        SubCanvasExecutor = executor,
    };

    private abstract class TestBlock : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public abstract string BlockTypeId { get; }
        public string DisplayName => BlockTypeId;
        public string Description => "";
        public string Category => "test";
        public virtual IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public virtual IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => this;

        public abstract Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken);
    }

    private sealed class Thrower : TestBlock
    {
        public const string Id = "test.thrower";
        public override string BlockTypeId => Id;

        public override Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class Zero : TestBlock
    {
        public const string Id = "test.zero";
        public override string BlockTypeId => Id;
        public override IReadOnlyCollection<BlockPortDefinition> Outputs => [new BlockPortDefinition("V", "V", PortValueType.Number, false)];

        public override Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["V"] = 0d });
    }

    private sealed class Divider : TestBlock
    {
        public const string Id = "test.divider";
        public override string BlockTypeId => Id;
        public override IReadOnlyCollection<BlockPortDefinition> Inputs => [new BlockPortDefinition("X", "X", PortValueType.Number, false)];

        public override Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    private sealed class Recorder(Dictionary<string, object?> seen) : TestBlock
    {
        public const string Id = "test.recorder";
        public static readonly string[] Fields = ["Code", "Category", "Message", "ExceptionType", "FailedNodeId", "FailedBlockTypeId"];
        public override string BlockTypeId => Id;

        public override IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new BlockPortDefinition("Code", "Code", PortValueType.String, false),
            new BlockPortDefinition("Category", "Category", PortValueType.Of("String?"), false),
            new BlockPortDefinition("Message", "Message", PortValueType.String, false),
            new BlockPortDefinition("ExceptionType", "ExceptionType", PortValueType.String, false),
            new BlockPortDefinition("FailedNodeId", "FailedNodeId", PortValueType.Of("String?"), false),
            new BlockPortDefinition("FailedBlockTypeId", "FailedBlockTypeId", PortValueType.Of("String?"), false),
            new BlockPortDefinition("CodeText", "CodeText", PortValueType.String, false),
        ];

        public override Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            foreach (var name in Fields.Append("CodeText"))
                seen[name] = inputs.GetValueOrDefault(name);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
