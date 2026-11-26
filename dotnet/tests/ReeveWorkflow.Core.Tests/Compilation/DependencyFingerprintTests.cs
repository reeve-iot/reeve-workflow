using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class DependencyFingerprintTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PortShape>> NoSubCanvases =
        new Dictionary<string, IReadOnlyList<PortShape>>();

    [Fact]
    public void The_same_shapes_in_a_different_order_hash_the_same()
    {
        var a = Shape("a", outputs: [("Out", PortValueType.Number)]);
        var b = Shape("b", inputs: [("In", PortValueType.Number, true)]);

        Hash(a, b).Should().Be(Hash(b, a));
    }

    [Fact]
    public void Two_shapes_sharing_a_block_type_id_hash_the_same_in_either_order()
    {
        var a = Raw(inputs: [Port("In")]);
        var b = Raw(outputs: [Port("Out")]);

        Hash(a, b).Should().Be(Hash(b, a));
    }

    [Fact]
    public void A_new_plugin_version_that_changed_no_shape_hashes_the_same()
    {
        var before = Raw(outputs: [Port("Out", "Number")], source: "plugin@1.0.0");
        var after = Raw(outputs: [Port("Out", "Number")], source: "plugin@2.0.0");

        Hash(after).Should().Be(Hash(before));
    }

    [Fact]
    public void A_port_type_respelled_in_another_case_moves_the_hash()
    {
        Hash(Raw(outputs: [Port("Out", "boolean")])).Should().NotBe(Hash(Raw(outputs: [Port("Out", "BOOLEAN")])));
    }

    [Fact]
    public void Registered_atom_and_constructor_names_respelled_in_another_case_move_the_hash()
    {
        HashPlugin(new WorkflowTypeRecord("Gauge", typeof(string))).Should()
            .NotBe(HashPlugin(new WorkflowTypeRecord("GAUGE", typeof(string))));
        HashPlugin(Constructor() with { Name = "Gauge" }).Should()
            .NotBe(HashPlugin(Constructor() with { Name = "GAUGE" }));
    }

    [Fact]
    public void A_host_declaring_a_type_with_no_widening_changes_the_hash()
    {
        var widget = new WorkflowTypeRecord("Widget", typeof(string));
        var gadget = new WorkflowTypeRecord("Gadget", typeof(string));

        HashRecords(widget).Should().NotBe(Hash(Raw()));
        HashRecords(widget).Should().NotBe(HashRecords(gadget));
    }

    [Fact]
    public void An_environment_with_the_same_resolved_content_hashes_the_same_however_it_was_built()
    {
        var shape = Raw(id: "run-workflow", runsWorkflowFrom: "WorkflowId");
        var first = Env(CanvasPath.Root, "sub", NodePortResolution.Resolved("child", Sig(Port("Greeting", "String"))));
        var second = Env(CanvasPath.Root, "sub", NodePortResolution.Resolved("child", Sig(Port("Greeting", "String"))));

        HashIn(first, shape).Should().Be(HashIn(second, shape));
    }

    [Fact]
    public void The_same_resolved_entries_added_in_a_different_order_hash_the_same()
    {
        var oneFirst = new NodePortEnvironmentBuilder()
            .Add(CanvasPath.Root, "one", NodePortResolution.Resolved("wf-1", CanvasSignature.Empty))
            .Add(CanvasPath.Root, "two", NodePortResolution.Resolved("wf-2", CanvasSignature.Empty))
            .Build();
        var twoFirst = new NodePortEnvironmentBuilder()
            .Add(CanvasPath.Root, "two", NodePortResolution.Resolved("wf-2", CanvasSignature.Empty))
            .Add(CanvasPath.Root, "one", NodePortResolution.Resolved("wf-1", CanvasSignature.Empty))
            .Build();

        HashIn(oneFirst, Raw()).Should().Be(HashIn(twoFirst, Raw()));
    }

    [Fact]
    public void The_same_namespace_checks_registered_in_a_different_order_hash_the_same()
    {
        var deviceFirst = Options(new StubNamespace("device"), new StubNamespace("scene"));
        var sceneFirst = Options(new StubNamespace("scene"), new StubNamespace("device"));

        HashOpt(deviceFirst, Raw()).Should().Be(HashOpt(sceneFirst, Raw()));
    }

    [Fact]
    public void A_null_environment_is_refused_rather_than_read_as_an_empty_one()
    {
        var compute = () => DependencyFingerprint.Compute([Raw()], new CompilationOptions(), null!);

        compute.Should().Throw<ArgumentNullException>().WithParameterName("environment");
    }

    [Fact]
    public void A_helper_path_answered_with_another_term_moves_the_digest()
    {
        var path = new HelperPath(["x"]);
        string Digest(NamespacePathCheckAnswer answer) => HashOpt(
            new CompilationOptions().WithHelperChecks(new Dictionary<HelperPath, NamespacePathCheckAnswer> { [path] = answer }), Raw());

        var asInt = Digest(new(NamespacePathCheck.Yes, "Int"));

        asInt.Should().NotBe(Digest(new(NamespacePathCheck.Yes, "String")));
        asInt.Should().NotBe(Digest(NamespacePathCheckAnswer.No));
        asInt.Should().NotBe(Digest(new(NamespacePathCheck.No, "Int")), "a No that carries a term still reads as No");
        asInt.Should().Be(Digest(new(NamespacePathCheck.Yes, "Int")));
    }

    [Fact]
    public void A_document_that_reads_no_helper_hashes_as_it_did_before_helper_answers_joined_the_digest()
    {
        var filledEmpty = new CompilationOptions().WithHelperChecks(new Dictionary<HelperPath, NamespacePathCheckAnswer>());

        HashOpt(filledEmpty, Raw()).Should().Be(Hash(Raw()));
    }

    [Fact]
    public void A_path_the_resolver_left_unanswered_hashes_as_the_No_it_reads_as()
    {
        var path = new HelperPath(["x"]);
        var asked = new CompilationOptions().WithHelperChecks(new Dictionary<HelperPath, NamespacePathCheckAnswer> { [path] = NamespacePathCheckAnswer.No });

        HashOpt(asked, Raw()).Should().NotBe(Hash(Raw()), "the version reads a helper, so its key is no longer the helper-free one");
    }

    [Fact]
    public void A_null_shapes_or_options_is_refused()
    {
        var nullShapes = () => DependencyFingerprint.Compute(null!, new CompilationOptions(), NodePortEnvironment.Empty);
        var nullOptions = () => DependencyFingerprint.Compute([Raw()], null!, NodePortEnvironment.Empty);

        nullShapes.Should().Throw<ArgumentNullException>().WithParameterName("shapes");
        nullOptions.Should().Throw<ArgumentNullException>().WithParameterName("options");
    }

    [Fact]
    public void The_compiler_version_moves_with_the_rule_set_and_not_with_its_order()
    {
        var ids = typeof(CompilationRules).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        var rows = ExpressionSignatures.All;
        DependencyFingerprint.CompilerVersion.Should().Be(DependencyFingerprint.VersionOf(ids, rows));
        DependencyFingerprint.VersionOf(Enumerable.Reverse(ids), rows).Should().Be(DependencyFingerprint.CompilerVersion);
        DependencyFingerprint.VersionOf([.. ids, "a-rule-added"], rows).Should().NotBe(DependencyFingerprint.CompilerVersion);
        DependencyFingerprint.VersionOf(ids.Skip(1), rows).Should().NotBe(DependencyFingerprint.CompilerVersion);
    }

    [Fact]
    public void The_compiler_version_moves_with_every_field_of_the_expression_signature_table_and_not_with_its_order()
    {
        var ids = typeof(CompilationRules).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        var rows = ExpressionSignatures.All;
        var round = rows.First(r => r.Parameters.Count > 0 && r.Failures.Count > 0);
        string Version(IEnumerable<ExpressionSignature> table) => DependencyFingerprint.VersionOf(ids, table);
        IEnumerable<ExpressionSignature> With(ExpressionSignature changed) => rows.Select(r => r.Id == round.Id ? changed : r);

        Version(Enumerable.Reverse(rows)).Should().Be(DependencyFingerprint.CompilerVersion);
        var moved = new[]
        {
            Version(rows.Skip(1)),
            Version([.. rows, round with { Id = "an-overload-added" }]),
            Version(With(round with { Id = round.Id + "-renamed" })),
            Version(With(round with { Name = round.Name + "x" })),
            Version(With(round with { Kind = round.Kind == ExpressionSignatureKind.Function ? ExpressionSignatureKind.Operator : ExpressionSignatureKind.Function })),
            Version(With(round with { Parameters = [.. round.Parameters, "Int"] })),
            Version(With(round with { Parameters = [.. round.Parameters.Select(p => p + "x")] })),
            Version(With(round with { Result = round.Result + "?" })),
            Version(With(round with { Failures = [.. round.Failures, "overflow"] })),
            Version(With(round with { Failures = [] })),
            Version(With(round with { Variadic = !round.Variadic })),
            Version(With(round with { Capability = round.Capability + "x" })),
        };

        moved.Should().OnlyContain(v => v != DependencyFingerprint.CompilerVersion).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_golden_fixture_hashes_to_its_recorded_digest()
    {
        var digest = Golden();
        digest.Should().Be("12fb7beecbf3bcf7e2803c2e367920d89247f119a08a0629ea606f94f7ed7d91",
            "the fixture's full digest for re-recording is {0}", digest);
    }

    private static string Golden()
    {
        var shape = new BlockShape(
            "golden.block",
            [new PortShape("In", Pt("Number"), true),
             new PortShape("In2", Pt("Expression<Boolean>"), false) { ScopeVariables = [new("item", "$T"), new("value", "Collection<Int?>")] }],
            [new PortShape("Out", Pt("Collection<String>"), false), new PortShape("Out2", Pt("Boolean"), true)],
            [new ConfigFieldShape("Mode", ConfigFieldType.Code, true), new ConfigFieldShape("Retries", ConfigFieldType.Int, false)],
            new BlockFlags(true, false, true, false, true),
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [Port("Item"), Port("Index", "Number")] },
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [Port("Result", "String"), Port("Extra")] },
            "plugin@3",
            "WorkflowId",
            ["Body"],
            [new WorkflowTypeParameter("T", ["Numeric", "Comparable"]), new WorkflowTypeParameter("R")]);
        var options = new CompilationOptions
        {
            TypeRegistry = new WorkflowTypeRegistry(
                [new WorkflowTypeRecord("Length", typeof(string)), new WorkflowTypeRecord("Meters", typeof(string)) { WidensTo = ["Length"], Aliases = ["m"] }]),
            NamespaceRegistrations = [HostNamespaceRegistration.For(new StubNamespace("device"))],
        };
        var environment = new NodePortEnvironmentBuilder()
            .Add(CanvasPath.Root.Child("loop", "Body"), "call",
                NodePortResolution.Resolved("child-wf", Sig(Port("Greeting", "String", required: true))))
            .Add(CanvasPath.Root, "missing", NodePortResolution.Failed("no such workflow"))
            .Build();

        return DependencyFingerprint.Compute([shape], options, environment);
    }

    [Fact]
    public void P1_duplicate_output_parameter_order_changes_the_verdict_and_now_the_hash_too()
    {
        const string childId = "11111111-1111-1111-1111-111111111111";
        var callerGraph = Graph(
            [StartNode(), Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId }),
             Node("sink", "deliver")],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "sink", ("x", "In"))]);
        BlockShape[] CallerShapes() =>
        [
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]),
            Shape("deliver", inputs: [("In", PortValueType.Number, true)]),
        ];

        CanvasSignature.TryRead(null, """[{"name":"x","type":"Number"},{"name":"x","type":"String"}]""", out var numberFirst, out _)
            .Should().BeTrue();
        CanvasSignature.TryRead(null, """[{"name":"x","type":"String"},{"name":"x","type":"Number"}]""", out var stringFirst, out _)
            .Should().BeTrue();

        var envA = new NodePortEnvironmentBuilder().Add(CanvasPath.Root, "sub", NodePortResolution.Resolved(childId, numberFirst!)).Build();
        var envB = new NodePortEnvironmentBuilder().Add(CanvasPath.Root, "sub", NodePortResolution.Resolved(childId, stringFirst!)).Build();

        var a = WorkflowCompiler.Compile(callerGraph, Catalogue(CallerShapes()), CanvasSignature.Empty, environment: envA);
        var b = WorkflowCompiler.Compile(callerGraph, Catalogue(CallerShapes()), CanvasSignature.Empty, environment: envB);
        a.Succeeded.Should().NotBe(b.Succeeded, "first-wins on a duplicate name makes the order a verdict input");

        DependencyFingerprint.Compute(a.BoundShapes.Values, new CompilationOptions(), envA)
            .Should().NotBe(DependencyFingerprint.Compute(b.BoundShapes.Values, new CompilationOptions(), envB));
    }

    [Fact]
    public void P2_duplicate_block_output_port_order_changes_the_verdict_and_now_the_hash_too()
    {
        var graph = Graph([StartNode(), Node("src", "src"), Node("sink", "deliver")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "sink", ("Out", "In"))]);
        BlockShape SrcDeclaring(PortValueType first, PortValueType second) => new("src", [],
            [new PortShape("Out", first, false), new PortShape("Out", second, false)], [], default,
            NoSubCanvases, NoSubCanvases, null);
        var deliver = Shape("deliver", inputs: [("In", PortValueType.Number, true)]);
        var start = Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId);

        var a = WorkflowCompiler.Compile(graph,
            Catalogue(start, SrcDeclaring(PortValueType.Number, PortValueType.String), deliver), CanvasSignature.Empty);
        var b = WorkflowCompiler.Compile(graph,
            Catalogue(start, SrcDeclaring(PortValueType.String, PortValueType.Number), deliver), CanvasSignature.Empty);
        a.Succeeded.Should().NotBe(b.Succeeded);

        Hash([.. a.BoundShapes.Values]).Should().NotBe(Hash([.. b.BoundShapes.Values]));
    }

    [Fact]
    public void Port_order_among_distinct_names_now_moves_the_hash_since_nothing_sorts_them()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var shy = Port("a\u00ADb");
            var plain = Port("ab");

            Hash(Raw(inputs: [shy, plain])).Should().NotBe(Hash(Raw(inputs: [plain, shy])));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Port_order_among_names_differing_only_by_ascii_case_now_moves_the_hash_too()
    {
        var upper = Port("IN");
        var lower = Port("in");

        Hash(Raw(inputs: [upper, lower])).Should().NotBe(Hash(Raw(inputs: [lower, upper])));
    }

    [Fact]
    public void D1_block_input_declaration_order_is_a_verdict_input_the_digest_now_sees()
    {
        var graph = Graph([StartNode(), Node("n", "twoin")], [Edge("e1", "start", "n")]);
        var aFirst = Shape("twoin", inputs: [("A", PortValueType.Number, true), ("B", PortValueType.Number, true)]);
        var bFirst = Shape("twoin", inputs: [("B", PortValueType.Number, true), ("A", PortValueType.Number, true)]);

        var a = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), aFirst), CanvasSignature.Empty);
        var b = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), bFirst), CanvasSignature.Empty);

        a.Diagnostics.Select(d => d.PortName).Should().NotEqual(b.Diagnostics.Select(d => d.PortName),
            "RequiredInputUnfed is reported once per required, unfed port in the shape's own declared order");
        Hash([.. a.BoundShapes.Values]).Should().NotBe(Hash([.. b.BoundShapes.Values]));
    }

    [Fact]
    public void D2_config_field_declaration_order_is_a_verdict_input_the_digest_now_sees()
    {
        BlockShape Relay((string, ConfigFieldType, bool)[] fields) => Shape("relay", configFields: fields);
        var graph = Graph([StartNode(), Node("n", "relay")], [Edge("e1", "start", "n")]);
        var aFirst = Relay([("X", ConfigFieldType.String, true), ("Y", ConfigFieldType.String, true)]);
        var bFirst = Relay([("Y", ConfigFieldType.String, true), ("X", ConfigFieldType.String, true)]);

        var a = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), aFirst), CanvasSignature.Empty);
        var b = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), bFirst), CanvasSignature.Empty);

        a.Diagnostics.Select(d => d.PortName).Should().NotEqual(b.Diagnostics.Select(d => d.PortName),
            "RequiredConfigMissing is reported once per required, unfilled field in the shape's own declared order");
        Hash([.. a.BoundShapes.Values]).Should().NotBe(Hash([.. b.BoundShapes.Values]));
    }

    [Fact]
    public void D3_environment_signature_input_order_is_a_verdict_input_the_digest_now_sees()
    {
        const string childId = "22222222-2222-2222-2222-222222222222";
        var graph = Graph([StartNode(), Node("n", "subrun", new { WorkflowId = childId })], [Edge("e1", "start", "n")]);
        var shape = Shape("subrun", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
            inputs: [("WorkflowId", PortValueType.String, true)]);

        var abOrder = new NodePortEnvironmentBuilder().Add(CanvasPath.Root, "n",
            NodePortResolution.Resolved(childId, Sig(Port("A", "Number", required: true), Port("B", "Number", required: true)))).Build();
        var baOrder = new NodePortEnvironmentBuilder().Add(CanvasPath.Root, "n",
            NodePortResolution.Resolved(childId, Sig(Port("B", "Number", required: true), Port("A", "Number", required: true)))).Build();

        var a = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape), CanvasSignature.Empty, environment: abOrder);
        var b = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape), CanvasSignature.Empty, environment: baOrder);

        a.Diagnostics.Select(d => d.PortName).Should().NotEqual(b.Diagnostics.Select(d => d.PortName),
            "the unfed diagnostics for A and B follow the resolved signature's own declared Inputs order");
        DependencyFingerprint.Compute(a.BoundShapes.Values, new CompilationOptions(), abOrder)
            .Should().NotBe(DependencyFingerprint.Compute(b.BoundShapes.Values, new CompilationOptions(), baOrder));
    }

    [Fact]
    public void D4_sub_canvas_input_order_reaches_the_compiled_signature_and_now_the_hash()
    {
        var node = Node("box", "box");
        BlockShape Container(params PortShape[] ins) => new("box", [], [], [], new BlockFlags(false, false, false, true, false),
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = ins },
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] }, null,
            SubCanvasNames: ["Body"]);
        var aFirst = Container(new("A", PortValueType.Number, true), new("B", PortValueType.Number, true));
        var bFirst = Container(new("B", PortValueType.Number, true), new("A", PortValueType.Number, true));

        var sigA = CanvasSignature.ForSubCanvas(node, aFirst, "Body", []);
        var sigB = CanvasSignature.ForSubCanvas(node, bFirst, "Body", []);

        sigA.Inputs.Select(p => p.Name).Should().Equal("A", "B");
        sigB.Inputs.Select(p => p.Name).Should().Equal("B", "A");
        Hash(aFirst).Should().NotBe(Hash(bFirst));
    }

    [Fact]
    public void G9_sub_canvas_output_declaration_order_is_a_verdict_input_the_digest_now_sees()
    {
        (string, string) Run(params string[] order)
        {
            var shape = new BlockShape("box", [], [], [], new BlockFlags(false, false, false, true, false),
                new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] },
                new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [.. order.Select(n => new PortShape(n, PortValueType.Number, true))] },
                null, SubCanvasNames: ["Body"]);
            var node = Node("n", "box");
            node.SubCanvases = new() { ["Body"] = Graph([StartNode(), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)], [Edge("b1", "start", "out")]) };
            var r = WorkflowCompiler.Compile(Graph([StartNode(), node], [Edge("e1", "start", "n")]),
                Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId), shape),
                CanvasSignature.Empty);
            return (Hash([.. r.BoundShapes.Values]), string.Join("|", r.Diagnostics.Select(d => d.PortName)));
        }

        var ab = Run("A", "B");
        var ba = Run("B", "A");

        ab.Item2.Should().NotBe(ba.Item2, "the inner WorkflowOutput node's RequiredInputUnfed follows declared order");
        ab.Item1.Should().NotBe(ba.Item1);
    }

    [Fact]
    public void Shape_order_among_block_type_ids_differing_only_by_ascii_case_is_still_order_independent()
    {
        var a = Raw(id: "FOO");
        var b = Raw(id: "foo");

        Hash(a, b).Should().Be(Hash(b, a));
    }

    [Fact]
    public void Namespace_order_among_names_tied_under_current_culture_is_still_ordinal()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var shyFirst = Options(new StubNamespace("a­b"), new StubNamespace("ab"));
            var plainFirst = Options(new StubNamespace("ab"), new StubNamespace("a­b"));

            HashOpt(shyFirst, Raw()).Should().Be(HashOpt(plainFirst, Raw()));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Type_parameter_declaration_order_is_a_verdict_input_the_digest_sees()
    {
        WorkflowTypeParameter[] forward = [new("T"), new("R")];

        Hash(Raw(typeParameters: forward)).Should().NotBe(Hash(Raw(typeParameters: [.. forward.Reverse()])));
    }

    [Fact]
    public void A_sub_canvas_declaration_order_is_a_verdict_input_the_digest_now_sees()
    {
        var firstThenSecond = BlockShape.From(new TwoBranchFactory(["First", "Second"]));
        var secondThenFirst = BlockShape.From(new TwoBranchFactory(["Second", "First"]));

        var a = WorkflowCompiler.Compile(TwoBranchNoWiring(), Catalogue([.. TwoBranchOthers(), firstThenSecond]), CanvasSignature.Empty);
        var b = WorkflowCompiler.Compile(TwoBranchNoWiring(), Catalogue([.. TwoBranchOthers(), secondThenFirst]), CanvasSignature.Empty);

        a.Diagnostics.Where(d => d.RuleId == CompilationRules.RequiredInputUnfed)
            .Select(d => d.CanvasPath![0].BranchName).Should().Equal("First", "Second");
        b.Diagnostics.Where(d => d.RuleId == CompilationRules.RequiredInputUnfed)
            .Select(d => d.CanvasPath![0].BranchName).Should().Equal("Second", "First");
        Hash([.. a.BoundShapes.Values]).Should().NotBe(Hash([.. b.BoundShapes.Values]));
    }

    [Fact]
    public void P2_a_non_empty_map_without_declared_names_is_refused()
    {
        var map = ImmutableDictionary.CreateRange(
            new[] { KeyValuePair.Create("First", (IReadOnlyList<PortShape>)[new PortShape("Out", PortValueType.String, false)]) });

        var act = () => new BlockShape("two-branch", [], [], [], default, NoSubCanvases, map, null);

        act.Should().Throw<ArgumentException>().WithParameterName("SubCanvasNames");
    }

    [Fact]
    public void P2_an_immutable_dictionary_with_declared_names_compiles_and_hashes_like_a_plain_dictionary()
    {
        var plain = new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["First"] = [new PortShape("Out", PortValueType.String, false)],
            ["Second"] = [new PortShape("Out", PortValueType.String, false)],
        };
        var immutable = ImmutableDictionary.CreateRange(plain);
        var noInputs = new Dictionary<string, IReadOnlyList<PortShape>> { ["First"] = [], ["Second"] = [] };

        BlockShape Build(IReadOnlyDictionary<string, IReadOnlyList<PortShape>> map) => new(
            "two-branch", [],
            [new PortShape("Picked", PortValueType.String, false)],
            [], new BlockFlags(false, false, false, true, false), noInputs, map, null, SubCanvasNames: ["First", "Second"]);

        var a = WorkflowCompiler.Compile(TwoBranchNoWiring(), Catalogue([.. TwoBranchOthers(), Build(plain)]), CanvasSignature.Empty);
        var b = WorkflowCompiler.Compile(TwoBranchNoWiring(), Catalogue([.. TwoBranchOthers(), Build(immutable)]), CanvasSignature.Empty);

        a.Succeeded.Should().BeTrue();
        b.Succeeded.Should().BeTrue();
        Hash([.. a.BoundShapes.Values]).Should().Be(Hash([.. b.BoundShapes.Values]));
    }

    [Fact]
    public void K8_outputs_mutated_after_construction_does_not_move_the_hash()
    {
        var outs = new List<PortShape> { new("Out", PortValueType.String, false) };
        var shape = new BlockShape("src", [], outs, [], default, NoSubCanvases, NoSubCanvases, null);
        var before = Hash(shape);
        outs[0] = new PortShape("Out", PortValueType.Number, false);

        Hash(shape).Should().Be(before);
    }

    [Fact]
    public void A_case_insensitive_sub_canvas_map_no_longer_lets_a_lookup_match_by_the_wrong_case()
    {
        var ignoreCase = new Dictionary<string, IReadOnlyList<PortShape>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Then"] = [Port("Out")],
        };
        var noInputs = new Dictionary<string, IReadOnlyList<PortShape>> { ["Then"] = [] };
        var shape = new BlockShape("box", [], [], [], default, noInputs, ignoreCase, null, SubCanvasNames: ["Then"]);

        shape.SubCanvasOutputs.TryGetValue("then", out _).Should().BeFalse(
            "SubCanvasOutputs is normalised to an ordinal comparer regardless of what the caller built it with");
    }

    [Fact]
    public void C_a_namespace_checks_list_mutated_after_init_no_longer_reaches_the_options()
    {
        var mutatedAfterInit = new List<WorkflowExpressionNamespace> { HostNamespaceRegistration.For(new NsCheck("device", NamespacePathCheck.Yes)) };
        var options = new CompilationOptions { NamespaceRegistrations = mutatedAfterInit };

        mutatedAfterInit.Add(HostNamespaceRegistration.For(new NsCheck("scene", NamespacePathCheck.No)));

        options.NamespaceRegistrations.Should().ContainSingle(
            "the registrations are copied when options are initialized");
    }

    [Fact]
    public void G_null_namespace_checks_is_refused_with_argument_null_exception()
    {
        var act = () => new CompilationOptions { NamespaceRegistrations = null! };

        act.Should().Throw<ArgumentNullException>().WithParameterName("value");
    }

    [Fact]
    public void K18_namespace_is_pinned_at_init_for_both_readers()
    {
        var flipping = new FlippingNamespace();
        var options = new CompilationOptions
        {
            NamespaceRegistrations = [new WorkflowExpressionNamespace("dev", 1, flipping,
                (_, _) => throw new InvalidOperationException("Fingerprinting does not create resolvers."))]
        };
        var graph = Graph([StartNode(), Node("n1", "notify", new { Message = "expr:${device.gone.temperature}" })],
            [Edge("e1", "start", "n1")]);

        var r = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty, options);
        var h = DependencyFingerprint.Compute([], options, NodePortEnvironment.Empty);
        var hDev = DependencyFingerprint.Compute([], RegistrationOptions("dev", keySegments: 1), NodePortEnvironment.Empty);

        r.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace,
            "the static check remains paired with the pinned 'dev' registration");
        h.Should().Be(hDev, "the fingerprint must hash the same pinned name ExpressionResolution matched against");
    }

    private sealed class FlippingNamespace : IExpressionNamespaceStaticCheck
    {
        private int reads;
        public string Namespace => reads++ == 0 ? "dev" : "device";
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => NamespacePathCheckAnswer.No;
    }

    [Fact]
    public void E_duplicate_config_field_order_changes_the_hash()
    {
        BlockShape Relay(params ConfigFieldShape[] fields) => new("relay", [], [], fields, default, NoSubCanvases, NoSubCanvases, null);
        var graph = Graph(
            [StartNode(), Node("r", "relay", new Dictionary<string, object?> { ["n"] = 2 }, passthrough: ["n"]), Node("sink", "deliver")],
            [Edge("e1", "start", "r"), Edge("e2", "r", "sink", ("n", "In"))]);
        var deliver = Shape("deliver", inputs: [("In", PortValueType.Number, true)]);
        var start = Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId);

        var a = WorkflowCompiler.Compile(graph, Catalogue(start, Relay(new("n", ConfigFieldType.Int, false), new("n", ConfigFieldType.Number, false)), deliver), CanvasSignature.Empty);
        var b = WorkflowCompiler.Compile(graph, Catalogue(start, Relay(new("n", ConfigFieldType.Number, false), new("n", ConfigFieldType.Int, false)), deliver), CanvasSignature.Empty);

        a.Succeeded.Should().BeTrue();
        b.Succeeded.Should().BeTrue();
        Hash([.. a.BoundShapes.Values]).Should().NotBe(Hash([.. b.BoundShapes.Values]));
    }

    [Fact]
    public void F_duplicate_sub_canvas_input_order_changes_the_verdict_and_the_hash()
    {
        BlockShape Container(params PortShape[] ins) => new("box", [], [], [], new BlockFlags(false, false, false, true, false),
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = ins },
            new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] }, null,
            SubCanvasNames: ["Body"]);
        WorkflowDefinition Boxed()
        {
            var body = Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("sink", "deliver")],
                [Edge("be", "in", "sink", ("v", "In"))]);
            var node = Node("box", "box");
            node.SubCanvases = new() { ["Body"] = body };
            return Graph([StartNode(), node], [Edge("e1", "start", "box")]);
        }

        var deliver = Shape("deliver", inputs: [("In", PortValueType.String, true)]);
        var start = Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId);
        var a = WorkflowCompiler.Compile(Boxed(), Catalogue(start, deliver, Container(new("v", PortValueType.Boolean, false), new("v", PortValueType.String, false))), CanvasSignature.Empty);
        var b = WorkflowCompiler.Compile(Boxed(), Catalogue(start, deliver, Container(new("v", PortValueType.String, false), new("v", PortValueType.Boolean, false))), CanvasSignature.Empty);

        a.Succeeded.Should().NotBe(b.Succeeded);
        Hash([.. a.BoundShapes.Values]).Should().NotBe(Hash([.. b.BoundShapes.Values]));
    }

    private sealed class TwoBranchFactory(string[] subCanvasOrder) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "two-branch";
        public string DisplayName => "two-branch";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("R")];
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("Picked", "Picked", PortValueType.OfDeclaration("$R"), false)];
        public bool OnlyDrivesItsSubCanvases => true;
        public IReadOnlyCollection<string> SubCanvasNames => subCanvasOrder;
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) => [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) =>
            [new BlockPortDefinition("Out", "Out", PortValueType.OfDeclaration("$R"), true)];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private static WorkflowDefinition TwoBranchNoWiring()
    {
        var node = Node("n", "two-branch");
        node.SubCanvases = new()
        {
            ["First"] = Graph([Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)]),
            ["Second"] = Graph([Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)]),
        };
        return Graph([StartNode(), node], [Edge("e1", "start", "n")]);
    }

    private static BlockShape[] TwoBranchOthers() =>
    [
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
    ];

    private sealed class NsCheck(string ns, NamespacePathCheck answer) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => ns;
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(answer, "String");
    }

    [Theory]
    [MemberData(nameof(TermNames))]
    public void Each_term_the_compiler_reads_moves_the_hash(string term)
    {
        var (a, b) = DifferingTerms[term];

        a().Should().NotBe(b(), $"'{term}' is a term a verdict turns on");
    }

    [Theory]
    [MemberData(nameof(SwapNames))]
    public void Moving_a_value_from_one_field_to_another_moves_the_hash(string swap)
    {
        var (a, b) = FieldSwaps[swap];

        a().Should().NotBe(b(), $"'{swap}' puts the same text in two different places");
    }

    [Theory]
    [MemberData(nameof(CollisionNames))]
    public void Two_inputs_a_delimiter_joined_stream_conflated_now_hash_apart(string collision)
    {
        var (a, b) = Collisions[collision];

        a().Should().NotBe(b(), $"'{collision}' is a measured collision of the delimiter-joined format");
    }

    public static TheoryData<string> TermNames() => Names(DifferingTerms);

    public static TheoryData<string> SwapNames() => Names(FieldSwaps);

    public static TheoryData<string> CollisionNames() => Names(Collisions);

    private static readonly Dictionary<string, (Func<string> A, Func<string> B)> DifferingTerms = new(StringComparer.Ordinal)
    {
        ["BlockShape.BlockTypeId"] = (() => Hash(Raw(id: "a")), () => Hash(Raw(id: "b"))),
        ["BlockShape.Inputs added"] = (() => Hash(Raw()), () => Hash(Raw(inputs: [Port("In")]))),
        ["BlockShape.Outputs added"] = (() => Hash(Raw()), () => Hash(Raw(outputs: [Port("Out")]))),
        ["BlockShape.RunsWorkflowFrom"] =
            (() => Hash(Raw(runsWorkflowFrom: "WorkflowId")), () => Hash(Raw(runsWorkflowFrom: "TemplateId"))),
        ["BlockShape.RunsWorkflowFrom null vs empty"] =
            (() => Hash(Raw(runsWorkflowFrom: null)), () => Hash(Raw(runsWorkflowFrom: ""))),
        ["BlockShape.TypeParameters"] =
            (() => Hash(Raw(typeParameters: [new("T")])), () => Hash(Raw(typeParameters: []))),
        ["WorkflowTypeParameter.Name"] =
            (() => Hash(Raw(typeParameters: [new("T")])), () => Hash(Raw(typeParameters: [new("R")]))),
        ["WorkflowTypeParameter.Constraints"] =
            (() => Hash(Raw(typeParameters: [new("T", ["Numeric"])])), () => Hash(Raw(typeParameters: [new("T", ["Comparable"])]))),
        ["BlockShape.SubCanvasInputs name"] = (
            () => Hash(Raw(subIn: SubCanvas("Then", Port("Value")))),
            () => Hash(Raw(subIn: SubCanvas("Else", Port("Value"))))),
        ["BlockShape.SubCanvasInputs port"] = (
            () => Hash(Raw(subIn: SubCanvas("Then", Port("Value", "Number")))),
            () => Hash(Raw(subIn: SubCanvas("Then", Port("Value", "String"))))),
        ["BlockShape.SubCanvasOutputs port"] = (
            () => Hash(Raw(subOut: SubCanvas("Then", Port("Value", "Number")))),
            () => Hash(Raw(subOut: SubCanvas("Then", Port("Value", "String"))))),
        ["PortShape.Name"] = (() => Hash(Raw(inputs: [Port("In")])), () => Hash(Raw(inputs: [Port("Other")]))),
        ["PortShape.Type"] =
            (() => Hash(Raw(inputs: [Port("In", "Number")])), () => Hash(Raw(inputs: [Port("In", "String")]))),
        ["PortShape.IsRequired"] = (
            () => Hash(Raw(inputs: [Port("In", required: true)])),
            () => Hash(Raw(inputs: [Port("In", required: false)]))),
        ["PortShape.SecretValues"] = (
            () => Hash(Raw(inputs: [Port("In", "Map<String,String>")])),
            () => Hash(Raw(inputs: [Port("In", "Map<String,String>") with { SecretValues = true }]))),
        ["PortShape.ScopeVariables"] = (
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>")])),
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>") with { ScopeVariables = [new("item", "Int")] }]))),
        ["PortShape.ScopeVariables order"] = (
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>") with { ScopeVariables = [new("item", "Int"), new("value", "Int")] }])),
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>") with { ScopeVariables = [new("value", "Int"), new("item", "Int")] }]))),
        ["ExpressionScopeVariable.Name"] = (
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>") with { ScopeVariables = [new("item", "Int")] }])),
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>") with { ScopeVariables = [new("value", "Int")] }]))),
        ["ExpressionScopeVariable.Type"] = (
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>") with { ScopeVariables = [new("item", "Int")] }])),
            () => Hash(Raw(inputs: [Port("In", "Expression<Boolean>") with { ScopeVariables = [new("item", "String")] }]))),
        ["ConfigFieldShape.Name"] = (
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.String, false)])),
            () => Hash(Raw(fields: [new ConfigFieldShape("n", ConfigFieldType.String, false)]))),
        ["ConfigFieldShape.Type"] = (
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.String, false)])),
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.Int, false)]))),
        ["ConfigFieldShape.AllowedValues names"] = (
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.Enum, false, AllowedValues: ["a", "b"])])),
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.Enum, false, AllowedValues: ["a", "c"])]))),
        ["ConfigFieldShape.AllowedValues order"] = (
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.Enum, false, AllowedValues: ["a", "b"])])),
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.Enum, false, AllowedValues: ["b", "a"])]))),
        ["ConfigFieldShape.AllowedValues split"] = (
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.Enum, false, AllowedValues: ["ab", "c"])])),
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.Enum, false, AllowedValues: ["a", "bc"])]))),
        ["ConfigFieldShape.Required"] = (
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.String, false)])),
            () => Hash(Raw(fields: [new ConfigFieldShape("m", ConfigFieldType.String, true)]))),
        ["BlockFlags.HasDynamicPorts"] =
            (() => Hash(Raw()), () => Hash(Raw(flags: new BlockFlags(HasDynamicPorts: true, false, false, false, false)))),
        ["BlockFlags.SupportsDeclaredOutputs"] =
            (() => Hash(Raw()), () => Hash(Raw(flags: new BlockFlags(false, SupportsDeclaredOutputs: true, false, false, false)))),
        ["BlockFlags.CanSuspend"] =
            (() => Hash(Raw()), () => Hash(Raw(flags: new BlockFlags(false, false, CanSuspend: true, false, false)))),
        ["BlockFlags.OnlyDrivesItsSubCanvases"] =
            (() => Hash(Raw()), () => Hash(Raw(flags: new BlockFlags(false, false, false, OnlyDrivesItsSubCanvases: true, false)))),
        ["BlockFlags.PortsAreResolvedElsewhere"] =
            (() => Hash(Raw()), () => Hash(Raw(flags: new BlockFlags(false, false, false, false, PortsAreResolvedElsewhere: true)))),
        ["BlockFlags.DeclaredOutputsCollect"] =
            (() => Hash(Raw()), () => Hash(Raw(flags: new BlockFlags(false, false, false, false, false, DeclaredOutputsCollect: true)))),
        ["WideningTable vocabulary"] = (
            () => Hash(Raw()),
            () => HashRecords(new WorkflowTypeRecord("Widget", typeof(string)))),
        ["WideningTable relation, same vocabulary"] = (
            () => HashRecords(new WorkflowTypeRecord("Y", typeof(string)), new WorkflowTypeRecord("X", typeof(string)) { WidensTo = ["Y"] }),
            () => HashRecords(new WorkflowTypeRecord("X", typeof(string)), new WorkflowTypeRecord("Y", typeof(string)) { WidensTo = ["X"] })),
        ["WideningTable alias, same vocabulary"] = (
            () => HashRecords(new WorkflowTypeRecord("Unit", typeof(string))),
            () => HashRecords(new WorkflowTypeRecord("Unit", typeof(string)) { Aliases = ["u"] })),
        ["CompilationOptions.NamespaceRegistrations added"] =
            (() => HashOpt(Options(), Raw()), () => HashOpt(Options(new StubNamespace("device")), Raw())),
        ["CompilationOptions.NamespaceRegistrations name"] = (
            () => HashOpt(Options(new StubNamespace("device")), Raw()),
            () => HashOpt(Options(new StubNamespace("scene")), Raw())),
        ["Environment entry added"] = (
            () => Hash(Raw()),
            () => HashIn(Env(CanvasPath.Root, "sub", NodePortResolution.Resolved("c", CanvasSignature.Empty)), Raw())),
        ["Environment NodeId"] = (
            () => HashIn(Env(CanvasPath.Root, "one", NodePortResolution.Resolved("c", CanvasSignature.Empty)), Raw()),
            () => HashIn(Env(CanvasPath.Root, "two", NodePortResolution.Resolved("c", CanvasSignature.Empty)), Raw())),
        ["Environment CanvasPath"] = (
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("c", CanvasSignature.Empty)), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("p", "Then"), "n", NodePortResolution.Resolved("c", CanvasSignature.Empty)), Raw())),
        ["Environment Reference"] = (
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("one", CanvasSignature.Empty)), Raw()),
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("two", CanvasSignature.Empty)), Raw())),
        ["Environment FailureReason"] = (
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Failed("one")), Raw()),
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Failed("two")), Raw())),
        ["Environment Failed vs Resolved"] = (
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Failed("gone")), Raw()),
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("c", CanvasSignature.Empty)), Raw())),
        ["Environment signature inputs"] = (
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("c", Sig(Port("G", "String")))), Raw()),
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("c", Sig(Port("G", "Number")))), Raw())),
        ["Environment signature outputs"] = (
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("c", SigOut(Port("G", "String")))), Raw()),
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("c", SigOut(Port("G", "Number")))), Raw())),
    };

    private static readonly Dictionary<string, (Func<string> A, Func<string> B)> FieldSwaps = new(StringComparer.Ordinal)
    {
        ["port name and port type"] = (
            () => Hash(Raw(inputs: [Port("X", "Y")])), () => Hash(Raw(inputs: [Port("Y", "X")]))),
        ["inputs and outputs"] = (
            () => Hash(Raw(inputs: [Port("P")])), () => Hash(Raw(outputs: [Port("P")]))),
        ["sub-canvas inputs and outputs"] = (
            () => Hash(Raw(subIn: SubCanvas("Then", Port("P")))),
            () => Hash(Raw(subOut: SubCanvas("Then", Port("P"))))),
        ["block type id and runs-workflow-from"] = (
            () => Hash(Raw(id: "a", runsWorkflowFrom: "b")), () => Hash(Raw(id: "b", runsWorkflowFrom: "a"))),
        ["canvas segment node id and branch name"] = (
            () => HashIn(Env(CanvasPath.Root.Child("a", "b"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("b", "a"), "n", Resolved), Raw())),
        ["canvas node id and entry node id"] = (
            () => HashIn(Env(CanvasPath.Root.Child("a", "b"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("n", "b"), "a", Resolved), Raw())),
    };

    private static readonly Dictionary<string, (Func<string> A, Func<string> B)> Collisions = new(StringComparer.Ordinal)
    {
        ["one port name absorbs a second port"] = (
            () => Hash(Raw(inputs: [Port("m", "t"), Port("n", "t")])),
            () => Hash(Raw(inputs: [Port("m:t:False,n", "t")]))),
        ["one block type id absorbs a second shape"] = (
            () => Hash(Raw(id: "a"), Raw(id: "b")),
            () => Hash(Raw(id: "a||||" + default(BlockFlags).ToString() + "|||\nb"))),
        ["one config field name absorbs a second field"] = (
            () => Hash(Raw(fields: [Field("m"), Field("n")])), () => Hash(Raw(fields: [Field("m:String:False,n")]))),
        ["one sub-canvas name absorbs a second sub-canvas"] = (
            () => Hash(Raw(subIn: SubCanvases("A", "B"))), () => Hash(Raw(subIn: SubCanvases("A=||B")))),
        ["one constraint name absorbs a second"] = (
            () => Hash(Raw(typeParameters: [new("T", ["a", "b"])])), () => Hash(Raw(typeParameters: [new("T", ["a,b"])]))),
        ["one canvas segment absorbs the next"] = (
            () => HashIn(Env(CanvasPath.Root.Child("x", "Then").Child("Body", "b"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("x", "Then//Body->b"), "n", Resolved), Raw())),
        ["one canvas segment absorbs two more"] = (
            () => HashIn(Env(CanvasPath.Root.Child("a", "b").Child("c", "d"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("a", "b//c->d"), "n", Resolved), Raw())),
        ["a branch name absorbs the node id"] = (
            () => HashIn(Env(CanvasPath.Root.Child("p", "q#z"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("p", "q"), "z#n", Resolved), Raw())),
        ["adjacent port name and type"] = (
            () => Hash(Raw(inputs: [Port("AB", "C")])), () => Hash(Raw(inputs: [Port("A", "BC")]))),
        ["adjacent canvas node id and branch name"] = (
            () => HashIn(Env(CanvasPath.Root.Child("ab", "c"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("a", "bc"), "n", Resolved), Raw())),
        ["a null reference against an empty one"] = (
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved(null, CanvasSignature.Empty)), Raw()),
            () => HashIn(Env(CanvasPath.Root, "n", NodePortResolution.Resolved("", CanvasSignature.Empty)), Raw())),
    };

    private static readonly NodePortResolution Resolved =
        NodePortResolution.Resolved("c", CanvasSignature.Empty);

    private static TheoryData<string> Names(Dictionary<string, (Func<string>, Func<string>)> cases) => Names(cases.Keys);

    private static TheoryData<string> Names(IEnumerable<string> keys)
    {
        var data = new TheoryData<string>();
        foreach (var name in keys.OrderBy(n => n, StringComparer.Ordinal))
            data.Add(name);

        return data;
    }

    private static string Hash(params BlockShape[] shapes) =>
        DependencyFingerprint.Compute(shapes, new CompilationOptions(), NodePortEnvironment.Empty);

    private static string HashIn(NodePortEnvironment environment, params BlockShape[] shapes) =>
        DependencyFingerprint.Compute(shapes, new CompilationOptions(), environment);

    private static string HashOpt(CompilationOptions options, params BlockShape[] shapes) =>
        DependencyFingerprint.Compute(shapes, options, NodePortEnvironment.Empty);

    private static CompilationOptions Options(params IExpressionNamespaceStaticCheck[] checks) =>
        new() { NamespaceRegistrations = checks.Select(check => HostNamespaceRegistration.For(check)).ToArray() };

    private static CompilationOptions RegistrationOptions(string name, int keySegments = 2,
        Func<ExpressionNamespaceScope, CancellationToken, Task<IExpressionNamespaceResolver>>? factory = null,
        int answerVersion = 0) => new()
    {
        NamespaceRegistrations =
        [
            new WorkflowExpressionNamespace(name, keySegments, new StubNamespace(name),
                factory ?? ((_, _) => throw new InvalidOperationException("Fingerprinting does not create namespace resolvers.")))
            {
                AnswerVersion = answerVersion,
            }
        ]
    };

    private static PortValueType Pt(string name) => PortValueType.Of(name);

    private static PortShape Port(string name, string type = "String", bool required = false) =>
        new(name, PortValueType.Of(type), required);

    private static ConfigFieldShape Field(string name) => new(name, ConfigFieldType.String, false);

    private static CanvasSignature Sig(params PortShape[] inputs) => new(inputs, []);

    private static CanvasSignature SigOut(params PortShape[] outputs) => new([], outputs);

    private static NodePortEnvironment Env(CanvasPath path, string nodeId, NodePortResolution resolution) =>
        new NodePortEnvironmentBuilder().Add(path, nodeId, resolution).Build();

    private static IReadOnlyDictionary<string, IReadOnlyList<PortShape>> SubCanvas(
        string name, params PortShape[] ports) =>
        new Dictionary<string, IReadOnlyList<PortShape>> { [name] = ports };

    private static IReadOnlyDictionary<string, IReadOnlyList<PortShape>> SubCanvases(params string[] names) =>
        names.ToDictionary(n => n, _ => (IReadOnlyList<PortShape>)[], StringComparer.Ordinal);

    private static BlockShape Raw(
        string id = "a",
        IReadOnlyList<PortShape>? inputs = null,
        IReadOnlyList<PortShape>? outputs = null,
        IReadOnlyList<ConfigFieldShape>? fields = null,
        BlockFlags flags = default,
        IReadOnlyDictionary<string, IReadOnlyList<PortShape>>? subIn = null,
        IReadOnlyDictionary<string, IReadOnlyList<PortShape>>? subOut = null,
        string? source = null,
        string? runsWorkflowFrom = null,
        IReadOnlyList<WorkflowTypeParameter>? typeParameters = null)
    {
        var names = (subIn?.Keys ?? []).Union(subOut?.Keys ?? [], StringComparer.Ordinal).ToList();
        return new(id, inputs ?? [], outputs ?? [], fields ?? [], flags,
            Filled(subIn, names), Filled(subOut, names), source, runsWorkflowFrom,
            names.Count == 0 ? null : names, typeParameters);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PortShape>> Filled(
        IReadOnlyDictionary<string, IReadOnlyList<PortShape>>? source, IReadOnlyList<string> names)
    {
        var map = source is null
            ? new Dictionary<string, IReadOnlyList<PortShape>>(StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<PortShape>>(source, StringComparer.Ordinal);
        foreach (var name in names)
            map.TryAdd(name, []);

        return map;
    }

    private sealed class TypedHelper(string term) : IHelperResolver
    {
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.ToDictionary(p => p, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, term)));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private sealed class StubNamespace(string name) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => name;

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "String");
    }

    private static readonly Type[] CoveredTypes =
    [
        typeof(BlockShape), typeof(PortShape), typeof(ExpressionScopeVariable), typeof(ConfigFieldShape),
        typeof(BlockFlags), typeof(PortValueType), typeof(CanvasSignature), typeof(NodePortResolution),
        typeof(NodeKey), typeof(CanvasPath), typeof(CanvasPathSegment), typeof(CompilationOptions),
        typeof(PortTypeWideningTable), typeof(IExpressionNamespaceStaticCheck),
        typeof(WorkflowExpressionNamespace), typeof(WorkflowTypeConstructorRecord), typeof(WorkflowTypeRecord),
        typeof(WorkflowTypeVocabulary), typeof(RegisteredPortType), typeof(RegisteredPortConstructor),
        typeof(WorkflowTypeParameter),
    ];

    private enum MemberKind { Hashed, ExcludedNotHashed, Derived }

    private sealed record MemberCoverage(MemberKind Kind, string Reason, Func<string>? A = null, Func<string>? B = null);

    private static MemberCoverage Hashed((Func<string> A, Func<string> B) pair) =>
        new(MemberKind.Hashed, "", pair.A, pair.B);

    private static MemberCoverage Excluded(Func<string> a, Func<string> b, string reason) =>
        new(MemberKind.ExcludedNotHashed, reason, a, b);

    private static WorkflowTypeConstructorRecord Constructor() =>
        new("Gauge", typeof(string)) { AllowedArguments = ["Boolean"] };

    private static (Func<string> A, Func<string> B) ConstructorPair(WorkflowTypeConstructorRecord a, WorkflowTypeConstructorRecord b) => (
        () => HashRecords(a),
        () => HashRecords(b));

    private static WorkflowTypeRecord Atom() => new("Depot", typeof(string));

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo StringInfo =
        StrictJson.Info(typeof(string));

    private static string HashRecords(params WorkflowTypeRegistrationRecord[] records) =>
        HashOpt(new CompilationOptions { TypeRegistry = new WorkflowTypeRegistry(records) }, Raw());

    private static (Func<string> A, Func<string> B) AtomPair(WorkflowTypeRecord a, WorkflowTypeRecord b) =>
        (() => HashRecords(a), () => HashRecords(b));

    private static string HashPlugin(params WorkflowTypeRegistrationRecord[] records)
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:p", records);
        return HashOpt(new CompilationOptions { TypeRegistry = registry }, Raw());
    }

    private static string HashPluginLate(params WorkflowTypeRegistrationRecord[] records)
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:other", [new WorkflowTypeRecord("Spare", typeof(string))]);
        registry.Unregister("plugin:other");
        registry.Register("plugin:p", records);
        return HashOpt(new CompilationOptions { TypeRegistry = registry }, Raw());
    }

    private static (Func<string> A, Func<string> B) ConstructedPair(WorkflowTypeConstructorRecord a, WorkflowTypeConstructorRecord b) =>
        (() => HashRecords(a), () => HashRecords(b));

    private static MemberCoverage Derived(string reason) => new(MemberKind.Derived, reason);

    private static readonly Dictionary<string, MemberCoverage> MemberCoverageTable = new(StringComparer.Ordinal)
    {
        ["BlockShape.BlockTypeId"] = Hashed(DifferingTerms["BlockShape.BlockTypeId"]),
        ["BlockShape.Inputs"] = Hashed(DifferingTerms["BlockShape.Inputs added"]),
        ["BlockShape.Outputs"] = Hashed(DifferingTerms["BlockShape.Outputs added"]),
        ["BlockShape.ConfigurationFields"] = Hashed(DifferingTerms["ConfigFieldShape.Name"]),
        ["BlockShape.Flags"] = Hashed(DifferingTerms["BlockFlags.HasDynamicPorts"]),
        ["BlockShape.SubCanvasInputs"] = Hashed(DifferingTerms["BlockShape.SubCanvasInputs port"]),
        ["BlockShape.SubCanvasOutputs"] = Hashed(DifferingTerms["BlockShape.SubCanvasOutputs port"]),
        ["BlockShape.Source"] = Excluded(
            () => Hash(Raw(outputs: [Port("Out", "Number")], source: "plugin@1.0.0")),
            () => Hash(Raw(outputs: [Port("Out", "Number")], source: "plugin@2.0.0")),
            "diagnostics only (type's own remarks): a plugin re-published under a new Source with no shape "
            + "change must invalidate nothing, so hashing it would over-invalidate on every republish."),
        ["BlockShape.RunsWorkflowFrom"] = Hashed(DifferingTerms["BlockShape.RunsWorkflowFrom"]),
        ["BlockShape.TypeParameters"] = Hashed(DifferingTerms["BlockShape.TypeParameters"]),
        ["WorkflowTypeParameter.Name"] = Hashed(DifferingTerms["WorkflowTypeParameter.Name"]),
        ["WorkflowTypeParameter.Constraints"] = Hashed(DifferingTerms["WorkflowTypeParameter.Constraints"]),
        ["BlockShape.SubCanvasNames"] = Hashed(DifferingTerms["BlockShape.SubCanvasInputs name"]),

        ["PortShape.Name"] = Hashed(DifferingTerms["PortShape.Name"]),
        ["PortShape.Type"] = Hashed(DifferingTerms["PortShape.Type"]),
        ["PortShape.IsRequired"] = Hashed(DifferingTerms["PortShape.IsRequired"]),
        ["PortShape.SecretValues"] = Hashed(DifferingTerms["PortShape.SecretValues"]),
        ["PortShape.ScopeVariables"] = Hashed(DifferingTerms["PortShape.ScopeVariables"]),
        ["ExpressionScopeVariable.Name"] = Hashed(DifferingTerms["ExpressionScopeVariable.Name"]),
        ["ExpressionScopeVariable.Type"] = Hashed(DifferingTerms["ExpressionScopeVariable.Type"]),

        ["ConfigFieldShape.Name"] = Hashed(DifferingTerms["ConfigFieldShape.Name"]),
        ["ConfigFieldShape.Type"] = Hashed(DifferingTerms["ConfigFieldShape.Type"]),
        ["ConfigFieldShape.Required"] = Hashed(DifferingTerms["ConfigFieldShape.Required"]),
        ["ConfigFieldShape.AllowedValues"] = Hashed(DifferingTerms["ConfigFieldShape.AllowedValues names"]),

        ["BlockFlags.HasDynamicPorts"] = Hashed(DifferingTerms["BlockFlags.HasDynamicPorts"]),
        ["BlockFlags.SupportsDeclaredOutputs"] = Hashed(DifferingTerms["BlockFlags.SupportsDeclaredOutputs"]),
        ["BlockFlags.CanSuspend"] = Hashed(DifferingTerms["BlockFlags.CanSuspend"]),
        ["BlockFlags.OnlyDrivesItsSubCanvases"] = Hashed(DifferingTerms["BlockFlags.OnlyDrivesItsSubCanvases"]),
        ["BlockFlags.PortsAreResolvedElsewhere"] = Hashed(DifferingTerms["BlockFlags.PortsAreResolvedElsewhere"]),
        ["BlockFlags.DeclaredOutputsCollect"] = Hashed(DifferingTerms["BlockFlags.DeclaredOutputsCollect"]),

        ["PortValueType.Name"] = Hashed(DifferingTerms["PortShape.Type"]),
        ["PortValueType.ElementType"] = Derived(
            "a pure function of Name (peels a literal 'Collection<' prefix and trailing '>'): two types with "
            + "equal Name always have equal ElementType, and Name is already hashed, so this would add nothing."),
        ["PortValueType.IsExpression"] = Derived(
            "a pure function of Name (starts with 'Expression<' and ends with '>'): two types with equal Name always "
            + "agree, and Name is already hashed, so this adds nothing."),
        ["PortValueType.IsCollection"] = Derived(
            "a pure function of Name (Name starts with 'Collection<'): two types with equal Name always agree "
            + "on IsCollection, and Name is already hashed, so this adds nothing."),
        ["PortValueType.IsMap"] = Derived(
            "a pure function of Name (Name starts with 'Map<' and is not nullable): two types with equal Name always agree "
            + "on IsMap, and Name is already hashed, so this adds nothing."),
        ["PortValueType.IsDefault"] = Derived(
            "a pure function of whether the instance is default(PortValueType), which never reaches this "
            + "table: every hashed type is held by a PortShape or a widening table, and both refuse a default."),

        ["CanvasSignature.Inputs"] = Hashed(DifferingTerms["Environment signature inputs"]),
        ["CanvasSignature.Outputs"] = Hashed(DifferingTerms["Environment signature outputs"]),

        ["NodePortResolution.Reference"] = Hashed(DifferingTerms["Environment Reference"]),
        ["NodePortResolution.Signature"] = Hashed(DifferingTerms["Environment signature inputs"]),
        ["NodePortResolution.FailureReason"] = Hashed(DifferingTerms["Environment FailureReason"]),
        ["NodePortResolution.Found"] = Derived(
            "computed as Signature is not null: two resolutions with equal Signature (both null or both set) "
            + "always agree on Found, and Signature's own content is already hashed, so this adds nothing."),

        ["NodeKey.CanvasPath"] = Hashed(DifferingTerms["Environment CanvasPath"]),
        ["NodeKey.NodeId"] = Hashed(DifferingTerms["Environment NodeId"]),

        ["CanvasPath.Segments"] = Hashed(DifferingTerms["Environment CanvasPath"]),
        ["CanvasPath.IsRoot"] = Derived(
            "computed as Segments.Count == 0: two paths with equal Segments always agree on IsRoot, and "
            + "Segments is already hashed segment by segment, so this adds nothing."),

        ["CanvasPathSegment.NodeId"] = Hashed((
            () => HashIn(Env(CanvasPath.Root.Child("x", "same"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("y", "same"), "n", Resolved), Raw()))),
        ["CanvasPathSegment.BranchName"] = Hashed((
            () => HashIn(Env(CanvasPath.Root.Child("same", "x"), "n", Resolved), Raw()),
            () => HashIn(Env(CanvasPath.Root.Child("same", "y"), "n", Resolved), Raw()))),

        ["CompilationOptions.NamespaceRegistrations"] = Hashed(DifferingTerms["CompilationOptions.NamespaceRegistrations added"]),
        ["CompilationOptions.TypeRegistry"] = Hashed(AtomPair(Atom(), Atom() with { Name = "Pallet" })),
        ["CompilationOptions.Vocabulary"] = Hashed(AtomPair(Atom(), Atom() with { ClrType = typeof(Uri) })),

        ["PortTypeWideningTable.KnownTypes"] = Hashed(DifferingTerms["WideningTable vocabulary"]),
        ["PortTypeWideningTable.Constructors"] = Hashed((
            () => Hash(Raw()),
            () => HashRecords(Constructor()))),

        ["WorkflowTypeConstructorRecord.Name"] = Hashed(ConstructorPair(Constructor(), Constructor() with { Name = "Dial" })),
        ["WorkflowTypeConstructorRecord.Erasure"] = Hashed(ConstructorPair(Constructor(), Constructor() with { Erasure = typeof(Uri) })),
        ["WorkflowTypeConstructorRecord.AllowedArguments"] = Hashed(ConstructorPair(Constructor(), Constructor() with { AllowedArguments = ["Boolean", "Number"] })),
        ["WorkflowTypeConstructorRecord.JsonTypeInfo"] = Excluded(
            () => HashRecords(Constructor()),
            () => HashRecords(Constructor() with { JsonTypeInfo = StringInfo }),
            "no verdict reads a serializer; what rests on one, the JsonSerializable claim, is hashed as a claim, and the literal form is hashed as a form."),
        ["WorkflowTypeConstructorRecord.Claims"] = Hashed(ConstructedPair(
            Constructor() with { Erasure = typeof(string) },
            Constructor() with { Erasure = typeof(string), Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Equatable } })),
        ["WorkflowTypeConstructorRecord.Literal"] = Hashed(ConstructedPair(
            Constructor() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String), JsonTypeInfo = StringInfo },
            Constructor() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String, WorkflowLiteralFormat.Uuid), JsonTypeInfo = StringInfo })),

        ["WorkflowTypeRecord.Name"] = Hashed(AtomPair(Atom(), Atom() with { Name = "Pallet" })),
        ["WorkflowTypeRecord.ClrType"] = Hashed(AtomPair(Atom(), Atom() with { ClrType = typeof(Uri) })),
        ["WorkflowTypeRecord.Aliases"] = Hashed(AtomPair(Atom(), Atom() with { Aliases = ["Yard"] })),
        ["WorkflowTypeRecord.WidensTo"] = Hashed((
            () => HashRecords(Atom(), new WorkflowTypeRecord("Pallet", typeof(string))),
            () => HashRecords(Atom(), new WorkflowTypeRecord("Pallet", typeof(string)) { WidensTo = ["Depot"] }))),
        ["WorkflowTypeRecord.JsonTypeInfo"] = Excluded(
            () => HashRecords(Atom()),
            () => HashRecords(Atom() with { JsonTypeInfo = StrictJson.Info(typeof(string)) }),
            "no verdict reads a serializer; what rests on one, the JsonSerializable claim, is hashed as a claim, and the literal form is hashed as a form."),
        ["WorkflowTypeRecord.Claims"] = Hashed(AtomPair(Atom(), Atom() with { Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Equatable } })),
        ["WorkflowTypeRecord.Literal"] = Hashed(AtomPair(
            Atom() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String), JsonTypeInfo = StringInfo },
            Atom() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Json), JsonTypeInfo = StringInfo })),

        ["WorkflowTypeVocabulary.Generation"] = Excluded(
            () => HashPlugin(Atom()), () => HashPluginLate(Atom()),
            "the number counts registrations since the process started; hashing it would change every verdict at each restart."),
        ["WorkflowTypeVocabulary.Table"] = Hashed(DifferingTerms["WideningTable vocabulary"]),
        ["WorkflowTypeVocabulary.Types"] = Hashed(AtomPair(Atom(), Atom() with { Name = "Pallet" })),
        ["WorkflowTypeVocabulary.Constructors"] = Hashed(ConstructedPair(Constructor(), Constructor() with { Name = "Dial" })),

        ["RegisteredPortType.Name"] = Hashed(AtomPair(Atom(), Atom() with { Name = "Pallet" })),
        ["RegisteredPortType.Owner"] = Excluded(
            () => HashRecords(Atom()), () => HashPlugin(Atom()),
            "who registered a type decides no verdict; a plugin's type and a host's with the same record compile alike."),
        ["RegisteredPortType.ClrType"] = Hashed(AtomPair(Atom(), Atom() with { ClrType = typeof(Uri) })),
        ["RegisteredPortType.Aliases"] = Hashed(AtomPair(Atom(), Atom() with { Aliases = ["Yard"] })),
        ["RegisteredPortType.WidensTo"] = Hashed((
            () => HashRecords(Atom(), new WorkflowTypeRecord("Pallet", typeof(string))),
            () => HashRecords(Atom(), new WorkflowTypeRecord("Pallet", typeof(string)) { WidensTo = ["Depot"] }))),

        ["RegisteredPortType.Literal"] = Hashed(AtomPair(
            Atom() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Int), JsonTypeInfo = StringInfo },
            Atom() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Number), JsonTypeInfo = StringInfo })),
        ["RegisteredPortConstructor.Literal"] = Hashed(ConstructedPair(
            Constructor() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Boolean), JsonTypeInfo = StringInfo },
            Constructor() with { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Number), JsonTypeInfo = StringInfo })),
        ["RegisteredPortType.Implements"] = Hashed(AtomPair(Atom(), Atom() with { Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Comparable } })),
        ["RegisteredPortConstructor.Implements"] = Hashed(ConstructedPair(
            Constructor() with { Erasure = typeof(string) },
            Constructor() with { Erasure = typeof(string), Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Comparable } })),

        ["RegisteredPortConstructor.Name"] = Hashed(ConstructedPair(Constructor(), Constructor() with { Name = "Dial" })),
        ["RegisteredPortConstructor.Owner"] = Excluded(
            () => HashRecords(Constructor()), () => HashPlugin(Constructor()),
            "who registered a constructor decides no verdict."),
        ["RegisteredPortConstructor.Variance"] = Derived(
            "Collection and Map are covariant and every registered constructor invariant, so it is a function of the name."),
        ["RegisteredPortConstructor.Arity"] = Derived(
            "A host constructor takes one argument and the built-in Map two, so it is a function of the name."),
        ["RegisteredPortConstructor.Key"] = Derived(
            "Only the built-in Map has a fixed key, String, and a host constructor has none, so it is a function of the name."),
        ["RegisteredPortConstructor.Erasure"] = Hashed(ConstructedPair(Constructor(), Constructor() with { Erasure = typeof(Uri) })),
        ["RegisteredPortConstructor.AllowedArguments"] = Hashed(ConstructedPair(Constructor(), Constructor() with { AllowedArguments = ["Boolean", "Number"] })),

        ["IExpressionNamespaceStaticCheck.Namespace"] = Hashed(DifferingTerms["CompilationOptions.NamespaceRegistrations name"]),
        ["WorkflowExpressionNamespace.Namespace"] = Hashed((
            () => HashOpt(RegistrationOptions("device"), Raw()),
            () => HashOpt(RegistrationOptions("scene"), Raw()))),
        ["WorkflowExpressionNamespace.StaticCheck"] = Derived(
            "the registration digest reads the pinned Namespace value from its paired static check; hashing the check object itself adds no independent input."),
        ["WorkflowExpressionNamespace.KeySegments"] = Hashed((
            () => HashOpt(RegistrationOptions("device", keySegments: 1), Raw()),
            () => HashOpt(RegistrationOptions("device", keySegments: 2), Raw()))),
        ["WorkflowExpressionNamespace.AnswerVersion"] = Hashed((
            () => HashOpt(RegistrationOptions("device", answerVersion: 0), Raw()),
            () => HashOpt(RegistrationOptions("device", answerVersion: 1), Raw()))),
        ["CompilationOptions.HelperResolver"] = Excluded(
            () => HashOpt(RegistrationOptions("device"), Raw()),
            () => HashOpt(new CompilationOptions
            {
                NamespaceRegistrations = RegistrationOptions("device").NamespaceRegistrations,
                HelperResolver = new TypedHelper("Int"),
            }, Raw()),
            "the resolver object decides nothing; the answers it gave are hashed as the document's helper checks."),
        ["WorkflowExpressionNamespace.ResolverFactory"] = Excluded(
            () => HashOpt(RegistrationOptions("device"), Raw()),
            () => HashOpt(RegistrationOptions("device", factory: (_, _) => throw new InvalidOperationException("unused")), Raw()),
            "runtime-only factory identity cannot be hashed meaningfully; compilation depends on the paired static check, while run-time data is evaluated per scope."),
    };

    [Fact]
    public void Every_public_property_the_digest_can_reach_has_a_mutator_or_a_reviewed_exclusion()
    {
        var reflected = CoveredTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        reflected.Should().BeEquivalentTo(MemberCoverageTable.Keys,
            "a new property on any covered type must get a mutator (it moves the digest) or a reviewed "
            + "exclusion (it deliberately does not) added to MemberCoverageTable, never go unhashed silently");
    }

    [Theory]
    [MemberData(nameof(MemberCoverageNames))]
    public void Each_reflected_member_is_hashed_or_a_reviewed_exclusion(string member)
    {
        var coverage = MemberCoverageTable[member];
        switch (coverage.Kind)
        {
            case MemberKind.Hashed:
                coverage.A!().Should().NotBe(coverage.B!(), $"{member} is read by the digest");
                break;
            case MemberKind.ExcludedNotHashed:
                coverage.A!().Should().Be(coverage.B!(), $"{member} is excluded: {coverage.Reason}");
                break;
            case MemberKind.Derived:
                coverage.Reason.Should().NotBeNullOrWhiteSpace();
                break;
        }
    }

    public static TheoryData<string> MemberCoverageNames() => Names(MemberCoverageTable.Keys);

    [Fact]
    public void A_rich_fixture_hashes_the_same_through_different_collection_types_and_insertion_orders()
    {
        var other = Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId);

        var subInOrdinalDict = new Dictionary<string, IReadOnlyList<PortShape>>(StringComparer.Ordinal)
        {
            ["First"] = [Port("FIn1"), Port("FIn2")],
            ["Second"] = [Port("SIn1"), Port("SIn2")],
        };
        var subOutOrdinalDict = new Dictionary<string, IReadOnlyList<PortShape>>(StringComparer.Ordinal)
        {
            ["First"] = [Port("FOut1"), Port("FOut2")],
            ["Second"] = [Port("SOut1"), Port("SOut2")],
        };
        var subInImmutable = subInOrdinalDict.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        var subOutImmutable = subOutOrdinalDict.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

        var richA = new BlockShape(
            "rich.block", [Port("In1", "String", true), Port("In2", "Number")],
            [Port("Out1", "Boolean"), Port("Out2", "Int")],
            [new ConfigFieldShape("F1", ConfigFieldType.String, true), new ConfigFieldShape("F2", ConfigFieldType.Int, false)],
            new BlockFlags(true, true, false, false, false), subInOrdinalDict, subOutOrdinalDict, "src@1",
            "WorkflowId", ["First", "Second"], [new WorkflowTypeParameter("T", ["Numeric", "Comparable"]), new WorkflowTypeParameter("R")]);
        var richB = new BlockShape(
            "rich.block", [Port("In1", "String", true), Port("In2", "Number")],
            [Port("Out1", "Boolean"), Port("Out2", "Int")],
            [new ConfigFieldShape("F1", ConfigFieldType.String, true), new ConfigFieldShape("F2", ConfigFieldType.Int, false)],
            new BlockFlags(true, true, false, false, false), subInImmutable, subOutImmutable, "src@1",
            "WorkflowId", ["First", "Second"],
            new List<WorkflowTypeParameter> { new("T", new List<string> { "Numeric", "Comparable" }), new("R") });

        var optionsA = new CompilationOptions
        {
            NamespaceRegistrations = [HostNamespaceRegistration.For(new StubNamespace("device")), HostNamespaceRegistration.For(new StubNamespace("scene"))],
            TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Widget", typeof(string))]),
        };
        var optionsB = new CompilationOptions
        {
            NamespaceRegistrations = [HostNamespaceRegistration.For(new StubNamespace("scene")), HostNamespaceRegistration.For(new StubNamespace("device"))],
            TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Widget", typeof(string))]),
        };
        var envA = new NodePortEnvironmentBuilder()
            .Add(CanvasPath.Root, "one", NodePortResolution.Resolved("wf-1", CanvasSignature.Empty))
            .Add(CanvasPath.Root, "two", NodePortResolution.Resolved("wf-2", CanvasSignature.Empty))
            .Build();
        var envB = new NodePortEnvironmentBuilder()
            .Add(CanvasPath.Root, "two", NodePortResolution.Resolved("wf-2", CanvasSignature.Empty))
            .Add(CanvasPath.Root, "one", NodePortResolution.Resolved("wf-1", CanvasSignature.Empty))
            .Build();

        var a = DependencyFingerprint.Compute(new List<BlockShape> { richA, other }, optionsA, envA);
        var b = DependencyFingerprint.Compute(new BlockShape[] { other, richB }, optionsB, envB);

        a.Should().Be(b);
    }
}
