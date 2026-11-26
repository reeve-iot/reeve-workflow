using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed partial class StoredDocumentCorpusTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Corpus.Value.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_reader_answers_what_the_corpus_says(string name)
    {
        var row = Corpus.Value[name];
        var text = Units().Replace(row.GetProperty("text").GetString()!,
            m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());

        StoredDocumentFault? fault;
        IReadOnlyList<StoredPortTypeSite>? sites = row.GetProperty("reads").GetString() switch
        {
            "definition" => StoredWorkflowDocument.Read(text, out fault)?.Sites,
            "parameters" => StoredWorkflowDocument.ReadParameters(text, PortTypeNameSite.InputParameter, out fault),
            var other => throw new InvalidOperationException($"reads '{other}'"),
        };

        if (row.TryGetProperty("fault", out var expected))
        {
            sites.Should().BeNull();
            fault!.Rule.Should().Be(expected.GetProperty("rule").GetString());
            fault.Location.Should().Be(expected.GetProperty("location").GetString());
            fault.NodeId.Should().Be(expected.GetProperty("nodeId").GetString());
            if (expected.TryGetProperty("canvasPath", out var canvasPath))
                fault.CanvasPath.Select(s => new { nodeId = s.NodeId, branchName = s.BranchName })
                    .Should().Equal(canvasPath.EnumerateArray().Select(s => new
                    {
                        nodeId = s.GetProperty("nodeId").GetString()!,
                        branchName = s.GetProperty("branchName").GetString()!,
                    }));
            if (expected.TryGetProperty("detail", out var detail))
                fault.Detail.Should().Be(detail.GetString());
            return;
        }

        fault.Should().BeNull();
        sites!.Select(s => new
        {
            site = s.Site.ToString(),
            location = s.Location,
            nodeId = s.NodeId,
            portName = s.PortName,
            index = s.Index,
            rawType = s.RawType,
            isRequired = s.IsRequired,
        }).Should().Equal(row.GetProperty("sites").EnumerateArray().Select(e => new
        {
            site = e.GetProperty("site").GetString()!,
            location = e.GetProperty("location").GetString()!,
            nodeId = e.GetProperty("nodeId").GetString(),
            portName = e.GetProperty("portName").GetString()!,
            index = e.GetProperty("index").GetInt32(),
            rawType = e.GetProperty("rawType").GetString(),
            isRequired = e.GetProperty("isRequired").GetBoolean(),
        }));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_site_that_reports_a_refusal_reports_it_by_the_code_the_rule_parity_contract_gives(string name)
    {
        var row = Corpus.Value[name];
        if (!row.TryGetProperty("fault", out var expected))
            return;
        var text = Units().Replace(row.GetProperty("text").GetString()!,
            m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
        var rule = expected.GetProperty("rule").GetString()!;
        var definition = row.GetProperty("reads").GetString() == "definition";
        var code = ParityIds.Value.Contains(rule) || rule is "duplicate-edge-id" or "output-mappings-unread" ? rule
            : definition ? CompilationRules.DefinitionUnreadable : CompilationRules.SignatureUnreadable;

        if (definition)
        {
            PortTypeNameCheck.Check(text, null, null).Unreadable.Select(d => d.RuleId).Should().Equal(code);
            StoredWorkflowDocument.Read(text, out var fault).Should().BeNull();
            StoredWorkflowDocument.Unreadable(fault!).RuleId.Should().Be(code);
            return;
        }

        PortTypeNameCheck.Check("""{"nodes":[],"edges":[]}""", text, null).Unreadable.Select(d => d.RuleId).Should().Equal(code);
        PortTypeNameCheck.Check("""{"nodes":[],"edges":[]}""", null, text).Unreadable.Select(d => d.RuleId).Should().Equal(code);
        CanvasSignature.TryRead(text, null, out _, out var diagnostics).Should().BeFalse();
        diagnostics.Select(d => d.RuleId).Should().Equal(code);
    }

    [Fact]
    public void The_reader_accepts_node_ids_that_differ_only_in_case()
    {
        const string text = """{"nodes":[{"id":"Light","position":{"x":0,"y":0},"configuration":{}},{"id":"light","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

        var document = StoredWorkflowDocument.Read(text, out var fault);

        fault.Should().BeNull();
        document.Should().NotBeNull();
        document!.Definition.Nodes.Select(node => node.Id).Should().Equal("Light", "light");
    }

    [Fact]
    public void Every_rule_has_a_row_and_every_row_one_answer()
    {
        var rows = Corpus.Value.Values.ToList();
        rows.Select(r => (Fault: r.TryGetProperty("fault", out var _), Sites: r.TryGetProperty("sites", out var _)))
            .Should().OnlyContain(r => r.Fault != r.Sites);
        rows.Where(r => r.TryGetProperty("fault", out var _)).Select(r => r.GetProperty("fault").GetProperty("rule").GetString())
            .Distinct().Should().BeEquivalentTo(StoredDocumentRules.All);
    }

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Corpus = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.WorkflowDocumentCorpusPath()));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.Clone(), StringComparer.Ordinal);
    });

    private static readonly Lazy<IReadOnlySet<string>> ParityIds = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.RuleParityPath()));
        return document.RootElement.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
    });

    [GeneratedRegex("⟨([0-9A-F]{4})⟩")]
    private static partial Regex Units();
}
