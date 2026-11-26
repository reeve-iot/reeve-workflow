using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class RuleParityTests
{
    private static readonly string[] Paths = ["root", "loop/Body", "loop/Body>loop2/Body"];
    private static readonly IReadOnlyList<JsonElement> Rows = Load(RepositoryFiles.RuleParityPath()).GetProperty("rules").EnumerateArray().ToList();

    private static readonly string OverTheNodeLogLimit = new('n', WorkflowGraphHelpers.NodeLogTextLimit + 1);

    private static string Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    [Fact]
    public void Every_compiler_rule_is_classed_once_and_nothing_else_is()
    {
        var catalogue = typeof(CompilationRules).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        var classed = Rows.Select(r => Text(r, "id")).ToList();

        catalogue.Except(classed).Should().BeEmpty("each is a CompilationRules id rule-parity.json does not class");
        classed.Except(catalogue).Should().BeEmpty("each is a rule-parity.json id CompilationRules does not hold");
        classed.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_row_says_what_its_class_needs()
    {
        foreach (var row in Rows)
        {
            var id = Text(row, "id");
            Text(row, "why").Should().NotBeEmpty(id);
            if (Text(row, "class") == "editor")
            {
                Text(row, "depth").Should().BeOneOf(["every", "root", "document", "registration"], id);
                Text(row, "agreement").Should().NotBeEmpty(id);
                foreach (Match file in Regex.Matches(Text(row, "agreement"), @"contracts/([\w-]+\.\w+)"))
                    RepositoryFiles.ContractExists(file.Groups[1].Value).Should().BeTrue($"'{id}' names {file.Value} as its agreement");
            }
            else
            {
                Text(row, "class").Should().Be("compiler-only", id);
                Text(row, "depth").Should().BeOneOf(["every", "none"], id);
            }
        }
    }

    public static TheoryData<string> GradedByTheAgreement() =>
        [.. Rows.Where(r => Text(r, "depth") is not ("registration" or "document")
            && Text(r, "agreement").Contains("contracts/rule-agreement.json", StringComparison.Ordinal)).Select(r => Text(r, "id"))];

    [Theory]
    [MemberData(nameof(GradedByTheAgreement))]
    public void The_agreement_expects_an_editor_rule_on_each_canvas_its_depth_names_and_no_other(string id)
    {
        var depth = Text(Rows.Single(r => Text(r, "id") == id), "depth");
        if (depth == "document") return;
        var paths = Load(RepositoryFiles.RuleAgreementPath()).GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("expect").EnumerateArray()).Select(k => k.GetString()!.Split(' '))
            .Where(k => k[0] == id).Select(k => k[1]).ToHashSet();

        paths.Should().Contain(depth == "every" ? Paths : ["root"]);
        if (depth == "root")
            paths.Should().Equal(["root"]);
    }

    public static TheoryData<string> CompilerOnlyAtEveryDepth() =>
        [.. Rows.Where(r => Text(r, "class") == "compiler-only" && Text(r, "depth") == "every").Select(r => Text(r, "id"))];

    [Theory]
    [MemberData(nameof(CompilerOnlyAtEveryDepth))]
    public void A_compiler_only_rule_of_every_depth_is_refused_two_branches_down(string id)
    {
        var (nodes, edges, options, phases) = Breaking(id);
        var nested = Graph([StartNode("s2"), .. nodes], [.. edges]);
        var inner = Container("loop2", nested);
        var body = Graph([StartNode("s1"), inner], [Edge("b1", "s1", "loop2")]);
        var graph = Graph([StartNode(), Container("loop", body)], [Edge("e1", "start", "loop")]);

        var document = StoredWorkflowDocument.ReadModel(graph, out _)!;
        var environment = id == CompilationRules.DottedName
            ? new NodePortEnvironmentBuilder()
                .Add(CanvasPath.Root.Child("loop", "Body").Child("loop2", "Body"), "x",
                    NodePortResolution.Resolved("child", new CanvasSignature([], [new PortShape("result.value", PortValueType.String, false)])))
                .Build()
            : null;
        var result = WorkflowCompiler.Compile(document, Shapes(), CanvasSignature.Empty, options, environment, phases: phases);

        result.Diagnostics.Should().Contain(d => d.RuleId == id
            && d.CanvasPath.SequenceEqual(new[] { new CanvasPathSegment("loop", "Body"), new CanvasPathSegment("loop2", "Body") }));
    }

    private static (WorkflowNodeDefinition[], WorkflowConnectionDefinition[], CompilationOptions?, IReadOnlyList<CompilerPhase>?) Breaking(string id) => id switch
    {
        CompilationRules.BlockTypeUnknown => ([Node("x", "plugin.gone")], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.PortTypeMismatch => ([Node("a", "number.out"), Node("b", "flag.in")],
            [Edge("n1", "s2", "a"), Edge("n2", "a", "b", ("N", "Flag"))], null, null),
        CompilationRules.PortTypeUnresolved => ([Node("o", "relay", passthrough: ["P"])], [LiteralEdge("n1", "s2", "o", "P", new List<object?>())], null, null),
        CompilationRules.TypeParameterConflict => ([Node("x", "pair")], [Literals("n1", "s2", "x", ("B", 1), ("C", "two"))], null, null),
        CompilationRules.TypeParameterUnbound => ([Node("x", "pair")], [Literals("n1", "s2", "x", ("A", new List<object?>()))], null, null),
        CompilationRules.TypeParameterConstraint => ([TypeArgument(Node("x", "numeric"), "T", "String")], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.PortTypeAlias => ([Node("x", "dyn.source", new { OutputPorts = """[{"name":"P","type":"string"}]""" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.PortTypeUnregistered => ([Node("x", "dyn.source", new { OutputPorts = """[{"name":"P","type":"Shipment"}]""" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.PortTypeJsonPosition => ([TypeArgument(Node("x", "generic"), "T", "Collection<Json>")], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.JsonProjectionInvalid => ([Node("a", "number.out"), Node("b", "flag.in")],
            [Edge("n1", "s2", "a"), Projected("n2", "a", "b", "N", "Flag")], null, null),
        CompilationRules.PortListWithoutDynamicPorts =>
            ([Node("x", "notify", new { Message = "m", InputPorts = """[{"name":"P","type":"String"}]""" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.DeclaredOutputsUnsupported =>
            ([Declared(Node("x", "notify", new { Message = "m" }), "P", null)], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.DeclaredOutputSourceUnknown =>
            ([Declared(Node("x", "branch"), "P", new() { ["Nowhere"] = "k" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.ErrorPolicyOnBoundaryNode =>
            ([OnBoundary(Node("x", WorkflowGraphHelpers.WorkflowOutputBlockTypeId))], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.SecretInputPassthrough =>
            ([Node("x", "secrets", passthrough: ["Headers"])], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.SecretInputKeyInvalid =>
            ([Node("x", "secrets", new { Headers = new Dictionary<string, string> { ["Authorization: Bearer x"] = "v" } })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.ExpressionSyntax => ([Node("x", "notify", new { Message = "expr:${a.b} +" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.ExpressionOutOfScope =>
            ([Node("x", "notify", new { Message = "expr:${previous.typo.value}" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.ExpressionUnknownNamespace =>
            ([Node("x", "notify", new { Message = "expr:${device.gone.temperature}" })], [Edge("n1", "s2", "x")],
                new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new RefusingDeviceNamespace(), 2)] }, null),
        CompilationRules.ExpressionFieldAccessInvalid =>
            ([Node("x", "notify", new { Message = "expr:${now.year.name}" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.ExpressionTypeMismatch =>
            ([Node("x", "notify", new { Message = "expr:1 + true" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.ExpressionResultTypeMismatch =>
            ([Node("x", "notify", new { Message = "expr:1 + 2" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.NodeIdTooLong =>
            ([Node(OverTheNodeLogLimit, "notify", new { Message = "m" })], [Edge("n1", "s2", OverTheNodeLogLimit)], null, null),
        CompilationRules.BlockTypeIdTooLong =>
            ([Node("x", OverTheNodeLogLimit)], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.DottedName =>
            ([Node("x", "workflow.run", new { WorkflowId = "child" })], [Edge("n1", "s2", "x")], null, null),
        CompilationRules.CompilationFault => ([Node("x", "notify", new { Message = "m" })], [Edge("n1", "s2", "x")], null,
            [.. WorkflowCompiler.Phases.Select(p => p.Name != "check-nodes" ? p : p with
            {
                Run = (frame, context) =>
                {
                    if (frame.Path.Segments.Count == 2)
                        throw new InvalidOperationException("injected");
                    p.Run(frame, context);
                },
            })]),
        _ => throw new InvalidOperationException($"'{id}' is compiler-only at every depth and has no case here; add one."),
    };

    private static WorkflowConnectionDefinition Projected(string id, string source, string target, string output, string input) => new()
    {
        Id = id, SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [input] = new { type = "direct", sourceOutputName = output, projection = new { path = "$" } },
        }),
    };

    private static WorkflowNodeDefinition OnBoundary(WorkflowNodeDefinition node)
    {
        node.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.Skip, RouteToErrorPort: false);
        return node;
    }

    private static WorkflowNodeDefinition Declared(WorkflowNodeDefinition node, string name, Dictionary<string, string>? sources)
    {
        node.OutputPorts = [new SubCanvasOutputPortDefinition { Name = name, DisplayName = name, Type = "String", Sources = sources }];
        return node;
    }

    private static WorkflowNodeDefinition Container(string id, WorkflowDefinition body)
    {
        var node = Node(id, "loop.for");
        node.SubCanvases = new() { ["Body"] = body };
        return node;
    }

    private static IBlockShapeSource Shapes() => Catalogue(
        Shape("loop.for", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]),
        Shape("branch", supportsDeclaredOutputs: true, subCanvasNames: ["Then"]),
        Shape("notify", inputs: [("Message", PortValueType.String, true)]),
        Shape("ParseJson", inputs: [("Json", PortValueType.String, false)]),
        Shape("number.out", outputs: [("N", PortValueType.Number)]),
        Shape("flag.in", inputs: [("Flag", PortValueType.Boolean, false)]),
        Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
            inputs: [("WorkflowId", PortValueType.String, true)]),
        Shape("relay"),
        Shape("secrets", inputs: [("Headers", PortValueType.MapOf(PortValueType.String), false)], secretInput: "Headers"),
        Shape("dyn.source", hasDynamicPorts: true),
        Shape("generic", typeParameters: [new WorkflowTypeParameter("T")]),
        Shape("pair", inputs: [("A", PortValueType.OfDeclaration("Collection<$T>"), false), ("B", PortValueType.OfDeclaration("$T"), false),
            ("C", PortValueType.OfDeclaration("$T"), false)],
            typeParameters: [new WorkflowTypeParameter("T")]),
        Shape("numeric", typeParameters: [new WorkflowTypeParameter("T", ["Numeric"])]));

    private static WorkflowConnectionDefinition Literals(string id, string source, string target, params (string Input, object? Value)[] literals) =>
        new()
        {
            Id = id,
            SourceNodeId = source,
            TargetNodeId = target,
            Mappings = JsonSerializer.SerializeToElement(literals.ToDictionary(l => l.Input, l => (object)new { type = "literal", literalValue = l.Value })),
        };

    private static WorkflowNodeDefinition TypeArgument(WorkflowNodeDefinition node, string name, string term)
    {
        node.TypeArguments = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = term };
        return node;
    }

    private static JsonElement Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private sealed class RefusingDeviceNamespace : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "device";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => NamespacePathCheckAnswer.No;
    }
}
