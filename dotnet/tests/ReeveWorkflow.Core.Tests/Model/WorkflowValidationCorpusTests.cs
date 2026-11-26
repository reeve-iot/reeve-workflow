using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using Xunit;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowValidationCorpusTests
{
    private const string Backend = "backend";
    private const string Editor = "editor";
    private static readonly string[] Sides = [Editor, Backend];
    private static readonly string[] CaseKeys = ["name", "why", "gradedBy", "notGradedBy", "signature", "definition", "expect"];

    private static readonly JsonElement File = Load(RepositoryFiles.ValidationCorpusPath());
    private static readonly IReadOnlyList<CorpusCase> Cases = [.. File.GetProperty("cases").EnumerateArray().Select(CorpusCase.Read)];
    private static readonly IReadOnlyDictionary<string, string> ParityClass = Load(RepositoryFiles.RuleParityPath())
        .GetProperty("rules").EnumerateArray()
        .ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("class").GetString()!);

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases)
            data.Add(c.Name);
        return data;
    }

    [Fact]
    public void The_corpus_is_not_empty() =>
        Cases.Should().NotBeEmpty("an empty corpus would make every case in this file vacuously pass");

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Each_case_names_the_sides_that_grade_it_and_why_the_others_do_not(string caseName)
    {
        var c = Case(caseName);

        c.Keys.Should().BeSubsetOf(CaseKeys, caseName);
        c.ExpectKeys.Should().BeSubsetOf(["verdict", "rules"], caseName);
        c.GradedBy.Should().NotBeEmpty(caseName).And.OnlyHaveUniqueItems(caseName).And.BeSubsetOf(Sides, caseName);
        c.NotGradedBy.Keys.Should().BeEquivalentTo(Sides.Except(c.GradedBy), $"'{caseName}' says why of each side it leaves out");
        c.NotGradedBy.Values.Should().NotContain(why => string.IsNullOrWhiteSpace(why), caseName);
        c.Verdict.Should().BeOneOf("valid", "invalid");
        c.Rules.Should().BeSubsetOf(ParityClass.Keys, $"'{caseName}' names rules of contracts/rule-parity.json");
        (c.Rules.Count > 0).Should().Be(c.Verdict == "invalid", $"'{caseName}' names the rules it refuses under, and only if it refuses");

        c.GradedBy.Should().Contain(Backend, caseName);
        var editorReaches = c.Verdict == "valid" || c.Rules.All(r => ParityClass[r] == "editor");
        c.GradedBy.Contains(Editor).Should().Be(editorReaches,
            $"'{caseName}' expects [{string.Join(", ", c.Rules)}], and the editor grades a case exactly when rule-parity.json classes each editor");
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_backend_reaches_a_case_exactly_when_its_marker_lists_the_backend(string caseName)
    {
        var c = Case(caseName);
        var result = Measure(c);

        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.Structure,
            $"case '{caseName}' fell through to 'structure' — a validator message drifted out from under " +
            "one of ClassifyStructure's substrings");
        Reaches(c, result).Should().Be(c.GradedBy.Contains(Backend),
            $"case '{caseName}' ({c.Why}) expects {c.Verdict} [{string.Join(", ", c.Rules)}]; the backend found " +
            $"[{string.Join(", ", result.Diagnostics.Select(d => d.RuleId).Distinct())}], and gradedBy is [{string.Join(", ", c.GradedBy)}]");
    }

    [Fact]
    public void The_cases_the_backend_reaches_are_the_cases_whose_marker_lists_it()
    {
        var file = Load(RepositoryFiles.ValidationCorpusPath()).GetProperty("cases").EnumerateArray().ToList();
        Cases.Select(c => c.Name).Should().Equal(file.Select(c => c.GetProperty("name").GetString()));
        Cases.Where(c => Reaches(c, Measure(c))).Select(c => c.Name).Should().Equal(file
            .Where(c => c.GetProperty("gradedBy").EnumerateArray().Any(s => s.GetString() == Backend))
            .Select(c => c.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("rule-agreement.json", typeof(RuleAgreementTests))]
    [InlineData("dangling-edge-agreement.json", typeof(DanglingEdgeAgreementTests))]
    public void The_note_names_each_generated_matrix_and_the_test_that_grades_it(string matrix, Type grader)
    {
        var note = string.Join(' ', File.GetProperty("$note").EnumerateArray().Select(line => line.GetString()));
        note.Should().Contain($"contracts/{matrix}").And.Contain(grader.Name);
        RepositoryFiles.ContractExists(matrix).Should().BeTrue(matrix);
    }

    [Fact]
    public void A_refusal_under_a_rule_the_case_does_not_expect_does_not_reach_it() =>
        Reaches(Case("required-input-unfed"), CompilationResult.Refused(
            [new CompilationDiagnostic(CompilationRules.Cycle, "Workflow contains a cycle.")])).Should().BeFalse();

    private static bool Reaches(CorpusCase c, CompilationResult result)
    {
        if ((result.Succeeded ? "valid" : "invalid") != c.Verdict)
            return false;
        return result.Succeeded || result.Diagnostics.Any(d => c.Rules.Contains(d.RuleId));
    }

    private static CompilationResult Measure(CorpusCase c)
    {
        var document = StoredWorkflowDocument.Read(c.Definition.GetRawText(), out var unreadable);
        return WorkflowCompilationService.CompileStored(
            document, unreadable, c.Signature("inputs"), c.Signature("outputs"), Blocks(), null, null, CancellationToken.None);
    }

    private static IBlockShapeSource Blocks()
    {
        static (string, PortValueType, bool)[] Ports(JsonElement ports) =>
            [.. ports.EnumerateArray().Select(p => (p.GetProperty("name").GetString()!,
                PortValueType.OfDeclaration(p.GetProperty("type").GetString()!), p.GetProperty("isRequired").GetBoolean()))];

        return Catalogue([.. File.GetProperty("blocks").EnumerateArray().Select(row =>
        {
            var branches = row.GetProperty("subCanvases").EnumerateArray().Select(b => b.GetString()!).ToList();
            return Shape(
                row.GetProperty("blockTypeId").GetString()!,
                inputs: Ports(row.GetProperty("inputs")),
                outputs: [.. row.GetProperty("outputs").EnumerateArray().Select(p =>
                    (p.GetProperty("name").GetString()!, PortValueType.OfDeclaration(p.GetProperty("type").GetString()!)))],
                hasDynamicPorts: row.GetProperty("hasDynamicPorts").GetBoolean(),
                supportsDeclaredOutputs: row.TryGetProperty("supportsDeclaredOutputs", out var declares) && declares.GetBoolean(),
                subCanvasInputs: branches.ToDictionary(b => b, b =>
                    row.GetProperty("subCanvasInputs").TryGetProperty(b, out var ports) ? Ports(ports) : []),
                subCanvasNames: branches,
                typeParameters: row.TryGetProperty("typeParameters", out var parameters)
                    ? [.. parameters.EnumerateArray().Select(t => new WorkflowTypeParameter(t.GetProperty("name").GetString()!))]
                    : null);
        })]);
    }

    private static CorpusCase Case(string name) => Cases.Single(c => c.Name == name);

    private static JsonElement Load(string path)
    {
        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private sealed record CorpusCase(
        string Name,
        string Why,
        IReadOnlyList<string> GradedBy,
        IReadOnlyDictionary<string, string> NotGradedBy,
        JsonElement? SignatureLists,
        JsonElement Definition,
        string Verdict,
        IReadOnlyList<string> Rules,
        IReadOnlyList<string> Keys,
        IReadOnlyList<string> ExpectKeys)
    {
        public static CorpusCase Read(JsonElement c)
        {
            var expect = c.GetProperty("expect");
            return new CorpusCase(
                c.GetProperty("name").GetString()!,
                c.GetProperty("why").GetString() ?? "",
                c.TryGetProperty("gradedBy", out var graded) ? [.. graded.EnumerateArray().Select(s => s.GetString()!)] : [],
                c.TryGetProperty("notGradedBy", out var not)
                    ? not.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                    : new Dictionary<string, string>(),
                c.TryGetProperty("signature", out var signature) ? signature.Clone() : null,
                c.GetProperty("definition").Clone(),
                expect.GetProperty("verdict").GetString()!,
                expect.TryGetProperty("rules", out var r) ? [.. r.EnumerateArray().Select(x => x.GetString()!)] : [],
                [.. c.EnumerateObject().Select(p => p.Name)],
                [.. expect.EnumerateObject().Select(p => p.Name)]);
        }

        public string? Signature(string side) =>
            SignatureLists is { } lists && lists.TryGetProperty(side, out var list) ? list.GetRawText() : null;
    }
}
