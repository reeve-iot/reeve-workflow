using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using Xunit.Abstractions;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilerModelFuzzTests(ITestOutputHelper output)
{
    internal sealed record Case(
        WorkflowDefinition Definition, IBlockShapeSource Shapes, CanvasSignature Signature,
        CompilationOptions Options, NodePortEnvironment Environment);

    internal sealed class Gen(Random r)
    {
        private static readonly string[] PortNames = ["a", "b", "Item", "Result", "Collection", "Filtered", "", "WorkflowId"];
        private static readonly string[] Branches = ["Body", "Then"];
        private static readonly string[] TypeTexts =
            ["Int", "String", "Any", "Bool", "Room", "Json", "Collection<Int>", "Collection<Any>",
             "Collection<Collection<String>>", "Collection<Room>", "Collection<Collection<Collection<Any>>>"];
        private static readonly string[] BadTypeTexts = ["", " ", "Collection<", "CollectionInt", "<>", "Int>"];
        private static readonly string[] Blocks = ["b0", "b1", "b2", "b3", "b4", "SubWorkflow", "unknown", "WorkflowInput", "WorkflowOutput"];
        private static readonly string[] ConfigKeys =
            ["a", "b", "Item", "Collection", "WorkflowId", "timeoutSeconds", "retryCount", "retryBackoffSeconds", "retryEnabled", "_designX", "InputPorts", "OutputPorts"];

        private T Pick<T>(IReadOnlyList<T> from) => from[r.Next(from.Count)];

        private string Name() => Chance(256) ? "a.b" : Pick(PortNames);

        private string DocName() => Name() is { Length: > 0 } name ? name : "c";

        private bool Chance(int oneIn) => r.Next(oneIn) == 0;

        private Guid Id() => new(Enumerable.Range(0, 16).Select(_ => (byte)r.Next(2)).ToArray());

        private PortValueType Type()
        {
            if (Chance(40))
                return Parsed(string.Concat(Enumerable.Repeat("Collection<", 300)) + "Int" + new string('>', 300));
            return Parsed(Pick(TypeTexts));
        }

        private static PortValueType Parsed(string text)
        {
            PortValueType.TryParse(text, out var type, out _).Should().BeTrue(text);
            return type;
        }

        private string TypeText() => Chance(6) ? Pick(BadTypeTexts) : Chance(30) ? "Collection<" + TypeText() + ">" : Pick(TypeTexts);

        private PortShape Port() => new(Name(), Type(), Chance(3));

        private IReadOnlyList<PortShape> Ports(int max) => [.. Enumerable.Range(0, r.Next(max + 1)).Select(_ => Port())];

        private BlockShape Shape(string id)
        {
            var names = Branches.Where(_ => Chance(3)).ToList();
            var resolvedElsewhere = id == "SubWorkflow" || Chance(5);
            return new BlockShape(
                id, Ports(3), Ports(3),
                [.. Enumerable.Range(0, r.Next(2)).Select(_ => new ConfigFieldShape(Pick(ConfigKeys), (ConfigFieldType)r.Next(5), Chance(2)))],
                new BlockFlags(Chance(3), Chance(3), Chance(4), Chance(4), resolvedElsewhere),
                names.ToDictionary(n => n, _ => Ports(2)), names.ToDictionary(n => n, _ => Ports(2)),
                Chance(2) ? "plugin:" + id : null,
                id == "SubWorkflow" ? "WorkflowId" : resolvedElsewhere && Chance(2) ? Name() : null,
                names);
        }

        private CanvasSignature Signature() => new(Ports(3), Ports(3));

        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        private string Str(string s) => JsonSerializer.Serialize(s);

        private string Expression()
        {
            string[] forms =
            [
                "${previous.n" + r.Next(5) + "." + Name() + "}", "${previous.in." + Name() + "}",
                "${previous.n1}", "${previous}", "${parameter." + Name() + "}", "${parameter}",
                "${now." + Pick(["hour", "x", ""]) + "}", "${now}", "${host." + Name() + "}", "${other.x.y}",
                "${", "${}", "${a +}", "${(" + new string('(', 40) + "1" + new string(')', 41) + "}",
                "${previous.n1.a.b.c}", "${previous..}", "${previous. n1 .a}", "${ }", "${.}", "${parameter..}",
            ];
            var variable = Pick(forms);
            string[] around =
            [
                variable, variable + " + 1", "len(" + variable + ")", variable + " ? ${parameter.a} : ${previous.n2.b}",
                "!" + variable, "[" + variable + "][0]", variable + "[\"k\"]", "\"s\" + " + variable, "1 +", "",
                string.Concat(Enumerable.Repeat("(", 33)) + variable + new string(')', 33), "unknownFn(" + variable + ", 2)",
            ];
            return (Chance(8) ? "" : "expr:") + Pick(around);
        }

        private string Value(int depth = 0)
        {
            return r.Next(depth > 2 ? 7 : 9) switch
            {
                0 => Str(Expression()),
                1 => Str(Name()),
                2 => Pick(["0", "1", "-1", "1.5", "2147483648", "1e300", "-0"]),
                3 => Pick(["true", "false"]),
                4 => "null",
                5 => Str(Id().ToString()),
                6 => Str(""),
                7 => "[" + string.Join(",", Enumerable.Range(0, r.Next(3)).Select(_ => Value(depth + 1))) + "]",
                _ => "{" + string.Join(",", Enumerable.Range(0, r.Next(3)).Select(i => $"\"k{i}\":{Value(depth + 1)}")) + "}",
            };
        }

        private string PortList() =>
            JsonSerializer.Serialize("[" + string.Join(",", Enumerable.Range(0, r.Next(3)).Select(_ =>
                $"{{\"name\":{Str(DocName())},\"type\":{(Chance(8) ? "null" : Str(TypeText()))},\"isRequired\":{(Chance(2) ? "true" : "false")}}}")) + "]");

        private Dictionary<string, JsonElement>? Config()
        {
            var config = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            for (var i = r.Next(4); i > 0; i--)
            {
                var key = Pick(ConfigKeys);
                var value = key is "InputPorts" or "OutputPorts" && !Chance(6) ? PortList() : Value();
                config[key] = Json(value == "null" ? "0" : value);
            }
            return config;
        }

        private JsonElement? Mappings(IReadOnlyList<string> ids)
        {
            var entries = new List<string>();
            foreach (var target in PortNames.Where(n => n.Length > 0 && Chance(4)))
            {
                var value = Value();
                entries.Add(Chance(2)
                    ? $"{Str(target)}:{{\"type\":\"literal\",\"literalValue\":{(value == "null" ? "0" : value)}}}"
                    : $"{Str(target)}:{{\"type\":\"direct\",\"sourceOutputName\":{Str(DocName())}}}");
            }
            return Json("{" + string.Join(",", entries) + "}");
        }

        public WorkflowDefinition Canvas(int level, List<(CanvasPath Path, string NodeId)> nodes, CanvasPath path)
        {
            var count = r.Next(level == 0 ? 7 : 4);
            var ids = new List<string>();
            var canvas = new List<WorkflowNodeDefinition>();
            for (var i = 0; i < count; i++)
            {
                var id = i == 0 && !Chance(8) ? "in" : Chance(15) && ids.Count > 0 ? Pick(ids) : "n" + i;
                ids.Add(id);
                var node = new WorkflowNodeDefinition
                {
                    Id = id,
                    BlockTypeId = id == "in" ? "WorkflowInput" : Pick(Blocks),
                    Config = Config(),
                    Passthrough = Chance(5) ? [.. PortNames.Where(n => n.Length > 0 && Chance(3))] : null,
                    OutputPorts = Chance(5)
                        ? [.. Enumerable.Range(0, r.Next(3)).Select(_ => new SubCanvasOutputPortDefinition
                        {
                            Name = DocName(), DisplayName = "d", Type = Chance(6) ? null : TypeText(),
                            Sources = Chance(2) ? Branches.Append("Nope").Where(_ => Chance(2)).ToDictionary(b => b, _ => Name()) : null,
                        })]
                        : null,
                    ErrorPolicy = Chance(6) ? new NodeErrorConfig((NodeErrorPolicy)r.Next(4), Chance(2) ? Value() : null) : null,
                };
                nodes.Add((path, id));
                if (level < 2 && Chance(3))
                {
                    node.SubCanvases = [];
                    foreach (var branch in Branches.Append("Nope").Where(_ => Chance(2)))
                        node.SubCanvases[branch] = Canvas(level + 1, nodes, path.Child(id, branch));
                }
                canvas.Add(node);
            }

            var edges = new List<WorkflowConnectionDefinition>();
            for (var i = count == 0 ? 0 : r.Next(count + 3); i > 0; i--)
            {
                var from = r.Next(count);
                var to = Chance(6) ? r.Next(count) : Math.Min(count - 1, from + 1 + r.Next(2));
                edges.Add(new WorkflowConnectionDefinition
                {
                    Id = "e" + i,
                    SourceNodeId = Chance(15) ? "ghost" : ids[from],
                    TargetNodeId = Chance(15) ? "ghost" : ids[to],
                    Mappings = Mappings(ids),
                });
            }

            return new WorkflowDefinition
            {
                Nodes = canvas,
                Edges = edges,
            };
        }

        public Case Next()
        {
            var shapes = Blocks.Take(6).ToDictionary(id => id, Shape, StringComparer.OrdinalIgnoreCase);
            var nodes = new List<(CanvasPath, string)>();
            var definition = Canvas(0, nodes, CanvasPath.Root);

            var environment = new NodePortEnvironmentBuilder();
            foreach (var (path, id) in nodes.Distinct().Where(_ => Chance(2)))
            {
                environment.Add(path, id, Chance(4)
                    ? NodePortResolution.Failed("no")
                    : NodePortResolution.Resolved(Chance(2) ? Id().ToString() : Name(), Signature()));
            }

            var options = new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new HostCheck((NamespacePathCheck)r.Next(3)))] };
            return new Case(definition, new Catalogue(shapes), Signature(), options, environment.Build());
        }
    }

    private sealed class Catalogue(Dictionary<string, BlockShape> shapes) : IBlockShapeSource
    {
        public BlockShape? Find(string blockTypeId) => shapes.GetValueOrDefault(blockTypeId);
    }

    private sealed class HostCheck(NamespacePathCheck answer) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "host";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(answer, "String");
    }

    private static void ServiceSteps(Case c)
    {
        if (StoredWorkflowDocument.ReadModel(c.Definition, out _) is not { } document)
            return;
        foreach (var frame in CanvasWalk.Frames(document.Definition))
        {
            foreach (var node in frame.Canvas.Nodes)
            {
                if (c.Shapes.Find(node.BlockTypeId) is { Flags.PortsAreResolvedElsewhere: true, RunsWorkflowFrom: { } reference }
                    && !EffectivePorts.IsReferenceInputMapped(frame.Canvas, node.Id, reference))
                {
                    _ = EffectivePorts.EffectiveReference(node, reference);
                }
            }
        }

        _ = DependencyFingerprint.Compute(WorkflowCompiler.ShapesUsedBy(document.Definition, c.Shapes).Values, c.Options, c.Environment);
    }

    [Fact]
    public void Every_generated_model_the_reader_accepts_compiles_to_a_verdict_never_an_exception()
    {
        var count = int.TryParse(Environment.GetEnvironmentVariable("T19B_FUZZ_N"), out var n) ? n : 3000;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("T19B_FUZZ_SEED"), out var s) ? s : 1919;
        var random = new Random(seed);
        var thrown = new Dictionary<string, (int Count, string Example)>();
        var rules = new Dictionary<string, int>();
        var readerRefusals = new Dictionary<string, int>();
        int accepted = 0, compiled = 0;
        var attempts = 0;

        for (; attempts < count; attempts++)
        {
            var gen = new Gen(new Random(random.Next()));
            var c = gen.Next();
            if (StoredWorkflowDocument.ReadModel(c.Definition, out var readerFault) is not null)
                accepted++;
            else
            {
                var refusal = readerFault!.Rule + " " + readerFault.Detail;
                readerRefusals[refusal] = readerRefusals.GetValueOrDefault(refusal) + 1;
            }

            try
            {
                var result = WorkflowCompiler.Compile(c.Definition, c.Shapes, c.Signature, c.Options, c.Environment);
                if (result.Succeeded)
                    compiled++;
                foreach (var diagnostic in result.Diagnostics)
                    rules[diagnostic.RuleId] = rules.GetValueOrDefault(diagnostic.RuleId) + 1;

                if (result.Fault is { } fault)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
                ServiceSteps(c);
            }
            catch (Exception ex)
            {
                var frame = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("ReeveWorkflow"))?.Trim() ?? "?";
                var key = $"{ex.GetType().Name} {frame}";
                thrown[key] = thrown.TryGetValue(key, out var seen)
                    ? (seen.Count + 1, seen.Example)
                    : (1, $"case {attempts}: {ex.Message.Split('\n')[0]} :: {StoredWorkflowDocument.Write(c.Definition, out _)}");
            }
        }

        output.WriteLine($"cases={attempts} budget={count} accepted={accepted} compiled={compiled} exceptions={thrown.Values.Sum(t => t.Count)}");
        output.WriteLine("reader refusals: " + string.Join(" ", readerRefusals.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}")));
        output.WriteLine(string.Join(" ", rules.OrderBy(r => r.Key).Select(r => $"{r.Key}={r.Value}")));
        foreach (var (key, (hits, example)) in thrown.OrderByDescending(t => t.Value.Count))
            output.WriteLine($"{hits} x {key}\n    {example}");

        accepted.Should().BeGreaterThan(count / 2, "the corpus must reach the phases, not stop at the reader");
        thrown.Should().BeEmpty();
    }
}
