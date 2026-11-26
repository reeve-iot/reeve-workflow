using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed partial class DanglingEdgeAgreementTests
{
    private static readonly JsonElement File = LoadFile();

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var testCase in File.GetProperty("cases").EnumerateArray())
            data.Add(testCase.GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void The_file_covers_every_depth_missing_end_and_mapping_kind() =>
        File.GetProperty("cases").GetArrayLength().Should().Be(3 * 3 * 3 + 2);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_compiler_finds_exactly_what_the_file_lists(string caseName)
    {
        var testCase = File.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == caseName);
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(
            testCase.GetProperty("definition").GetRawText(), WorkflowGraphHelpers.JsonOptions)!;

        var result = WorkflowCompiler.Compile(definition, Blocks(), CanvasSignature.Empty);

        result.Diagnostics.Select(Key).Order(StringComparer.Ordinal).Should().Equal(
            testCase.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
    }

    private static string Key(CompilationDiagnostic d)
    {
        if (d.RuleId == CompilationRules.EdgeEndpointUnknown && ConnectionEnd().Match(d.Message) is { Success: true } m)
            return $"{d.RuleId} {m.Groups[1].Value} {m.Groups[2].Value}";
        if (d.RuleId == CompilationRules.RequiredInputUnfed)
            return $"{d.RuleId} {d.NodeId} {d.PortName}";
        return $"unclassified: {d.RuleId} {d.Message}";
    }

    [GeneratedRegex("^Connection '([^']+)' names (source|target) node ")]
    private static partial Regex ConnectionEnd();

    private static IBlockShapeSource Blocks()
    {
        var shapes = new List<BlockShape> { Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId) };
        foreach (var row in File.GetProperty("blocks").EnumerateArray())
        {
            var branches = Names(row, "subCanvases");
            shapes.Add(Shape(
                row.GetProperty("blockTypeId").GetString()!,
                inputs: [.. Names(row, "requiredStringInputs").Select(n => (n, PortValueType.String, true))],
                outputs: [.. Names(row, "stringOutputs").Select(n => (n, PortValueType.String))],
                subCanvasInputs: branches.ToDictionary(b => b, _ => Array.Empty<(string, PortValueType, bool)>()),
                subCanvasNames: branches));
        }

        return Catalogue([.. shapes]);
    }

    private static List<string> Names(JsonElement row, string property) =>
        [.. row.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    private static JsonElement LoadFile()
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(RepositoryFiles.DanglingEdgeAgreementPath()));
        return document.RootElement.Clone();
    }
}
