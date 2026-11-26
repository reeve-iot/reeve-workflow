using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class StoredDocumentFuzzTests
{
    private const int Documents = 9000;

    private const int ReadableFloor = 600;

    private static readonly string[] Seeds =
    [
        """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"a","blockTypeId":"dyn","configuration":{"InputPorts":"[{\"name\":\"i\",\"type\":\"Int\",\"isRequired\":true}]","OutputPorts":"[{\"name\":\"o\",\"type\":\"CollectionInt\"}]"},"position":{"x":0,"y":0}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"a","mappings":{"i":{"type":"direct","sourceOutputName":"x"}}}]}""",
        """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"container","outputPorts":[{"name":"p","displayName":"P","type":"Collection","sources":{"Then":"raw"}}],"subCanvases":{"Then":{"nodes":[{"id":"c","blockTypeId":"dyn","configuration":{"OutputPorts":"[{\"name\":\"q\",\"type\":\"Pt \"}]","DeviceId":"d"},"position":{"x":0,"y":0}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{"In":{"type":"literal","literalValue":5}}}]}""",
        """{"nodes":[{"id":"n","blockTypeId":"fixed","position":{"x":1.5,"y":-2},"errorPolicy":{"policy":"Skip","routeToErrorPort":false},"passthrough":["a"],"configuration":{"InputPorts":"[]"}}],"outputMappings":[{"sourceNodeId":"n","sourceOutput":"o","workflowOutput":"w"}],"edges":[]}""",
        """{"Nodes":[{"Id":"a","BlockTypeId":"dyn","Position":{"X":1,"Y":2},"Config":{"DeviceId":"x","Kind":"k","OutputPorts":"[{\"Name\":\"o\",\"Type\":\"CollectionDevice\"}]"}}],"Connections":[{"Id":"e","SourceNodeId":"a","TargetNodeId":"a"}]}""",
    ];

    private static readonly string[] Parameters =
    [
        """[{"name":"a","type":"CollectionString","isRequired":true},{"name":"b"}]""",
        """[{"Name":"a","TYPE":"Int"}]""",
        "null",
    ];

    private static readonly string[] Fragments =
    [
        "{", "}", "[", "]", "\"", ",", ":", "null", "5", "true", "\\ud800", "\\udc00", "\ud800", "\\u00", "\\", "﻿",
        "\"nodes\":[]", "\"Nodes\":null", "\"config\":{}", "\"configuration\":{}", "\"edges\":null", "\"type\":5", "\"type\":null",
        "\"id\":\"a\"", "\"InputPorts\":\"[{\\\"name\\\":\\\"z\\\"}]\"", "[[[[[[[[[[[[[[[[", "Collection<", "\"subCanvases\":{\"B\":{}}",
    ];

    private static readonly IBlockShapeSource Catalogue = CompilerTestGraphs.Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("dyn", hasDynamicPorts: true, source: "plugin:dyn"),
        Shape("container", supportsDeclaredOutputs: true,
            subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
            subCanvasNames: ["Then"]));

    private static string Mutate(Random random, string text)
    {
        var mutations = random.Next(1, 4);
        for (var m = 0; m < mutations; m++)
        {
            var at = random.Next(text.Length + 1);
            text = random.Next(7) switch
            {
                0 when text.Length > 0 => text.Remove(Math.Min(at, text.Length - 1), 1),
                1 => text.Insert(at, Fragments[random.Next(Fragments.Length)]),
                2 => FlipCase(random, text),
                3 => Structural(random, text),
                4 => Duplicate(random, text),
                5 => text.Insert(at, new string((char)random.Next(0xD000, 0xE000), 1)),
                _ => text.Insert(at, Fragments[random.Next(Fragments.Length)]),
            };
        }

        return text;
    }

    private static string FlipCase(Random random, string text)
    {
        var letters = Enumerable.Range(0, text.Length).Where(i => char.IsLetter(text[i])).ToList();
        if (letters.Count == 0)
            return text;
        var i = letters[random.Next(letters.Count)];
        var c = text[i];
        return text[..i] + (char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) + text[(i + 1)..];
    }

    private static string Duplicate(Random random, string text)
    {
        var comma = text.IndexOf(',', random.Next(text.Length + 1));
        var quote = comma < 0 ? -1 : text.IndexOf('"', comma);
        var end = quote < 0 ? -1 : text.IndexOfAny([',', '}'], quote);
        return end < 0 ? text : text.Insert(end, "," + text[(comma + 1)..end]);
    }

    private static string Structural(Random random, string text)
    {
        try
        {
            return StructuralOrThrow(random, text);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return text;
        }
    }

    private static string StructuralOrThrow(Random random, string text)
    {
        var root = JsonNode.Parse(text);

        var containers = new List<JsonNode>();
        void Collect(JsonNode? node)
        {
            if (node is JsonObject o)
            {
                containers.Add(o);
                foreach (var (_, v) in o.ToList())
                    Collect(v);
            }
            else if (node is JsonArray a)
            {
                containers.Add(a);
                foreach (var v in a.ToList())
                    Collect(v);
            }
        }

        Collect(root);
        if (containers.Count == 0)
            return text;
        var target = containers[random.Next(containers.Count)];
        JsonNode? replacement = random.Next(6) switch
        {
            0 => null,
            1 => JsonValue.Create(7),
            2 => JsonValue.Create("Collection<Int"),
            3 => new JsonArray(),
            4 => new JsonObject(),
            _ => JsonValue.Create(true),
        };
        if (target is JsonObject obj && obj.Count > 0)
        {
            var key = obj.ElementAt(random.Next(obj.Count)).Key;
            if (random.Next(2) == 0)
                obj[key] = replacement;
            else
            {
                var value = obj[key];
                obj.Remove(key);
                obj[random.Next(2) == 0 ? key.ToUpperInvariant() : char.ToUpperInvariant(key[0]) + key[1..]] = value;
            }
        }
        else if (target is JsonArray array)
        {
            array.Add(replacement);
        }

        return root!.ToJsonString();
    }

    private static IEnumerable<(string Definition, string? Inputs)> Corpus()
    {
        var random = new Random(20260923);
        for (var i = 0; i < Documents; i++)
        {
            var seed = Seeds[random.Next(Seeds.Length)];
            var definition = random.Next(10) == 0 ? seed : Mutate(random, seed);
            var inputs = random.Next(3) == 0 ? Mutate(random, Parameters[random.Next(Parameters.Length)]) : Parameters[random.Next(Parameters.Length)];
            yield return (definition, inputs);
        }
    }

    private static IEnumerable<string> Keys(IEnumerable<CompilationDiagnostic> diagnostics) =>
        diagnostics.Select(PortTypeNameCheckTests.Key).Order(StringComparer.Ordinal);

    [Fact]
    public void No_document_makes_the_reader_the_checker_or_the_compiler_throw()
    {
        int readable = 0, unreadable = 0;
        foreach (var (definition, inputs) in Corpus())
        {
            var because = JsonSerializer.Serialize(definition);
            var read = () => StoredWorkflowDocument.Read(definition, out _);
            read.Should().NotThrow(because);
            var check = () => PortTypeNameCheck.Check(definition, inputs, inputs);
            check.Should().NotThrow(because);

            if (StoredWorkflowDocument.Read(definition, out _) is { } document)
            {
                readable++;
                var compile = () => WorkflowCompiler.Compile(document, Catalogue, CanvasSignature.Empty);
                compile.Should().NotThrow(because);
                var compileModel = () => WorkflowCompiler.Compile(document.Definition, Catalogue, CanvasSignature.Empty);
                compileModel.Should().NotThrow(because);
            }
            else
            {
                unreadable++;
            }
        }

        readable.Should().BeGreaterThan(ReadableFloor, "the corpus must reach the compiler, not only the lexer");
        unreadable.Should().BeGreaterThan(Documents / 10);
    }

    [Fact]
    public void On_every_readable_document_the_checker_the_compiler_and_the_model_path_name_the_same_sites()
    {
        var compared = 0;
        foreach (var (definition, _) in Corpus())
        {
            if (StoredWorkflowDocument.Read(definition, out _) is not { } document)
                continue;

            var because = JsonSerializer.Serialize(definition);
            var checker = Keys(PortTypeNameCheck.Check(definition, null, null).Diagnostics).ToList();
            var compiler = Keys(WorkflowCompiler.Compile(document, Catalogue, CanvasSignature.Empty).Diagnostics
                .Where(d => d.RuleId == CompilationRules.PortTypeMalformed)).ToList();
            var model = Keys(WorkflowCompiler.Compile(document.Definition, Catalogue, CanvasSignature.Empty).Diagnostics
                .Where(d => d.RuleId == CompilationRules.PortTypeMalformed)).ToList();

            compiler.Should().Equal(checker, because);
            model.Should().Equal(compiler, because);
            compared++;
        }

        compared.Should().BeGreaterThan(ReadableFloor);
    }

    [Fact]
    public void The_block_collector_names_every_block_the_reader_or_the_engine_reads()
    {
        int accepted = 0, engineOnly = 0;
        foreach (var (definition, _) in Corpus())
        {
            var because = JsonSerializer.Serialize(definition);
            WorkflowDefinition? model;
            if (StoredWorkflowDocument.Read(definition, out _) is { } document)
            {
                model = document.Definition;
                accepted++;
            }
            else
            {
                try
                {
                    model = WorkflowDefinitionParse.FromText(definition);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    continue;
                }

                if (model is null)
                    continue;
                engineOnly++;
            }

            IReadOnlySet<string> collected;
            try
            {
                collected = WorkflowDefinitionBlockTypes.Collect(definition);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            BlockTypeIds(model).Where(id => !collected.Contains(id)).Should().BeEmpty(because);
        }

        accepted.Should().BeGreaterThan(ReadableFloor);
        engineOnly.Should().BeGreaterThan(Documents / 20);
    }

    private static IEnumerable<string> BlockTypeIds(WorkflowDefinition? canvas) =>
        (canvas?.Nodes ?? []).Where(n => n is not null).SelectMany(n =>
            (n.SubCanvases?.Values.SelectMany(BlockTypeIds) ?? []).Concat(n.BlockTypeId is { Length: > 0 } id ? [id] : []));
}
