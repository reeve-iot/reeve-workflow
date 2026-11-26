using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class PortTypeNameCheckTests
{
    private static string Json(WorkflowDefinition definition) => Stored(definition);

    private static WorkflowDefinition WithDynamicPorts(string inputPortsJson, string outputPortsJson) => Graph(
        [StartNode(), Node("a", "dyn", new { InputPorts = inputPortsJson, OutputPorts = outputPortsJson })],
        [Edge("e1", "start", "a")]);

    [Fact]
    public void A_concatenated_dynamic_output_spelling_is_reported_with_its_site_fault_and_hint()
    {
        var result = PortTypeNameCheck.Check(
            Json(WithDynamicPorts("[]", """[{"name":"Items","type":"CollectionInt"}]""")), null, null);

        var found = result.Malformed.Should().ContainSingle().Which;
        found.Site.Should().Be(PortTypeNameSite.DynamicOutputPort);
        found.Error.Fault.Should().Be(PortTypeSyntaxFault.ReservedCollectionPrefix);
        found.Error.Hint.Should().Contain("Collection<T>");
        found.Diagnostic.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        found.Diagnostic.NodeId.Should().Be("a");
        found.Diagnostic.PortName.Should().Be("Items");
        found.Diagnostic.CanvasPath.Should().BeEmpty();
        result.IsWellFormed.Should().BeFalse();
        result.Diagnostics.Should().Equal(found.Diagnostic);
    }

    [Theory]
    [InlineData("FailWorkflow")]
    [InlineData("ContinueWithDefault")]
    [InlineData("EmitError")]
    [InlineData("Skip")]
    public void An_error_policy_named_as_the_engine_writes_it_reads_well_formed(string policy)
    {
        var json = $$"""{"nodes":[{"id":"a","errorPolicy":{"policy":"{{policy}}"},"outputPorts":[{"name":"p","type":"Int"}],"position":{"x":0,"y":0},"configuration":{} }],"edges":[]}""";

        var check = () => PortTypeNameCheck.Check(json, null, null);

        check.Should().NotThrow().Which.IsWellFormed.Should().BeTrue();
        StoredWorkflowDocument.Read(json, out _)!.Definition.Nodes.Single().ErrorPolicy!.Policy.ToString().Should().Be(policy);
    }

    [Fact]
    public void The_checker_names_no_block_source_and_the_compiler_names_the_blocks()
    {
        var definition = WithDynamicPorts("[]", """[{"name":"Items","type":"CollectionInt"}]""");
        var shape = Shape("dyn", hasDynamicPorts: true, source: "plugin:dyn");

        var checker = PortTypeNameCheck.Check(Json(definition), null, null).Diagnostics.Should().ContainSingle().Which;
        var compiler = WorkflowCompiler.Compile(definition, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape), CanvasSignature.Empty)
            .Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeMalformed).Which;

        checker.BlockSource.Should().BeNull();
        compiler.BlockSource.Should().Be("plugin:dyn");
        compiler.Message.Should().Be(checker.Message);
    }

    [Fact]
    public void A_port_list_entry_that_is_not_an_object_makes_the_definition_unreadable()
    {
        var result = PortTypeNameCheck.Check(Json(WithDynamicPorts("[null]", "[]")), null, null);

        result.Malformed.Should().BeEmpty();
        var refusal = result.Unreadable.Should().ContainSingle().Which;
        refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        refusal.NodeId.Should().Be("a");
        refusal.Message.Should().Contain("rule L7").And.Contain("at '/nodes/1/configuration/InputPorts#/0'");
    }

    [Fact]
    public void A_dynamic_input_and_a_declared_output_inside_a_sub_canvas_carry_its_path()
    {
        var inner = Node("inner", "dyn", new { InputPorts = """[{"name":"In","type":"Foo Bar"}]""" });
        inner.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "Out" }];
        var host = Node("loop", "foreach");
        host.SubCanvases = new() { ["Body"] = Graph([StartNode("in"), inner]) };

        var result = PortTypeNameCheck.Check(Json(Graph([StartNode(), host])), null, null);

        result.Malformed.Select(m => (m.Site, m.Error.Fault, m.Diagnostic.NodeId, m.Diagnostic.PortName)).Should().Equal(
            (PortTypeNameSite.DynamicInputPort, PortTypeSyntaxFault.IllegalCharacter, "inner", "In"),
            (PortTypeNameSite.DeclaredOutput, PortTypeSyntaxFault.Absent, "inner", "Out"));
        result.Malformed.Should().OnlyContain(m =>
            m.Diagnostic.CanvasPath.SequenceEqual(new[] { new CanvasPathSegment("loop", "Body") }));
    }

    [Fact]
    public void Each_parameter_list_is_checked_and_a_bare_Collection_hints_the_element_type()
    {
        var result = PortTypeNameCheck.Check(
            Json(Graph([StartNode()])), """[{"name":"zone"}]""", """[{"name":"all","type":"Collection"}]""");

        result.Malformed.Select(m => (m.Site, m.Error.Fault, m.Diagnostic.NodeId, m.Diagnostic.PortName)).Should().Equal(
            (PortTypeNameSite.InputParameter, PortTypeSyntaxFault.Absent, (string?)null, "zone"),
            (PortTypeNameSite.OutputParameter, PortTypeSyntaxFault.CollectionWithoutBracket, (string?)null, "all"));
        result.Malformed[1].Error.Hint.Should().Contain("Collection<T>");
    }

    [Fact]
    public void A_parameter_list_that_cannot_be_read_is_reported_as_unreadable_not_as_well_formed()
    {
        var result = PortTypeNameCheck.Check(Json(Graph([StartNode()])), """{"not":"a list"}""", null);

        result.Malformed.Should().BeEmpty();
        var refusal = result.Unreadable.Should().ContainSingle().Which;
        refusal.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
        refusal.Message.Should().Contain("input parameters").And.Contain("rule L7").And.Contain("at ''");
        result.IsWellFormed.Should().BeFalse();
        result.Diagnostics.Should().Equal(refusal);
    }

    [Fact]
    public void A_document_whose_every_name_parses_is_well_formed()
    {
        var result = PortTypeNameCheck.Check(
            Json(WithDynamicPorts("""[{"name":"In","type":"Collection<Device>"}]""", """[{"name":"Out","type":"device"}]""")),
            """[{"name":"n","type":"Int"}]""", null);

        result.IsWellFormed.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "L1", "")]
    [InlineData("{", "L1", "")]
    [InlineData("""{"nodes":[{"id":"a","blockTypeId":"x","id":"b","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "L3", "/nodes/0/id")]
    [InlineData("""{"nodes":[{"id":"\ud800","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "L4", "/nodes/0/id")]
    [InlineData("""{"Nodes":[]}""", "L5", "/Nodes")]
    [InlineData("""{"edges":[],"connections":null}""", "stored-key-unknown", "/connections")]
    [InlineData("""{"nodes":[null],"edges":[]}""", "L7", "/nodes/0")]
    public void A_definition_the_reader_refuses_is_one_diagnostic_never_an_exception(string? json, string rule, string location)
    {
        var result = PortTypeNameCheck.Check(json, null, null);

        result.Malformed.Should().BeEmpty();
        var refusal = result.Unreadable.Should().ContainSingle().Which;
        refusal.RuleId.Should().Be(rule == "stored-key-unknown" ? CompilationRules.StoredKeyUnknown : CompilationRules.DefinitionUnreadable);
        refusal.Message.Should().Contain($"rule {rule}, at '{location}'");
    }

    [Fact]
    public void Port_lists_are_read_on_every_node_by_the_checker_and_the_compiler_alike()
    {
        var graph = WithDynamicPorts("[]", """[{"name":"Items","type":"Pt "}]""");

        var checker = PortTypeNameCheck.Check(Json(graph), null, null);
        var compiler = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("dyn")), CanvasSignature.Empty);

        checker.Malformed.Should().ContainSingle();
        compiler.Diagnostics.Where(d => d.RuleId == CompilationRules.PortTypeMalformed).Select(Key)
            .Should().Equal(checker.Diagnostics.Select(Key));
    }

    private static readonly IBlockShapeSource DynamicCatalogue = Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("dyn", hasDynamicPorts: true),
        Shape("foreach", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] },
            subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]));

    [Fact]
    public void The_checker_and_the_compiler_report_the_same_sites_for_every_corpus_name()
    {
        var names = RepositoryFiles.PortTypeNameCorpusNames();
        names.Should().HaveCountGreaterThan(9000);
        var compared = 0;
        var withFindings = 0;
        var unwritable = new List<string>();

        foreach (var name in names)
        {
            var entry = JsonSerializer.Serialize(new[] { new { name = "P", type = name } });
            var inner = Node("inner", "dyn", new { InputPorts = entry, OutputPorts = entry });
            inner.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "D", Type = name }];
            var root = Node("a", "dyn", new { InputPorts = entry, OutputPorts = entry });
            root.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "D", Type = name }];
            var loop = Node("loop", "foreach");
            loop.SubCanvases = new() { ["Body"] = Graph([StartNode("in"), inner]) };
            var graph = Graph([StartNode(), root, loop]);

            if (StoredWorkflowDocument.Write(graph, out _) is null)
            {
                WorkflowCompiler.Compile(graph, DynamicCatalogue, CanvasSignature.Empty).Diagnostics.Should().ContainSingle()
                    .Which.Message.Should().Contain("rule L4", $"'{name}'");
                unwritable.Add(name);
                continue;
            }

            var checker = Outcome(() => PortTypeNameCheck.Check(Json(graph), entry, entry).Diagnostics);
            var compiler = Outcome(() =>
            {
                CanvasSignature.TryRead(entry, entry, out _, out var signatureDiagnostics);
                return signatureDiagnostics.Concat(WorkflowCompiler.Compile(graph, DynamicCatalogue, CanvasSignature.Empty)
                    .Diagnostics.Where(d => d.RuleId is CompilationRules.PortTypeMalformed or CompilationRules.PortTypeMapKey));
            });

            checker.Should().BeEquivalentTo(compiler, because: $"'{name}'");
            compared++;
            if (checker.Count > 0)
                withFindings++;
        }

        unwritable.Should().Equal(names.Where(HasUnpairedSurrogate),
            "only a name with an unpaired surrogate cannot be written");
        compared.Should().Be(names.Count - unwritable.Count);
        withFindings.Should().Be(names.Except(unwritable).Count(n => !PortValueType.TryParse(n, out _, out _)),
            "every malformed name is found, and no well-formed one");
    }

    internal static IReadOnlyList<string> Outcome(Func<IEnumerable<CompilationDiagnostic>> run)
    {
        try
        {
            return [.. run().Select(Key)];
        }
        catch (Exception e)
        {
            return [$"throws {e.GetType().Name}"];
        }
    }

    internal static string Key(CompilationDiagnostic d) =>
        $"{d.RuleId}|{d.NodeId}|{d.PortName}|{string.Join("/", d.CanvasPath.Select(s => $"{s.NodeId}.{s.BranchName}"))}|{d.Message}";

    private static bool HasUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return true;
        }

        return false;
    }
}
