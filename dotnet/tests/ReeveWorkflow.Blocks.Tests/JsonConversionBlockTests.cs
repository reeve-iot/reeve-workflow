using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Compilation;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class JsonConversionBlockTests
{
    private static WorkflowBlockContext Context(string term)
    {
        var type = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!;
        return new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Logger = NullLogger.Instance,
            Services = new ServiceProviderStub(),
            Types = new Dictionary<string, ClosedPortType> { ["Value"] = type },
        };
    }

    private static async Task<IReadOnlyDictionary<string, object?>> Run(IWorkflowBlockPlugin block, WorkflowBlockContext context, object? input, string port) =>
        await block.ExecuteAsync(new Dictionary<string, object?> { [port] = input }, context, CancellationToken.None);

    [Fact]
    public void JsonToText_takes_a_JsonSerializable_Value_of_type_T_and_gives_String_Text()
    {
        var factory = new JsonToTextBlockFactory();

        factory.Category.Should().Be("Conversion");
        factory.TypeParameters.Should().ContainSingle().Which.Should().BeEquivalentTo(new WorkflowTypeParameter("T", ["JsonSerializable"]));
        factory.Inputs.Should().ContainSingle().Which.Should().Match<BlockPortDefinition>(p => p.Name == "Value" && p.IsRequired);
        factory.Inputs.Single().Type.Should().Be(PortValueType.OfDeclaration("$T"));
        factory.Outputs.Should().ContainSingle().Which.Should().Match<BlockPortDefinition>(p => p.Name == "Text" && p.Type == PortValueType.String);
    }

    [Fact]
    public void TextToJson_takes_String_Text_and_gives_a_nullable_Json_Value()
    {
        var factory = new TextToJsonBlockFactory();
        IWorkflowBlockFactory declared = factory;

        factory.Category.Should().Be("Conversion");
        declared.TypeParameters.Should().BeEmpty();
        factory.Inputs.Should().ContainSingle().Which.Should().Match<BlockPortDefinition>(p => p.Name == "Text" && p.Type == PortValueType.String && p.IsRequired);
        factory.Outputs.Should().ContainSingle().Which.Should().Match<BlockPortDefinition>(p => p.Name == "Value");
        factory.Outputs.Single().Type.Should().Be(PortValueType.OfDeclaration("Json?"));
    }

    [Theory]
    [InlineData("Int", 7L, "7")]
    [InlineData("String", "abc", "\"abc\"")]
    [InlineData("Boolean", true, "true")]
    [InlineData("Number", 2.5, "2.5")]
    [InlineData("String", "zażółć \"q\" <a>&'+", "\"zażółć \\\"q\\\" <a>&'+\"")]
    public async Task JsonToText_writes_a_scalar_as_its_JSON_text(string term, object value, string text)
    {
        var result = await Run(new JsonToTextBlock(), Context(term), value, "Value");

        result["Text"].Should().Be(text);
    }

    [Fact]
    public async Task JsonToText_writes_a_collection_element_by_element()
    {
        var result = await Run(new JsonToTextBlock(), Context("Collection<Int>"), new List<long> { 1, 2 }, "Value");

        result["Text"].Should().Be("[1,2]");
    }

    [Fact]
    public async Task JsonToText_fails_for_a_missing_or_null_Value_and_for_a_node_that_was_not_compiled()
    {
        var empty = new WorkflowBlockContext { ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance, Services = new ServiceProviderStub() };

        await FluentActions.Invoking(() => Run(new JsonToTextBlock(), Context("Int"), null, "Value")).Should().ThrowAsync<ArgumentException>().WithMessage("*'Value'*");
        await FluentActions.Invoking(() => Run(new JsonToTextBlock(), Context("Int"), 1L, "Other")).Should().ThrowAsync<ArgumentException>().WithMessage("*'Value'*");
        await FluentActions.Invoking(() => Run(new JsonToTextBlock(), empty, 1L, "Value")).Should().ThrowAsync<InvalidOperationException>().WithMessage("*not compiled*");
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData(" [1, 2] ", "[1, 2]")]
    [InlineData("7", "7")]
    public async Task TextToJson_gives_the_Json_value_the_text_holds(string text, string raw)
    {
        var result = await Run(new TextToJsonBlock(), Context("Int"), text, "Text");

        result["Value"].Should().BeOfType<JsonElement>().Which.GetRawText().Should().Be(raw);
    }

    [Fact]
    public async Task TextToJson_gives_a_null_Value_for_the_text_null()
    {
        var result = await Run(new TextToJsonBlock(), Context("Int"), "null", "Text");

        result.Should().ContainKey("Value").WhoseValue.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    public async Task TextToJson_fails_the_node_on_text_that_is_not_JSON(string text)
    {
        await FluentActions.Invoking(() => Run(new TextToJsonBlock(), Context("Int"), text, "Text")).Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task TextToJson_fails_the_node_naming_the_member_when_the_text_repeats_one()
    {
        var thrown = await FluentActions.Invoking(() => Run(new TextToJsonBlock(), Context("Int"), """{"a":{"b":1,"b":2}}""", "Text"))
            .Should().ThrowAsync<JsonException>();

        thrown.Which.Message.Should().Contain("'b'").And.Contain("$.a");
    }

    [Fact]
    public async Task TextToJson_fails_for_a_missing_Text_or_one_that_is_not_text()
    {
        await FluentActions.Invoking(() => Run(new TextToJsonBlock(), Context("Int"), null, "Text")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => Run(new TextToJsonBlock(), Context("Int"), 5L, "Text")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => Run(new TextToJsonBlock(), Context("Int"), "{}", "Other")).Should().ThrowAsync<ArgumentException>();
    }

    private sealed class ServiceProviderStub : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
