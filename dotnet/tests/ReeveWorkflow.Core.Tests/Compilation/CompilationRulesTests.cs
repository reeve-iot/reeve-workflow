using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationRulesTests
{
    [Fact]
    public void Every_rule_the_corpus_grades_has_a_matching_constant()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.ValidationCorpusPath()));
        var corpusRuleIds = doc.RootElement.GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("expect").TryGetProperty("rules", out var rules) ? rules.EnumerateArray() : [])
            .Select(r => r.GetString()!).Distinct().ToList();

        corpusRuleIds.Should().NotBeEmpty("a corpus expecting no rule would make this test vacuously pass");
        corpusRuleIds.Should().BeSubsetOf(RuleValues());
    }

    [Fact]
    public void Every_rule_id_is_kebab_case_and_none_repeats_another()
    {
        var values = RuleValues().ToList();

        values.Should().OnlyContain(id => Regex.IsMatch(id, "^[a-z]+(-[a-z]+)*$"));
        values.Should().OnlyHaveUniqueItems();
    }

    private static IEnumerable<string> RuleValues() =>
        typeof(CompilationRules)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
}
