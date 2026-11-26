using System.Text.Json;
using BenchmarkDotNet.Attributes;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Benchmarks;

public partial class ExecutorBenchmarks
{
    private const string TargetNode = "target";

    private IWorkflowBlockFactory? targetFactory;
    private CompiledNode? targetCompiled;
    private IReadOnlySet<string>? targetRawInputs;
    private Dictionary<string, IReadOnlyDictionary<string, object?>>? executedOutputs;
    private IReadOnlyDictionary<string, object?>? runParameters;

    private async Task SetupInputResolution()
    {
        targetFactory = world!.Blocks.Find(BenchmarkWorld.Relay)!;
        targetRawInputs = WorkflowExecutionHelpers.ExpressionInputNames(targetFactory);
        runParameters = new Dictionary<string, object?> { ["threshold"] = 20.0, ["mode"] = "manual" };

        executedOutputs = [];
        for (var i = 1; i <= 20; i++)
        {
            executedOutputs[$"p{i}"] = new Dictionary<string, object?>
            {
                ["x"] = (double)i, ["y"] = (long)i * 10, ["z"] = i * 0.5, ["s"] = $"value-{i}", ["ok"] = true
            };
        }

        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(
            """
            {
              "nodes": [
                { "id": "start", "blockTypeId": "WorkflowInput", "position": { "x": 0, "y": 0 }, "configuration": {} },
                { "id": "p3", "blockTypeId": "Source", "position": { "x": 0, "y": 0 }, "configuration": {} },
                { "id": "p11", "blockTypeId": "Source", "position": { "x": 0, "y": 0 }, "configuration": {} },
                { "id": "p20", "blockTypeId": "Source", "position": { "x": 0, "y": 0 }, "configuration": {} },
                { "id": "target", "blockTypeId": "Relay", "position": { "x": 0, "y": 0 },
                  "configuration": { "Label": "from-config", "timeoutSeconds": 5, "__note": "editor only" } }
              ],
              "edges": [
                { "id": "s3", "sourceNodeId": "start", "targetNodeId": "p3", "mappings": {} },
                { "id": "s11", "sourceNodeId": "start", "targetNodeId": "p11", "mappings": {} },
                { "id": "s20", "sourceNodeId": "start", "targetNodeId": "p20", "mappings": {} },
                { "id": "e1", "sourceNodeId": "p3", "targetNodeId": "target",
                  "mappings": { "A": { "type": "direct", "sourceOutputName": "x" } } },
                { "id": "e2", "sourceNodeId": "p11", "targetNodeId": "target",
                  "mappings": { "B": { "type": "direct", "sourceOutputName": "y" } } },
                { "id": "e3", "sourceNodeId": "p20", "targetNodeId": "target",
                  "mappings": {
                    "C": { "type": "direct", "sourceOutputName": "z" },
                    "Label": { "type": "literal", "literalValue": "relay" },
                    "Calc": { "type": "literal", "literalValue": "expr:${previous.p20.z} * 2 + ${previous.p11.y}" }
                  } }
              ]
            }
            """, WorkflowGraphHelpers.JsonOptions)!;
        var compiled = await WorkflowCompiler.CompileAsync(definition, new CatalogueBlockShapeSource(world.Blocks), CanvasSignature.Empty);
        Require(compiled.Program is not null && compiled.Diagnostics.Count == 0,
            "the input-resolution definition must compile: "
            + string.Join("; ", compiled.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        targetCompiled = CompiledWalk.Root(compiled.Program!).Required(TargetNode);

        var inputs = ResolveBindAndEvaluate();
        Require(Num(inputs, "A") == 3 && Num(inputs, "B") == 110 && Num(inputs, "C") == 10 && Num(inputs, "Calc") == 130
            && Equals(inputs["Label"], "relay"),
            $"resolved inputs A={Num(inputs, "A")} B={Num(inputs, "B")} C={Num(inputs, "C")} Calc={Num(inputs, "Calc")} "
            + $"Label={inputs["Label"]}");
        Require(inputs.Count == 5 && !inputs.ContainsKey("timeoutSeconds") && !inputs.ContainsKey("__note"),
            $"resolved {inputs.Count} inputs; config keys that must not reach the block did");

        var bound = ResolveAndBind();
        Require(bound["Calc"] is string text && ExpressionSource.IsExpression(text),
            "before expression evaluation Calc must still be the expr: text");

        var full = ResolveFullPipeline();
        Require(Num(full, "Calc") == 130 && full.Count == 5, "the full pipeline must give the same five inputs");
    }

    private Dictionary<string, object?> ResolveAndBind()
    {
        var inputs = WorkflowGraphExecutor.ResolveInputs(targetCompiled!, executedOutputs!);
        WorkflowGraphExecutor.MergeCompiledConfigIntoInputs(inputs, targetCompiled!);
        return inputs;
    }

    private Dictionary<string, object?> ResolveBindAndEvaluate()
    {
        var inputs = ResolveAndBind();
        if (!WorkflowExecutionHelpers.HasExpressionInputs(inputs, targetRawInputs))
            return inputs;

        var scope = WorkflowGraphExecutor.EvaluationScope(
            new Dictionary<ReeveWorkflow.Core.Expressions.HelperPath, object?>(), executedOutputs!, runParameters, TimeZoneInfo.Utc, null, null);
        WorkflowGraphExecutor.EvaluateExpressionInputs(inputs, targetCompiled!, scope, targetRawInputs);
        return inputs;
    }

    private Dictionary<string, object?> ResolveFullPipeline()
    {
        var inputs = ResolveBindAndEvaluate();
        var refusal = (Exception?)WorkflowGraphExecutor.RefuseValueOutsideEnum(targetFactory!, targetCompiled!, inputs)
            ?? WorkflowGraphExecutor.RefuseAbsentRequiredInput(targetCompiled!, inputs);
        return refusal is null ? inputs : throw refusal;
    }

    [Benchmark(Description = "Scenario 5i-1: node inputs, wires+literal+config only (ResolveInputs, MergeCompiledConfigIntoInputs)")]
    public IReadOnlyDictionary<string, object?> InputsBind() => ResolveAndBind();

    [Benchmark(Description = "Scenario 5i-2: node inputs, bind + expr: (adds EvaluationScope and EvaluateExpressionInputs)")]
    public IReadOnlyDictionary<string, object?> InputsBindAndExpression() => ResolveBindAndEvaluate();

    [Benchmark(Description = "Scenario 5i-3: node inputs, the walk's whole pre-block pipeline (adds enum and required-input checks)")]
    public IReadOnlyDictionary<string, object?> InputsFullPipeline() => ResolveFullPipeline();
}
