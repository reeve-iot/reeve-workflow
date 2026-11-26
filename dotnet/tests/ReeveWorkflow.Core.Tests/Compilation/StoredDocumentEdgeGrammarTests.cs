using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class StoredDocumentEdgeGrammarTests
{
    private static string Valid(string edge) =>
        $"{{\"nodes\":[{{\"id\":\"a\",\"position\":{{\"x\":0,\"y\":0}},\"configuration\":{{}}}},{{\"id\":\"b\",\"position\":{{\"x\":0,\"y\":0}},\"configuration\":{{}}}}],\"edges\":[{edge}]}}";

    [Theory]
    [InlineData("{\"nodes\":[]}", "L7", "/edges")]
    [InlineData("{\"nodes\":[],\"edges\":[{\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\",\"mappings\":{}}]}", "L7", "/edges/0/id")]
    [InlineData("{\"nodes\":[],\"edges\":[{\"id\":\"\",\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\",\"mappings\":{}}]}", "L7", "/edges/0/id")]
    [InlineData("{\"nodes\":[],\"edges\":[{\"id\":\"e\",\"targetNodeId\":\"b\",\"mappings\":{}}]}", "L7", "/edges/0/sourceNodeId")]
    [InlineData("{\"nodes\":[],\"edges\":[{\"id\":\"e\",\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\"}]}", "L7", "/edges/0/mappings")]
    [InlineData("{\"nodes\":[],\"edges\":[{\"id\":\"e\",\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\",\"mappings\":[]}]}", "L7", "/edges/0/mappings")]
    [InlineData("{\"nodes\":[],\"edges\":[{\"id\":\"e\",\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\",\"mappings\":{\"In\":{}}}]}", "L7", "/edges/0/mappings/In/type")]
    [InlineData("{\"nodes\":[],\"edges\":[{\"id\":\"e\",\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\",\"mappings\":{\"In\":{\"type\":\"literal\",\"literalValue\":null}}}]}", "L7", "/edges/0/mappings/In/literalValue")]
    public void Edge_forms_refuse_at_their_field(string text, string rule, string location)
    {
        StoredWorkflowDocument.Read(text, out var fault).Should().BeNull();
        fault.Should().NotBeNull();
        fault!.Rule.Should().Be(rule);
        fault.Location.Should().Be(location);
    }

    [Fact]
    public void Duplicate_edge_ids_and_output_mappings_have_named_reader_rules()
    {
        var duplicate = Valid("{\"id\":\"e\",\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\",\"mappings\":{}},{\"id\":\"e\",\"sourceNodeId\":\"b\",\"targetNodeId\":\"a\",\"mappings\":{}}");
        StoredWorkflowDocument.Read(duplicate, out var duplicateFault).Should().BeNull();
        duplicateFault!.Rule.Should().Be(StoredDocumentRules.DuplicateEdgeId);
        duplicateFault.Location.Should().Be("/edges/1/id");

        StoredWorkflowDocument.Read("{\"nodes\":[],\"edges\":[],\"outputMappings\":[]}", out var outputFault).Should().BeNull();
        outputFault!.Rule.Should().Be(StoredDocumentRules.OutputMappingsUnread);
        outputFault.Location.Should().Be("/outputMappings");
    }
}
