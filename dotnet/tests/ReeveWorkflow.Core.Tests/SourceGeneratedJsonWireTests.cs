using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Tests;

public class SourceGeneratedJsonWireTests
{
    private static readonly JsonElement Document =
        JsonDocument.Parse("""{"b":1,"a":[true,null,"é<>"],"c":{"d":1.25}}""").RootElement.Clone();

    public static TheoryData<Type, object?> LibraryAtomValues() => new()
    {
        { typeof(long), 0L }, { typeof(long), long.MinValue }, { typeof(long), long.MaxValue },
        { typeof(Nullable<long>), null }, { typeof(Nullable<long>), 42L },
        { typeof(double), 0.1 }, { typeof(double), 1e300 }, { typeof(double), -0.0 }, { typeof(double), 3.0 },
        { typeof(Nullable<double>), null }, { typeof(Nullable<double>), 2.5 },
        { typeof(string), "" }, { typeof(string), "quote \" <tag> & é \u2028" },
        { typeof(bool), true }, { typeof(Nullable<bool>), null }, { typeof(Nullable<bool>), false },
        { typeof(DateTimeOffset), new DateTimeOffset(2026, 11, 9, 10, 15, 30, 123, TimeSpan.FromHours(2)) },
        { typeof(DateTimeOffset), DateTimeOffset.MinValue },
        { typeof(Nullable<DateTimeOffset>), null },
        { typeof(Nullable<DateTimeOffset>), new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero) },
        { typeof(JsonElement), Document },
        { typeof(Nullable<JsonElement>), null }, { typeof(Nullable<JsonElement>), Document },
    };

    [Theory]
    [MemberData(nameof(LibraryAtomValues))]
    public void A_library_atom_is_written_with_the_bytes_reflection_wrote(Type type, object? value)
    {
        var written = JsonSerializer.Serialize(value, WorkflowDateTimeText.Options.GetTypeInfo(type));

        written.Should().Be(JsonSerializer.Serialize(value, ReflectionLibraryAtomOptions().GetTypeInfo(type)));
    }

    [Theory]
    [InlineData("\"2026-11-09T10:15:30\"")]
    [InlineData("\"2026-11-09T10:15:30.5+02:00\"")]
    [InlineData("\"2026-11-09T10:15:30Z\"")]
    [InlineData("null")]
    public void A_library_date_time_is_read_as_reflection_read_it(string json)
    {
        var reflection = ReflectionLibraryAtomOptions();

        JsonSerializer.Deserialize(json, WorkflowDateTimeText.Options.GetTypeInfo(typeof(Nullable<DateTimeOffset>)))
            .Should().Be(JsonSerializer.Deserialize(json, reflection.GetTypeInfo(typeof(Nullable<DateTimeOffset>))));
        if (json != "null")
        {
            JsonSerializer.Deserialize(json, WorkflowDateTimeText.Options.GetTypeInfo(typeof(DateTimeOffset)))
                .Should().Be(JsonSerializer.Deserialize(json, reflection.GetTypeInfo(typeof(DateTimeOffset))));
        }
    }

    [Fact]
    public void A_date_time_without_an_offset_still_reads_as_utc_through_the_nullable_metadata()
    {
        var read = JsonSerializer.Deserialize("\"2026-11-09T10:15:30\"", WorkflowDateTimeText.Options.GetTypeInfo(typeof(Nullable<DateTimeOffset>)));

        read.Should().Be(new DateTimeOffset(2026, 11, 9, 10, 15, 30, TimeSpan.Zero));
    }

    public static TheoryData<object?> ListedLiteralValues() => new()
    {
        null, "text \" é", true, false, (sbyte)-3, (byte)3, (short)-300, (ushort)300, -70000, 70000u, long.MaxValue, ulong.MaxValue,
        1.5f, 0.1, 12.345m, Document,
        new Dictionary<string, object?> { ["b"] = 1L, ["a"] = new List<object?> { true, null, "x" }, ["c"] = new Dictionary<string, object?>() },
        new List<object?> { 1L, 2.5, "three", null, new object?[] { false } },
        new object?[] { new Dictionary<string, object?> { ["k"] = 0.25m } },
    };

    public static TheoryData<object?> UnlistedLiteralValues() => new()
    {
        new SortedDictionary<string, int> { ["z"] = 1, ["y"] = 2 },
        new[] { 1, 2, 3 },
        new UnlistedRecord("name", 7, [new UnlistedRecord("inner", 0, [])]),
    };

    [Theory]
    [MemberData(nameof(ListedLiteralValues))]
    [MemberData(nameof(UnlistedLiteralValues))]
    public void A_literal_is_converted_to_the_element_reflection_produced(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value, AbstractionsJson.AnyValue);

        element.GetRawText().Should().Be(ReflectionSerializeToElement(value).GetRawText());
    }

    [Theory]
    [MemberData(nameof(ListedLiteralValues))]
    public void A_literal_of_a_listed_type_is_converted_by_the_generated_metadata_alone(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value, GeneratedOnly().GetTypeInfo(typeof(object)));

        element.GetRawText().Should().Be(ReflectionSerializeToElement(value).GetRawText());
    }

    [Fact]
    public void Without_reflection_a_literal_of_an_unlisted_type_is_refused()
    {
        var convert = () => JsonSerializer.SerializeToElement(
            (object)new UnlistedRecord("name", 1, []), GeneratedOnly().GetTypeInfo(typeof(object)));

        convert.Should().Throw<NotSupportedException>().WithMessage($"*{nameof(UnlistedRecord)}*");
    }

    [Fact]
    public void A_library_date_time_that_is_not_one_is_refused_as_reflection_refused_it()
    {
        var generated = () => JsonSerializer.Deserialize("\"not a date\"", WorkflowDateTimeText.Options.GetTypeInfo(typeof(DateTimeOffset)));
        var reflection = () => JsonSerializer.Deserialize("\"not a date\"", ReflectionLibraryAtomOptions().GetTypeInfo(typeof(DateTimeOffset)));

        generated.Should().Throw<JsonException>().WithMessage("*not a date and time*");
        reflection.Should().Throw<JsonException>().WithMessage("*not a date and time*");
    }

    private static JsonSerializerOptions GeneratedOnly() => new() { TypeInfoResolver = AbstractionsJsonContext.Default };

    private static JsonSerializerOptions ReflectionLibraryAtomOptions()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(WorkflowDateTimeText.Options.Converters.Single());
        options.MakeReadOnly();
        return options;
    }

    private static JsonElement ReflectionSerializeToElement(object? value) => JsonSerializer.SerializeToElement(value);

    public sealed record UnlistedRecord(string Name, int Count, IReadOnlyList<UnlistedRecord> Children);
}
