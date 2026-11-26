using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.JsonPath;

namespace ReeveWorkflow.Core.Tests.JsonPath;

public sealed class JsonPathBudgetTests
{
    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Repeated(string selector, int times, int segments) =>
        "$" + string.Concat(Enumerable.Repeat("[" + string.Join(",", Enumerable.Repeat(selector, times)) + "]", segments));

    private static JsonElement Ints(int count) => Element("[" + string.Join(",", Enumerable.Repeat("1", count)) + "]");

    [Theory]
    [InlineData("*")]
    [InlineData("0:")]
    [InlineData("::1")]
    [InlineData("0")]
    public void A_union_that_multiplies_its_nodes_fails_at_the_step_budget(string selector)
    {
        var query = JsonPathQuery.Parse(Repeated(selector, 60, 5));

        FluentActions.Invoking(() => query.Evaluate(Element("[[[[[1]]]]]")))
            .Should().Throw<JsonPathBudgetException>()
            .Which.Reason.Should().Be($"takes more than {JsonPathBudget.MaxSteps} steps");
    }

    [Fact]
    public void A_query_whose_steps_reach_the_budget_selects_every_node_and_one_step_more_fails()
    {
        var query = JsonPathQuery.Parse("$[*]");

        query.Evaluate(Ints(JsonPathBudget.MaxSteps - 1)).Should().HaveCount(JsonPathBudget.MaxSteps - 1);
        FluentActions.Invoking(() => query.Evaluate(Ints(JsonPathBudget.MaxSteps)))
            .Should().Throw<JsonPathBudgetException>()
            .Which.Reason.Should().Be($"takes more than {JsonPathBudget.MaxSteps} steps");
    }

    [Fact]
    public void A_slice_pays_for_the_values_it_passes_over_as_well_as_those_it_selects()
    {
        var query = JsonPathQuery.Parse(Repeated("999:", 101, 1));

        FluentActions.Invoking(() => query.Evaluate(Ints(1000)))
            .Should().Throw<JsonPathBudgetException>()
            .Which.Reason.Should().Be($"takes more than {JsonPathBudget.MaxSteps} steps");
        JsonPathQuery.Parse(Repeated("999:", 99, 1)).Evaluate(Ints(1000)).Should().HaveCount(99);
    }

    [Fact]
    public void A_selector_that_finds_nothing_still_takes_a_step()
    {
        var objects = Element("[" + string.Join(",", Enumerable.Repeat("{}", 600)) + "]");
        var names = Repeated("'missing'", 200, 1)[1..];

        JsonPathQuery.Parse("$[0:400]" + names).Evaluate(objects).Should().BeEmpty();
        FluentActions.Invoking(() => JsonPathQuery.Parse("$[*]" + names).Evaluate(objects))
            .Should().Throw<JsonPathBudgetException>()
            .Which.Reason.Should().Be($"takes more than {JsonPathBudget.MaxSteps} steps");
    }

    [Fact]
    public void The_nodes_a_query_selects_hold_at_most_the_byte_budget_of_JSON()
    {
        var text = new string('x', 1024 * 1024);
        var source = Element("[\"" + text + "\"]");
        var copies = (int)(JsonPathBudget.MaxSelectedBytes / (text.Length + 2));

        JsonPathQuery.Parse(Repeated("0", copies, 1)).Evaluate(source).Should().HaveCount(copies);
        FluentActions.Invoking(() => JsonPathQuery.Parse(Repeated("0", copies + 1, 1)).Evaluate(source))
            .Should().Throw<JsonPathBudgetException>()
            .Which.Reason.Should().Be($"selects more than {JsonPathBudget.MaxSelectedBytes} bytes of JSON");
    }

    [Fact]
    public void The_whole_source_selected_by_the_root_counts_against_the_byte_budget()
    {
        var source = Element("\"" + new string('x', (int)JsonPathBudget.MaxSelectedBytes) + "\"");

        FluentActions.Invoking(() => JsonPathQuery.Parse("$").Evaluate(source))
            .Should().Throw<JsonPathBudgetException>()
            .Which.Reason.Should().Be($"selects more than {JsonPathBudget.MaxSelectedBytes} bytes of JSON");
    }

    [Fact]
    public void The_budget_holds_for_a_query_that_reports_paths()
    {
        var query = JsonPathQuery.Parse(Repeated("*", 60, 5));

        FluentActions.Invoking(() => query.EvaluateWithPaths(Element("[[[[[1]]]]]")))
            .Should().Throw<JsonPathBudgetException>();
    }
}
