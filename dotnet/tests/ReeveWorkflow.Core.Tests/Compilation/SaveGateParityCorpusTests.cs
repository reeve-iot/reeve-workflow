using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class SaveGateParityCorpusTests
{
    private static readonly string[] SubIds = ["SubWorkflow", "subworkflow", "SUBWORKFLOW"];
    private static readonly string[] Ids = ["n0", "n1", "n2", "n3", "n4", "n5"];
    private static readonly string[] Targets = ["WorkflowId", "workflowid", "Other", "", " WorkflowId", "WorkflowId "];
    private static readonly string[] SourceOutputs = ["Out", "x", "y", "Result"];

    [Fact]
    public void Per_node_the_compiler_refuses_a_mapped_reference_exactly_when_the_save_gate_does()
    {
        var catalogue = Catalogue(AllShapes());
        int checkedNodes = 0, refusedByBoth = 0, unreadable = 0;
        var mismatches = new List<string>();
        for (var seed = 0; seed < 4000; seed++)
        {
            var definition = Canvas(new Random(seed), 0, allowDuplicates: false);
            var canvases = Canvases(definition, []).ToList();
            for (var ci = 0; ci < canvases.Count; ci++)
            {
                for (var ni = 0; ni < canvases[ci].Canvas.Nodes.Count; ni++)
                {
                    if (!IsSub(canvases[ci].Canvas.Nodes.ElementAt(ni)))
                        continue;

                    var isolated = Isolate(definition, ci, ni, out var path, out var target);
                    var gate = Gate(isolated);
                    gate?.Should().Contain("wiring an edge", "after isolation only the mapping check can fail");

                    var result = WorkflowCompiler.Compile(isolated, catalogue, CanvasSignature.Empty);
                    if (result.Diagnostics.Any(d => d.RuleId == CompilationRules.DefinitionUnreadable))
                    {
                        result.Diagnostics.Should().ContainSingle();
                        unreadable++;
                        continue;
                    }

                    var mapped = result.Diagnostics.Where(d => d.RuleId == CompilationRules.ReferenceInputMapped).ToList();
                    mapped.Should().HaveCountLessThanOrEqualTo(1);
                    var compilerRefuses = mapped.Any(d => d.NodeId == target.Id
                        && (d.CanvasPath ?? []).SelectMany(s => new[] { s.NodeId, s.BranchName }).SequenceEqual(path));

                    checkedNodes++;
                    if (gate is not null && compilerRefuses)
                        refusedByBoth++;
                    if ((gate is not null) != compilerRefuses)
                        mismatches.Add($"seed {seed}, canvas {ci}, node {target.Id}: gate={gate is not null}");
                }
            }
        }

        mismatches.Should().BeEmpty();
        (checkedNodes, refusedByBoth, unreadable).Should().Be((9_196, 667, 1_439), "the seeded corpus is fixed, so its size is too");
    }

    [Fact]
    public async Task A_graph_the_save_gate_refuses_is_never_answered_valid_and_one_it_admits_is_never_refused_as_mapped()
    {
        var blocks = new CompilationServiceTests.MovableCatalogue();
        foreach (var shape in AllShapes())
            blocks.Add(shape);
        var service = CompilationScopes.Service(blocks, new AnyChildStore());
        int gateRefused = 0, gateAdmitted = 0;
        var violations = new List<string>();
        for (var seed = 0; seed < 3000; seed++)
        {
            var definition = Canvas(new Random(100_000 + seed), 0, allowDuplicates: seed % 3 == 0);
            var gate = Gate(definition);
            var verdict = await service.EnsureAsync(
                CompilationServiceTests.Version(definition), null, CancellationToken.None);

            if (gate is not null)
            {
                gateRefused++;
                if (verdict.IsValid)
                    violations.Add($"seed {seed}: the gate refused ({gate}) but the verdict is valid");
            }
            else
            {
                gateAdmitted++;
                if (verdict.Diagnostics.Any(d => d.RuleId == CompilationRules.ReferenceInputMapped))
                    violations.Add($"seed {seed}: the gate admitted it but the compiler refused it as mapped");
            }
        }

        violations.Should().BeEmpty();
        gateRefused.Should().BeGreaterThan(100);
        gateAdmitted.Should().BeGreaterThan(100);
    }

    [Fact]
    public void BoundShapes_equals_what_is_hashed_when_no_canvas_repeats_an_id_and_is_a_subset_otherwise()
    {
        var catalogue = Catalogue(AllShapes());
        var properSubsets = 0;
        for (var seed = 0; seed < 4000; seed++)
        {
            var repeats = seed % 2 == 0;
            var definition = Canvas(new Random(200_000 + seed), 0, allowDuplicates: repeats);
            var result = WorkflowCompiler.Compile(definition, catalogue, CanvasSignature.Empty);
            if (result.Diagnostics.Any(d => d.RuleId == CompilationRules.DefinitionUnreadable))
                continue;
            var bound = result.BoundShapes.Keys.ToHashSet();
            var hashed = WorkflowCompiler.ShapesUsedBy(definition, catalogue).Keys.ToHashSet();

            bound.Should().BeSubsetOf(hashed);
            if (!Canvases(definition, []).Any(c => c.Canvas.Nodes.GroupBy(n => n.Id).Any(g => g.Count() > 1)))
                bound.Should().BeEquivalentTo(hashed, $"seed {seed} repeats no id on any canvas");
            else if (bound.Count < hashed.Count)
                properSubsets++;
        }

        properSubsets.Should().BeGreaterThan(0, "the corpus must reach the case where the two differ");
    }

    [Theory]
    [InlineData("wire")]
    [InlineData("ghost wire")]
    [InlineData("literal")]
    public void The_save_gate_refuses_a_mapping_into_WorkflowId_beside_a_configured_value(string kind)
    {
        object mapping = kind == "literal"
            ? new { type = "literal", literalValue = Guid.NewGuid().ToString() }
            : new { type = "direct", sourceOutputName = "childId" };
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition
                {
                    Id = "call", BlockTypeId = WorkflowGraphHelpers.SubWorkflowBlockTypeId,
                    Config = new() { ["WorkflowId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()) },
                },
            ],
            Edges =
            [
                new WorkflowConnectionDefinition
                {
                    Id = "e1", SourceNodeId = kind == "ghost wire" ? "ghost" : "start", TargetNodeId = "call",
                    Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["WorkflowId"] = mapping }),
                },
            ],
        };

        var act = () => WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(definition);

        act.Should().Throw<WorkflowValidationException>().WithMessage("*wiring an edge into the WorkflowId input*");
    }

    private static BlockShape[] AllShapes() =>
    [
        Shape("noop", outputs: [("Out", PortValueType.String)], inputs: [("Other", PortValueType.String, false)]),
        Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, inputs: [("WorkflowId", PortValueType.String, true)],
            portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId"),
        Shape("container.block", subCanvasNames: ["Body"]),
    ];

    private static WorkflowDefinition Isolate(
        WorkflowDefinition definition, int canvasIndex, int nodeIndex, out IReadOnlyList<string> path, out WorkflowNodeDefinition target)
    {
        var isolated = JsonSerializer.Deserialize<WorkflowDefinition>(
            Stored(definition), WorkflowGraphHelpers.JsonOptions)!;
        var canvases = Canvases(isolated, []).ToList();
        for (var ci = 0; ci < canvases.Count; ci++)
        {
            for (var ni = 0; ni < canvases[ci].Canvas.Nodes.Count; ni++)
            {
                var node = canvases[ci].Canvas.Nodes.ElementAt(ni);
                if (ci == canvasIndex && ni == nodeIndex)
                    node.Config = new() { ["WorkflowId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()) };
                else if (IsSub(node))
                    node.BlockTypeId = "noop";
            }
        }

        path = canvases[canvasIndex].Path;
        target = canvases[canvasIndex].Canvas.Nodes.ElementAt(nodeIndex);
        return isolated;
    }

    private static object? MappingEntry(Random r) => (r.Next(6) is var kind && kind == 3 && r.Next(8) != 0 ? 5 : kind) switch
    {
        0 => new { type = "literal", literalValue = Guid.NewGuid().ToString() },
        1 => new { type = "direct", sourceOutputName = "Out" },
        2 => new { type = "direct", sourceOutputName = SourceOutputs[r.Next(SourceOutputs.Length)] },
        3 => "not-an-object",
        4 => new { type = "literal", literalValue = 5 },
        _ => new { type = "direct", sourceOutputName = "Out" },
    };

    private static JsonElement? Mappings(Random r)
    {
        var byTarget = new Dictionary<string, object?>();
        var count = r.Next(0, 3);
        for (var i = 0; i < count; i++)
            byTarget[Targets[r.Next(Targets.Length)]] = MappingEntry(r);
        return JsonSerializer.SerializeToElement(byTarget);
    }

    private static Dictionary<string, JsonElement>? SubConfig(Random r) => r.Next(8) switch
    {
        0 => new(),
        1 => new() { ["WorkflowId"] = JsonSerializer.SerializeToElement(" ") },
        2 => new() { ["WorkflowId"] = JsonSerializer.SerializeToElement("${x}") },
        3 => new() { ["WorkflowId"] = JsonSerializer.SerializeToElement("not-a-guid") },
        4 => new() { ["WorkflowId"] = JsonSerializer.SerializeToElement(5) },
        _ => new() { ["WorkflowId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()) },
    };

    private static WorkflowDefinition Canvas(Random r, int depth, bool allowDuplicates)
    {
        var nodes = new List<WorkflowNodeDefinition> { StartNode("s") };
        var count = r.Next(1, 5);
        var used = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            string id;
            do id = Ids[r.Next(Ids.Length)]; while (!allowDuplicates && !used.Add(id));
            nodes.Add(r.Next(depth < 2 ? 4 : 3) switch
            {
                0 => new WorkflowNodeDefinition { Id = id, BlockTypeId = "noop" },
                1 or 2 => new WorkflowNodeDefinition { Id = id, BlockTypeId = SubIds[r.Next(SubIds.Length)], Config = SubConfig(r) },
                _ => new WorkflowNodeDefinition
                {
                    Id = id, BlockTypeId = "container.block",
                    SubCanvases = new() { ["Body"] = Canvas(r, depth + 1, allowDuplicates) },
                },
            });
        }

        var pool = nodes.Select(n => n.Id).Append("ghost").ToArray();
        var edges = new List<WorkflowConnectionDefinition>();
        var edgeCount = r.Next(0, 6);
        for (var i = 0; i < edgeCount; i++)
        {
            edges.Add(new WorkflowConnectionDefinition
            {
                Id = $"e{i}",
                SourceNodeId = pool[r.Next(pool.Length)],
                TargetNodeId = r.Next(10) == 0 ? pool[r.Next(pool.Length)].ToUpperInvariant() : pool[r.Next(pool.Length)],
                Mappings = Mappings(r),
            });
        }

        _ = r.Next(2);
        return new WorkflowDefinition { Nodes = nodes, Edges = edges };
    }

    private static IEnumerable<(IReadOnlyList<string> Path, WorkflowDefinition Canvas)> Canvases(
        WorkflowDefinition definition, IReadOnlyList<string> path)
    {
        yield return (path, definition);
        foreach (var node in definition.Nodes)
            foreach (var (name, sub) in node.SubCanvases ?? [])
                foreach (var canvas in Canvases(sub, [.. path, node.Id, name]))
                    yield return canvas;
    }

    private static string? Gate(WorkflowDefinition definition)
    {
        try
        {
            WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(definition);
            return null;
        }
        catch (WorkflowValidationException ex)
        {
            return ex.Message;
        }
    }

    private static bool IsSub(WorkflowNodeDefinition node) =>
        string.Equals(node.BlockTypeId, WorkflowGraphHelpers.SubWorkflowBlockTypeId, StringComparison.OrdinalIgnoreCase);

    private sealed class AnyChildStore : IWorkflowStore
    {
        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            var at = DateTimeOffset.UnixEpoch;
            var version = new WorkflowVersion(Guid.NewGuid(), workflowId, 1, "{}", "[]", "[]", Guid.NewGuid(), at);
            var workflow = new Workflow(workflowId, "child", null, Guid.NewGuid(), at);
            workflow.SetCurrentVersion(version, at);
            return Task.FromResult<Workflow?>(workflow);
        }

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowVersion?>(null);

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid, string)?>(null);
    }
}
