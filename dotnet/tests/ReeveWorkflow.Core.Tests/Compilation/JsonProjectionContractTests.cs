using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.JsonPath;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class JsonProjectionContractTests
{
    private static readonly Lazy<JsonElement> Contract = new(() =>
        JsonDocument.Parse(File.ReadAllText(RepositoryFiles.JsonProjectionPath())).RootElement.Clone());

    public static TheoryData<string> CompileRows() => new(Rows("compile").Select(r => r.GetProperty("name").GetString()!));

    public static TheoryData<string> RunRows() => new(Rows("run").Select(r => r.GetProperty("name").GetString()!));

    public static TheoryData<string> ExpressionRunRows() =>
        new(Rows("run").Where(r => !r.TryGetProperty("wireOnly", out _)).Select(r => r.GetProperty("name").GetString()!));

    public static TheoryData<string> ExpressionRows() => new(Rows("expression").Select(r => r.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(CompileRows))]
    public void Each_compile_row_reports_its_codes(string name)
    {
        var row = Row("compile", name);
        var (graph, shapes, signature, target, input) = Build(row);
        var options = row.TryGetProperty("hostTypes", out var hosts)
            ? HostTypes([.. hosts.EnumerateArray().Select(h => h.GetString()!)])
            : null;

        var run = WorkflowCompiler.Run(graph, shapes, signature, options);

        var expected = row.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        run.Diagnostics.Select(d => d.RuleId).ToHashSet(StringComparer.Ordinal)
            .Should().BeEquivalentTo(expected, string.Join("; ", run.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        if (row.TryGetProperty("message", out var message))
            run.Diagnostics.Should().ContainSingle(d => d.Message == message.GetString());
        if (expected.Count == 0)
            WorkflowCompiler.Compile(graph, shapes, signature, options).Program.Should().NotBeNull();
        if (target is null || expected.Contains(CompilationRules.DefinitionUnreadable))
            return;
        var types = run.Types[new NodeKey(CanvasPath.Root, target)];
        if (row.TryGetProperty("term", out var term))
            Terms(types.Inputs)[input!].Should().Be(term.GetString());
        if (row.TryGetProperty("binding", out var binding))
        {
            foreach (var parameter in binding.EnumerateObject())
                types.Bindings[parameter.Name]!.ToString().Should().Be(parameter.Value.GetString());
        }
    }

    [Theory]
    [MemberData(nameof(RunRows))]
    public void Each_run_row_gives_its_value_absence_or_category(string name)
    {
        var row = Row("run", name);
        var vocabulary = WorkflowTypeVocabulary.Builtin;
        var term = vocabulary.ClosedTypeOf(PortTerm.Parse(row.GetProperty("term").GetString(), PortTermGrammar.Document))!;
        var jsonText = row.TryGetProperty("mode", out var mode) && mode.GetString() == "jsonText";
        var projection = new CompiledProjection(JsonPathQuery.Parse(row.GetProperty("path").GetString()!), jsonText, term, null, vocabulary);
        object? source = row.TryGetProperty("sourceNull", out _) ? null
            : row.TryGetProperty("sourceText", out var text) ? JsonDocument.Parse(text.GetString()!).RootElement.Clone()
            : row.GetProperty("source").Clone();
        var expect = row.GetProperty("expect");

        if (expect.TryGetProperty("category", out var category))
        {
            FluentActions.Invoking(() => projection.TryBind(source, "wire", out _))
                .Should().Throw<JsonProjectionException>().Which.Category.Should().Be(category.GetString());
            return;
        }

        var present = projection.TryBind(source, "wire", out var value);
        if (expect.TryGetProperty("absent", out _))
        {
            present.Should().BeFalse();
            return;
        }

        present.Should().BeTrue();
        var wanted = expect.GetProperty("value");
        if (value is null)
        {
            wanted.ValueKind.Should().Be(JsonValueKind.Null);
            return;
        }

        value.Should().BeAssignableTo(term.ClrType);
        var written = value is JsonElement node ? node : term.ToJson(value)!.Value;
        JsonElement.DeepEquals(written, wanted).Should().BeTrue($"{written.GetRawText()} should be {wanted.GetRawText()}");
    }

    [Theory]
    [MemberData(nameof(ExpressionRunRows))]
    public void Each_run_row_gives_the_same_value_or_category_through_jsonPath(string name)
    {
        var row = Row("run", name);
        var vocabulary = WorkflowTypeVocabulary.Builtin;
        var term = row.GetProperty("term").GetString()!;
        var jsonText = row.TryGetProperty("mode", out var mode) && mode.GetString() == "jsonText";
        object? source = row.TryGetProperty("sourceNull", out _) ? null
            : row.TryGetProperty("sourceText", out var text) ? JsonDocument.Parse(text.GetString()!).RootElement.Clone()
            : row.GetProperty("source").Clone();
        var (typing, evaluation) = ReadScope(vocabulary, source);

        var typed = ExpressionTyper.Type(Call(jsonText, row.GetProperty("path").GetString()!, term), typing);

        typed.Refusals.Should().BeEmpty();
        typed.Tree!.Term.ToString().Should().Be(term);
        var expect = row.GetProperty("expect");
        if (expect.TryGetProperty("category", out var category))
        {
            var thrown = FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(typed.Tree, evaluation))
                .Should().Throw<ExpressionEvaluationException>().Which;
            thrown.Category.Should().Be(category.GetString());
            thrown.Code.Should().Be(TypedExpressionEvaluator.FailureCode);
            return;
        }

        var value = TypedExpressionEvaluator.Evaluate(typed.Tree, evaluation);
        if (expect.TryGetProperty("absent", out _) || expect.GetProperty("value").ValueKind == JsonValueKind.Null)
        {
            value.Should().BeNull();
            return;
        }

        var written = vocabulary.ClosedTypeOf(typed.Tree.Term)!.ToJson(value!)!.Value;
        JsonElement.DeepEquals(written, expect.GetProperty("value")).Should().BeTrue($"{written.GetRawText()} should be {expect.GetProperty("value").GetRawText()}");
    }

    [Theory]
    [MemberData(nameof(ExpressionRows))]
    public void Each_expression_row_reports_its_codes(string name)
    {
        var row = Row("expression", name);
        var vocabulary = row.TryGetProperty("hostTypes", out var hosts)
            ? HostTypes([.. hosts.EnumerateArray().Select(h => h.GetString()!)]).Vocabulary
            : WorkflowTypeVocabulary.Builtin;
        var jsonText = row.TryGetProperty("mode", out var mode) && mode.GetString() == "jsonText";
        var (typing, _) = ReadScope(vocabulary, null);

        var typed = ExpressionTyper.Type(Call(jsonText, row.GetProperty("path").GetString()!, row.GetProperty("type").GetString()!), typing);

        var expected = row.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        typed.Refusals.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal)
            .Should().BeEquivalentTo(expected, string.Join("; ", typed.Refusals.Select(r => r.RuleId + ": " + r.Reason)));
        if (row.TryGetProperty("message", out var message))
            typed.Refusals.Should().ContainSingle(r => r.Reason == message.GetString());
        if (expected.Count == 0)
            typed.Tree!.Term.ToString().Should().Be(row.GetProperty("term").GetString());
    }

    internal static string Call(bool jsonText, string path, string type)
    {
        static string Literal(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return $"{(jsonText ? "jsonPathText" : "jsonPath")}(${{previous.s.Out}}, {Literal(path)}, {Literal(type)})";
    }

    internal static (ExpressionTypingScope Typing, ExpressionEvaluationScope Evaluation) ReadScope(WorkflowTypeVocabulary vocabulary, object? source)
    {
        var typing = new ExpressionTypingScope.Builder().Vocabulary(vocabulary)
            .Previous((node, output) => node == "s" && output == "Out"
                ? ReadLookup.Typed(PortTerm.Parse("Json?", PortTermGrammar.Document))
                : ReadLookup.Missing("no such output"))
            .Build();
        var evaluation = new ExpressionEvaluationScope.Builder().Vocabulary(vocabulary)
            .Previous((node, output) => node == "s" && output == "Out" ? (true, source) : (false, null))
            .Build();
        return (typing, evaluation);
    }

    internal static IEnumerable<JsonElement> Rows(string section) => Contract.Value.GetProperty(section).EnumerateArray();

    internal static JsonElement Row(string section, string name) => Rows(section).Single(r => r.GetProperty("name").GetString() == name);

    private static (WorkflowDefinition Graph, IBlockShapeSource Shapes, CanvasSignature Signature, string? Target, string? Input) Build(JsonElement row)
    {
        var sourceTerm = row.TryGetProperty("source", out var s) ? s.GetString()! : "Json?";
        var source = Shape("source", outputs: [("Out", PortValueType.OfDeclaration(sourceTerm))]);
        var into = row.GetProperty("into");
        var mapping = new JsonObject { ["type"] = row.TryGetProperty("literal", out _) ? "literal" : "direct" };
        if (mapping["type"]!.GetValue<string>() == "literal")
            mapping["literalValue"] = 1;
        else
            mapping["sourceOutputName"] = "Out";
        if (row.GetProperty("projection") is { ValueKind: JsonValueKind.Object } projection)
            mapping["projection"] = JsonNode.Parse(projection.GetRawText());

        WorkflowNodeDefinition target;
        var shapes = new List<BlockShape> { source };
        var signature = CanvasSignature.Empty;
        string input;
        string? typed = "t";
        if (into.TryGetProperty("input", out var declared))
        {
            var text = declared.GetString()!;
            var parameters = PortTerm.TryParse(text, PortTermGrammar.Declaration, out var parsed, out _) && parsed.ContainsParameter
                ? TypeParameterBinding.Parameters(parsed).Distinct().Select(p => new WorkflowTypeParameter(p, [])).ToList()
                : null;
            shapes.Add(Shape("sink", inputs: [("In", PortValueType.OfDeclaration(text), true)], typeParameters: parameters));
            target = Node("t", "sink");
            input = "In";
        }
        else if (into.TryGetProperty("passthrough", out _))
        {
            shapes.Add(Shape("relay"));
            target = Node("t", "relay", passthrough: ["Data"]);
            input = "Data";
        }
        else
        {
            target = Node("t", WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
            input = "O";
            signature = new CanvasSignature([], [new PortShape("O", PortValueType.Of(into.GetProperty("workflowOutput").GetString()!), true)]);
            typed = null;
        }

        var edge = new WorkflowConnectionDefinition
        {
            Id = "e", SourceNodeId = "s", TargetNodeId = "t",
            Mappings = JsonSerializer.SerializeToElement(new JsonObject { [input] = mapping }),
        };
        var graph = Graph([StartNode(), Node("s", "source"), target], [Edge("e0", "start", "s"), edge]);
        return (graph, Catalogue([.. shapes]), signature, typed, input);
    }
}
