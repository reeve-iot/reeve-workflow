using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using System.Text.Json;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class GridTests
{
    private static string Chr(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static readonly string[] Ws =
    [
        " ", "\t", Chr(0x0085), Chr(0x2028), Chr(0x2029), Chr(0x00a0), Chr(0x3000), Chr(0x1680), Chr(0x200a), Chr(0x202f), Chr(0x205f),
        "\r\n", " " + Chr(0x0085) + Chr(0x2028) + " ",
    ];

    private static readonly string[] Odd =
    [
        "Collection" + Chr(0xfeff), "Collection<" + Chr(0xfeff) + ">", "Collection<" + Chr(0x200b) + ">", "Collection" + Chr(0x200b), "Collection<>",
        "Collection<<>>", "Collection<<<>>>", "Collection< <> >", "Collection<< >>", "Collection<" + Chr(0x0000) + ">",
        "Collection" + Chr(0x0001), "Collection<>>", "Collection<<>", "Collection>", "Collection<", "CollectionCollection<>",
        "Collection<Collection<>>", "Collection<Collection >", "Collection< Collection >",
        "Collection a b", "CollectionFoo" + Chr(0x00a0) + "Bar", "Collection" + Chr(0xfeff) + " ", "CollectionPt ", "Collection< Pt>", "Collection<Pt >",
        "Collection<" + Chr(0x00a0) + "Pt>", "CollectionCollection" + Chr(0x200b), "a b", "Foo" + Chr(0x00a0) + "Bar", " Pt", "Pt ", Chr(0x200b), Chr(0x180e),
        "Collection<a b>", "CollectionZz", "Collection<Zz>", "CollectionCollection ", "Collection<Collection" + Chr(0x00a0) + ">",
    ];

    private static readonly string[] Ins =
    [
        "Any", "Collection", "Widget", "CollectionWidget", "Collection ", "CollectionCollection", "Int",
        "CollectionPt ", "Pt ", "Collection" + Chr(0x200b), "CollectionZz", "Collection<Int>", "Number",
    ];

    private static List<string> Outs()
    {
        var outs = new List<string>(Odd);
        foreach (var ws in Ws)
        {
            outs.Add("Collection" + ws);
            outs.Add("Collection<" + ws + ">");
            outs.Add("CollectionCollection" + ws);
            outs.Add("Collection<Collection<" + ws + ">>");
            outs.Add("Collection<<" + ws + ">>");
            outs.Add("CollectionCollection<" + ws + ">");
        }
        return outs;
    }

    private static WorkflowDefinition GraphFor(string outType, string inType)
    {
        var outputs = JsonSerializer.Serialize(new[] { new { name = "Items", type = outType } });
        var inputs = JsonSerializer.Serialize(new[] { new { name = "In", type = inType, isRequired = true } });
        return Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = outputs }), Node("b", "dyn.sink", new { InputPorts = inputs })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Items", "In"))]);
    }

    private static readonly IBlockShapeSource Catalogue2 = Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("dyn.source", hasDynamicPorts: true),
        Shape("dyn.sink", hasDynamicPorts: true));

    [Fact]
    public void Compile_never_throws_on_any_r7_or_real_grid_pair()
    {
        var outs = Outs().Concat(RealOuts);
        var ins = Ins.Concat(RealIns);

        foreach (var o in outs)
            foreach (var i in ins)
            {
                var compile = () => WorkflowCompiler.Compile(GraphFor(o, i), Catalogue2, CanvasSignature.Empty);
                compile.Should().NotThrow(because: $"out '{Esc(o)}', in '{Esc(i)}'");
            }
    }

    [Fact]
    public void A_malformed_end_reports_port_type_malformed_alone_never_stacked_with_a_mismatch()
    {
        var outs = Outs().Concat(RealOuts);
        var ins = Ins.Concat(RealIns);

        foreach (var o in outs)
            foreach (var i in ins)
            {
                var oWellFormed = PortValueType.TryParse(o, out _, out _);
                var iWellFormed = PortValueType.TryParse(i, out _, out _);
                if (oWellFormed && iWellFormed)
                    continue;

                var result = WorkflowCompiler.Compile(GraphFor(o, i), Catalogue2, CanvasSignature.Empty);
                var because = $"out '{Esc(o)}' (well-formed: {oWellFormed}), in '{Esc(i)}' (well-formed: {iWellFormed})";

                result.Diagnostics.Should().Contain(
                    d => d.RuleId == CompilationRules.PortTypeMalformed, because: because);
                result.Diagnostics.Should().NotContain(
                    d => d.RuleId == CompilationRules.PortTypeMismatch || d.RuleId == CompilationRules.PortTypeUnresolved
                        || d.RuleId == CompilationRules.WirePortUnknown,
                    because: because);
            }
    }

    [Fact]
    public void The_port_type_name_checker_reports_what_the_compiler_reports_on_every_grid_pair()
    {
        var compared = 0;
        foreach (var o in Outs().Concat(RealOuts))
            foreach (var i in Ins.Concat(RealIns))
            {
                var graph = GraphFor(o, i);
                var checker = PortTypeNameCheckTests.Outcome(() => PortTypeNameCheck.Check(Stored(graph), null, null).Diagnostics);
                var compiler = PortTypeNameCheckTests.Outcome(() => WorkflowCompiler.Compile(graph, Catalogue2, CanvasSignature.Empty)
                    .Diagnostics.Where(d => d.RuleId == CompilationRules.PortTypeMalformed));

                checker.Should().BeEquivalentTo(compiler, because: $"out '{Esc(o)}', in '{Esc(i)}'");
                compared++;
            }

        compared.Should().BeGreaterThan(1000);
    }

    private static readonly string[] RealOuts =
    [
        "Int", "int", "Number", "Float", "String", "bool", "Boolean", "Any", "Collection", "CollectionInt", "Collection<Int>",
        "collection<int>", "CollectionCollectionInt", "Collection<Collection<Number>>", "CollectionCollection", "CollectionAny",
        "Collection<Any>", "Widget", "widget", "CollectionWidget", "Collection<Widget>", "Dictionary<K,V>", "Collection<Dictionary<K,V>>",
        "Dictionary<String,Int>", "Device", "CollectionDevice", "Collections", "Json", "DateTime", "Dict", "CollectionDict",
    ];

    private static readonly string[] RealIns =
    [
        "Any", "Collection", "Int", "Number", "Boolean", "String", "CollectionNumber", "CollectionInt", "Collection<Number>",
        "CollectionCollectionNumber", "CollectionCollection", "Widget", "CollectionWidget", "Dictionary<K,V>", "CollectionDictionary<K,V>",
        "Dictionary<String,Int>", "Device", "Json", "Collections",
    ];

    private static string Esc(string s)
    {
        var b = new System.Text.StringBuilder();
        foreach (var c in s) b.Append(c < 0x20 || c > 0x7e ? $"\\u{(int)c:x4}" : c.ToString());
        return b.ToString();
    }
}
