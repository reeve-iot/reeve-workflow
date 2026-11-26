using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class FieldAccessCompilationTests
{
    private static IReadOnlyList<CompilationDiagnostic> Compile(
        string expression, CanvasSignature? signature = null, CompilationOptions? options = null, string sink = "String")
    {
        options ??= new CompilationOptions { TypeRegistry = DeviceVocabulary() };
        var graph = Graph(
            [StartNode(), Node("a", "read"), Node("n1", "notify", new { Message = "expr:" + expression })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "n1")]);
        return WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs:
            [
                ("Doc", PortValueType.Json), ("Count", PortValueType.Number), ("Names", PortValueType.Of("Collection<String>")),
                ("Grid", PortValueType.Of("Collection<Collection<Number>>")), ("Device", PortValueType.Of("Device")),
                ("Devices", PortValueType.Of("Collection<Device>")),
            ]),
            Shape("notify", inputs: [("Message", PortValueType.Of(sink), true)])), signature ?? CanvasSignature.Empty, options).Diagnostics;
    }

    [Theory]
    [InlineData("${previous.a.Count} + 1", "Number")]
    [InlineData("${previous.a.Names}", "Collection<String>")]
    [InlineData("len(toString(${previous.a.Count}))", "Int")]
    [InlineData("${previous.a.Device}", "Device")]
    public void A_path_the_declared_type_allows_compiles(string expression, string result) =>
        Compile(expression, sink: result).Should().BeEmpty();

    [Theory]
    [InlineData("${previous.a.Count.x}", "Number has no member 'x'")]
    [InlineData("${previous.a.Count[0]}", "Number has no index")]
    [InlineData("${previous.a.Names.length}", "Collection<String> has no member 'length'")]
    [InlineData("${previous.a.Names[2]}", "an index into a collection is not allowed")]
    [InlineData("${previous.a.Names[0].x}", "an index into a collection is not allowed")]
    [InlineData("${previous.a.Grid[0][1]}", "an index into a collection is not allowed")]
    [InlineData("${now.year.x}", "Int has no member 'x'")]
    [InlineData("${now.iso.x}", "String has no member 'x'")]
    [InlineData("${now[0]}", "DateTime has no index")]
    [InlineData("${previous.a.Doc.title}", "a Json value is opaque")]
    [InlineData("${previous.a.Doc[2]}", "a Json value is opaque")]
    public void A_member_or_index_out_of_a_scalar_a_collection_or_a_json_value_is_refused_naming_the_type(string expression, string why) =>
        Compile(expression).Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionFieldAccessInvalid && d.NodeId == "n1" && d.Message.Contains(why));

    [Fact]
    public void A_json_value_is_no_result_of_an_expression()
    {
        Compile("${previous.a.Doc}").Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionTypeMismatch && d.NodeId == "n1" && d.Message.Contains("Json"));
    }

    [Theory]
    [InlineData("${previous.a.Device.name}", "a Device has no member 'name'")]
    [InlineData("${previous.a.Device[0]}", "a Device has no index")]
    [InlineData("${previous.a.Devices[0].name}", "an index into a collection is not allowed")]
    public void A_field_read_past_a_registered_type_is_refused(string expression, string why) =>
        Compile(expression, options: Host("device", 1, "Number", hostTypes: ["Device"])).Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionFieldAccessInvalid && d.NodeId == "n1" && d.Message.Contains(why));

    [Fact]
    public void A_host_key_typed_with_a_registered_name_refuses_a_member()
    {
        Compile("${device.kitchen.temp.unit}", options: Host("device", 2, "Device", hostTypes: ["Device"])).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ExpressionFieldAccessInvalid);
    }

    [Theory]
    [InlineData("${previous.a.Count} + ${previous.a.Count.x}")]
    [InlineData("${previous.a.Count.x} + ${previous.a.Count}")]
    [InlineData("${now.year} + ${now.year.x}")]
    [InlineData("${now.year.x} + ${now.year}")]
    [InlineData("${previous.a.Names} == ${previous.a.Names.length}")]
    [InlineData("${previous.a.Names.length} == ${previous.a.Names}")]
    public void Every_path_is_typed_even_when_another_shares_its_key(string expression) =>
        Compile(expression).Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionFieldAccessInvalid);

    [Fact]
    public void Two_refused_paths_of_one_key_are_both_reported_in_a_fixed_order()
    {
        var forward = Compile("${previous.a.Count.x} + ${previous.a.Count.y}").Select(d => d.Message).ToArray();
        var backward = Compile("${previous.a.Count.y} + ${previous.a.Count.x}").Select(d => d.Message).ToArray();

        forward.Should().HaveCount(2).And.Equal(backward);
    }

    [Fact]
    public void An_untyped_host_key_is_refused_once_whichever_path_of_it_comes_first()
    {
        var options = Host("device", 2, null);

        Compile("${device.kitchen.temp} || ${device.kitchen.temp.unit}", options: options).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        Compile("${device.kitchen.temp.unit} || ${device.kitchen.temp}", options: options).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
    }

    [Fact]
    public void A_key_refused_once_is_reported_once_however_many_paths_share_it()
    {
        Compile("${previous.zz.out.a} + ${previous.zz.out.b}").Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_host_type_that_is_not_a_port_type_is_reported_as_malformed_and_not_as_missing()
    {
        Compile("${device.kitchen.temp.unit}", options: Host("device", 2, "Collection<")).Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.PortTypeMalformed && d.Message.Contains("malformed")
                && !d.Message.Contains("states no port type"));
    }

    [Fact]
    public void An_empty_segment_is_a_syntax_error_before_it_reaches_a_check()
    {
        foreach (var text in new[] { "${now..x}", "${now.}", "${previous.w.}", "${trigger.}" })
            Compile(text).Should().ContainSingle(text).Which.RuleId.Should().Be(CompilationRules.ExpressionSyntax);
    }

    [Fact]
    public void A_bare_now_is_a_date_time_and_has_no_members()
    {
        Compile("${now}", sink: "DateTime").Should().BeEmpty();
    }

    [Fact]
    public void A_json_parameter_is_refused_where_it_is_declared()
    {
        var signature = new CanvasSignature([new PortShape("limits", PortValueType.Json, false)], []);

        Compile("${parameter.limits.max}", signature).Should().Contain(d => d.RuleId == CompilationRules.PortTypeJsonPosition && d.PortName == "limits");
    }

    [Fact]
    public void A_parameter_is_typed_by_its_declaration()
    {
        var signature = new CanvasSignature([new PortShape("n", PortValueType.Number, false)], []);

        Compile("${parameter.n.max}", signature).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ExpressionFieldAccessInvalid);
        Compile("${parameter.n} + 1", signature).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
    }

    [Fact]
    public void A_required_parameter_is_not_nullable_and_adds()
    {
        var signature = new CanvasSignature([new PortShape("n", PortValueType.Number, true)], []);

        Compile("${parameter.n} + 1", signature, sink: "Number").Should().BeEmpty();
    }

    [Theory]
    [InlineData("${previous.a}", "'previous' needs a key of 2 segments")]
    [InlineData("${previous}", "'previous' needs a key of 2 segments")]
    [InlineData("${parameter}", "'parameter' needs a key")]
    public void A_path_shorter_than_its_namespace_key_is_refused_as_out_of_scope(string expression, string why) =>
        Compile(expression).Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionOutOfScope && d.Message.Contains(why));

    [Fact]
    public void A_host_key_that_the_check_types_as_json_is_opaque()
    {
        Compile("${device.kitchen.state.power}", options: Host("device", 2, "Json")).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ExpressionFieldAccessInvalid);
    }

    [Fact]
    public void A_host_key_typed_as_a_scalar_refuses_a_member_with_the_type_named()
    {
        Compile("${device.kitchen.temp.unit}", options: Host("device", 2, "Number")).Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.ExpressionFieldAccessInvalid && d.Message.Contains("Number has no member 'unit'"));
    }

    [Fact]
    public void A_host_key_whose_check_states_no_type_is_refused_naming_the_namespace_and_key()
    {
        Compile("${device.kitchen.temp.unit}", options: Host("device", 2, null)).Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.PortTypeMalformed && d.Message.Contains("states no port type")
                && d.Message.Contains("'device.kitchen.temp'"));
    }

    [Fact]
    public void A_host_key_with_no_field_path_needs_a_type_too()
    {
        Compile("${device.kitchen.temp}", options: Host("device", 2, null)).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
    }

    [Fact]
    public void A_host_path_shorter_than_its_key_is_refused_before_the_check_is_asked()
    {
        var asked = false;
        var options = Host("device", 2, "Json", _ => asked = true);

        Compile("${device.kitchen}", options: options).Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionOutOfScope && d.Message.Contains("'device' needs a key of 2 segments"));
        asked.Should().BeFalse();
    }

    [Fact]
    public void A_host_check_is_given_the_key_alone_and_never_the_field_path()
    {
        IReadOnlyList<string>? received = null;
        var options = Host("device", 2, "Json", segments => received = segments);

        Compile("${device.kitchen.temp.unit[0]}", options: options);

        received.Should().Equal("kitchen", "temp");
    }

    [Fact]
    public void The_key_length_a_registration_declares_moves_the_compile_verdict_key()
    {
        var one = DependencyFingerprint.Compute([], Host("device", 1, null), NodePortEnvironment.Empty);
        var two = DependencyFingerprint.Compute([], Host("device", 2, null), NodePortEnvironment.Empty);

        one.Should().NotBe(two);
    }

    private static CompilationOptions Host(
        string name, int keySegments, string? keyType, Action<IReadOnlyList<string>>? record = null,
        string[]? hostTypes = null)
    {
        IReadOnlyList<WorkflowExpressionNamespace> registrations =
        [
            new WorkflowExpressionNamespace(name, keySegments, new TypedCheck(name, keyType, record),
                (_, _) => throw new InvalidOperationException("compile must not make a resolver")),
        ];
        return new CompilationOptions
        {
            NamespaceRegistrations = registrations,
            TypeRegistry = DeviceVocabulary(hostTypes ?? []),
        };
    }

    private static WorkflowTypeRegistry DeviceVocabulary(params string[] more) =>
        new(more.Append("Device").Distinct().Select(t => new WorkflowTypeRecord(t, typeof(string))));

    private sealed class TypedCheck(string name, string? keyType, Action<IReadOnlyList<string>>? record) : IExpressionNamespaceStaticCheck
    {
        public string Namespace => name;

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments)
        {
            record?.Invoke(segments);
            return new(NamespacePathCheck.Yes, keyType);
        }
    }
}
