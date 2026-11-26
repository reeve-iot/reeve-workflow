using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ValueGateTests
{
    private static ClosedPortType Closed(string term) =>
        WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!;

    private static string? Admit(string term, object? value, out object? admitted) => ValueGate.Admit(Closed(term), value, out admitted);

    [Theory]
    [InlineData("Int")]
    [InlineData("String")]
    [InlineData("Json")]
    [InlineData("Collection<Int>")]
    public void Null_is_refused_on_a_term_that_is_not_nullable(string term)
    {
        Admit(term, null, out _).Should().Contain("null is not a value of").And.Contain($"'{term}'");
    }

    [Theory]
    [InlineData("Int?")]
    [InlineData("String?")]
    [InlineData("Json?")]
    [InlineData("Collection<Int>?")]
    public void Null_is_admitted_on_a_nullable_term_and_stays_null(string term)
    {
        Admit(term, null, out var admitted).Should().BeNull();
        admitted.Should().BeNull();
    }

    [Theory]
    [InlineData("Int", 7)]
    [InlineData("Int?", 7)]
    [InlineData("Int", (short)7)]
    [InlineData("Int", (byte)7)]
    [InlineData("Int", (sbyte)7)]
    [InlineData("Int", (ushort)7)]
    [InlineData("Int", 7u)]
    [InlineData("Int", 7L)]
    public void A_small_integer_on_Int_becomes_a_long(string term, object value)
    {
        Admit(term, value, out var admitted).Should().BeNull();
        admitted.Should().BeOfType<long>().Which.Should().Be(7L);
    }

    [Theory]
    [InlineData("Number", 1.5f)]
    [InlineData("Number?", 1.5f)]
    [InlineData("Number", 1.5)]
    public void A_float_on_Number_becomes_a_double(string term, object value)
    {
        Admit(term, value, out var admitted).Should().BeNull();
        admitted.Should().BeOfType<double>().Which.Should().Be(1.5);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(3L)]
    public void An_integer_on_Number_is_not_converted(object value)
    {
        Admit("Number", value, out _).Should().Contain("is not a value of 'Number'");
    }

    [Fact]
    public void A_decimal_on_Number_is_not_converted()
    {
        Admit("Number", 1.5m, out _).Should().Contain("Decimal is not a value of 'Number'");
    }

    [Fact]
    public void A_ulong_on_Int_is_not_converted()
    {
        Admit("Int", 7UL, out _).Should().Contain("UInt64 is not a value of 'Int'");
    }

    [Fact]
    public void A_double_on_Int_and_a_long_on_Boolean_are_refused()
    {
        Admit("Int", 7.0, out _).Should().Contain("Double is not a value of 'Int'");
        Admit("Boolean", 1L, out _).Should().Contain("Int64 is not a value of 'Boolean'");
    }

    [Fact]
    public void A_text_on_Int_is_refused_and_a_text_on_String_is_admitted()
    {
        Admit("Int", "7", out _).Should().Contain("String is not a value of 'Int'");
        Admit("String", "7", out var admitted).Should().BeNull();
        admitted.Should().Be("7");
    }

    [Fact]
    public void A_list_of_objects_on_a_collection_of_Int_is_refused_and_a_list_of_longs_is_admitted()
    {
        Admit("Collection<Int>", new List<object?> { 1L }, out _).Should().Contain("is not a value of 'Collection<Int>'");
        Admit("Collection<Int>", new List<long> { 1L }, out var admitted).Should().BeNull();
        admitted.Should().BeOfType<List<long>>();
        Admit("Collection<Int>", new long[] { 1L }, out _).Should().BeNull();
    }

    [Fact]
    public void A_list_of_longs_on_a_collection_of_nullable_Int_is_refused_because_the_CLR_does_not_covary_it()
    {
        Admit("Collection<Int?>", new List<long> { 1L }, out _).Should().NotBeNull();
        Admit("Collection<Int?>", new List<long?> { 1L, null }, out _).Should().BeNull();
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("Json?")]
    public void A_JSON_null_element_is_refused_on_Json_and_on_Json_nullable(string term)
    {
        using var document = JsonDocument.Parse("null");
        Admit(term, document.RootElement.Clone(), out _).Should().Contain("JSON null").And.Contain($"'{term}'");
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("Json?")]
    public void A_default_JsonElement_is_refused_on_Json_and_on_Json_nullable(string term)
    {
        Admit(term, default(JsonElement), out _).Should().Contain("JSON undefined");
    }

    [Theory]
    [InlineData("""{"a":1}""")]
    [InlineData("[1,2]")]
    [InlineData("3")]
    [InlineData("\"text\"")]
    [InlineData("false")]
    public void Any_other_JsonElement_is_admitted_on_Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        Admit("Json", document.RootElement.Clone(), out var admitted).Should().BeNull();
        admitted.Should().BeOfType<JsonElement>();
    }

    [Fact]
    public void A_text_on_Json_is_refused_because_the_gate_serialises_nothing()
    {
        Admit("Json", "{}", out _).Should().Contain("String is not a value of 'Json'");
    }

    [Fact]
    public void A_JSON_null_element_on_a_non_Json_term_is_refused_by_its_CLR_type()
    {
        using var document = JsonDocument.Parse("null");
        Admit("String", document.RootElement.Clone(), out _).Should().Contain("is not a value of 'String'");
    }
}
