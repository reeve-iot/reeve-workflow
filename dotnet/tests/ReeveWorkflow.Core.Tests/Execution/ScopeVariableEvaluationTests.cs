using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ScopeVariableEvaluationTests
{
    private const string Definition = """
        {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
        {"id":"src","blockTypeId":"src","position":{"x":120,"y":0},"configuration":{}},
        {"id":"n","blockTypeId":"keep","position":{"x":240,"y":0},"configuration":{"Predicate":"expr:${item} > ${previous.src.Limit}","Plain":"expr:${previous.src.Limit} > 0","Total":"expr:${value} + 1"}}],
        "edges":[{"id":"e0","sourceNodeId":"in","targetNodeId":"src","mappings":{}},
        {"id":"e1","sourceNodeId":"src","targetNodeId":"n","mappings":{"Items":{"type":"direct","sourceOutputName":"Items"}}}]}
        """;

    private static readonly Func<WorkflowBlockContext, long, object?> Test = (context, item) =>
        context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = item });

    [Fact]
    public async Task A_block_evaluates_the_input_once_per_item_with_the_item_bound()
    {
        var answers = await RunAsync([1L, 5L, 3L, 8L], context => [.. new long[] { 1, 5, 3, 8 }.Select(item => Test(context, item))]);

        answers.Should().Equal(false, true, false, true);
    }

    [Fact]
    public async Task An_integer_of_another_width_is_admitted_as_the_Int_the_term_names()
    {
        var answers = await RunAsync([1L], context => [context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = 9 })]);

        answers.Should().Equal(true);
    }

    [Fact]
    public async Task An_Int_result_on_an_Expression_Number_input_is_answered_as_a_double()
    {
        var answers = await RunAsync([1L], context => [context.Expressions.Evaluate("Total", new Dictionary<string, object?> { ["value"] = 3L })]);

        answers.Should().Equal(4.0);
        answers[0].Should().BeOfType<double>();
    }

    [Fact]
    public async Task An_input_with_no_variable_evaluates_with_none()
    {
        var answers = await RunAsync([1L], context => [context.Expressions.Evaluate("Plain", new Dictionary<string, object?>())]);

        answers.Should().Equal(true);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("undeclared")]
    [InlineData("wrongType")]
    [InlineData("null")]
    [InlineData("notExpressionInput")]
    [InlineData("noSuchInput")]
    public async Task A_variable_that_is_missing_undeclared_or_refused_by_the_gate_is_the_blocks_defect(string defect)
    {
        var failure = await FailureAsync(context =>
        {
            return defect switch
            {
                "missing" => context.Expressions.Evaluate("Predicate", new Dictionary<string, object?>()),
                "undeclared" => context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = 1L, ["value"] = 1L }),
                "wrongType" => context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = "text" }),
                "null" => context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = null }),
                "notExpressionInput" => context.Expressions.Evaluate("Items", new Dictionary<string, object?>()),
                _ => context.Expressions.Evaluate("Nope", new Dictionary<string, object?>()),
            };
        });

        failure.Should().BeOfType<ArgumentException>();
        WorkflowFailureCoding.Of(failure).Code.Should().Be(WorkflowErrorCodes.BlockFailed);
        failure.Message.Should().Contain(defect switch
        {
            "missing" => "was not given a value",
            "undeclared" => "declares no scope variable 'value'",
            "wrongType" => "is not a value of 'Int'",
            "null" => "null is not a value of 'Int'",
            "notExpressionInput" => "is not an Expression<T> input",
            _ => "is not an Expression<T> input",
        });
    }

    [Fact]
    public async Task An_evaluation_failure_is_expression_runtime_error_with_its_category()
    {
        var failure = await FailureAsync(
            context => context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = 1L }),
            predicate: "expr:${item} / ${previous.src.Zero} > 1");

        failure.Should().BeOfType<ExpressionEvaluationException>();
        var coding = WorkflowFailureCoding.Of(failure);
        coding.Code.Should().Be(WorkflowErrorCodes.ExpressionRuntimeError);
        coding.Category.Should().Be("division-by-zero");
    }

    [Fact]
    public async Task A_sandboxed_block_is_handed_what_the_sandbox_wraps_and_not_the_library_object()
    {
        var wrapper = new Wrapper();
        object? seen = null;
        var sandbox = new Sandbox(new WorkflowBlockSandbox(new NoHostServices(), new NoServices(), e => e, inner => wrapper.Around(inner)));

        await ExecuteAsync(new Hooks([1L], context =>
        {
            seen = context.Expressions;
            context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = 9L });
        }), "expr:${item} > ${previous.src.Limit}", sandbox);

        seen.Should().BeSameAs(wrapper);
        wrapper.Inner.Should().NotBeNull();
        wrapper.Calls.Should().Be(1);
    }

    [Fact]
    public async Task With_no_host_resolver_the_expressions_object_holds_no_service_provider()
    {
        var services = new NoServices();
        var reached = false;

        Reaches((Func<object>)(() => services), services).Should().BeTrue();

        await ExecuteAsync(new Hooks([1L], context =>
        {
            context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = 9L });
            reached = Reaches(context.Expressions, services);
        }), "expr:${item} > ${previous.src.Limit}", services: services);

        reached.Should().BeFalse();
    }

    [Fact]
    public void A_context_built_by_hand_has_nothing_to_evaluate()
    {
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance, Services = new NoServices(),
        };

        FluentActions.Invoking(() => context.Expressions.Evaluate("Predicate", new Dictionary<string, object?>()))
            .Should().Throw<InvalidOperationException>().WithMessage("*outside a compiled run*");
    }

    private static async Task<List<object?>> RunAsync(long[] items, Func<WorkflowBlockContext, object?[]> body)
    {
        var answers = new List<object?>();
        await ExecuteAsync(new Hooks(items, context => answers.AddRange(body(context))), "expr:${item} > ${previous.src.Limit}");
        return answers;
    }

    private static async Task<Exception> FailureAsync(Func<WorkflowBlockContext, object?> act, string? predicate = null)
    {
        Exception? caught = null;
        var hooks = new Hooks([1L], context =>
        {
            try
            {
                act(context);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        await ExecuteAsync(hooks, predicate ?? "expr:${item} > ${previous.src.Limit}");
        return caught ?? throw new InvalidOperationException("The block's call did not fail.");
    }

    private static bool Reaches(object root, object target)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>([root]);
        while (pending.Count > 0)
        {
            if (seen.Count >= 20000)
                throw new InvalidOperationException("The object graph is larger than the walk's bound; nothing was shown.");
            var current = pending.Pop();
            if (ReferenceEquals(current, target))
                return true;
            if (!seen.Add(current) || current is string or Type or System.Reflection.MemberInfo)
                continue;
            for (var type = current.GetType(); type is not null; type = type.BaseType)
            {
                if (type.IsPrimitive || type.IsEnum)
                    break;
                foreach (var field in type.GetFields(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsPointer && !field.FieldType.IsByRef && field.GetValue(current) is { } next)
                        pending.Push(next);
                }
            }
        }

        return false;
    }

    private static async Task ExecuteAsync(
        Hooks hooks, string predicate, IWorkflowBlockSandbox? sandbox = null, IServiceProvider? services = null)
    {
        var definition = StoredWorkflowDocument.Read(Definition.Replace("expr:${item} > ${previous.src.Limit}", predicate), out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        var catalogue = Catalogue(hooks);
        var compiled = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(catalogue), CanvasSignature.Empty);
        compiled.Diagnostics.Should().BeEmpty(string.Join("; ", compiled.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var executor = new WorkflowGraphExecutor(
            catalogue, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
            NullHelperResolver.Instance, new WorkflowGraphExecutorOptions(), sandbox: sandbox);
        var parameters = new Dictionary<string, object?>();
        await executor.ExecuteGraphAsync(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = parameters },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), services ?? new NoServices(), [], [], WorkflowTraceMode.Full,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: parameters, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
            mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(compiled.Program));
    }

    private static RegisteredWorkflowBlocks Catalogue(Hooks hooks) => new(
    [
        new Factory("src", hooks, [], [
            new("Items", "Items", PortValueType.OfDeclaration("Collection<Int>"), false),
            new("Limit", "Limit", PortValueType.Int, false),
            new("Zero", "Zero", PortValueType.Int, false)]),
        new Factory("keep", hooks,
            [new("Items", "Items", PortValueType.OfDeclaration("Collection<$T>"), true),
             new("Predicate", "Predicate", PortValueType.OfDeclaration("Expression<Boolean>"), true)
                { ScopeVariables = [new ExpressionScopeVariable("item", "$T")] },
             new("Plain", "Plain", PortValueType.OfDeclaration("Expression<Boolean>"), false),
             new("Total", "Total", PortValueType.OfDeclaration("Expression<Number>"), false)
                { ScopeVariables = [new ExpressionScopeVariable("value", "Int")] }],
            [], [new WorkflowTypeParameter("T", [])]),
    ]);

    private sealed record Hooks(long[] Items, Action<WorkflowBlockContext> Run);

    private sealed class Factory(
        string id, Hooks hooks, BlockPortDefinition[] inputs, BlockPortDefinition[] outputs,
        IReadOnlyList<WorkflowTypeParameter>? typeParameters = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => typeParameters ?? [];
        public IWorkflowBlockPlugin Create() => new Block(id, hooks);
    }

    private sealed class Block(string id, Hooks hooks) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            if (id == "src")
            {
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
                {
                    ["Items"] = context.Types["Items"].CreateList(hooks.Items.Cast<object?>()),
                    ["Limit"] = 3L,
                    ["Zero"] = 0L,
                });
            }

            hooks.Run(context);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class Sandbox(WorkflowBlockSandbox confinement) : IWorkflowBlockSandbox
    {
        public Task<WorkflowBlockSandbox?> SandboxAsync(
            IWorkflowBlockFactory factory, IServiceProvider services, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowBlockSandbox?>(factory.BlockTypeId == "keep" ? confinement : null);
    }

    private sealed class Wrapper : IWorkflowBlockExpressions
    {
        public IWorkflowBlockExpressions? Inner { get; private set; }
        public int Calls { get; private set; }
        public Wrapper Around(IWorkflowBlockExpressions inner)
        {
            Inner = inner;
            return this;
        }

        public object? Evaluate(string input, IReadOnlyDictionary<string, object?> variables)
        {
            Calls++;
            return Inner!.Evaluate(input, variables);
        }
    }

    private sealed class NoHostServices : IWorkflowHostServices
    {
        public IReadOnlyCollection<string> GrantedPermissions => [];
        public T? TryGet<T>() where T : class => null;
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
