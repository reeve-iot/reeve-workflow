using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.JsonPath;

namespace ReeveWorkflow.Core.Tests.JsonPath;

public sealed class JsonPathComplianceTests
{
    private static readonly Lazy<Dictionary<string, JsonElement>> Suite = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "JsonPath", "cts.json")));
        var tests = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var test in document.RootElement.GetProperty("tests").EnumerateArray())
            tests.Add(test.GetProperty("name").GetString()!, test.Clone());
        return tests;
    });

    public static TheoryData<string> Names() => new(Suite.Value.Keys);

    [Fact]
    public void The_suite_holds_every_test_of_the_pinned_commit() => Suite.Value.Should().HaveCount(706);

    [Theory]
    [MemberData(nameof(Names))]
    public void Each_compliance_test_passes(string name)
    {
        var test = Suite.Value[name];
        var selector = test.GetProperty("selector").GetString()!;
        var hasForbiddenMarker = HasForbiddenMarker(selector);
        var accepted = JsonPathQuery.TryParse(selector, out var query, out var error);
        var invalidSelector = test.TryGetProperty("invalid_selector", out var invalid) && invalid.GetBoolean();
        if (!invalidSelector)
            (error?.IsProjectionRestriction == true).Should().Be(hasForbiddenMarker, $"'{selector}' marker determines whether the projection restriction applies");
        if (error?.IsProjectionRestriction == true)
        {
            error.Message.Should().Be($"the path '{selector}' uses {error.Unsupported}; a projection path takes only child segments with name, index, wildcard and slice selectors");
            return;
        }
        if (invalidSelector)
        {
            accepted.Should().BeFalse($"'{selector}' is not an RFC 9535 query");
            return;
        }
        accepted.Should().BeTrue(error?.Message);
        var document = test.GetProperty("document");
        var found = query!.EvaluateWithPaths(document);
        var plain = query.Evaluate(document);
        var expected = test.TryGetProperty("result", out var result)
            ? [(result, test.GetProperty("result_paths"))]
            : test.GetProperty("results").EnumerateArray().Zip(test.GetProperty("results_paths").EnumerateArray()).ToList();

        expected.Should().Contain(candidate => Matches(candidate.Item1, candidate.Item2, found, plain),
            $"'{selector}' found {string.Join(", ", found.Select(f => f.Path + "=" + f.Value.GetRawText()))}");
    }

    private static bool HasForbiddenMarker(string selector)
    {
        char quote = '\0';
        var escaped = false;
        for (var i = 0; i < selector.Length; i++)
        {
            var current = selector[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (current == '\\') escaped = true;
                else if (current == quote) quote = '\0';
                continue;
            }
            if (current is '\'' or '"') { quote = current; continue; }
            if (i + 1 < selector.Length && current == '.' && selector[i + 1] == '.') return true;
            if (current == '?') return true;
        }
        return false;
    }

    private static bool Matches(
        JsonElement values, JsonElement paths, IReadOnlyList<(JsonElement Value, string Path)> found, IReadOnlyList<JsonElement> plain)
    {
        var expectedValues = values.EnumerateArray().ToList();
        var expectedPaths = paths.EnumerateArray().Select(p => p.GetString()).ToList();
        if (expectedValues.Count != found.Count || plain.Count != found.Count)
            return false;
        for (var i = 0; i < found.Count; i++)
        {
            if (!JsonElement.DeepEquals(expectedValues[i], found[i].Value) || !JsonElement.DeepEquals(expectedValues[i], plain[i])
                || expectedPaths[i] != found[i].Path)
                return false;
        }

        return true;
    }
}
