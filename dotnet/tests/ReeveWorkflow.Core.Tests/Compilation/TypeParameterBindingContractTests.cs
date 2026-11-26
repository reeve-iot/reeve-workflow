using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class TypeParameterBindingContractTests
{
    private static readonly JsonElement Contract =
        JsonDocument.Parse(File.ReadAllText(RepositoryFiles.TypeParameterBindingPath())).RootElement;

    private static readonly Lazy<WorkflowTypeRegistry> Registry = new(BuildRegistry);

    [Fact]
    public void Every_unit_vector_holds_for_the_compilers_own_decision()
    {
        Failures(BindingMutant.None).Should().BeEmpty();
    }

    [Fact]
    public void Each_unit_section_holds_vectors()
    {
        foreach (var section in new[] { "literalTerms", "evidence", "resolution", "lub" })
            Contract.GetProperty(section).GetArrayLength().Should().BeGreaterThan(5, section);
    }

    public static TheoryData<string> CaseNames()
    {
        var names = new TheoryData<string>();
        foreach (var row in Contract.GetProperty("cases").EnumerateArray())
            names.Add(row.GetProperty("name").GetString()!);
        return names;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void A_worked_graph_compiles_as_the_specification_says(string name)
    {
        var row = Contract.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var graph = Canvas(row.GetProperty("canvas"), "start");
        var context = WorkflowCompiler.Run(
            graph, BlockCatalogue(), CanvasSignature.Empty, new CompilationOptions { TypeRegistry = Registry.Value });

        var expect = row.GetProperty("expect");
        var refusals = context.Diagnostics
            .Select(d => $"{d.RuleId}|{string.Join("/", d.CanvasPath.Select(s => $"{s.NodeId}/{s.BranchName}"))}|{d.NodeId}|{d.PortName}");
        var expected = expect.GetProperty("refusals").EnumerateArray().Select(r =>
            $"{r.GetProperty("rule").GetString()}|{r.GetProperty("canvas").GetString()}|{r.GetProperty("node").GetString()}|{r.GetProperty("port").GetString()}");
        refusals.Should().BeEquivalentTo(expected, string.Join("; ", context.Diagnostics.Select(d => d.Message)));

        if (!expect.TryGetProperty("ports", out var ports))
            return;
        foreach (var port in ports.EnumerateObject())
            EffectiveTerm(context, port.Name).Should().Be(port.Value.GetString(), port.Name);
    }

    internal static List<string> Failures(BindingMutant mutant)
    {
        var vocabulary = Registry.Value.Current;
        var failures = new List<string>();
        void Check(string vector, string? actual, string? expected)
        {
            if (actual != expected)
                failures.Add($"{vector}: {actual ?? "none"}, expected {expected ?? "none"}");
        }

        foreach (var row in Contract.GetProperty("literalTerms").EnumerateArray())
        {
            var literal = row.GetProperty("literal");
            Check($"literal {literal.GetRawText()}", Text(TypeParameterBinding.LiteralTerm(literal, vocabulary, mutant)), Optional(row, "term"));
        }

        foreach (var row in Contract.GetProperty("evidence").EnumerateArray())
        {
            var declared = PortTerm.Parse(row.GetProperty("declared").GetString()!, PortTermGrammar.Declaration);
            var into = new List<ParameterEvidence>();
            string vector;
            if (row.TryGetProperty("literal", out var literal))
            {
                vector = $"{declared} <- literal {literal.GetRawText()}";
                TypeParameterBinding.MatchLiteral(declared, literal, into, vocabulary, mutant);
            }
            else
            {
                var source = Term(row.GetProperty("source").GetString()!, vocabulary);
                vector = $"{declared} <- {Text(source)}";
                TypeParameterBinding.Match(declared, source, into, vocabulary, mutant);
            }

            Check(vector, string.Join(", ", into.Select(Text).Order(StringComparer.Ordinal)),
                string.Join(", ", row.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal)));
        }

        foreach (var row in Contract.GetProperty("resolution").EnumerateArray())
        {
            var evidence = row.GetProperty("evidence").EnumerateArray().Select(e => Evidence(e.GetString()!, vocabulary)).ToList();
            var constraints = row.TryGetProperty("constraints", out var c) ? c.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
            var expected = row.TryGetProperty("term", out var term) ? term.GetString()
                : row.TryGetProperty("refusal", out var refusal) ? refusal.GetString() : "unbound";
            Check($"resolve [{string.Join(", ", evidence.Select(Text))}] with [{string.Join(", ", constraints)}]",
                Resolution(new WorkflowTypeParameter("T", constraints), evidence, vocabulary, mutant), expected);
        }

        foreach (var row in Contract.GetProperty("lub").EnumerateArray())
        {
            var terms = row.GetProperty("evidence").EnumerateArray().Select(e => Term(e.GetString()!, vocabulary)).ToList();
            var join = mutant == BindingMutant.None ? vocabulary.LeastUpperBound(terms) : vocabulary.LeastUpperBound(terms, mutant);
            Check($"lub [{string.Join(", ", terms.Select(Text))}]", Text(join), Optional(row, "lub"));
        }

        return failures;
    }

    private static string? Resolution(
        WorkflowTypeParameter parameter, List<ParameterEvidence> evidence, WorkflowTypeVocabulary vocabulary, BindingMutant mutant)
    {
        var resolution = TypeParameterBinding.Resolve(evidence, vocabulary, mutant);
        return resolution.Kind switch
        {
            ParameterResolutionKind.Unbound => "unbound",
            ParameterResolutionKind.Conflict => CompilationRules.TypeParameterConflict,
            _ => TypeParameterBinding.Refusal(parameter, resolution.Term!, vocabulary, mutant) ?? Text(resolution.Term),
        };
    }

    private static string? Optional(JsonElement row, string name) =>
        row.GetProperty(name).ValueKind == JsonValueKind.Null ? null : row.GetProperty(name).GetString();

    private static PortTerm Term(string text, WorkflowTypeVocabulary vocabulary) =>
        text == "null" ? PortTerm.Null : vocabulary.Table.Canonicalize(PortTerm.Parse(text, PortTermGrammar.Document));

    private static ParameterEvidence Evidence(string text, WorkflowTypeVocabulary vocabulary)
    {
        var exact = text.Split(" = ");
        if (exact.Length == 2)
            return new ParameterEvidence(exact[0], EvidenceKind.Exact, Term(exact[1], vocabulary));
        var lower = text.Split(" >= ");
        return new ParameterEvidence(lower[0], EvidenceKind.Lower, Term(lower[1], vocabulary));
    }

    private static string? Text(PortTerm? term) => term is null ? null : term.Kind == PortTermKind.Null ? "null" : term.ToString();

    private static string Text(ParameterEvidence evidence) =>
        $"{evidence.Parameter} {(evidence.Kind == EvidenceKind.Exact ? "=" : ">=")} {Text(evidence.Term)}";

    private static string EffectiveTerm(CompilationContext context, string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        var path = CanvasPath.Root;
        var segments = key[..colon].Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i += 2)
            path = path.Child(segments[i], segments[i + 1]);
        var parts = key[(colon + 1)..].Split('.');
        if (!context.Types.TryGetValue(new NodeKey(path, parts[0]), out var types))
            return "no node types";
        var side = parts[1] == "in" ? types.Inputs : types.Outputs ?? [];
        return side.FirstOrDefault(p => p.Name == parts[2]) is { } port ? Terms([port])[port.Name] : "no port";
    }

    private static WorkflowDefinition Canvas(JsonElement canvas, string entry)
    {
        var nodes = new List<WorkflowNodeDefinition> { StartNode(entry) };
        var mappings = new Dictionary<(string Source, string Target), Dictionary<string, object?>>();
        Dictionary<string, object?> Between(string source, string target)
        {
            if (!mappings.TryGetValue((source, target), out var into))
                mappings[(source, target)] = into = [];
            return into;
        }

        foreach (var wire in Elements(canvas, "wires"))
        {
            var from = wire.GetProperty("from").GetString()!.Split('.');
            var to = wire.GetProperty("to").GetString()!.Split('.');
            var into = Between(from[0], to[0]);
            if (from.Length == 2)
                into[to[1]] = new { type = "direct", sourceOutputName = from[1] };
        }

        foreach (var literal in Elements(canvas, "literals"))
        {
            var to = literal.GetProperty("to").GetString()!.Split('.');
            var value = literal.GetProperty("value");
            Between(entry, to[0])[to[1]] = value.ValueKind == JsonValueKind.Null ? new { type = "literal" } : new { type = "literal", literalValue = value };
        }

        var blocks = Contract.GetProperty("blocks");
        foreach (var row in Elements(canvas, "nodes"))
        {
            var id = row.GetProperty("id").GetString()!;
            var block = row.GetProperty("block").GetString()!;
            var config = row.TryGetProperty("config", out var c)
                ? c.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
                : new Dictionary<string, JsonElement>();
            var node = Node(id, block, config);
            if (row.TryGetProperty("typeArguments", out var arguments))
                node.TypeArguments = arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            if (blocks.TryGetProperty(block, out var shape) && shape.TryGetProperty("subCanvases", out var branches))
            {
                node.SubCanvases = [];
                foreach (var branch in branches.EnumerateObject())
                {
                    node.SubCanvases[branch.Name] = row.TryGetProperty("subCanvases", out var given) && given.TryGetProperty(branch.Name, out var body)
                        ? Canvas(body, "entry")
                        : Graph([StartNode("entry")]);
                }
            }

            nodes.Add(node);
            if (!mappings.Keys.Any(k => k.Target == id))
                mappings[(entry, id)] = [];
        }

        var edges = mappings.Select((m, i) => new WorkflowConnectionDefinition
        {
            Id = "e" + i,
            SourceNodeId = m.Key.Source,
            TargetNodeId = m.Key.Target,
            Mappings = JsonSerializer.SerializeToElement(m.Value),
        });
        return Graph(nodes, edges);
    }

    private static IEnumerable<JsonElement> Elements(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var list) ? list.EnumerateArray() : [];

    private static IBlockShapeSource BlockCatalogue()
    {
        var shapes = new List<BlockShape>
        {
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
        };
        foreach (var block in Contract.GetProperty("blocks").EnumerateObject())
            shapes.Add(BlockShapeOf(block.Name, block.Value));
        return Catalogue([.. shapes]);
    }

    private static BlockShape BlockShapeOf(string id, JsonElement block)
    {
        var required = Elements(block, "required").Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        List<PortShape> Ports(JsonElement owner, string side) => owner.TryGetProperty(side, out var ports)
            ? [.. ports.EnumerateObject().Select(p => new PortShape(p.Name, PortValueType.OfDeclaration(p.Value.GetString()!), required.Contains(p.Name)))]
            : [];

        var branchInputs = new Dictionary<string, IReadOnlyList<PortShape>>();
        var branchOutputs = new Dictionary<string, IReadOnlyList<PortShape>>();
        if (block.TryGetProperty("subCanvases", out var branches))
        {
            foreach (var branch in branches.EnumerateObject())
            {
                branchInputs[branch.Name] = Ports(branch.Value, "inputs");
                branchOutputs[branch.Name] = Ports(branch.Value, "outputs");
            }
        }

        return new BlockShape(
            id, Ports(block, "inputs"), Ports(block, "outputs"),
            [.. Elements(block, "configuration").Select(f => new ConfigFieldShape(
                f.GetProperty("name").GetString()!, ConfigFieldType.Enum, false,
                [.. f.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()!)]))],
            default, branchInputs, branchOutputs, null,
            SubCanvasNames: [.. branchInputs.Keys],
            TypeParameters: [.. Elements(block, "typeParameters").Select(p => new WorkflowTypeParameter(
                p.GetProperty("name").GetString()!, [.. Elements(p, "constraints").Select(x => x.GetString()!)]))]);
    }

    private static WorkflowTypeRegistry BuildRegistry()
    {
        var vocabulary = Contract.GetProperty("vocabulary");
        var records = new List<WorkflowTypeRegistrationRecord>();
        foreach (var atom in vocabulary.GetProperty("atoms").EnumerateArray())
        {
            records.Add(new WorkflowTypeRecord(atom.GetProperty("name").GetString()!, Stand.In(atom.GetProperty("clr").GetString()!))
            {
                WidensTo = [.. Elements(atom, "widensTo").Select(w => w.GetString()!)],
                Claims = Elements(atom, "claims").Select(c => Enum.Parse<WorkflowCapability>(c.GetString()!)).ToHashSet(),
            });
        }

        foreach (var constructor in vocabulary.GetProperty("constructors").EnumerateArray())
        {
            records.Add(new WorkflowTypeConstructorRecord(
                constructor.GetProperty("name").GetString()!, Stand.In(constructor.GetProperty("clr").GetString()!))
            {
                AllowedArguments = [.. Elements(constructor, "allowedArguments").Select(a => a.GetString()!)],
            });
        }

        return new WorkflowTypeRegistry(records);
    }

    private static class Stand
    {
        internal record Reading;

        internal sealed record Celsius : Reading;

        internal sealed record Hygrometer : Reading;

        internal class Shipment;

        internal sealed class ReeferShipment : Shipment;

        internal sealed class TankerShipment : Shipment;

        internal interface IUp;

        internal interface IDown;

        internal sealed class Left : IUp, IDown;

        internal sealed class Right : IUp, IDown;

        internal class GaugeRef;

        internal sealed class Thermometer : GaugeRef;

        internal sealed class BoxRef;

        internal static Type In(string name) => typeof(Stand).GetNestedType(name, System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"No CLR stand-in '{name}'.");
    }
}
