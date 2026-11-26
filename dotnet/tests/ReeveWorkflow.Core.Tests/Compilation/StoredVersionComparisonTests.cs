using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class StoredVersionComparisonTests
{
    private static readonly WorkflowDiffCanvasStep[] Body = [new("c", "Body")];

    private static readonly string[] NotCompared =
    [
        "node.position", "position.x", "position.y", "declared output.displayName",
        "error policy.routeToErrorPort", "connection.id", "parameter.displayName",
        "canvas.outputMappings", "output mapping.sourceNodeId", "output mapping.sourceOutput", "output mapping.workflowOutput",
    ];

    [Fact]
    public void Every_field_of_the_kind_table_is_read_or_named_with_its_reason()
    {
        var table = StoredDocumentSchema.Objects(StoredDocumentSchema.Definition)
            .Concat(StoredDocumentSchema.Objects(StoredDocumentSchema.ParameterList))
            .Concat(StoredDocumentSchema.Objects(StoredDocumentSchema.PortList))
            .SelectMany(kind => kind.Fields.Select(field => $"{kind.Name}.{field.Key}"));

        StoredVersionComparison.Fields.Select(f => f.Label).Should().BeEquivalentTo(table);
        StoredVersionComparison.Fields.Should().OnlyContain(f => f.Says.Length > 0);
        StoredVersionComparison.Fields.Where(f => !f.Compared).Select(f => f.Label).Should().BeEquivalentTo(NotCompared);
    }

    [Theory]
    [InlineData(false, "/nodes/5/id")]
    [InlineData(true, "/nodes/3/subCanvases/Body/nodes/3/id")]
    public void A_duplicate_node_id_is_refused_by_the_version_diff_with_its_rule_and_id(bool inBody, string pointer)
    {
        var duplicate = Base();
        var canvas = inBody ? duplicate.Definition["nodes"]![3]!["subCanvases"]!["Body"]! : duplicate.Definition;
        canvas["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "a", ["blockTypeId"] = "Act", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() });

        var compare = () => StoredVersionComparison.Compare(Texts(1, duplicate), Texts(2, Base()), Catalogue());

        compare.Should().Throw<InvalidWorkflowDefinitionException>()
            .WithMessage($"*id 'a'*rule duplicate-node-id*at '{pointer}'*");
    }

    [Fact]
    public void Every_compared_field_has_a_change_that_is_reported_at_the_root_and_in_a_sub_canvas()
    {
        var compared = StoredVersionComparison.Fields.Where(f => f.Compared).Select(f => f.Label).ToList();
        compared.Where(label => !Changes.ContainsKey(label)).Should().BeEmpty("each compared field needs a change here");

        foreach (var label in compared)
        {
            var (change, reported) = Changes[label];
            foreach (var place in Places(label))
            {
                var diff = Diff(Base(), Mutated(change, place));
                reported(diff, place).Should().BeTrue($"{label} changed {place} is reported: {Show(diff)}");
            }
        }
    }

    [Fact]
    public void A_field_no_run_reads_changes_nothing_the_engine_does_and_is_not_reported()
    {
        foreach (var label in NotCompared.Where(Unread.ContainsKey))
        {
            foreach (var place in Places(label))
            {
                var changed = Mutated(Unread[label], place);
                Diff(Base(), changed).Should().Be(Empty, $"{label} changed {place}");
                Trace(changed).Should().Be(Trace(Base()), $"{label} changed {place}");
            }
        }
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_route_to_the_error_port_is_refused_before_a_version_diff(Place place)
    {
        var routed = Mutated(v => ErrorPolicy(v.Canvas)["routeToErrorPort"] = true, place);

        FluentActions.Invoking(() => Diff(Base(), routed)).Should()
            .Throw<InvalidWorkflowDefinitionException>().WithMessage("*rule error-policy-route-unread*routeToErrorPort*");
    }

    [Fact]
    public void Two_forms_the_engine_runs_identically_are_not_reported()
    {
        foreach (var (name, left, right) in Identical())
        {
            foreach (var place in (Place[])[Place.Root, Place.Body])
            {
                var a = Mutated(left, place);
                var b = Mutated(right, place);
                Trace(a).Should().Be(Trace(b), $"{name} {place} runs identically");
                Diff(a, b).Should().Be(Empty, $"{name} {place}");
            }
        }
    }

    [Fact]
    public void The_base_version_runs_every_block_on_both_canvases_and_ends_with_its_output()
    {
        var trace = Trace(Base()).Split('\n');

        trace.Select(line => line.Split(' ')[0]).Where(first => !Enum.GetNames<LogEntryLevel>().Contains(first))
            .Should().Equal("a", "a", "b", "c", "b", "outputs");
        trace.Should().Contain(line => line.StartsWith("a ", StringComparison.Ordinal) && line.EndsWith("k:\"LX\"}", StringComparison.Ordinal));
        trace[^1].Should().StartWith("outputs {w:\"b{t:");
    }

    [Fact]
    public void A_boundary_block_spelled_in_another_case_runs_differently_and_is_reported()
    {
        var lower = Mutated(v => Node(v.Canvas, "in")["blockTypeId"] = "workflowinput", Place.Root);

        Trace(lower).Should().NotBe(Trace(Base()));
        var diff = Diff(Base(), lower);
        diff.RemovedNodes.Should().ContainSingle(n => n.Id == "in");
        diff.AddedNodes.Should().ContainSingle(n => n.Id == "in");
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_whole_number_and_a_fraction_of_one_value_reach_a_block_as_two_values_and_are_reported(Place place)
    {
        var fraction = Mutated(v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse("""{"n":1.0}"""), place);
        var whole = Mutated(v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse("""{"n":1}"""), place);

        Trace(fraction).Should().NotBe(Trace(whole));
        Diff(whole, fraction).ModifiedNodes.Should().ContainSingle().Which.ConfigChanges.Should().ContainKey("n")
            .WhoseValue.Should().Be(new WorkflowDiffValueChange("1", "1.0"));
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_mappings_array_is_refused_before_a_version_diff_however_they_are_ordered(Place place)
    {
        var array = Mutated(v => Edge(v.Canvas, "e1")["mappings"] = JsonNode.Parse("""[{"sourceOutput":"o","targetInput":"k"},{"sourceOutput":"x","targetInput":"k"}]"""), place);

        FluentActions.Invoking(() => Diff(Base(), array)).Should().Throw<InvalidWorkflowDefinitionException>()
            .WithMessage("*an array where an object is required*rule L7*mappings*");
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_missing_or_null_configuration_is_refused_before_a_version_diff(Place place)
    {
        foreach (var invalid in (JsonNode?[])[null, Absent])
        {
            var document = Mutated(_ => { }, place);
            Set(Node(document.Canvas, "b"), "configuration", invalid);
            FluentActions.Invoking(() => Diff(Base(), document)).Should()
                .Throw<InvalidWorkflowDefinitionException>().WithMessage("*rule L7*configuration*");
        }
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void An_edge_rewired_or_remapped_under_its_id_is_reported(Place place)
    {
        foreach (var change in (Action<Version>[])[
            v => Edge(v.Canvas, "e2")["targetNodeId"] = "in",
            v => Edge(v.Canvas, "e2")["sourceNodeId"] = "b",
            v => Edge(v.Canvas, "e2")["mappings"] = new JsonObject { ["t"] = new JsonObject { ["type"] = "direct", ["sourceOutputName"] = "p" } }])
        {
            var changed = Mutated(change, place);
            var diff = Diff(Base(), changed);
            diff.RemovedEdges.Should().ContainSingle(e => e.Id == "e2").Which.Should().BeEquivalentTo(new
            {
                CanvasPath = Path(place), MappingsJson = Edge(Mutated(_ => { }, place).Canvas, "e2")["mappings"]!.ToJsonString(),
            });
            diff.AddedEdges.Should().ContainSingle(e => e.Id == "e2").Which.Should().BeEquivalentTo(new
            {
                CanvasPath = Path(place), MappingsJson = Edge(changed.Canvas, "e2")["mappings"]!.ToJsonString(),
            });
        }
    }

    [Fact]
    public void Declared_outputs_passthrough_the_output_wire_and_the_output_parameters_are_compared()
    {
        Diff(Base(), Mutated(v => Port(v.Canvas)["sources"]!["Body"] = "s", Place.Root))
            .ModifiedNodes.Single().ChangedFields.Should().Equal("outputPorts");
        Diff(Base(), Mutated(v => Node(v.Canvas, "a")["passthrough"] = new JsonArray("q", "s"), Place.Root))
            .ModifiedNodes.Single().ChangedFields.Should().Equal("passthrough");
        var wired = Diff(Base(), Mutated(v => Edge(v.Canvas, "e-w")["mappings"]!["w"]!["sourceOutputName"] = "r", Place.Root));
        wired.RemovedEdges.Should().ContainSingle(e => e.Id == "e-w");
        wired.AddedEdges.Should().ContainSingle(e => e.Id == "e-w");

        var outputs = Diff(Base(), Mutated(v => v.Outputs[0]!["type"] = "Number", Place.Root));
        outputs.OutputParameterChanges.Modified.Should().Equal("n");
        outputs.ParameterChanges.Should().BeEquivalentTo(new WorkflowDiffParameters([], [], []));
    }

    [Theory]
    [InlineData("{", "cannot be read")]
    public void An_output_parameter_list_the_reader_refuses_is_refused_as_an_input_list_is(string list, string says)
    {
        var refused = Base();
        refused.OutputsText = list;

        FluentActions.Invoking(() => Diff(Base(), refused)).Should().Throw<InvalidWorkflowDefinitionException>()
            .WithMessage($"*version 2*{says}*");
        refused.InputsText = list;
        refused.OutputsText = null;
        FluentActions.Invoking(() => Diff(Base(), refused)).Should().Throw<InvalidWorkflowDefinitionException>()
            .WithMessage($"*version 2*{says.Replace("output", "input")}*");
    }

    [Fact]
    public void An_edge_with_no_id_is_refused_before_a_version_diff()
    {
        var idless = Mutated(v => Edge(v.Canvas, "e1").Remove("id"), Place.Root);

        FluentActions.Invoking(() => Diff(Base(), idless)).Should().Throw<InvalidWorkflowDefinitionException>()
            .WithMessage("*rule L7*id*");
    }

    [Fact]
    public void A_parameter_with_its_keys_reordered_is_not_reported()
    {
        var reordered = Mutated(v => v.Inputs[0] = JsonNode.Parse("""{"isRequired":true,"type":"String","name":"n"}"""), Place.Root);

        Diff(Base(), reordered).Should().Be(Empty);
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_block_respelled_in_case_is_the_same_node_and_is_reported_since_the_run_hands_its_spelling_on(Place place)
    {
        static Action<Version> Emitting(string block) => v =>
        {
            Node(v.Canvas, "a")["blockTypeId"] = block;
            Node(v.Canvas, "a")["errorPolicy"] = new JsonObject { ["policy"] = "EmitError" };
            Edge(v.Canvas, "e2")["mappings"] = JsonNode.Parse("""{"err":{"type":"direct","sourceOutputName":"_error"}}""");
        };
        var upper = Mutated(Emitting("Fail"), place);
        var lower = Mutated(Emitting("fail"), place);

        Trace(upper).Should().Contain("blockTypeId:\"Fail\"");
        Trace(lower).Should().Contain("blockTypeId:\"fail\"");
        var diff = Diff(upper, lower);
        diff.ModifiedNodes.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = "a", ChangedFields = new[] { "blockTypeId" }, CanvasPath = Path(place), ErrorPolicyChanged = false, RetryPolicyChanged = false,
        });
        (diff.AddedNodes.Count + diff.RemovedNodes.Count + diff.AddedEdges.Count + diff.RemovedEdges.Count).Should().Be(0);

        var other = Diff(upper, Mutated(Emitting("Act"), place));
        other.RemovedNodes.Should().ContainSingle(n => n.Id == "a" && n.BlockTypeId == "Fail");
        other.AddedNodes.Should().ContainSingle(n => n.Id == "a" && n.BlockTypeId == "Act");
    }

    [Fact]
    public void Every_collection_of_the_kind_table_is_compared_in_the_order_a_run_reads_it()
    {
        var table = TableCollections().ToList();
        StoredVersionComparison.Collections.Select(c => c.Label).Should().BeEquivalentTo(table.Select(c => c.Label));
        foreach (var (label, order) in table)
        {
            if (label is "canvas.outputMappings")
                continue;
            OrderPairs.Should().ContainKey(label, $"{label} needs pairs that measure how a run reads its order");
            order.Where.Should().NotBeNullOrWhiteSpace();
            var pairs = OrderPairs[label];
            if (order is not StoredOrder.Unread)
                pairs.Should().Contain(p => p.RunsDifferently, $"{label} is read in order");
            if (order is not StoredOrder.Read)
                pairs.Should().Contain(p => !p.RunsDifferently, $"{label} is not read in every order");

            foreach (var pair in pairs)
            {
                foreach (var place in pair.Places ?? [Place.Root, Place.Body])
                {
                    var (a, b) = (Mutated(pair.Left, place), Mutated(pair.Right, place));
                    var because = $"{label}: {pair.Name} {place}";
                    if (pair.RunsDifferently)
                    {
                        Outcome(a, place).Should().NotBe(Outcome(b, place), because);
                        Diff(a, b).Should().NotBe(Empty, because);
                    }
                    else
                    {
                        Outcome(a, place).Should().Be(Outcome(b, place), because);
                        Diff(a, b).Should().Be(Empty, because);
                    }
                }
            }
        }
    }

    private static IEnumerable<(string Label, StoredOrder Order)> TableCollections()
    {
        static StoredOrder? OrderOf(StoredKind kind) => kind.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(StoredOrder)).Select(p => (StoredOrder?)p.GetValue(kind)).SingleOrDefault();

        foreach (var obj in StoredDocumentSchema.Objects(StoredDocumentSchema.Definition))
        {
            foreach (var field in obj.Fields)
            {
                var label = $"{obj.Name}.{field.Key}";
                if (OrderOf(field.Kind) is { } order)
                {
                    yield return (label, order);
                }
            }
        }

        yield return ("parameter list", OrderOf(StoredDocumentSchema.ParameterList)!);
        yield return ("port list", OrderOf(StoredDocumentSchema.PortList)!);
    }

    [Fact]
    public void A_node_added_beside_a_reorder_the_walk_does_not_read_is_reported_as_added_alone()
    {
        var added = Mutated(v =>
        {
            Reorder(v.Canvas["nodes"]!.AsArray(), 1, 2);
            AddNode(v.Canvas, "z", "Act");
            AddEdge(v.Canvas, "e-z", "in", "z", last: false);
        }, Place.Root);

        var diff = Diff(Base(), added);
        diff.AddedNodes.Should().ContainSingle(n => n.Id == "z");
        diff.AddedEdges.Should().ContainSingle(e => e.TargetNodeId == "z");
        diff.ModifiedCanvases.Should().BeEmpty();
    }

    [Fact]
    public void A_canvas_one_version_cannot_sort_has_no_order_compared_and_its_cycle_s_wire_is_reported()
    {
        var cyclic = Mutated(v =>
        {
            Reorder(v.Canvas["edges"]!.AsArray(), 0, 1);
            AddEdge(v.Canvas, "e-back", "b", "a");
        }, Place.Root);

        var diff = Diff(Base(), cyclic);
        diff.AddedEdges.Should().ContainSingle(e => e.Id == "e-back");
        diff.ModifiedCanvases.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void Two_default_outputs_that_fail_to_parse_differently_are_logged_differently_and_are_reported(Place place)
    {
        static Action<Version> Unparsable(string text) => v =>
        {
            Node(v.Canvas, "a")["blockTypeId"] = "Fail";
            ErrorPolicy(v.Canvas)["defaultOutputJson"] = text;
        };
        var (open, list) = (Mutated(Unparsable("{"), place), Mutated(Unparsable("[1"), place));

        Trace(open).Should().NotBe(Trace(list));
        Diff(open, list).ModifiedNodes.Should().ContainSingle(n => n.Id == "a").Which.ChangedFields.Should().Equal("errorPolicy");
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_change_to_an_entry_ordered_after_one_it_depends_on_is_reported(Place place)
    {
        static JsonObject Typed(JsonObject port, string type) { port["type"] = type; return port; }
        var text = Mutated(Container(Port("y", "x1"), Typed(Port("z", "y"), "String")), place);
        var number = Mutated(Container(Port("y", "x1"), Typed(Port("z", "y"), "Number")), place);
        Diff(text, number).ModifiedNodes.Should().ContainSingle(n => n.Id == "d").Which.ChangedFields.Should().Equal("outputPorts");
    }

    [Fact]
    public void A_timeout_built_alike_beside_a_retry_change_is_not_listed()
    {
        var before = Mutated(v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse("""{"timeoutSeconds":0,"retryEnabled":true,"retryCount":2}"""), Place.Root);
        var after = Mutated(v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse("""{"timeoutSeconds":1,"retryEnabled":true,"retryCount":5}"""), Place.Root);

        var node = Diff(before, after).ModifiedNodes.Should().ContainSingle().Subject;
        node.ConfigChanges.Keys.Should().Equal("retryCount");
        node.RetryPolicyChanged.Should().BeTrue();
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void Two_wires_into_one_input_are_refused_on_every_canvas_so_their_order_is_no_change(Place place)
    {
        var (before, after) = (Mutated(IntoB("t", last: false), place), Mutated(IntoB("t", last: true), place));

        foreach (var version in (Version[])[before, after])
        {
            var path = string.Join("/", Path(place).Select(s => s.NodeId + ":" + s.SubCanvas));
            Verdict(version).Select(d => d.RuleId + " " + string.Join("/", d.CanvasPath.Select(s => s.NodeId + ":" + s.BranchName)))
                .Should().Contain($"{CompilationRules.DuplicateInputMapping} {path}");
        }
        Diff(before, after).Should().Be(Empty);
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_wire_deleted_and_drawn_again_changes_the_walk_and_is_reported_as_the_canvas_edges(Place place)
    {
        var drawn = Mutated(Sibling("s", last: true), place);
        var redrawn = Mutated(v => { Sibling("s", last: true)(v); Redraw(v, "e1"); }, place);

        Trace(drawn).Should().NotBe(Trace(redrawn));
        Diff(drawn, redrawn).Should().BeEquivalentTo(Empty with { ModifiedCanvases = [new WorkflowDiffCanvas(Path(place), ["edges"])] });
    }

    [Fact]
    public void Two_start_nodes_of_a_sub_canvas_in_another_order_run_in_it_and_are_reported_as_the_canvas_nodes()
    {
        var first = Mutated(Starts("t1", "t2"), Place.Body);
        var second = Mutated(Starts("t2", "t1"), Place.Body);

        Trace(first).Should().NotBe(Trace(second));
        Diff(first, second).Should().BeEquivalentTo(Empty with { ModifiedCanvases = [new WorkflowDiffCanvas(Body, ["nodes"])] });
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void Two_ports_that_read_each_other_in_another_order_run_differently_and_are_reported(Place place)
    {
        var chained = Mutated(Container(Port("y", "x1"), Port("z", "y")), place);
        var reversed = Mutated(Container(Port("z", "y"), Port("y", "x1")), place);

        Trace(chained).Should().NotBe(Trace(reversed));
        Diff(chained, reversed).ModifiedNodes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Id = "d", ChangedFields = new[] { "outputPorts" }, CanvasPath = Path(place) });
    }

    [Fact]
    public void Two_ports_of_one_name_on_two_branches_in_another_order_compile_differently_and_are_reported()
    {
        const string then = """{"name":"y","type":"String","sources":{"Then":"p"}}""";
        const string otherwise = """{"name":"y","type":"Number","sources":{"Else":"q"}}""";
        static string Document(string first, string second) => """
            {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
              {"id":"br","blockTypeId":"Branch","outputPorts":[FIRST,SECOND],
               "subCanvases":{"Then":{"nodes":[{"id":"i","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"w","blockTypeId":"WorkflowOutput","position":{"x":0,"y":0},"configuration":{}}],
                                      "edges":[{"id":"f","sourceNodeId":"i","targetNodeId":"w","mappings":{"p":{"type":"direct","sourceOutputName":"x"}}}]},
                              "Else":{"nodes":[{"id":"i","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"w","blockTypeId":"WorkflowOutput","position":{"x":0,"y":0},"configuration":{}}],
                                      "edges":[{"id":"f","sourceNodeId":"i","targetNodeId":"w","mappings":{"q":{"type":"direct","sourceOutputName":"x"}}}]}},"position":{"x":0,"y":0},"configuration":{}},
              {"id":"s","blockTypeId":"Str","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e1","sourceNodeId":"in","targetNodeId":"br","mappings":{}},
                      {"id":"e2","sourceNodeId":"br","targetNodeId":"s","mappings":{"s":{"type":"direct","sourceOutputName":"y"}}}]}
            """.Replace("FIRST", first).Replace("SECOND", second);
        var catalogue = CompilerTestGraphs.Catalogue(
            CompilerTestGraphs.Shape("Branch", supportsDeclaredOutputs: true, subCanvasNames: ["Then", "Else"],
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
                {
                    ["Then"] = [("x", PortValueType.String, false)], ["Else"] = [("x", PortValueType.Number, false)],
                }),
            CompilerTestGraphs.Shape("Str", inputs: [("s", PortValueType.String, true)], outputs: [("o", PortValueType.String)]));
        string RuleIds(string text) => string.Join("\n", WorkflowCompiler.Compile(
            StoredWorkflowDocument.Read(text, out _)!.Definition, catalogue, CanvasSignature.Empty).Diagnostics.Select(d => d.RuleId));
        var (a, b) = (Document(then, otherwise), Document(otherwise, then));

        RuleIds(a).Should().BeEmpty();
        RuleIds(b).Should().Be(CompilationRules.PortTypeMismatch);
        StoredVersionComparison.Compare(new(1, a, null, null), new(2, b, null, null), Catalogue()).ModifiedNodes
            .Should().ContainSingle(n => n.Id == "br").Which.ChangedFields.Should().Equal("outputPorts");
    }

    [Fact]
    public void A_root_output_mapping_is_refused_before_a_version_diff()
    {
        var mapped = Mutated(v => v.Canvas["outputMappings"] = JsonNode.Parse("""[{"sourceNodeId":"b","sourceOutput":"o","workflowOutput":"w"}]"""), Place.Root);

        FluentActions.Invoking(() => Diff(Base(), mapped)).Should().Throw<InvalidWorkflowDefinitionException>()
            .WithMessage("*rule output-mappings-unread*outputMappings*");
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_workflow_output_spelled_in_another_case_runs_differently_and_is_reported(Place place)
    {
        var exact = Mutated(Outputs(("o1", "a")), place);
        var lower = Mutated(v => { Outputs(("o1", "a"))(v); Node(v.Canvas, "o1")["blockTypeId"] = "workflowoutput"; }, place);

        Trace(exact).Should().NotBe(Trace(lower));
        var diff = Diff(exact, lower);
        diff.RemovedNodes.Should().ContainSingle(n => n.Id == "o1" && n.CanvasPath.SequenceEqual(Path(place)));
        diff.AddedNodes.Should().ContainSingle(n => n.Id == "o1" && n.CanvasPath.SequenceEqual(Path(place)));
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_whole_number_and_a_fraction_as_one_literal_reach_a_block_as_two_values_and_are_reported(Place place)
    {
        var whole = Mutated(v => Mapping(v.Canvas, "u")["literalValue"] = v.Raw("1"), place);
        var fraction = Mutated(v => Mapping(v.Canvas, "u")["literalValue"] = v.Raw("1.0"), place);

        Trace(whole).Should().NotBe(Trace(fraction));
        var diff = Diff(whole, fraction);
        diff.RemovedEdges.Should().ContainSingle(e => e.Id == "e2");
        diff.AddedEdges.Should().ContainSingle(e => e.Id == "e2");
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_node_id_in_another_case_is_another_node(Place place)
    {
        var lower = Mutated(v => AddNode(v.Canvas, "z", "Act"), Place.Body);
        var upper = Mutated(v => AddNode(v.Canvas, "Z", "Act"), Place.Body);

        Trace(lower).Should().NotBe(Trace(upper));
        var diff = Diff(Mutated(v => AddNode(v.Canvas, "z", "Act"), place), Mutated(v => AddNode(v.Canvas, "Z", "Act"), place));
        diff.RemovedNodes.Should().ContainSingle(n => n.Id == "z" && n.CanvasPath.SequenceEqual(Path(place)));
        diff.AddedNodes.Should().ContainSingle(n => n.Id == "Z" && n.CanvasPath.SequenceEqual(Path(place)));
    }

    [Fact]
    public void A_sub_canvas_name_in_another_case_is_another_canvas()
    {
        var exact = Mutated(v => Node(v.Canvas, "c")["subCanvases"] = new JsonObject { ["Body"] = Canvas() }, Place.Root);
        var lower = Mutated(v => Node(v.Canvas, "c")["subCanvases"] = new JsonObject { ["body"] = Canvas() }, Place.Root);

        Trace(exact).Should().NotBe(Trace(lower));
        Diff(exact, lower).ModifiedNodes.Should().ContainSingle(n => n.Id == "c").Which.ChangedFields.Should().Equal("subCanvases");
    }

    private static void LoopD(JsonObject canvas, JsonObject? body)
    {
        AddNode(canvas, "d", "Loop");
        if (body is not null)
            canvas["nodes"]!.AsArray()[^1]!["subCanvases"] = new JsonObject { ["Body"] = body };
        AddEdge(canvas, "e-d", "in", "d");
    }

    private static JsonObject OpenedEmpty() => (JsonObject)JsonNode.Parse(
        """{"nodes":[{"id":"__workflow_input__","blockTypeId":"WorkflowInput","position":{"x":50,"y":100},"configuration":{}}],"edges":[]}""")!;

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_declared_branch_left_out_is_no_change_from_the_empty_branch_the_editor_writes_for_it(Place place)
    {
        var absent = Mutated(v => LoopD(v.Canvas, null), place);
        var opened = Mutated(v => LoopD(v.Canvas, OpenedEmpty()), place);

        Trace(absent).Should().Be(Trace(opened));
        Diff(absent, opened).Should().BeSameAs(Empty);
        Diff(opened, absent).Should().BeSameAs(Empty);
    }

    [Fact]
    public void A_declared_branch_left_out_is_compared_as_it_is_stored_where_the_blocks_are_not_known()
    {
        var absent = Mutated(v => LoopD(v.Canvas, null), Place.Root);
        var opened = Mutated(v => LoopD(v.Canvas, OpenedEmpty()), Place.Root);

        var diff = StoredVersionComparison.Compare(Texts(1, absent), Texts(2, opened));

        diff.ModifiedNodes.Should().ContainSingle(n => n.Id == "d").Which.ChangedFields.Should().Equal("subCanvases");
        diff.AddedNodes.Should().ContainSingle().Which.Id.Should().Be("__workflow_input__");
    }

    [Fact]
    public void A_declared_branch_left_out_is_a_change_from_one_that_holds_a_node()
    {
        var absent = Mutated(v => LoopD(v.Canvas, null), Place.Root);
        var drawn = Mutated(v => LoopD(v.Canvas, Canvas()), Place.Root);

        Trace(absent).Should().NotBe(Trace(drawn));
        Diff(absent, drawn).AddedNodes.Select(n => n.Id).Should().BeEquivalentTo(["in", "a", "b"]);
    }

    [Fact]
    public void Two_branches_stored_in_another_order_are_no_change()
    {
        var first = Mutated(v => Node(v.Canvas, "c")["subCanvases"] = new JsonObject { ["Body"] = Canvas(), ["Other"] = OpenedEmpty() }, Place.Root);
        var second = Mutated(v => Node(v.Canvas, "c")["subCanvases"] = new JsonObject { ["Other"] = OpenedEmpty(), ["Body"] = Canvas() }, Place.Root);

        Trace(first).Should().Be(Trace(second));
        Diff(first, second).Should().BeSameAs(Empty);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    public void A_parameter_name_in_another_case_is_another_parameter(string list)
    {
        var place = list == "input" ? Place.InputList : Place.OutputList;
        var lower = Mutated(Signed("""[{"name":"n","type":"String","isRequired":true}]"""), place);
        var upper = Mutated(Signed("""[{"name":"N","type":"String","isRequired":true}]"""), place);

        Compiled(lower).Should().NotBe(Compiled(upper));
        var changes = Diff(lower, upper) is var diff && place == Place.InputList ? diff.ParameterChanges : diff.OutputParameterChanges;
        changes.Should().BeEquivalentTo(new WorkflowDiffParameters(["N"], ["n"], []));
    }

    [Theory]
    [InlineData(Place.InputList)]
    [InlineData(Place.OutputList)]
    public void The_second_parameter_of_a_name_is_compared_too_since_the_compiler_reads_its_type(Place place)
    {
        var boolean = Mutated(Signed("""[{"name":"n","type":"String","isRequired":true},{"name":"n","type":"Boolean"}]"""), place);
        var malformed = Mutated(Signed("""[{"name":"n","type":"String","isRequired":true},{"name":"n","type":"Collection<"}]"""), place);

        Compiled(boolean).Should().NotBe(Compiled(malformed));
        var diff = Diff(boolean, malformed);
        (place == Place.InputList ? diff.ParameterChanges : diff.OutputParameterChanges).Modified.Should().Equal("n");
    }

    [Theory]
    [InlineData(Place.Root)]
    [InlineData(Place.Body)]
    public void A_null_configuration_value_reaches_the_block_and_is_reported_against_an_absent_one(Place place)
    {
        var nulled = Mutated(v => Node(v.Canvas, "b")["configuration"] = new JsonObject { ["k"] = null }, place);

        FluentActions.Invoking(() => Trace(nulled)).Should().Throw<InvalidOperationException>()
            .WithMessage("*null where removing the configuration key is required*rule L7*");
    }

    [Theory]
    [InlineData(WorkflowGraphExecutor.ConfigTimeoutSeconds, "\"\"")]
    [InlineData(WorkflowGraphExecutor.ConfigTimeoutSeconds, "\"5\"")]
    [InlineData(WorkflowGraphExecutor.ConfigRetryEnabled, "\"yes\"")]
    public void A_stored_wrong_kind_resilience_value_is_refused_instead_of_breaking_the_diff(
        string key, string rawValue)
    {
        var malformed = Mutated(v =>
        {
            Node(v.Canvas, "b")["configuration"]![key] = JsonNode.Parse(rawValue);
        }, Place.Root);

        var comparison = key == WorkflowGraphExecutor.ConfigRetryEnabled
            ? Mutated(v => Node(v.Canvas, "b")["configuration"]![key] = JsonValue.Create(false), Place.Root)
            : Base();
        var diff = Diff(malformed, comparison);

        diff.ModifiedNodes.Should().ContainSingle(node => node.Id == "b")
            .Which.ConfigChanges.Should().ContainKey(key);
    }

    [Theory]
    [InlineData(WorkflowGraphExecutor.ConfigRetryEnabled, "false", true)]
    [InlineData(WorkflowGraphExecutor.ConfigRetryCount, "5", true)]
    [InlineData(WorkflowGraphExecutor.ConfigRetryBackoffSeconds, "4", true)]
    [InlineData(WorkflowGraphExecutor.ConfigTimeoutSeconds, "5", false)]
    public void A_resilience_key_the_pipeline_is_built_from_is_reported_and_only_a_retry_key_as_the_retry_policy(
        string key, string value, bool retry)
    {
        const string retrying = """{"retryEnabled":true,"retryCount":2,"retryBackoffSeconds":1}""";
        var before = Mutated(v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse(retrying), Place.Root);
        var after = Mutated(v =>
        {
            Node(v.Canvas, "b")["configuration"] = JsonNode.Parse(retrying);
            Node(v.Canvas, "b")["configuration"]![key] = JsonNode.Parse(value);
        }, Place.Root);

        Pipeline(before).Should().NotBe(Pipeline(after));
        var node = Diff(before, after).ModifiedNodes.Should().ContainSingle().Subject;
        node.ConfigChanges.Keys.Should().Equal(key);
        node.RetryPolicyChanged.Should().Be(retry);
    }

    [Fact]
    public void A_retry_key_the_pipeline_is_built_the_same_from_is_neither_listed_nor_a_retry_policy_change()
    {
        var before = Mutated(v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse("""{"retryEnabled":false,"retryCount":3,"k":1}"""), Place.Root);
        var after = Mutated(v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse("""{"retryEnabled":false,"retryCount":5,"k":2}"""), Place.Root);

        Pipeline(before).Should().Be(Pipeline(after));
        var node = Diff(before, after).ModifiedNodes.Should().ContainSingle().Subject;
        node.ConfigChanges.Keys.Should().Equal("k");
        node.RetryPolicyChanged.Should().BeFalse();
    }

    [Fact]
    public void A_timeout_left_to_the_host_is_reported_against_one_written_out_where_the_block_is_not_known()
    {
        var absent = Mutated(v => Node(v.Canvas, "b")["configuration"] = new JsonObject(), Place.Root);
        var written = Mutated(v => Node(v.Canvas, "b")["configuration"] = new JsonObject { ["timeoutSeconds"] = 30 }, Place.Root);

        Diff(absent, written).Should().Be(Empty);
        StoredVersionComparison.Compare(Texts(1, absent), Texts(2, written)).ModifiedNodes.Should().ContainSingle()
            .Which.ConfigChanges.Keys.Should().Equal("timeoutSeconds");
    }

    private sealed record OrderPair(string Name, bool RunsDifferently, Action<Version> Left, Action<Version> Right, Place[]? Places = null);

    private static readonly Dictionary<string, OrderPair[]> OrderPairs = new()
    {
        ["canvas.nodes"] =
        [
            new("two workflow outputs feeding one key, a later one winning", true, Outputs(("o1", "a"), ("o2", "b")), Outputs(("o2", "b"), ("o1", "a"))),
            new("two start nodes, run in node order", true, Starts("t1", "t2"), Starts("t2", "t1"), [Place.Body]),
            new("two nodes the edges already order", false, v => { }, v => Reorder(v.Canvas["nodes"]!.AsArray(), 1, 2)),
        ],
        ["canvas.edges"] =
        [
            new("a sibling wired before or after", true, Sibling("s", last: false), Sibling("s", last: true)),
            new("a wire drawn again", true, Sibling("s", last: true), v => { Sibling("s", last: true)(v); Redraw(v, "e1"); }),
            new("a failing sibling wired before or after", true, Failing("f", last: false), Failing("f", last: true)),
            new("two wires into one node on different inputs", false, IntoB("m", last: true), IntoB("m", last: false)),
            new("a wire into a workflow output drawn again beside a sibling", false, IntoOutput(last: false), IntoOutput(last: true)),
            new("two wires to different nodes", false, v => { }, v => Reorder(v.Canvas["edges"]!.AsArray(), 0, 1)),
        ],
        ["node.configuration"] =
        [
            new("keys", false, v => { }, v => Node(v.Canvas, "a")["configuration"] = Reversed((JsonObject)Node(v.Canvas, "a")["configuration"]!)),
        ],
        ["node.typeArguments"] =
        [
            new("parameter names", false,
                v => Node(v.Canvas, "a")["typeArguments"] = JsonNode.Parse("""{"T":"Int","U":"String"}"""),
                v => Node(v.Canvas, "a")["typeArguments"] = JsonNode.Parse("""{"U":"String","T":"Int"}""")),
        ],
        ["node.subCanvases"] =
        [
            new("names", false, v => Node(v.Canvas, "a")["subCanvases"] = JsonNode.Parse("""{"X":{"nodes":[],"edges":[]},"Y":{"nodes":[],"edges":[]}}"""),
                v => Node(v.Canvas, "a")["subCanvases"] = JsonNode.Parse("""{"Y":{"nodes":[],"edges":[]},"X":{"nodes":[],"edges":[]}}""")),
        ],
        ["node.passthrough"] =
        [
            new("names", false, v => Node(v.Canvas, "a")["passthrough"] = new JsonArray("q", "k"), v => Node(v.Canvas, "a")["passthrough"] = new JsonArray("k", "q")),
        ],
        ["node.outputPorts"] =
        [
            new("a port reading what the other writes", true, Container(Port("y", "x1"), Port("z", "y")), Container(Port("z", "y"), Port("y", "x1"))),
            new("two ports writing one name, a later one winning", true, Container(Port("n", "x1"), Port("n", "y")), Container(Port("n", "y"), Port("n", "x1"))),
            new("two ports from two keys", false, Container(Port("p1", "x1"), Port("p2", "y")), Container(Port("p2", "y"), Port("p1", "x1"))),
        ],
        ["declared output.sources"] =
        [
            new("sub-canvas names", false, Container(JsonNode.Parse("""{"name":"p","sources":{"Body":"x1","Else":"y"}}""")!.AsObject()),
                Container(JsonNode.Parse("""{"name":"p","sources":{"Else":"y","Body":"x1"}}""")!.AsObject())),
        ],
        ["connection.mappings"] =
        [
            new("targets", false, v => { }, v => Edge(v.Canvas, "e2")["mappings"] = Reversed((JsonObject)Edge(v.Canvas, "e2")["mappings"]!)),
        ],
        ["parameter list"] =
        [
            new("two parameters of one name, the compiler taking the first", true,
                Signed("""[{"name":"n","type":"String","isRequired":true},{"name":"n","type":"Boolean","isRequired":true}]"""),
                Signed("""[{"name":"n","type":"Boolean","isRequired":true},{"name":"n","type":"String","isRequired":true}]"""),
                [Place.InputList, Place.OutputList]),
            new("two names", false,
                Signed("""[{"name":"n","type":"String","isRequired":true},{"name":"m","type":"Boolean","isRequired":true}]"""),
                Signed("""[{"name":"m","type":"Boolean","isRequired":true},{"name":"n","type":"String","isRequired":true}]"""),
                [Place.InputList, Place.OutputList]),
        ],
        ["port list"] =
        [
            new("the entries of the text the block is handed", true, InputPorts("p", "q"), InputPorts("q", "p")),
        ],
    };

    private static void AddNode(JsonObject canvas, string id, string block) =>
        canvas["nodes"]!.AsArray().Add(new JsonObject { ["id"] = id, ["blockTypeId"] = block, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() });

    private static void AddEdge(JsonObject canvas, string id, string source, string target, string? mappings = null, bool last = true)
    {
        var edge = new JsonObject { ["id"] = id, ["sourceNodeId"] = source, ["targetNodeId"] = target };
        edge["mappings"] = JsonNode.Parse(mappings ?? "{}");
        if (last)
            canvas["edges"]!.AsArray().Add(edge);
        else
            canvas["edges"]!.AsArray().Insert(0, edge);
    }

    private static void Reorder(JsonArray list, int i, int j)
    {
        var (first, second) = (list[i]!.DeepClone(), list[j]!.DeepClone());
        list[i] = second;
        list[j] = first;
    }

    private static JsonObject Reversed(JsonObject obj) =>
        new(obj.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));

    private static Action<Version> Sibling(string id, bool last) => v =>
    {
        AddNode(v.Canvas, id, "Act");
        AddEdge(v.Canvas, "e-" + id, "in", id, last: last);
    };

    private static Action<Version> Failing(string id, bool last) => v =>
    {
        AddNode(v.Canvas, id, "Fail");
        AddEdge(v.Canvas, "e-" + id, "in", id, last: last);
    };

    private static void Redraw(Version v, string id)
    {
        var edges = v.Canvas["edges"]!.AsArray();
        var edge = Edge(v.Canvas, id);
        edges.Remove(edge);
        edge["id"] = id + "-drawn";
        edges.Add(edge);
    }

    private static Action<Version> Starts(string first, string second) => v =>
    {
        AddNode(v.Canvas, first, "Act");
        AddNode(v.Canvas, second, "Act");
    };

    private static Action<Version> IntoB(string input, bool last) => v =>
        AddEdge(v.Canvas, "e-" + input, "in", "b", $$"""{"{{input}}":{"type":"direct","sourceOutputName":"x"} }""", last);

    private static Action<Version> IntoOutput(bool last) => v =>
    {
        AddNode(v.Canvas, "out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
        AddEdge(v.Canvas, "e-out", "a", "out", """{"w9":{"type":"direct","sourceOutputName":"o"}}""", last);
    };

    private static Action<Version> Outputs(params (string Id, string Source)[] outputs) => v =>
    {
        foreach (var (id, source) in outputs)
        {
            AddNode(v.Canvas, id, WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
            AddEdge(v.Canvas, "e-" + id, source, id, """{"w9":{"type":"direct","sourceOutputName":"o"}}""");
        }
    };

    private static JsonObject Port(string name, string source) =>
        new() { ["name"] = name, ["sources"] = new JsonObject { ["Body"] = source } };

    private static Action<Version> Container(params JsonObject[] ports) => v =>
    {
        v.Canvas["nodes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "d", ["blockTypeId"] = "Loop", ["outputPorts"] = new JsonArray([.. ports.Select(p => (JsonNode)p.DeepClone())]),
            ["subCanvases"] = new JsonObject
            {
                ["Body"] = JsonNode.Parse("""
                    {"nodes": [{"id": "i", "blockTypeId": "WorkflowInput","position":{"x":0,"y":0},"configuration":{}}, {"id": "w", "blockTypeId": "WorkflowOutput","position":{"x":0,"y":0},"configuration":{}}],
                     "edges": [{"id": "f", "sourceNodeId": "i", "targetNodeId": "w",
                                "mappings": {"x1": {"type": "direct", "sourceOutputName": "x"}, "y": {"type": "direct", "sourceOutputName": "o"}}}]}
                    """),
            }, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject(),
        });
        AddEdge(v.Canvas, "e-d", "in", "d");
    };

    private static Action<Version> InputPorts(params string[] names) => v =>
        Node(v.Canvas, "a")["configuration"]!["InputPorts"] =
            new JsonArray([.. names.Select(n => (JsonNode)new JsonObject { ["name"] = n, ["type"] = "String" })]).ToJsonString();

    private static Action<Version> Signed(string entries) => v =>
    {
        v.Definition["nodes"] = JsonNode.Parse("""
            [{"id": "in", "blockTypeId": "WorkflowInput", "position": {"x": 0, "y": 0}, "configuration": {}}, {"id": "s", "blockTypeId": "S", "position": {"x": 0, "y": 0}, "configuration": {}}, {"id": "out", "blockTypeId": "WorkflowOutput", "position": {"x": 0, "y": 0}, "configuration": {}}]
            """);
        v.Definition["edges"] = JsonNode.Parse("""
            [{"id": "e1", "sourceNodeId": "in", "targetNodeId": "s", "mappings": {"p": {"type": "direct", "sourceOutputName": "n"}}},
             {"id": "e2", "sourceNodeId": "s", "targetNodeId": "out", "mappings": {"n": {"type": "direct", "sourceOutputName": "o"}}}]
            """);
        v.List.Clear();
        foreach (var entry in JsonNode.Parse(entries)!.AsArray())
            v.List.Add(entry!.DeepClone());
    };

    private static string Outcome(Version version, Place place) =>
        place is Place.InputList or Place.OutputList ? Compiled(version) : Trace(version);

    private static string Compiled(Version version)
    {
        var texts = Texts(0, version);
        if (!CanvasSignature.TryRead(texts.InputParametersJson, texts.OutputParametersJson, out var signature, out var refused))
            return string.Join("\n", refused.Select(d => $"{d.RuleId} {d.Message}"));

        var definition = StoredWorkflowDocument.Read(texts.DefinitionJson, out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        var catalogue = CompilerTestGraphs.Catalogue(
            CompilerTestGraphs.Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            CompilerTestGraphs.Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            CompilerTestGraphs.Shape("S", inputs: [("p", PortValueType.String, true)], outputs: [("o", PortValueType.String)]));
        var result = WorkflowCompiler.Compile(definition, catalogue, signature!);
        return "compiled " + string.Join("\n", result.Diagnostics.Select(d => $"{d.RuleId} {d.NodeId} {d.PortName} {d.Message}"));
    }

    private static IReadOnlyList<CompilationDiagnostic> Verdict(Version version)
    {
        var texts = Texts(0, version);
        CanvasSignature.TryRead(texts.InputParametersJson, texts.OutputParametersJson, out var signature, out _).Should().BeTrue();
        var blocks = new Blocks();
        var catalogue = CompilerTestGraphs.Catalogue([.. ((IWorkflowBlockFactory[])[blocks.Act, blocks.Loop, blocks.Fail]).Select(BlockShape.From)]);
        return WorkflowCompiler.Compile(StoredWorkflowDocument.Read(texts.DefinitionJson, out _)!.Definition, catalogue, signature!).Diagnostics;
    }

    private static string Pipeline(Version version)
    {
        var definition = StoredWorkflowDocument.Read(version.DefinitionText(), out _)!.Definition;
        var node = definition.Nodes.Single(n => n.Id == "b");
        var act = Catalogue().Find("Act")!;
        return string.Join(",", from idempotentOnly in (bool[])[false, true] from container in (int?[])[null, 7919]
            let read = WorkflowGraphExecutor.ReadResilience(WorkflowCompiler.ConfiguredResilienceOf(node), node.Id, act, idempotentOnly, container)
            select $"{read.TimeoutSeconds}/{read.EffectiveRetries}/{(read.EffectiveRetries > 0 ? read.RetryBackoffSeconds : 0)}");
    }

    public enum Place
    {
        Root,
        Body,
        InputList,
        OutputList,
    }

    private static readonly JsonNode Absent = JsonValue.Create("absent")!;

    private sealed class Version
    {
        public required JsonObject Definition { get; init; }
        public required JsonArray Inputs { get; set; }
        public required JsonArray Outputs { get; set; }
        public string? InputsText { get; set; }
        public string? OutputsText { get; set; }
        public JsonObject Canvas { get; set; } = null!;
        public JsonArray List { get; set; } = null!;
        private Dictionary<string, string> RawTexts { get; init; } = [];

        public JsonNode Raw(string text)
        {
            var marker = $"raw-{RawTexts.Count}-{Guid.NewGuid():N}";
            RawTexts[marker] = text;
            return JsonValue.Create(marker)!;
        }

        public string DefinitionText() =>
            RawTexts.Aggregate(Definition.ToJsonString(), (text, raw) => text.Replace($"\"{raw.Key}\"", raw.Value));

        public Version Clone() => new()
        {
            Definition = (JsonObject)Definition.DeepClone(), Inputs = (JsonArray)Inputs.DeepClone(),
            Outputs = (JsonArray)Outputs.DeepClone(), InputsText = InputsText, OutputsText = OutputsText,
            RawTexts = new(RawTexts),
        };
    }

    private static Version Base()
    {
        var root = Canvas();
        var body = Canvas();
        ((JsonArray)root["nodes"]!).Add(new JsonObject
        {
            ["id"] = "c", ["blockTypeId"] = "Loop", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
            ["configuration"] = new JsonObject(), ["subCanvases"] = new JsonObject { ["Body"] = body },
        });
        ((JsonArray)root["edges"]!).Add(new JsonObject { ["id"] = "e3", ["sourceNodeId"] = "in", ["targetNodeId"] = "c", ["mappings"] = new JsonObject() });
        AddNode(root, "fin", WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
        AddEdge(root, "e-w", "b", "fin", """{"w":{"type":"direct","sourceOutputName":"o"}}""");
        const string list = """[{"name":"n","type":"String","isRequired":true}]""";
        return new Version { Definition = root, Inputs = (JsonArray)JsonNode.Parse(list)!, Outputs = (JsonArray)JsonNode.Parse(list)! };
    }

    private static JsonObject Canvas()
    {
        var canvas = (JsonObject)JsonNode.Parse("""
        {
          "nodes": [
            {"id": "in", "blockTypeId": "WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
            {"id": "a", "blockTypeId": "Act", "position": {"x": 1, "y": 2},
             "configuration": {"k": 1, "InputPorts": "[{\"name\":\"p\",\"type\":\"String\",\"isRequired\":true}]"},
             "passthrough": ["q"],
             "outputPorts": [{"name": "r", "displayName": "R", "type": "String", "sources": {"Body": "r"}}],
             "errorPolicy": {"policy": "ContinueWithDefault", "defaultOutputJson": "{\"o\":\"default\"}"}},
            {"id": "b", "blockTypeId": "Act","position":{"x":0,"y":0},"configuration":{}}
          ],
          "edges": [
            {"id": "e1", "sourceNodeId": "in", "targetNodeId": "a", "mappings": {"k": {"type": "direct", "sourceOutputName": "x"}}},
            {"id": "e2", "sourceNodeId": "a", "targetNodeId": "b", "mappings": {
              "t": {"type": "direct", "sourceOutputName": "o"},
              "u": {"type": "literal", "literalValue": 5},
              "v": {"type": "direct", "sourceOutputName": "o"}}}
          ]
        }
        """)!;
        foreach (var node in canvas["nodes"]!.AsArray().OfType<JsonObject>())
        {
            node.TryAdd("position", new JsonObject { ["x"] = 0, ["y"] = 0 });
            node.TryAdd("configuration", new JsonObject());
        }
        return canvas;
    }


    private static void CompleteCanvas(JsonObject canvas)
    {
        if (canvas["nodes"] is not JsonArray nodes)
            return;
        foreach (var node in nodes.OfType<JsonObject>())
        {
            node.TryAdd("position", new JsonObject { ["x"] = 0, ["y"] = 0 });
            node.TryAdd("configuration", new JsonObject());
            if (node["subCanvases"] is JsonObject children)
                foreach (var child in children.Select(pair => pair.Value).OfType<JsonObject>())
                    CompleteCanvas(child);
        }
    }

    private static Version Mutated(Action<Version> change, Place place)
    {
        var version = Base();
        version.Canvas = place == Place.Body ? (JsonObject)Node(version.Definition, "c")["subCanvases"]!["Body"]! : version.Definition;
        version.List = place == Place.OutputList ? version.Outputs : version.Inputs;
        change(version);
        CompleteCanvas(version.Definition);
        return version;
    }

    private static IEnumerable<Place> Places(string label) =>
        label.StartsWith("parameter.", StringComparison.Ordinal) ? [Place.InputList, Place.OutputList] : [Place.Root, Place.Body];

    private static IReadOnlyList<WorkflowDiffCanvasStep> Path(Place place) => place == Place.Body ? Body : [];

    private static JsonObject Node(JsonObject canvas, string id) =>
        (JsonObject)canvas["nodes"]!.AsArray().Single(n => (string?)n!["id"] == id)!;

    private static JsonObject Edge(JsonObject canvas, string id) =>
        (JsonObject)canvas["edges"]!.AsArray().Single(e => (string?)e!["id"] == id)!;

    private static JsonObject Port(JsonObject canvas) => (JsonObject)Node(canvas, "a")["outputPorts"]![0]!;

    private static JsonObject ErrorPolicy(JsonObject canvas) => (JsonObject)Node(canvas, "a")["errorPolicy"]!;

    private static JsonObject Mapping(JsonObject canvas, string target) => (JsonObject)Edge(canvas, "e2")["mappings"]![target]!;

    private static void Set(JsonObject owner, string key, JsonNode? value)
    {
        if (ReferenceEquals(value, Absent))
            owner.Remove(key);
        else
            owner[key] = value?.DeepClone();
    }

    private static void InputPort(JsonObject canvas, string key, JsonNode value)
    {
        var configuration = (JsonObject)Node(canvas, "a")["configuration"]!;
        var ports = JsonNode.Parse((string)configuration["InputPorts"]!)!;
        ports[0]![key] = value;
        configuration["InputPorts"] = ports.ToJsonString();
    }

    private delegate bool Reported(WorkflowDiffResponse diff, Place place);

    private static Reported NodeField(string id, string key) => (diff, place) =>
        diff.ModifiedNodes.Any(n => n.Id == id && n.ChangedFields.Contains(key) && n.CanvasPath.SequenceEqual(Path(place)));

    private static Reported Swapped(string id) => (diff, place) =>
        diff.RemovedNodes.Any(n => n.Id == id && n.CanvasPath.SequenceEqual(Path(place)))
        && diff.AddedNodes.Any(n => n.CanvasPath.SequenceEqual(Path(place)));

    private static readonly Reported EdgeSwapped = (diff, place) =>
        diff.RemovedEdges.Any(e => e.CanvasPath.SequenceEqual(Path(place)))
        && diff.AddedEdges.Any(e => e.CanvasPath.SequenceEqual(Path(place)));

    private static readonly Reported Parameter = (diff, place) =>
    {
        var (changed, other) = place == Place.OutputList
            ? (diff.OutputParameterChanges, diff.ParameterChanges)
            : (diff.ParameterChanges, diff.OutputParameterChanges);
        return (changed.Modified.Count > 0 || (changed.Added.Count > 0 && changed.Removed.Count > 0))
            && other.Added.Count + other.Removed.Count + other.Modified.Count == 0;
    };

    private static readonly Dictionary<string, (Action<Version> Change, Reported Reported)> Changes = new()
    {
        ["canvas.nodes"] = (v => v.Canvas["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "z", ["blockTypeId"] = "Act", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() }),
            (diff, place) => diff.AddedNodes.Any(n => n.Id == "z" && n.CanvasPath.SequenceEqual(Path(place)))),
        ["canvas.edges"] = (v => v.Canvas["edges"]!.AsArray().Add(new JsonObject { ["id"] = "ez", ["sourceNodeId"] = "a", ["targetNodeId"] = "b", ["mappings"] = new JsonObject() }),
            (diff, place) => diff.AddedEdges.Any(e => e.CanvasPath.SequenceEqual(Path(place))) && diff.RemovedEdges.Count == 0),
        ["node.id"] = (v => Node(v.Canvas, "b")["id"] = "b2", Swapped("b")),
        ["node.blockTypeId"] = (v => Node(v.Canvas, "b")["blockTypeId"] = "ACT", NodeField("b", "blockTypeId")),
        ["node.configuration"] = (v => Node(v.Canvas, "a")["configuration"]!["k"] = 2, NodeField("a", "configuration")),
        ["node.typeArguments"] = (v => Node(v.Canvas, "a")["typeArguments"] = new JsonObject { ["T"] = "Number" }, NodeField("a", "typeArguments")),
        ["node.subCanvases"] = (v => Node(v.Canvas, "a")["subCanvases"] = new JsonObject { ["X"] = new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() } },
            NodeField("a", "subCanvases")),
        ["node.passthrough"] = (v => Node(v.Canvas, "a")["passthrough"]!.AsArray().Add("s"), NodeField("a", "passthrough")),
        ["node.outputPorts"] = (v => Node(v.Canvas, "a")["outputPorts"]!.AsArray().Add(new JsonObject { ["name"] = "r2" }),
            NodeField("a", "outputPorts")),
        ["node.errorPolicy"] = (v => Node(v.Canvas, "a").Remove("errorPolicy"), NodeField("a", "errorPolicy")),
        ["declared output.name"] = (v => Port(v.Canvas)["name"] = "r2", NodeField("a", "outputPorts")),
        ["declared output.type"] = (v => Port(v.Canvas)["type"] = "Number", NodeField("a", "outputPorts")),
        ["declared output.sources"] = (v => Port(v.Canvas)["sources"]!["Body"] = "s", NodeField("a", "outputPorts")),
        ["error policy.policy"] = (v => ErrorPolicy(v.Canvas)["policy"] = "EmitError", NodeField("a", "errorPolicy")),
        ["error policy.defaultOutputJson"] = (v => ErrorPolicy(v.Canvas)["defaultOutputJson"] = """{"o":"other"}""",
            NodeField("a", "errorPolicy")),
        ["connection.sourceNodeId"] = (v => Edge(v.Canvas, "e2")["sourceNodeId"] = "in", EdgeSwapped),
        ["connection.targetNodeId"] = (v => Edge(v.Canvas, "e2")["targetNodeId"] = "a", EdgeSwapped),
        ["connection.mappings"] = (v => Edge(v.Canvas, "e2")["mappings"] = new JsonObject(), EdgeSwapped),
        ["mapping.type"] = (v => Mapping(v.Canvas, "t")["type"] = "literal", EdgeSwapped),
        ["mapping.sourceOutputName"] = (v => Mapping(v.Canvas, "t")["sourceOutputName"] = "p", EdgeSwapped),
        ["mapping.literalValue"] = (v => Mapping(v.Canvas, "u")["literalValue"] = 6, EdgeSwapped),
        ["mapping.projection"] = (v => Mapping(v.Canvas, "t")["projection"] = new JsonObject { ["path"] = "$.a" }, EdgeSwapped),
        ["projection.path"] = (v => Mapping(v.Canvas, "t")["projection"] = new JsonObject { ["path"] = "$.b[0]" }, EdgeSwapped),
        ["projection.mode"] = (v => Mapping(v.Canvas, "t")["projection"] = new JsonObject { ["path"] = "$.a", ["mode"] = "jsonText" }, EdgeSwapped),
        ["projection.type"] = (v => Mapping(v.Canvas, "t")["projection"] = new JsonObject { ["path"] = "$.a", ["type"] = "Int" }, EdgeSwapped),
        ["port entry.name"] = (v => InputPort(v.Canvas, "name", "p2"), NodeField("a", "configuration")),
        ["port entry.type"] = (v => InputPort(v.Canvas, "type", "Number"), NodeField("a", "configuration")),
        ["port entry.isRequired"] = (v => InputPort(v.Canvas, "isRequired", false), NodeField("a", "configuration")),
        ["port entry.displayName"] = (v => InputPort(v.Canvas, "displayName", "Another"), NodeField("a", "configuration")),
        ["parameter.name"] = (v => v.List[0]!["name"] = "m", Parameter),
        ["parameter.type"] = (v => v.List[0]!["type"] = "Number", Parameter),
        ["parameter.isRequired"] = (v => v.List[0]!["isRequired"] = false, Parameter),
    };

    private static readonly Dictionary<string, Action<Version>> Unread = new()
    {
        ["node.position"] = v => Node(v.Canvas, "a").Remove("position"),
        ["position.x"] = v => Node(v.Canvas, "a")["position"]!["x"] = 50,
        ["position.y"] = v => Node(v.Canvas, "a")["position"]!["y"] = 50,
        ["declared output.displayName"] = v => Port(v.Canvas)["displayName"] = "Another",
        ["connection.id"] = v => Edge(v.Canvas, "e1")["id"] = "renamed",
        ["parameter.displayName"] = v => v.List[0]!["displayName"] = "Another",
    };

    private static IEnumerable<(string Name, Action<Version> Left, Action<Version> Right)> Identical()
    {
        static Action<Version> Failing(JsonNode? policy) => v =>
        {
            Node(v.Canvas, "a")["blockTypeId"] = "Fail";
            Set(Node(v.Canvas, "a"), "errorPolicy", policy);
        };

        yield return ("an editor-only key", v => { }, v => Node(v.Canvas, "a")["configuration"]!["__design"] = 1);
        yield return ("configuration whitespace", v => { },
            v => Node(v.Canvas, "a")["configuration"] = v.Raw("""{ "InputPorts" : "[{\"name\":\"p\",\"type\":\"String\",\"isRequired\":true}]" , "k" :  1 }"""));
        yield return ("a value's spacing and escapes", v => Node(v.Canvas, "b")["configuration"] = v.Raw("""{"m":{"x":[1,"a"]}}"""),
            v => Node(v.Canvas, "b")["configuration"] = v.Raw("""{"m": { "x" : [ 1 , "\u0061" ] }}"""));

        yield return ("edge ids", v => { }, v => { Edge(v.Canvas, "e1")["id"] = "x"; Edge(v.Canvas, "e2")["id"] = "e1"; Edge(v.Canvas, "x")["id"] = "e2"; });

        foreach (var (left, right) in Pairs<JsonNode?>(
            Absent, new JsonObject { ["policy"] = "FailWorkflow" },
            new JsonObject { ["policy"] = "FailWorkflow", ["defaultOutputJson"] = """{"o":1}""" }))
            yield return ("a failing policy", Failing(left), Failing(right));
        static JsonObject Continuing(JsonNode? defaultOutput)
        {
            var policy = new JsonObject { ["policy"] = "ContinueWithDefault" };
            Set(policy, "defaultOutputJson", defaultOutput);
            return policy;
        }
        foreach (var (left, right) in Pairs<JsonNode?>(Absent, "{}"))
            yield return ("no default output", Failing(Continuing(left)), Failing(Continuing(right)));

        foreach (var policy in (string[])["EmitError", "Skip"])
        {
            yield return ($"a default output under {policy}",
                Failing(new JsonObject { ["policy"] = policy }),
                Failing(new JsonObject { ["policy"] = policy, ["defaultOutputJson"] = """{"o":"other"}""" }));
        }
        foreach (var (left, right) in Pairs<string>("""{"o":"d","p":1,"q":{"r":[1]}}""", """{ "q" : { "r" : [ 1 ] }, "p" : 1.0 , "o" : "\u0064" }"""))
        {
            yield return ("a default output's spacing, escapes, order and number form", Failing(Continuing(left)), Failing(Continuing(right)));
        }

        foreach (var (name, left, right) in (IEnumerable<(string, string, string)>)
        [
            ("a retry count with retries off", """{"retryEnabled":false,"retryCount":3}""", """{"retryEnabled":false,"retryCount":5}"""),
            ("a backoff with no retries", """{"retryCount":0,"retryBackoffSeconds":9}""", """{"retryCount":0,"retryBackoffSeconds":5}"""),
            ("no retries, counted two ways", """{"retryCount":0}""", """{"retryCount":-1}"""),
            ("a timeout below one second", """{"timeoutSeconds":0}""", """{"timeoutSeconds":1}"""),
            ("a negative timeout", """{"timeoutSeconds":-5}""", """{"timeoutSeconds":1}"""),
            ("the defaults written out", "{}", """{"timeoutSeconds":30,"retryEnabled":true,"retryCount":3,"retryBackoffSeconds":1}"""),
        ])
        {
            yield return ($"resilience: {name}", v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse(left),
                v => Node(v.Canvas, "b")["configuration"] = JsonNode.Parse(right));
        }

        yield return ("passthrough order and repeats", v => Node(v.Canvas, "a")["passthrough"] = new JsonArray("q", "k"),
            v => Node(v.Canvas, "a")["passthrough"] = new JsonArray("k", "q", "k"));
        foreach (var (key, empty) in (IEnumerable<(string, JsonNode)>)[("passthrough", new JsonArray()), ("outputPorts", new JsonArray()), ("subCanvases", new JsonObject())])
        {
            foreach (var (left, right) in Pairs<JsonNode?>(Absent, empty))
                yield return ($"an empty {key}", v => Set(Node(v.Canvas, "b"), key, left), v => Set(Node(v.Canvas, "b"), key, right));
        }
        yield return ("a node's keys in another order", v => { }, v =>
        {
            var nodes = v.Canvas["nodes"]!.AsArray();
            var a = Node(v.Canvas, "a");
            var reordered = new JsonObject(a.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
            nodes[nodes.IndexOf(a)] = reordered;
        });
        yield return ("a configuration's keys in another order", v => { }, v =>
        {
            var node = Node(v.Canvas, "a");
            node["configuration"] = new JsonObject(((JsonObject)node["configuration"]!).Reverse()
                .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        });
        yield return ("a parameter's type in another case", v => { }, v => { v.Inputs[0]!["type"] = "string"; v.Outputs[0]!["type"] = "STRING"; });
        yield return ("a declared output's type in another case", v => { }, v => Port(v.Canvas)["type"] = "string");
        yield return ("a parameter's absent flag", v => v.Inputs[0]!["isRequired"] = false, v => v.Inputs[0]!.AsObject().Remove("isRequired"));
    }

    private static IEnumerable<(T, T)> Pairs<T>(params T[] forms) =>
        from i in Enumerable.Range(0, forms.Length) from j in Enumerable.Range(i + 1, forms.Length - i - 1) select (forms[i], forms[j]);

    private static readonly WorkflowDiffParameters EmptyParameters = new([], [], []);

    private static readonly WorkflowDiffResponse Empty = new([], [], [], [], [], EmptyParameters, [], EmptyParameters);

    private static WorkflowDiffResponse Diff(Version a, Version b)
    {
        var diff = StoredVersionComparison.Compare(Texts(1, a), Texts(2, b), Catalogue());
        return Show(diff) == Show(Empty) ? Empty : diff;
    }

    private static RegisteredWorkflowBlocks Catalogue()
    {
        var blocks = new Blocks();
        return new RegisteredWorkflowBlocks([blocks.Act, blocks.Loop, blocks.Fail]);
    }

    private static StoredVersionTexts Texts(int number, Version v) =>
        new(number, v.DefinitionText(), v.InputsText ?? v.Inputs.ToJsonString(), v.OutputsText ?? v.Outputs.ToJsonString());

    private static string Show(WorkflowDiffResponse diff) => JsonSerializer.Serialize(diff);

    private static string Trace(Version version)
    {
        var definition = StoredWorkflowDocument.Read(version.DefinitionText(), out var fault)?.Definition
            ?? throw new InvalidOperationException(fault!.Describe());
        var blocks = new Blocks();
        var executor = new WorkflowGraphExecutor(
            new RegisteredWorkflowBlocks([blocks.Act, blocks.Loop, blocks.Fail]), NullLoggerFactory.Instance,
            NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance);
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["in"] = new Dictionary<string, object?> { ["x"] = "X", ["o"] = "O" },
        };
        var log = new List<WorkflowExecutionLogEntry>();
        string end;
        try
        {
            WorkflowGraphHelpers.ValidateRootCanvas(definition);
            var program = TestPrograms.Walk(definition, new RegisteredWorkflowBlocks([blocks.Act, blocks.Loop, blocks.Fail]));
            var walked = executor.ExecuteGraphAsync(
                outputs, Run, Run, Run, NoServices.Instance, log, [], WorkflowTraceMode.None,
                debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
                inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
                cancellationToken: CancellationToken.None,
                compiled: program).GetAwaiter().GetResult();
            end = "outputs " + Render(WorkflowExecutionHelpers.BuildResultOutputs(walked, program));
        }
        catch (Exception ex)
        {
            end = $"{ex.GetType().Name}: {ex.Message}";
        }

        return string.Join("\n", blocks.Calls.Concat(log.Select(e => $"{e.Level} {e.NodeId} {e.Message}")).Append(end));
    }

    private static readonly Guid Run = Guid.Parse("5e1f0000-0000-0000-0000-000000000001");

    private static string Render(object? value) => value switch
    {
        null => "null",
        string s => JsonSerializer.Serialize(s),
        long or int or double or bool => $"{value.GetType().Name}:{Convert.ToString(value, CultureInfo.InvariantCulture)}",
        IEnumerable<KeyValuePair<string, object?>> map => "{" + string.Join(",", map.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + ":" + Render(p.Value))) + "}",
        System.Collections.IEnumerable list => "[" + string.Join(",", list.Cast<object?>().Select(Render)) + "]",
        _ => $"{value.GetType().Name}:{value}",
    };

    private sealed class Blocks
    {
        public List<string> Calls { get; } = [];

        public IWorkflowBlockFactory Act => new Factory("Act", () => new Recording(this, fail: false),
            outputs: [new BlockPortDefinition("o", "o", PortValueType.String, false), new BlockPortDefinition("r", "r", PortValueType.String, false)]);

        public IWorkflowBlockFactory Fail => new Factory("Fail", () => new Recording(this, fail: true));

        public IWorkflowBlockFactory Loop => new Factory("Loop", () => new LoopBlock(this), ["Body"],
            [new BlockPortDefinition("x", "x", PortValueType.String, false), new BlockPortDefinition("o", "o", PortValueType.String, false)]);

        private sealed class Recording(Blocks blocks, bool fail) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                var seen = Render(inputs);
                blocks.Calls.Add($"{context.NodeId} {seen}");
                if (fail)
                    throw new InvalidOperationException("failed on " + seen);
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["o"] = context.NodeId + seen, ["r"] = "r" + seen });
            }
        }

        private sealed class LoopBlock(Blocks blocks) : IWorkflowBlockPlugin
        {
            public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                var result = await context.SubCanvasExecutor!.ExecuteAsync(
                    "Body", new Dictionary<string, object?> { ["x"] = "LX", ["o"] = "LO" }, cancellationToken);
                blocks.Calls.Add($"{context.NodeId} body {Render(result)}");
                return result;
            }
        }
    }

    private sealed class Factory(
        string id, Func<IWorkflowBlockPlugin> create, IReadOnlyCollection<string>? subCanvases = null,
        IReadOnlyCollection<BlockPortDefinition>? subCanvasInputs = null,
        IReadOnlyCollection<BlockPortDefinition>? outputs = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];
        public IReadOnlyCollection<string> SubCanvasNames => subCanvases ?? [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) => subCanvasInputs ?? [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => create();
    }

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
