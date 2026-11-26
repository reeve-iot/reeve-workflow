using System.Text;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests;

public class MapTermTests
{
    [Theory]
    [InlineData("Map<String,Int>")]
    [InlineData("Map<string,Int>")]
    [InlineData("Map<STRING,Json>")]
    [InlineData("Map<String,Int>?")]
    [InlineData("Map<String,Int?>")]
    [InlineData("Map<String,Collection<Int>>")]
    [InlineData("Collection<Map<String,Int>>?")]
    [InlineData("Map<String,Map<String,Gauge<Int>?>>?")]
    public void A_map_term_renders_as_it_was_written_and_reads_back_equal(string text)
    {
        var term = PortTerm.Parse(text, PortTermGrammar.Document);

        term.ToString().Should().Be(text);
        PortTerm.Parse(term.ToString(), PortTermGrammar.Document).Should().Be(term);
        PortValueType.Of(text).Name.Should().Be(text);
    }

    [Fact]
    public void A_map_term_holds_its_key_then_its_value_as_arguments()
    {
        var term = PortTerm.Parse("Map<string,Collection<Int>>?", PortTermGrammar.Document);

        term.IsMap.Should().BeTrue();
        term.IsCollection.Should().BeFalse();
        term.IsNullable.Should().BeTrue();
        term.Arguments.Select(a => a.ToString()).Should().Equal("string", "Collection<Int>");
        term.LastArgument.Should().Be(PortTerm.CollectionOf(PortTerm.Atom("Int")));
        term.AsNullable().Should().BeSameAs(term);
        term.WithoutNullable().Arguments.Should().HaveCount(2);
        PortTerm.Parse("Collection<Int>", PortTermGrammar.Document).Arguments.Should().ContainSingle();
        PortTerm.Atom("Int").Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Two_maps_are_equal_when_their_names_are_equal_ignoring_case_and_their_values_are()
    {
        var a = PortTerm.Parse("Map<String,Int>", PortTermGrammar.Document);

        a.Should().Be(PortTerm.Parse("Map<string,int>", PortTermGrammar.Document));
        a.Should().Be(PortTerm.MapOf(PortTerm.Atom("Int")));
        a.GetHashCode().Should().Be(PortTerm.MapOf(PortTerm.Atom("INT")).GetHashCode());
        a.Should().NotBe(PortTerm.MapOf(PortTerm.Atom("Number")));
        a.Should().NotBe(PortTerm.MapOf(PortTerm.Atom("Int"), isNullable: true));
        a.Should().NotBe(PortTerm.CollectionOf(PortTerm.Atom("Int")));
        a.Should().NotBe(PortTerm.Constructor("Gauge", PortTerm.Atom("Int")));
    }

    [Fact]
    public void Map_is_the_librarys_and_no_atom_or_one_argument_constructor_can_carry_the_name()
    {
        FluentActions.Invoking(() => PortTerm.Atom("Map")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PortTerm.Constructor("Map", PortTerm.Atom("Int"))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PortTerm.MapOf(PortTerm.Null)).Should().Throw<ArgumentException>();
        PortTerm.Atom("map").Kind.Should().Be(PortTermKind.Atom);
    }

    [Fact]
    public void A_map_nested_ten_thousand_deep_is_read_written_and_compared_without_recursion()
    {
        const int depth = 10_000;
        var text = new StringBuilder();
        for (var i = 0; i < depth; i++) text.Append("Map<String,");
        text.Append("Int").Append('>', depth);
        var written = text.ToString();

        var term = PortTerm.Parse(written, PortTermGrammar.Document);

        term.ToString().Should().Be(written);
        term.Equals(PortTerm.Parse(written, PortTermGrammar.Document)).Should().BeTrue();
        term.GetHashCode().Should().Be(PortTerm.Parse(written, PortTermGrammar.Document).GetHashCode());
    }

    [Theory]
    [InlineData("Map<Int,String>", PortTypeSyntaxFault.MapKey, 4)]
    [InlineData("Map<String?,Int>", PortTypeSyntaxFault.MapKey, 4)]
    [InlineData("Map<String<Int>,Int>", PortTypeSyntaxFault.MapKey, 4)]
    [InlineData("Map<Collection<String>,Int>", PortTypeSyntaxFault.MapKey, 4)]
    [InlineData("Map<Foo,Int>", PortTypeSyntaxFault.MapKey, 4)]
    [InlineData("Collection<Map<Int,Int>>", PortTypeSyntaxFault.MapKey, 15)]
    [InlineData("Map", PortTypeSyntaxFault.MapWithoutBracket, 0)]
    [InlineData("Map?", PortTypeSyntaxFault.MapWithoutBracket, 0)]
    [InlineData("Collection<Map>", PortTypeSyntaxFault.MapWithoutBracket, 11)]
    [InlineData("Map<String>", PortTypeSyntaxFault.MapWithoutValue, 10)]
    [InlineData("Map<String", PortTypeSyntaxFault.MapWithoutValue, 10)]
    [InlineData("Map<String,", PortTypeSyntaxFault.ExpectedTypeName, 11)]
    [InlineData("Map<String,Int,Int>", PortTypeSyntaxFault.IllegalCharacter, 14)]
    [InlineData("Map<String, Int>", PortTypeSyntaxFault.IllegalCharacter, 11)]
    [InlineData("Gauge<Int,Int>", PortTypeSyntaxFault.IllegalCharacter, 9)]
    [InlineData("Collection<Int,Int>", PortTypeSyntaxFault.IllegalCharacter, 14)]
    [InlineData("Map<String,Int", PortTypeSyntaxFault.MissingClose, 14)]
    public void A_map_term_that_is_not_a_sentence_is_refused_with_its_fault_and_position(string text, PortTypeSyntaxFault fault, int position)
    {
        PortValueType.TryParse(text, out _, out var error).Should().BeFalse();

        (error!.Fault, error.Position).Should().Be((fault, position));
    }

    [Fact]
    public void A_type_parameter_is_never_a_key_and_may_be_a_value_only_under_the_declaration_grammar()
    {
        PortTerm.TryParse("Map<$T,Int>", PortTermGrammar.Declaration, out _, out var key).Should().BeFalse();
        (key!.Fault, key.Position).Should().Be((PortTypeSyntaxFault.MapKey, 4));
        PortTerm.TryParse("Map<$T,Int>", PortTermGrammar.Document, out _, out var illegal).Should().BeFalse();
        (illegal!.Fault, illegal.Position).Should().Be((PortTypeSyntaxFault.IllegalCharacter, 4));

        var declared = PortTerm.Parse("Map<String,$T?>", PortTermGrammar.Declaration);
        declared.ContainsParameter.Should().BeTrue();
        PortTerm.TryParse("Map<String,$T?>", PortTermGrammar.Document, out _, out _).Should().BeFalse();
    }

    private static string RuleOf(string type)
    {
        var definition = Graph(
            [StartNode(), Node("a", "dyn", new { InputPorts = "[]", OutputPorts = $$"""[{"name":"Items","type":"{{type}}"}]""" })],
            [Edge("e1", "start", "a")]);
        return PortTypeNameCheck.Check(Stored(definition), null, null).Diagnostics.Select(d => d.RuleId).SingleOrDefault() ?? "";
    }

    [Theory]
    [InlineData("Map<Int,Int>", CompilationRules.PortTypeMapKey)]
    [InlineData("Map<String?,Int>", CompilationRules.PortTypeMapKey)]
    [InlineData("Map", CompilationRules.PortTypeMalformed)]
    [InlineData("Map<String,Int,Int>", CompilationRules.PortTypeMalformed)]
    [InlineData("Gauge<Int,Int>", CompilationRules.PortTypeMalformed)]
    [InlineData("Map<String,Int>", "")]
    public void A_stored_port_type_is_refused_under_the_rule_its_fault_belongs_to(string type, string rule)
    {
        RuleOf(type).Should().Be(rule);
    }

    [Fact]
    public void A_stored_map_holding_Json_is_refused_at_an_input_and_below_the_top_of_an_output()
    {
        var table = PortTypeWideningTable.Builtin;

        PortTermGrading.Grade("Map<String,Json>", TermSeam.Stored, TermPosition.Input, table, out var input).Should().BeNull();
        input!.Value.Rule.Should().Be(CompilationRules.PortTypeJsonPosition);
        PortTermGrading.Grade("Map<String,Json>", TermSeam.Stored, TermPosition.Output, table, out var output).Should().BeNull();
        output!.Value.Rule.Should().Be(CompilationRules.PortTypeJsonPosition);
        PortTermGrading.Grade("Map<String,Collection<Json?>>", TermSeam.Stored, TermPosition.Input, table, out var nested).Should().BeNull();
        nested!.Value.Rule.Should().Be(CompilationRules.PortTypeJsonPosition);
    }

    [Theory]
    [InlineData("Map<String,Int>", true)]
    [InlineData("Map<String,Collection<Int>>?", false)]
    [InlineData("Collection<Map<String,Int>>", true)]
    public void A_stored_map_is_a_port_type_at_a_root_input_or_an_output_port(string type, bool input)
    {
        var graded = PortTermGrading.Grade(type, TermSeam.Stored, input ? TermPosition.Input : TermPosition.Output, PortTypeWideningTable.Builtin, out var refusal);

        refusal.Should().BeNull();
        graded!.Term.ToString().Should().Be(type);
        PortTypeWideningTable.Builtin.RefusalOf(PortTerm.Parse(type, PortTermGrammar.Document)).Should().BeNull();
    }

    [Fact]
    public void A_stored_map_spelled_with_a_key_in_another_case_is_an_alias_fault()
    {
        PortTermGrading.Grade("Map<string,Int>", TermSeam.Stored, TermPosition.Input, PortTypeWideningTable.Builtin, out var refusal).Should().BeNull();

        refusal!.Value.Rule.Should().Be(CompilationRules.PortTypeAlias);
    }

    [Theory]
    [InlineData("""{"name":"o","type":"Map<String,Int>"}""")]
    [InlineData("""{"name":"o","type":"Collection<Map<String,Int>>"}""")]
    public void A_compiled_dynamic_output_port_declaring_a_map_compiles(string port)
    {
        var graph = Graph([StartNode(), Node("a", "dyn.source", new { OutputPorts = $"[{port}]" })], [Edge("e1", "start", "a")]);
        var catalogue = Catalogue([Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("dyn.source", hasDynamicPorts: true, source: "plugin:dyn")]);

        WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty).Diagnostics
            .Should().NotContain(d => d.RuleId == CompilationRules.PortTypeUnregistered);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("Expression")]
    public void A_block_declaring_a_map_port_is_graded_as_any_port(string where)
    {
        var declared = PortValueType.OfDeclaration(where == "Expression" ? "Expression<Map<String,Int>>" : "Map<String,Int>");
        var shape = where == "output"
            ? Shape("gadget", outputs: [("Out", declared)])
            : Shape("gadget", inputs: [("In", declared, false)]);

        BlockDeclarationRules.Ungraded(shape, PortTypeWideningTable.Builtin).Should().BeEmpty();
    }

    [Fact]
    public void A_declaration_of_a_map_of_Json_is_refused_as_a_declaration_fault()
    {
        PortTermGrading.Grade("Map<String,Json>", TermSeam.Declaration, TermPosition.Input, PortTypeWideningTable.Builtin, out var refusal).Should().BeNull();

        refusal!.Value.Rule.Should().Be(CompilationRules.BlockPortDeclarationInvalid);
    }

    [Fact]
    public void A_constructor_may_admit_a_map_argument_and_a_term_holding_one_is_registered()
    {
        new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Int", "Map<String,Int>"] }.Validate().Should().BeEmpty();

        var table = PortTypeWideningTable.Builtin.WithConstructor(new("Gauge", typeof(string)) { AllowedArguments = ["Int", "Map<String,Int>"] });
        PortTermGrading.Grade("Gauge<Map<String,Int>>", TermSeam.Stored, TermPosition.Input, table, out var noRefusal).Should().NotBeNull();
        noRefusal.Should().BeNull();
        PortTermGrading.Grade("Gauge<Map<String,Number>>", TermSeam.Stored, TermPosition.Input, table, out var refused).Should().BeNull();
        refused!.Value.Rule.Should().Be(CompilationRules.PortTypeUnregistered);
    }

    [Fact]
    public void A_map_key_spelled_in_another_case_is_an_alias_and_a_declaration_with_a_bad_key_is_a_declaration_fault()
    {
        PortTypeWideningTable.Builtin.Canonicalize(PortTerm.Parse("Map<string,Int>", PortTermGrammar.Document)).ToString()
            .Should().Be("Map<String,Int>");

        PortTermGrading.Grade("Map<Int,Int>", TermSeam.Declaration, TermPosition.Input, PortTypeWideningTable.Builtin, out var declared)
            .Should().BeNull();
        declared!.Value.Rule.Should().Be(CompilationRules.BlockPortDeclarationInvalid);
    }

    [Fact]
    public void A_reserved_name_in_a_map_value_is_found()
    {
        ReservedPortTypeNames.In("Map<String,Collection<Any>>").Should().Be("Any");
        ReservedPortTypeNames.In("Map<String,Int>").Should().BeNull();
    }

    [Fact]
    public void A_map_value_is_substituted_for_a_type_parameter_and_listed_among_the_parameters()
    {
        var declared = PortTerm.Parse("Map<String,$T>", PortTermGrammar.Declaration);

        TypeParameterBinding.Substitute(declared, _ => PortTerm.Atom("Int")).Should().Be(PortTerm.MapOf(PortTerm.Atom("Int")));
        TypeParameterBinding.Parameters(declared).Should().Equal("T");
    }
}
