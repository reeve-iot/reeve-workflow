using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.JsonPath;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

[CollectionDefinition("LocalTimeZone", DisableParallelization = true)]
public sealed class LocalTimeZoneCollection;

[Collection("LocalTimeZone")]
public sealed class DateTimeWithoutOffsetTests
{
    public static TheoryData<string, int> Zones() => new() { { "Pacific/Kiritimati", 14 }, { "Pacific/Pago_Pago", -11 }, { "UTC", 0 } };

    private static WorkflowTypeVocabulary Vocabulary => WorkflowTypeVocabulary.Builtin;

    private static ClosedPortType Term(string term) => Vocabulary.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static void InZone(string zone, int hours, Action read)
    {
        var before = Environment.GetEnvironmentVariable("TZ");
        try
        {
            Environment.SetEnvironmentVariable("TZ", zone);
            TimeZoneInfo.ClearCachedData();
            TimeZoneInfo.Local.BaseUtcOffset.Should().Be(TimeSpan.FromHours(hours), $"the server must be in {zone} for this test to mean anything");
            read();
        }
        finally
        {
            Environment.SetEnvironmentVariable("TZ", before);
            TimeZoneInfo.ClearCachedData();
        }
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void A_literal_without_an_offset_is_the_same_instant_in_every_zone(string zone, int hours)
    {
        InZone(zone, hours, () =>
        {
            var type = Term("DateTime");
            foreach (var (text, instant) in new[]
                     {
                         ("2026-01-02T03:04:05", "2026-01-02T03:04:05+00:00"),
                         ("2026-01-02", "2026-01-02T00:00:00+00:00"),
                         ("0001-01-01T00:00:00", "0001-01-01T00:00:00+00:00"),
                         ("9999-12-31T23:59:59.9999999", "9999-12-31T23:59:59.9999999+00:00"),
                         ("2026-01-02T03:04:05Z", "2026-01-02T03:04:05+00:00"),
                         ("2026-01-02T03:04:05+02:00", "2026-01-02T03:04:05+02:00"),
                     })
            {
                var raw = Json(JsonSerializer.Serialize(text));
                Vocabulary.IsLiteralOf(raw, PortValueType.Of("DateTime")).Should().BeTrue(text);
                Written(type, WorkflowCompiler.Coerce(raw, type, Vocabulary)).Should().Be(instant, text);
            }
        });
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void A_projected_value_without_an_offset_is_the_same_instant_in_every_zone(string zone, int hours)
    {
        InZone(zone, hours, () =>
        {
            var source = Json("{\"at\":\"2026-01-02T03:04:05\",\"m\":{\"a\":\"2026-01-02T03:04:05\",\"b\":\"2026-01-02T03:04:05-05:00\"}}");

            Projected("$.at", "DateTime", source).Should().Be("2026-01-02T03:04:05+00:00");
            Projected("$.m", "Map<String,DateTime>", source).Should().Be(
                "{\"a\":\"2026-01-02T03:04:05+00:00\",\"b\":\"2026-01-02T03:04:05-05:00\"}");
        });
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void A_stored_value_and_a_stored_fire_without_an_offset_are_the_same_instant_in_every_zone(string zone, int hours)
    {
        InZone(zone, hours, () =>
        {
            var type = Term("DateTime");
            Written(type, PersistedValueCodec.Read(type, Json("\"2026-01-02T03:04:05\""), "outputs", "n", "at")).Should().Be("2026-01-02T03:04:05+00:00");

            var (_, envelope) = PersistedValueCodec.ReadTriggerEnvelope("{\"firedAt\":\"2026-01-02T03:04:05\"}", _ => null);
            envelope!.FiredAtUtc.Should().Be(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        });
    }

    [Fact]
    public void Every_corpus_row_that_states_an_instant_reads_to_it_in_every_zone()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.LiteralCoercionPath()));
        var rows = file.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.TryGetProperty("instant", out _)).Select(r => (r.GetProperty("value").GetString()!, r.GetProperty("instant").GetString()!)).ToList();
        rows.Should().HaveCountGreaterThan(5);

        foreach (var (zone, hours) in new[] { ("Pacific/Kiritimati", 14), ("Pacific/Pago_Pago", -11) })
        {
            InZone(zone, hours, () =>
            {
                var type = Term("DateTime");
                foreach (var (text, instant) in rows)
                    Written(type, WorkflowCompiler.Coerce(Json(JsonSerializer.Serialize(text)), type, Vocabulary)).Should().Be(instant, $"{text} in {zone}");
            });
        }
    }

    private static string Written(ClosedPortType type, object? value) => type.ToJson(value)!.Value.GetString()!;

    private static string Projected(string path, string term, JsonElement source)
    {
        var closed = Term(term);
        new CompiledProjection(JsonPathQuery.Parse(path), false, closed, null, Vocabulary).TryBind(source, "wire", out var value).Should().BeTrue();
        return closed.ToJson(value)!.Value.ToString().Trim('"').Replace("\\u002B", "+");
    }
}
