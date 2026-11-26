using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class HostPathTermTests
{
    private static async Task<IReadOnlyList<CompilationDiagnostic>> Compile(string expression, CompilationOptions options, string sink = "String")
    {
        var result = await WorkflowCompiler.CompileAsync(
            Graph([StartNode(), Node("n1", "notify", new { Message = "expr:" + expression })], [Edge("e1", "start", "n1")]),
            Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Shape("notify", inputs: [("Message", PortValueType.Of(sink), true)])),
            CanvasSignature.Empty, options);
        return result.Diagnostics;
    }

    private static CompilationOptions Host(string? term, Action<IReadOnlyList<string>>? record = null) => new()
    {
        NamespaceRegistrations =
        [
            new WorkflowExpressionNamespace("depot", 2, new Check(term, record),
                (_, _) => throw new InvalidOperationException("compile must not make a resolver")),
        ],
        TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Pallet", typeof(string))]),
    };

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Int")]
    [InlineData("Number?")]
    [InlineData("String")]
    [InlineData("DateTime?")]
    [InlineData("Collection<Int>")]
    [InlineData("Collection<Int?>?")]
    [InlineData("Pallet")]
    public async Task A_registered_closed_term_is_accepted(string term) =>
        (await Compile("${depot.dock.free}", Host(term), sink: term)).Should().BeEmpty();

    [Theory]
    [InlineData("Json")]
    [InlineData("Json?")]
    public async Task A_json_key_read_whole_is_no_expression_result(string term) =>
        (await Compile("${depot.dock.free}", Host(term))).Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);

    [Theory]
    [InlineData(null, "port-type-malformed")]
    [InlineData("", "port-type-malformed")]
    [InlineData("  ", "port-type-malformed")]
    [InlineData("Collection<", "port-type-malformed")]
    [InlineData("Int??", "port-type-malformed")]
    [InlineData("$T", "port-type-malformed")]
    [InlineData("Int ", "port-type-malformed")]
    [InlineData("string", "port-type-alias")]
    [InlineData("Any", "port-type-alias")]
    [InlineData("Float", "port-type-alias")]
    [InlineData("Collection<float>", "port-type-alias")]
    [InlineData("Shipment", "port-type-unregistered")]
    [InlineData("Collection<Shipment>", "port-type-unregistered")]
    [InlineData("Gauge<Int>", "port-type-unregistered")]
    [InlineData("Expression<Boolean>", "port-type-unregistered")]
    [InlineData("Collection<Json>", "port-type-json-position")]
    [InlineData("Collection<Json?>?", "port-type-json-position")]
    public async Task A_term_the_vocabulary_does_not_spell_as_written_is_refused_by_the_code_a_stored_type_gets(string? term, string rule)
    {
        var diagnostic = (await Compile("${depot.dock.free}", Host(term))).Should().ContainSingle().Which;

        diagnostic.RuleId.Should().Be(rule);
        diagnostic.NodeId.Should().Be("n1");
        diagnostic.Message.Should().Contain("'depot.dock.free'");
    }

    [Fact]
    public async Task An_untyped_key_is_refused_once_however_many_paths_read_it()
    {
        (await Compile("${depot.dock.free} || ${depot.dock.free.x} || ${depot.dock.free.y}", Host(null)))
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
    }

    [Fact]
    public async Task A_default_answer_nobody_filled_in_refuses_rather_than_admits()
    {
        var options = new CompilationOptions
        {
            NamespaceRegistrations = [Registration(new DefaultAnswer())],
        };

        (await Compile("${depot.dock.free}", options)).Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public async Task A_no_is_an_unknown_namespace_whatever_term_it_carries()
    {
        var options = new CompilationOptions { NamespaceRegistrations = [Registration(new Check(null, null, NamespacePathCheck.No))] };

        (await Compile("${depot.dock.free}", options)).Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public async Task The_check_is_asked_the_key_alone()
    {
        IReadOnlyList<string>? received = null;

        await Compile("${depot.dock.free.name}", Host("Json", segments => received = segments));

        received.Should().Equal("dock", "free");
    }

    [Fact]
    public async Task A_refused_term_does_not_stack_a_field_access_refusal_on_top()
    {
        (await Compile("${depot.dock.free.name}", Host("string"))).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.PortTypeAlias);
    }

    [Fact]
    public async Task Without_a_helper_resolver_every_helper_path_is_an_unknown_namespace()
    {
        (await Compile("${helper.vacation_mode}", new CompilationOptions())).Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ExpressionUnknownNamespace);
        (await Compile("${helper.kitchen_light.count_on}", new CompilationOptions { HelperResolver = NullHelperResolver.Instance }))
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public async Task A_helper_path_is_typed_by_the_helper_check_which_is_given_every_segment_after_helper()
    {
        var helper = new Helper(segments => new(NamespacePathCheck.Yes, "Int"));

        (await Compile("${helper.kitchen_light.count_on} + 1", new CompilationOptions { HelperResolver = helper }, sink: "Int")).Should().BeEmpty();

        helper.Asked.Should().Equal(["kitchen_light", "count_on"]);
    }

    [Fact]
    public async Task A_helper_path_answering_json_is_no_expression_result()
    {
        (await Compile("${helper.mode}", new CompilationOptions { HelperResolver = new Helper(_ => new(NamespacePathCheck.Yes, "Json?")) }))
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
    }

    [Theory]
    [InlineData(null, "port-type-malformed")]
    [InlineData("Collection<", "port-type-malformed")]
    [InlineData("string", "port-type-alias")]
    [InlineData("Shipment", "port-type-unregistered")]
    [InlineData("Collection<Json>", "port-type-json-position")]
    public async Task A_helper_answer_off_the_vocabulary_is_refused_at_the_expression(string? term, string rule)
    {
        var diagnostic = (await Compile("${helper.mode}", new CompilationOptions { HelperResolver = new Helper(_ => new(NamespacePathCheck.Yes, term)) }))
            .Should().ContainSingle().Which;

        diagnostic.RuleId.Should().Be(rule);
        diagnostic.Message.Should().Contain("'helper.mode'");
    }

    [Fact]
    public async Task A_helper_check_that_says_no_is_an_unknown_namespace()
    {
        (await Compile("${helper.gone}", new CompilationOptions { HelperResolver = new Helper(_ => NamespacePathCheckAnswer.No) }))
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public async Task A_helper_check_that_throws_is_reported_by_the_public_compile_api()
    {
        var error = new InvalidOperationException("helper store is down");

        var act = () => Compile("${helper.mode}", new CompilationOptions { HelperResolver = new Helper(_ => throw error) });
        var fault = await act.Should().ThrowAsync<CompilationFaultException>();
        fault.Which.InnerException.Should().BeSameAs(error);
    }

    [Fact]
    public async Task Public_compile_refuses_a_model_with_an_unpaired_surrogate_before_collecting_helper_paths()
    {
        var resolver = new Helper(_ => throw new InvalidOperationException("unreadable definitions must not check helpers"));
        var definition = Graph([Node("\ud800", "notify", new { Message = "expr:${helper.mode}" })], []);

        var result = await WorkflowCompiler.CompileAsync(definition,
            Catalogue(Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            CanvasSignature.Empty, new CompilationOptions { HelperResolver = resolver });

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        resolver.Calls.Should().Be(0);
    }

    [Fact]
    public void The_answer_version_defaults_to_zero_and_refuses_a_negative_value()
    {
        var registration = Registration(new Check("Int", null));
        registration.AnswerVersion.Should().Be(0);

        var act = () => new WorkflowExpressionNamespace("depot", 1, new Check("Int", null), (_, _) => throw new InvalidOperationException())
        {
            AnswerVersion = -1,
        };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_raised_answer_version_changes_the_dependency_hash_and_an_equal_one_does_not()
    {
        string Hash(int version) => DependencyFingerprint.Compute(
            [], new CompilationOptions { NamespaceRegistrations = [Registration(new Check("Int", null), version)] },
            NodePortEnvironment.Empty);

        Hash(1).Should().NotBe(Hash(0));
        Hash(2).Should().NotBe(Hash(1));
        Hash(1).Should().Be(Hash(1));
    }

    private static WorkflowExpressionNamespace Registration(IExpressionNamespaceStaticCheck check, int answerVersion = 0) =>
        new("depot", 2, check, (_, _) => throw new InvalidOperationException("compile must not make a resolver"))
        {
            AnswerVersion = answerVersion,
        };

    private sealed class Check(
        string? term, Action<IReadOnlyList<string>>? record, NamespacePathCheck verdict = NamespacePathCheck.Yes)
        : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "depot";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments)
        {
            record?.Invoke(segments);
            return new(verdict, term);
        }
    }

    private sealed class DefaultAnswer : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "depot";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => default;
    }

    private sealed class Helper(Func<IReadOnlyList<string>, NamespacePathCheckAnswer> check) : IHelperResolver
    {
        public IReadOnlyList<string>? Asked { get; private set; }
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            Calls++;
            Asked = paths.FirstOrDefault()?.Segments;
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.ToDictionary(p => p, p => check(p.Segments)));
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }
}
