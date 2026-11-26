using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Tests;

public class PortValueTypeTests
{
    [Fact]
    public void A_name_the_library_never_seeded_is_a_type_like_any_other()
    {
        var shipment = PortValueType.Of("Shipment");

        shipment.Name.Should().Be("Shipment");
        shipment.Should().Be(PortValueType.Of("Shipment"));
        shipment.Should().NotBe(PortValueType.String);
    }

    [Fact]
    public void A_port_type_with_no_name_is_a_bug_in_the_host_not_a_graph_the_engine_carries()
    {
        var of = () => PortValueType.Of("   ");

        of.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.IllegalCharacter);
    }

    [Fact]
    public void A_default_port_value_type_names_no_type()
    {
        PortValueType undeclared = default;

        undeclared.IsDefault.Should().BeTrue();
        var read = () => undeclared.Name;
        read.Should().Throw<InvalidOperationException>();
        undeclared.Should().Be(default(PortValueType));
        undeclared.Should().NotBe(PortValueType.String);
        undeclared.ToString().Should().Be("default(PortValueType)");
    }

    [Fact]
    public void Casing_does_not_split_one_type_into_two()
    {
        PortValueType.Of("shipment").Should().Be(PortValueType.Of("Shipment"));
        PortValueType.Of("shipment").GetHashCode().Should().Be(PortValueType.Of("Shipment").GetHashCode());
    }

    [Fact]
    public void A_collection_is_spelled_in_the_one_notation()
    {
        PortValueType.CollectionOf(PortValueType.String).Name.Should().Be("Collection<String>");
        PortValueType.CollectionOf(PortValueType.Of("Shipment")).Name.Should().Be("Collection<Shipment>");
    }

    [Fact]
    public void CollectionOf_never_collapses()
    {
        PortValueType.CollectionOf(PortValueType.String).Name.Should().Be("Collection<String>");
        PortValueType.CollectionOf(PortValueType.CollectionOf(PortValueType.String)).Name.Should().Be("Collection<Collection<String>>");
    }

    [Fact]
    public void A_concatenated_spelling_is_a_syntax_error_not_a_second_notation()
    {
        var of = () => PortValueType.Of("CollectionShipment");

        of.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.ReservedCollectionPrefix);
    }

    [Fact]
    public void A_collection_carries_its_element_and_anything_else_carries_itself()
    {
        PortValueType.CollectionOf(PortValueType.Of("Shipment")).ElementType
            .Should().Be(PortValueType.Of("Shipment"));
        PortValueType.Of("Shipment").ElementType.Should().Be(PortValueType.Of("Shipment"));
        PortValueType.CollectionOf(PortValueType.Int).ElementType.Should().Be(PortValueType.Int);
    }

    [Fact]
    public void A_collection_of_default_is_refused_as_an_argument_not_read_through()
    {
        var build = () => PortValueType.CollectionOf(default);

        build.Should().Throw<ArgumentException>().WithParameterName("element");
    }

    [Fact]
    public void Every_seeded_type_survives_the_trip_through_its_collection()
    {
        foreach (var type in PortValueType.Builtins)
            PortValueType.CollectionOf(type).ElementType.Should().Be(type, because: $"{type} is one type, not two");
    }

    [Fact]
    public void The_library_seeds_exactly_what_the_editor_seeds()
    {
        var declared = Regex.Matches(File.ReadAllText(BuiltinsPath()), @"\{\s*name:\s*'(?<name>[^']+)'")
            .Select(m => m.Groups["name"].Value)
            .ToArray();

        PortValueType.Builtins.Select(t => t.Name).Should().Equal(declared);
    }

    [Fact]
    public void A_port_type_survives_a_round_trip_as_its_name()
    {
        foreach (var type in new[]
                 {
                     PortValueType.String, PortValueType.Int, PortValueType.CollectionOf(PortValueType.Int),
                     PortValueType.Of("Shipment"), PortValueType.CollectionOf(PortValueType.Of("Shipment"))
                 })
        {
            var json = JsonSerializer.Serialize(type, UnescapedJsonOptions);
            json.Should().Be($"\"{type.Name}\"");
            JsonSerializer.Deserialize<PortValueType>(json).Should().Be(type);
        }
    }

    private static readonly JsonSerializerOptions UnescapedJsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void A_port_a_host_stored_before_this_release_still_reads()
    {
        var port = JsonSerializer.Deserialize<BlockPortDefinition>(
            """{"name":"DeviceId","displayName":"Device","type":"Device","isRequired":true}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        port!.Type.Should().Be(PortValueType.Of("Device"));
    }

    [Fact]
    public void An_absent_or_blank_port_type_is_refused_never_read_as_Any()
    {
        var absent = () => JsonSerializer.Deserialize<PortValueType>("null");
        var blank = () => JsonSerializer.Deserialize<PortValueType>("\"\"");

        absent.Should().Throw<JsonException>().WithMessage("*found null*");
        blank.Should().Throw<JsonException>().WithMessage("*is blank*");
    }

    [Fact]
    public void A_malformed_name_is_refused_by_the_converter_with_the_grammars_own_message()
    {
        var value = () => JsonSerializer.Deserialize<PortValueType>("\"CollectionInt\"");
        var key = () => JsonSerializer.Deserialize<Dictionary<PortValueType, int>>("""{"Collection<Int":1}""");
        var blankKey = () => JsonSerializer.Deserialize<Dictionary<PortValueType, int>>("""{"":1}""");

        value.Should().Throw<JsonException>().WithMessage("*write Collection<T> with the type of its elements*");
        key.Should().Throw<JsonException>().WithMessage("*at 14*");
        blankKey.Should().Throw<JsonException>().WithMessage("*is blank*");
    }

    [Fact]
    public void A_port_definition_refuses_a_default_type_however_it_is_given_one()
    {
        var constructed = () => new BlockPortDefinition("p", "p", default, false);
        var copied = () => new BlockPortDefinition("p", "p", PortValueType.Int, false) with { Type = default };
        var read = () => JsonSerializer.Deserialize<BlockPortDefinition>(
            """{"name":"p","displayName":"p","isRequired":false}""", new JsonSerializerOptions(JsonSerializerDefaults.Web));

        constructed.Should().Throw<ArgumentException>().WithParameterName("Type");
        copied.Should().Throw<ArgumentException>().WithParameterName("Type");
        read.Should().Throw<ArgumentException>().WithMessage("*cannot be default(PortValueType)*");
    }

    [Fact]
    public void A_number_is_refused_rather_than_guessed_back_into_a_name()
    {
        var read = () => JsonSerializer.Deserialize<PortValueType>("6");

        read.Should().Throw<JsonException>().WithMessage("*port type is written as its name*");
    }

    [Fact]
    public void The_object_a_4_0_0_host_could_read_is_refused_rather_than_read_as_Any()
    {
        var read = () => JsonSerializer.Deserialize<PortValueType>("""{"Name":"Device"}""");

        read.Should().Throw<JsonException>().WithMessage("*port type is written as its name*");
    }

    [Fact]
    public void A_descriptor_this_release_writes_is_one_the_release_before_the_struct_could_read()
    {
        var json = JsonSerializer.Serialize(
            new BlockPortDefinition("DeviceId", "Device", PortValueType.Of("Device"), true),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"type\":\"Device\"");
    }

    [Fact]
    public void A_map_keyed_by_port_type_reads_its_keys_back()
    {
        var byType = new Dictionary<PortValueType, int>
        {
            [PortValueType.Of("Shipment")] = 1,
            [PortValueType.CollectionOf(PortValueType.Of("Shipment"))] = 2
        };

        var json = JsonSerializer.Serialize(byType, UnescapedJsonOptions);

        json.Should().Be("""{"Shipment":1,"Collection<Shipment>":2}""");
        JsonSerializer.Deserialize<Dictionary<PortValueType, int>>(json).Should().Equal(byType);
    }

    [Fact]
    public void A_port_type_that_may_be_absent_round_trips_as_null()
    {
        JsonSerializer.Serialize<PortValueType?>(null).Should().Be("null");
        JsonSerializer.Serialize<PortValueType?>(PortValueType.Of("Shipment")).Should().Be("\"Shipment\"");
        JsonSerializer.Deserialize<PortValueType?>("null").Should().BeNull();
        JsonSerializer.Deserialize<PortValueType?>("\"Shipment\"").Should().Be(PortValueType.Of("Shipment"));
    }

    [Fact]
    public void A_leading_angle_bracket_is_refused_at_Of_before_any_reader_such_as_the_widening_table_ever_sees_it()
    {
        var of = () => PortValueType.Of("<Foo>");

        of.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.ExpectedTypeName);
    }

    private static string BuiltinsPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..",
            "packages", "core", "src", "portTypes", "builtins.ts"));

    [Fact]
    public void Of_unwraps_a_10000_deep_bracket_nesting_without_overflowing_a_256KB_stack()
    {
        var deepBracketed = string.Concat(Enumerable.Repeat("Collection<", 10_000)) + "Point" + string.Concat(Enumerable.Repeat(">", 10_000));
        PortValueType? actual = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { actual = PortValueType.Of(deepBracketed); }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        failure.Should().BeNull();
        actual!.Value.Name.Should().Be(deepBracketed);
        actual.Value.IsCollection.Should().BeTrue();
    }
}
