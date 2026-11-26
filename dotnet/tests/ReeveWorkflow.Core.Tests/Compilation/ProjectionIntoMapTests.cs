using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.JsonPath;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class ProjectionIntoMapTests
{
    private static CompiledProjection Projection(string path, string term)
    {
        var vocabulary = WorkflowTypeVocabulary.Builtin;
        return new CompiledProjection(JsonPathQuery.Parse(path), false, vocabulary.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!, null, vocabulary);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void A_projected_map_keeps_the_members_in_document_order()
    {
        Projection("$.m", "Map<String,Int>").TryBind(Json("{\"m\":{\"z\":1,\"a\":2,\"m\":3}}"), "wire", out var value).Should().BeTrue();

        value.Should().BeAssignableTo<IReadOnlyDictionary<string, long>>().Which.Keys.Should().Equal("z", "a", "m");
    }

    [Fact]
    public void A_repeated_member_name_is_refused_by_name()
    {
        var projection = Projection("$.m", "Map<String,Int>");

        FluentActions.Invoking(() => projection.TryBind(Json("{\"m\":{\"a\":1,\"a\":1}}"), "wire", out _))
            .Should().Throw<JsonProjectionException>().Where(e => e.Category == JsonProjectionException.Narrowing && e.Message.Contains("'a'"));
    }

    [Theory]
    [InlineData("{\"m\":{\"a\":1,\"b\":null}}", "'b'", "null")]
    [InlineData("{\"m\":{\"a\":1,\"b\":\"x\"}}", "'b'", "string")]
    public void A_failing_member_is_named(string source, string member, string found)
    {
        var projection = Projection("$.m", "Map<String,Int>");

        FluentActions.Invoking(() => projection.TryBind(Json(source), "wire", out _))
            .Should().Throw<JsonProjectionException>().Where(e => e.Message.Contains(member) && e.Message.Contains("object at '$.m'") && e.Message.Contains(found));
    }
}
