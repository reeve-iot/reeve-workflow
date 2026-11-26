using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed partial class RuleAgreementTests
{
    private static readonly JsonElement File = Load(RepositoryFiles.RuleAgreementPath());
    private static readonly HashSet<string> CompilerOnly = [.. Load(RepositoryFiles.RuleParityPath()).GetProperty("rules")
        .EnumerateArray().Where(r => r.GetProperty("class").GetString() == "compiler-only").Select(r => r.GetProperty("id").GetString()!)];

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var testCase in File.GetProperty("cases").EnumerateArray())
            data.Add(testCase.GetProperty("name").GetString()!);
        return data;
    }

    [Fact]
    public void The_file_covers_every_depth_and_variant() =>
        File.GetProperty("cases").GetArrayLength().Should().Be(3 * 134 + 3 + 7 + 9 + 14 + 6 + 6 * 5 * 2 + 2 * (3 * 23 + 2 + 6 + 1));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_compiler_finds_exactly_what_the_file_lists(string caseName)
    {
        var testCase = File.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == caseName);
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(
            testCase.GetProperty("definition").GetRawText(), WorkflowGraphHelpers.JsonOptions)!;

        var result = WorkflowCompiler.Compile(definition, Blocks(), Signature(testCase));

        var listed = testCase.TryGetProperty("compilerOnly", out var only) ? Names(only) : [];
        var expected = testCase.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!);
        var found = result.Diagnostics.Where(d => !CompilerOnly.Contains(d.RuleId) && !IsCompilerOnlyPart(d)).SelectMany(Keys)
            .Concat(result.Diagnostics.SelectMany(Keys).Where(listed.Contains));

        found.Distinct().Order(StringComparer.Ordinal).Should().Equal(expected.Concat(listed).Distinct().Order(StringComparer.Ordinal));
    }

    private static bool IsCompilerOnlyPart(CompilationDiagnostic d) =>
        d.RuleId == CompilationRules.NodePortsUnresolved && !d.Message.Contains("names no callee", StringComparison.Ordinal);

    private static IEnumerable<string> Keys(CompilationDiagnostic d)
    {
        var path = d.CanvasPath is { Count: > 0 } segments
            ? string.Join('>', segments.Select(s => $"{s.NodeId}/{s.BranchName}"))
            : "root";
        var at = $"{FindingCode(d)} {path}";
        switch (d.RuleId)
        {
            case CompilationRules.Cycle or CompilationRules.NoStartNode or CompilationRules.InputHasIncoming or CompilationRules.DuplicateEdgePair:
                return [at];
            case CompilationRules.ExtraRoot or CompilationRules.Unreachable:
                return NodeList().Match(d.Message).Groups[1].Value.Split(", ").Select(id => $"{at} {id}");
            case CompilationRules.DuplicateInputMapping:
                return InputOnNode().Matches(d.Message).Select(m => $"{at} {m.Groups[2].Value} {m.Groups[1].Value}");
            case CompilationRules.EdgeEndpointUnknown when ConnectionEnd().Match(d.Message) is { Success: true } m:
                return [$"{at} {m.Groups[1].Value} {m.Groups[2].Value}"];
            case CompilationRules.EdgeEndpointUnknown:
                return [$"{at} {d.NodeId}"];
            case CompilationRules.DuplicateNodeId or CompilationRules.ReferenceInputMapped or CompilationRules.NodePortsUnresolved
                or CompilationRules.SubCanvasNameUnknown:
                return [$"{at} {d.NodeId}"];
            default:
                return [$"{at} {d.NodeId ?? "-"} {d.PortName}"];
        }
    }

    private static string FindingCode(CompilationDiagnostic diagnostic) => diagnostic.RuleId;

    [GeneratedRegex("^Connection '([^']+)' names (source|target) node ")]
    private static partial Regex ConnectionEnd();

    [GeneratedRegex(": ([^:]+)\\.$")]
    private static partial Regex NodeList();

    [GeneratedRegex("input '([^']*)' on node '([^']*)'")]
    private static partial Regex InputOnNode();

    private static CanvasSignature Signature(JsonElement testCase)
    {
        if (!testCase.TryGetProperty("signature", out var signature))
            return CanvasSignature.Empty;
        static List<PortShape> Ports(JsonElement side) =>
            [.. side.EnumerateArray().Select(p => new PortShape(
                p.GetProperty("name").GetString()!, TypeOf(p), p.GetProperty("isRequired").GetBoolean()))];
        return new CanvasSignature(Ports(signature.GetProperty("inputs")), Ports(signature.GetProperty("outputs")));
    }

    private static IBlockShapeSource Blocks()
    {
        var shapes = new List<BlockShape>();
        foreach (var row in File.GetProperty("blocks").EnumerateArray())
        {
            var branches = Names(row.GetProperty("subCanvases"));
            var reference = row.GetProperty("runsWorkflowFrom").GetString();
            var enumFields = row.TryGetProperty("enumFields", out var declared) ? declared.EnumerateArray().ToList() : [];
            shapes.Add(Shape(
                row.GetProperty("blockTypeId").GetString()!,
                inputs: [.. Ports(row.GetProperty("inputs"))],
                outputs: [.. Names(row.GetProperty("outputs")).Select(n => (n, PortValueType.String))],
                configFields: [.. Names(row.GetProperty("requiredConfigFields")).Select(n => (n, ConfigFieldType.String, true)),
                    .. enumFields.Select(f => (f.GetProperty("name").GetString()!, ConfigFieldType.Enum, f.TryGetProperty("required", out var req) && req.GetBoolean()))],
                enumValues: enumFields.Count == 0 ? null : Names(enumFields[0].GetProperty("values")),
                hasDynamicPorts: row.GetProperty("hasDynamicPorts").GetBoolean(),
                supportsDeclaredOutputs: row.GetProperty("supportsDeclaredOutputs").GetBoolean(),
                declaredOutputsCollect: row.TryGetProperty("declaredOutputsCollect", out var collects) && collects.GetBoolean(),
                portsAreResolvedElsewhere: reference is not null,
                runsWorkflowFrom: reference,
                subCanvasInputs: branches.ToDictionary(b => b, b =>
                    row.GetProperty("subCanvasInputs").TryGetProperty(b, out var ports) ? Ports(ports) : []),
                subCanvasOutputs: branches.ToDictionary(b => b, b =>
                    row.GetProperty("subCanvasOutputs").TryGetProperty(b, out var ports) ? Ports(ports) : []),
                subCanvasNames: branches,
                typeParameters: row.TryGetProperty("typeParameters", out var parameters)
                    ? [.. parameters.EnumerateArray().Select(p => new WorkflowTypeParameter(p.GetProperty("name").GetString()!, []))]
                    : null));
        }

        return Catalogue([.. shapes]);
    }

    private static (string, PortValueType, bool)[] Ports(JsonElement ports) =>
        [.. ports.EnumerateArray().Select(p => (p.GetProperty("name").GetString()!, TypeOf(p), p.GetProperty("isRequired").GetBoolean()))];

    private static PortValueType TypeOf(JsonElement port) =>
        port.TryGetProperty("type", out var type) ? PortValueType.Of(type.GetString()!) : PortValueType.String;

    private static List<string> Names(JsonElement names) => [.. names.EnumerateArray().Select(e => e.GetString()!)];

    private static JsonElement Load(string path)
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
