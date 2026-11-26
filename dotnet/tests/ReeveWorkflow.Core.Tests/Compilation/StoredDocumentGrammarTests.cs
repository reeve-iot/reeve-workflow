using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class StoredDocumentGrammarTests
{
    private static readonly JsonElement Contract = Load(RepositoryFiles.StoredDocumentGrammarPath());

    private static readonly string[] WorkflowRowFields =
    [
        "InputParametersJson", "OutputParametersJson", "ExecutionTimeoutSeconds", "RetryPolicyJson", "Mode",
        "MaxConcurrentRuns", "MaxQueuedRuns", "OverflowBehavior", "CheckpointPolicyJson",
    ];

    private static JsonElement[] Rows(string list) => [.. Contract.GetProperty(list).EnumerateArray()];

    private static string[] Labels(string list) => [.. Rows(list).Select(row => row.GetProperty("path").GetString()!)];

    private static string[] RuleIds() => [.. Rows("rules").Select(row => row.GetProperty("id").GetString()!)];

    [Fact]
    public void Every_field_of_the_kind_table_is_a_path_row_and_no_path_row_is_another_field()
    {
        var labels = StoredVersionComparison.Fields.Select(field => field.Label).ToList();

        labels.Should().OnlyHaveUniqueItems();
        Labels("paths").Should().BeEquivalentTo(labels);
    }

    [Fact]
    public void Every_object_kind_the_schema_reaches_is_an_object_row_and_no_object_row_is_another_kind()
    {
        var labels = ObjectLabels();

        labels.Should().OnlyHaveUniqueItems();
        Labels("objects").Should().BeEquivalentTo(labels);
    }

    [Fact]
    public void Every_rule_of_the_compiler_and_the_readers_is_a_rule_row()
    {
        var compiler = CompilationRules();
        var readers = StoredDocumentRules.All;
        var contracted = RuleIds();

        contracted.Should().OnlyHaveUniqueItems();
        contracted.Should().Contain(compiler, "each is a CompilationRules id the contract holds no row for");
        contracted.Should().Contain(readers, "each is a reader rule id the contract holds no row for");
        contracted.Except(compiler).Except(readers).Should().Equal("definition-undrawable");
    }

    [Fact]
    public void The_workflow_row_holds_every_field_a_create_and_an_update_write()
    {
        Labels("workflowRow").Should().BeEquivalentTo([.. WorkflowRowFields.Select(name => $"workflow row.{name}")]);
    }

    [Fact]
    public void No_reader_source_names_a_rule_id_literally()
    {
        var faults = Directory.EnumerateFiles(RepositoryFiles.ReaderSourceDirectory(), "*.cs")
            .Where(path => Path.GetFileName(path) != "StoredDocumentRules.cs")
            .Where(path => Regex.IsMatch(File.ReadAllText(path), "\"L[1-9]\""))
            .Select(Path.GetFileName);

        faults.Should().BeEmpty("every fault site names a StoredDocumentRules const");
    }

    private static List<string> ObjectLabels()
    {
        var labels = new List<string>();
        var seen = new HashSet<StoredKind>(ReferenceEqualityComparer.Instance);

        void Visit(StoredKind kind)
        {
            switch (kind)
            {
                case ObjectKind obj when seen.Add(obj):
                    labels.Add(obj.Name);
                    foreach (var field in obj.Fields)
                        Visit(field.Kind);
                    break;
                case ArrayKind array: Visit(array.Element); break;
                case MapKind map: Visit(map.Value); break;
            }
        }

        Visit(StoredDocumentSchema.Definition);
        Visit(StoredDocumentSchema.ParameterList);
        Visit(StoredDocumentSchema.PortList);
        return labels;
    }

    private static List<string> CompilationRules() =>
        [.. typeof(CompilationRules).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)];

    private static JsonElement Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
