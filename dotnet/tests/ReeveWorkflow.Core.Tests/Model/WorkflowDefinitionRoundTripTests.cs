using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowDefinitionRoundTripTests
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Corpus.Names)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Every_corpus_definition_survives_the_model(string caseName) =>
        RoundTrip(Corpus.Definition(caseName), caseName);

    [Fact]
    public void A_definition_using_every_field_survives_the_model() =>
        RoundTrip(JsonNode.Parse(EveryFieldDefinition)!, "every-field");

    [Fact]
    public void The_every_field_fixture_names_every_property_the_model_declares()
    {
        string[] aliases = ["config"];

        var declared = new[]
            {
                typeof(WorkflowDefinition), typeof(WorkflowNodeDefinition),
                typeof(WorkflowConnectionDefinition), typeof(WorkflowNodePosition),
                typeof(SubCanvasOutputPortDefinition), typeof(WorkflowOutputMappingDefinition),
            }
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .Distinct()
            .Where(n => !aliases.Contains(n))
            .ToList();

        declared.Should().NotBeEmpty("reflection finding no properties would make this test vacuous");

        var missing = declared.Where(n => !EveryFieldDefinition.Contains($"\"{n}\"", StringComparison.Ordinal)).ToList();
        missing.Should().BeEmpty(
            "every property the model declares must appear in the every-field fixture, or dropping it in a "
            + "future move would go unnoticed");
    }

    private static void RoundTrip(JsonNode original, string caseName)
    {
        var model = JsonSerializer.Deserialize<WorkflowDefinition>(
            original.ToJsonString(), WorkflowGraphHelpers.JsonOptions)!;
        var roundTripped = JsonNode.Parse(JsonSerializer.Serialize(model, WriteOptions))!;

        Canonical(roundTripped).Should().Be(Canonical(original),
            $"case '{caseName}' must mean the same thing after the model has read and rewritten it");
    }

    private static string Canonical(JsonNode node) => Reduce(node).ToJsonString();

    private static JsonNode? Reduce(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var reduced = new JsonObject();
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (obj[key] is null) continue;
                    var canonicalKey = key switch
                    {
                        "configuration" => "config",
                        _ => key,
                    };
                    if (!reduced.ContainsKey(canonicalKey))
                        reduced[canonicalKey] = Reduce(obj[key]?.DeepClone());
                }
                return reduced;
            }
            case JsonArray array:
            {
                var reduced = new JsonArray();
                foreach (var item in array)
                    reduced.Add(Reduce(item?.DeepClone()));
                return reduced;
            }
            default:
                return node;
        }
    }

    private const string EveryFieldDefinition = """
    {
      "nodes": [
        {
          "id": "in",
          "blockTypeId": "WorkflowInput",
          "position": { "x": 12.5, "y": -7.25 },
          "configuration": { "Seed": 41 }
        },
        {
          "id": "branch",
          "blockTypeId": "IfElse",
          "position": { "x": 120, "y": 0 },
          "configuration": { "Condition": "${parameter.on}" },
          "typeArguments": { "T": "Int" },
          "passthrough": ["RoomId", "DeviceId"],
          "errorPolicy": { "policy": "ContinueWithDefault", "defaultOutputJson": "{\"v\":1}", "routeToErrorPort": true },
          "outputPorts": [
            {
              "name": "setpoint",
              "displayName": "Setpoint",
              "type": "Number",
              "sources": { "then": "Out" }
            }
          ],
          "subCanvases": {
            "then": {
              "nodes": [
                { "id": "sub-in", "blockTypeId": "WorkflowInput", "position": { "x": 0, "y": 0 }, "configuration": {} },
                { "id": "sub-out", "blockTypeId": "WorkflowOutput", "position": { "x": 80, "y": 0 }, "configuration": {} }
              ],
              "edges": [
                { "id": "se1", "sourceNodeId": "sub-in", "targetNodeId": "sub-out", "mappings": { "Out": { "sourceOutputName": "Value" } } }
              ]
            }
          }
        }
      ],
      "edges": [
        { "id": "e1", "sourceNodeId": "in", "targetNodeId": "branch", "mappings": { "Value": { "sourceOutputName": "Out" } } }
      ],
      "outputMappings": [
        { "sourceNodeId": "branch", "sourceOutput": "setpoint", "workflowOutput": "Result" }
      ]
    }
    """;

    private static class Corpus
    {
        private static readonly JsonNode Root = Load();

        public static IEnumerable<string> Names =>
            Root["cases"]!.AsArray().Select(c => c!["name"]!.GetValue<string>());

        public static JsonNode Definition(string name) =>
            Root["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!["definition"]!;

        private static JsonNode Load()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "contracts", "workflow-validation-corpus.json");
                if (File.Exists(candidate))
                    return JsonNode.Parse(File.ReadAllText(candidate))!;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("contracts/workflow-validation-corpus.json not found");
        }
    }
}
