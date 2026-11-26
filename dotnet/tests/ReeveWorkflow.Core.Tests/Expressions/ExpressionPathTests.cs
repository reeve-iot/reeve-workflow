using FluentAssertions;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionPathTests
{
    private static readonly ExpressionKeyShapes Shapes = ExpressionKeyShapes.Library.With([("trigger", 1), ("device", 2)]);

    private static ExpressionPathParts Split(string path)
    {
        ExpressionPath.TrySplit(path, Shapes, out var parts, out var error).Should().BeTrue(error);
        return parts;
    }

    private static string Refused(string path)
    {
        ExpressionPath.TrySplit(path, Shapes, out _, out var error).Should().BeFalse();
        return error;
    }

    private static string Fields(ExpressionPathParts parts) => string.Concat(parts.Fields.Select(f => f.ToString()));

    [Fact]
    public void Previous_key_is_the_node_and_the_output_and_the_rest_is_a_field_path()
    {
        var parts = Split("previous.w.TriggerData.auth");

        parts.Namespace.Should().Be("previous");
        parts.Key.Should().Equal("w", "TriggerData");
        Fields(parts).Should().Be(".auth");
    }

    [Fact]
    public void An_index_after_the_key_is_the_first_field_step_and_steps_chain()
    {
        var parts = Split("previous.w.Items[0].name[12][3]");

        parts.Key.Should().Equal("w", "Items");
        Fields(parts).Should().Be("[0].name[12][3]");
        parts.Fields[0].IsIndex.Should().BeTrue();
        parts.Fields[0].Index.Should().Be(0);
    }

    [Fact]
    public void Parameter_key_is_one_segment()
    {
        var parts = Split("parameter.limits.max");

        parts.Key.Should().Equal("limits");
        Fields(parts).Should().Be(".max");
    }

    [Fact]
    public void Now_key_is_empty_or_one_field()
    {
        Split("now").Key.Should().BeEmpty();
        Split("now.year").Key.Should().Equal("year");
        Split("now.year.x").Key.Should().Equal("year");
        Fields(Split("now.year.x")).Should().Be(".x");
    }

    [Fact]
    public void Helper_key_is_the_whole_path_and_nothing_is_accessed_past_it()
    {
        var parts = Split("helper.KitchenLight.brightness");

        parts.Key.Should().Equal("KitchenLight", "brightness");
        parts.Fields.Should().BeEmpty();
        Refused("helper.KitchenLight[0]").Should().Contain("no index");
    }

    [Theory]
    [InlineData("trigger.payload.auth", 1, ".auth")]
    [InlineData("device.kitchen.temp.unit", 2, ".unit")]
    public void A_host_namespace_takes_the_key_length_its_registration_declares(string path, int key, string fields)
    {
        var parts = Split(path);

        parts.Key.Should().HaveCount(key);
        Fields(parts).Should().Be(fields);
        parts.NamespaceKnown.Should().BeTrue();
    }

    [Theory]
    [InlineData("previous.w", "'previous' needs a key of 2 segments")]
    [InlineData("previous", "'previous' needs a key of 2 segments")]
    [InlineData("parameter", "'parameter' needs a key")]
    [InlineData("device.kitchen", "'device' needs a key of 2 segments")]
    [InlineData("trigger", "'trigger' needs a key")]
    public void A_path_shorter_than_its_key_is_refused_naming_the_namespace(string path, string why) =>
        Refused(path).Should().Contain(why);

    [Theory]
    [InlineData("previous.w[0].out", "inside the key")]
    [InlineData("now[0].year", "between 'now' and its key")]
    [InlineData("previous.w.out[]", "decimal digits")]
    [InlineData("previous.w.out[x]", "decimal digits")]
    [InlineData("previous.w.out[1", "decimal digits")]
    [InlineData("previous.w.out[99999999999]", "larger than 2147483647")]
    [InlineData("previous.w.out[0]x", "must follow a name")]
    [InlineData("previous.w.out]", "closes an index")]
    [InlineData("trigger.a]b", "closes an index")]
    [InlineData("a]", "closes an index")]
    [InlineData("previous.w.out.a]", "closes an index")]
    [InlineData("previous.w.out[0]]", "closes an index")]
    [InlineData("previous.w.out[0].]", "closes an index")]
    [InlineData("previous.w.out[1[2]", "decimal digits")]
    public void An_index_out_of_place_or_a_malformed_step_is_refused(string path, string why) =>
        Refused(path).Should().Contain(why);

    [Theory]
    [InlineData("${previous.w.out]}")]
    [InlineData("${trigger.a]b}")]
    [InlineData("${a]}")]
    [InlineData("${previous.w.out.a]}")]
    [InlineData("${previous.w.Items[0]]}")]
    [InlineData("${previous.w.Items]0[}")]
    [InlineData("${previous.w.out[0]x}")]
    public void A_close_bracket_without_an_index_is_a_syntax_error(string source) =>
        FluentActions.Invoking(() => new ExpressionLexer(source).Tokenize())
            .Should().Throw<ExpressionSyntaxException>().WithMessage("*malformed variable reference*");

    [Theory]
    [InlineData("${previous.w.Items[0].name}")]
    [InlineData("${previous.w.Grid[0][1]}")]
    [InlineData("${a[0]}")]
    public void A_close_bracket_that_closes_an_index_lexes(string source) =>
        FluentActions.Invoking(() => new ExpressionLexer(source).Tokenize()).Should().NotThrow();

    [Theory]
    [InlineData("now.")]
    [InlineData("now..x")]
    [InlineData("previous..out")]
    [InlineData("previous.w..x")]
    [InlineData("previous.w.")]
    [InlineData("previous.w.out.")]
    [InlineData("previous.w.out.a.")]
    [InlineData("previous.w.out[0].")]
    [InlineData("device..x.y")]
    [InlineData("trigger.")]
    [InlineData(".x")]
    [InlineData("helper.a..b")]
    public void An_empty_path_segment_is_refused_by_the_grammar(string path) =>
        Refused(path).Should().Be("a path segment is empty");

    [Theory]
    [InlineData("${now.}")]
    [InlineData("${previous.w.}")]
    [InlineData("${trigger.}")]
    [InlineData("${now..x}")]
    [InlineData("${.}")]
    public void An_empty_path_segment_is_a_syntax_error(string source) =>
        FluentActions.Invoking(() => new ExpressionLexer(source).Tokenize())
            .Should().Throw<ExpressionSyntaxException>().WithMessage("*malformed variable reference*a path segment is empty*");

    [Theory]
    [InlineData("previous.w.out.a.b", "previous:w:out")]
    [InlineData("previous.w.out[0].a", "previous:w:out")]
    [InlineData("device.kitchen.temp.unit", "device:kitchen:temp")]
    [InlineData("weather.paris.temp", "weather:paris:temp")]
    [InlineData("previous.w", "previous:w")]
    public void The_dependency_key_is_the_namespace_and_its_key_whatever_follows(string path, string key) =>
        ExpressionPath.DependencyKeyOf(path, Shapes).Should().Be(key);

    [Fact]
    public void A_name_with_spaces_is_one_segment()
    {
        var parts = Split("device.Living Room.power.unit");

        parts.Key.Should().Equal("Living Room", "power");
        Fields(parts).Should().Be(".unit");
    }

    [Fact]
    public void An_unregistered_namespace_keeps_every_dotted_segment_as_its_key()
    {
        var parts = Split("weather.paris.temp");

        parts.NamespaceKnown.Should().BeFalse();
        parts.Key.Should().Equal("paris", "temp");
        parts.Fields.Should().BeEmpty();
    }

    [Theory]
    [InlineData("previous.w.out", "previous")]
    [InlineData("now[0]", "now")]
    [InlineData("now", "now")]
    public void The_namespace_is_the_text_before_the_first_dot_or_bracket(string path, string expected) =>
        ExpressionPath.NamespaceOf(path).Should().Be(expected);
}
