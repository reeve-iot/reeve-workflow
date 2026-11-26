using System.Collections.Concurrent;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class TypedExpressionCompilerTests
{
    private static readonly ExpressionVectorScopes Scopes = ExpressionVectorScopes.Load();

    private static TypedExpression Typed(string expression, ExpressionTypingScope typing)
    {
        var result = ExpressionTyper.Type(expression, typing);
        result.Refusals.Should().BeEmpty(expression);
        return result.Tree!;
    }

    private static TypedExpression Workflow(string expression) => Typed(expression, Scopes.Typing("workflow"));

    private static ExpressionEvaluationException Thrown(TypedExpression tree, ExpressionEvaluationScope scope)
    {
        return FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(tree, scope))
            .Should().Throw<ExpressionEvaluationException>().Which;
    }

    [Fact]
    public void A_typed_tree_exposes_a_helper_once_when_the_expression_reads_it_twice()
    {
        var typing = new ExpressionTypingScope.Builder()
            .Helper(new Dictionary<HelperPath, NamespacePathCheckAnswer> { [new HelperPath(["h"])] = new(NamespacePathCheck.Yes, "Int") })
            .Build();
        var tree = Typed("${helper.h} + ${helper.h}", typing);
        tree.HelperPaths.Should().ContainSingle().Which.Segments.Should().Equal("h");
    }

    [Fact]
    public void A_closure_refuses_to_evaluate_without_a_scope()
    {
        var closure = TypedExpressionCompiler.Compile(Workflow("1"));

        FluentActions.Invoking(() => closure.Evaluate(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_closure_carries_the_term_of_its_tree()
    {
        var tree = Workflow("${previous.n.ki} ?? 4");

        TypedExpressionCompiler.Compile(tree).Term.Should().Be(tree.Term);
    }

    [Theory]
    [InlineData("false && ${ns.R}", "Boolean", false, false, 0)]
    [InlineData("true || ${ns.R}", "Boolean", false, true, 0)]
    [InlineData("true ? 1 : ${ns.R}", "Int", 9L, 1L, 0)]
    [InlineData("false ? ${ns.R} : 2", "Int", 9L, 2L, 0)]
    [InlineData("${previous.n.ki} ?? ${ns.R}", "Int", 9L, 5L, 0)]
    [InlineData("true && ${ns.R}", "Boolean", false, false, 1)]
    [InlineData("false || ${ns.R}", "Boolean", true, true, 1)]
    [InlineData("true ? ${ns.R} : 2", "Int", 9L, 9L, 1)]
    [InlineData("false ? 1 : ${ns.R}", "Int", 9L, 9L, 1)]
    [InlineData("${previous.n.ni} ?? ${ns.R}", "Int", 9L, 9L, 1)]
    public void A_short_circuit_resolves_the_operand_it_takes_and_never_the_one_it_skips(
        string expression, string term, object answer, object expected, int calls)
    {
        var probe = new Probe(term, answer);
        var (typing, scope) = probe.Scopes();
        var tree = Typed(expression, typing);

        TypedExpressionCompiler.Compile(tree).Evaluate(scope).Should().Be(expected);
        probe.Calls.Should().Be(calls);

        probe.Calls = 0;
        TypedExpressionEvaluator.Evaluate(tree, scope).Should().Be(expected);
        probe.Calls.Should().Be(calls, "evaluation resolves only the operands it takes");
    }

    [Theory]
    [InlineData("${previous.n.i}", Source.Value)]
    [InlineData("${parameter.p}", Source.Value)]
    [InlineData("${helper.h}", Source.Value)]
    [InlineData("${ns.R}", Source.Value)]
    [InlineData("${item}", Source.Value)]
    [InlineData("${now.hour}", Source.UtcHour)]
    [InlineData("${now.localHour}", Source.LocalHour)]
    public void A_read_answers_the_scope_of_each_evaluation(string expression, Source source)
    {
        var tree = Typed(expression, ReadingScopes.Typing());
        var closure = TypedExpressionCompiler.Compile(tree);
        var first = ReadingScopes.Evaluation(value: 11, utcHour: 3, localHour: 5);
        var second = ReadingScopes.Evaluation(value: 22, utcHour: 4, localHour: 6);

        long Wanted(Source kind, long value, long utc, long local) => kind switch { Source.Value => value, Source.UtcHour => utc, _ => local };
        closure.Evaluate(first).Should().Be(Wanted(source, 11, 3, 5));
        closure.Evaluate(second).Should().Be(Wanted(source, 22, 4, 6));
        closure.Evaluate(first).Should().Be(Wanted(source, 11, 3, 5));
    }

    public enum Source
    {
        Value,
        UtcHour,
        LocalHour,
    }

    [Fact]
    public void A_previous_output_that_is_absent_fails_as_value_absent_naming_its_path()
    {
        var typing = new ExpressionTypingScope.Builder().Previous((_, _) => ReadLookup.Typed(Terms.Int)).Build();
        var scope = new ExpressionEvaluationScope.Builder().Previous((_, _) => (false, null)).Build();

        var failure = Thrown(Typed("${previous.n.i}", typing), scope);

        failure.Category.Should().Be("value-absent");
        failure.MissingPath.Should().Be("previous.n.i");
    }

    [Fact]
    public void A_host_namespace_the_scope_does_not_bind_fails_as_value_absent()
    {
        var typing = new Probe("Int", 1L).Scopes().Typing;

        var failure = Thrown(Typed("${ns.R}", typing), new ExpressionEvaluationScope.Builder().Build());

        failure.Category.Should().Be("value-absent");
        failure.MissingPath.Should().Be("ns.R");
    }

    [Fact]
    public void A_host_path_that_does_not_resolve_fails_as_value_absent()
    {
        var (typing, scope) = new Probe("Int", Probe.NotFound).Scopes();

        var failure = Thrown(Typed("${ns.R}", typing), scope);

        failure.Category.Should().Be("value-absent");
        failure.MissingPath.Should().Be("ns.R");
    }

    [Fact]
    public void A_scope_variable_that_is_not_bound_fails_as_value_absent()
    {
        var tree = Typed("${item}", ReadingScopes.Typing());

        var failure = Thrown(tree, new ExpressionEvaluationScope.Builder().Build());

        failure.Category.Should().Be("value-absent");
        failure.MissingPath.Should().Be("item");
    }

    [Fact]
    public void A_resolver_that_throws_fails_the_read_with_no_category_and_keeps_the_exception()
    {
        var (typing, scope) = new Probe("Int", new InvalidOperationException("broken")).Scopes();

        var failure = Thrown(Typed("${ns.R}", typing), scope);

        failure.Category.Should().BeNull();
        failure.MissingPath.Should().BeNull();
        failure.Message.Should().Be("reading '${ns.R}' threw InvalidOperationException: broken");
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void A_cancellation_in_a_resolver_leaves_the_closure_unwrapped()
    {
        var (typing, scope) = new Probe("Int", new OperationCanceledException("stop")).Scopes();
        var tree = Typed("${ns.R}", typing);

        FluentActions.Invoking(() => TypedExpressionCompiler.Compile(tree).Evaluate(scope)).Should().Throw<OperationCanceledException>().WithMessage("stop");
        FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(tree, scope)).Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_value_off_its_term_fails_as_narrowing_naming_its_path()
    {
        var (typing, scope) = new Probe("Int", "not a number").Scopes();

        var failure = Thrown(Typed("${ns.R}", typing), scope);

        failure.Category.Should().Be("narrowing");
        failure.MissingPath.Should().Be("ns.R");
    }

    [Theory]
    [InlineData("1 / 0.0", "division-by-zero")]
    [InlineData("5 % ${previous.n.zero}", "division-by-zero")]
    [InlineData("${previous.n.big} + 1", "overflow")]
    [InlineData("-${previous.n.lo}", "overflow")]
    [InlineData("abs(${previous.n.lo})", "overflow")]
    [InlineData("${previous.n.huge} * 10", "overflow")]
    [InlineData("round(${previous.n.huge})", "overflow")]
    [InlineData("jsonPath(parseJson(\"[1\"), \"$\", \"Int?\")", "json-parse")]
    public void An_operation_that_refuses_its_operands_fails_in_the_category_the_spec_names(string expression, string category)
    {
        var failure = Thrown(Workflow(expression), Scopes.Evaluation("workflow"));

        failure.Category.Should().Be(category);
        failure.Code.Should().Be(TypedExpressionEvaluator.FailureCode);
    }

    [Theory]
    [InlineData("jsonPath")]
    [InlineData("jsonPathText")]
    public void A_projection_whose_path_multiplies_its_nodes_past_the_budget_fails_as_too_large(string function)
    {
        var path = "$" + string.Concat(Enumerable.Repeat("[" + string.Join(",", Enumerable.Repeat("*", 60)) + "]", 5));
        var term = function == "jsonPath" ? "Collection<Int>" : "Collection<String>";

        var failure = Thrown(Workflow($"{function}(parseJson(\"[[[[[1]]]]]\"), \"{path}\", \"{term}\")"), Scopes.Evaluation("workflow"));

        failure.Category.Should().Be("too-large");
        failure.Code.Should().Be(TypedExpressionEvaluator.FailureCode);
        failure.Message.Should().Contain($"the path '{path}' takes more than");
    }

    [Fact]
    public void A_projection_near_the_budget_still_delivers_its_nodes()
    {
        var path = "$" + string.Concat(Enumerable.Repeat("[" + string.Join(",", Enumerable.Repeat("*", 60)) + "]", 2));
        var source = "[[" + string.Join(",", Enumerable.Repeat("1", 26)) + "]]";
        var tree = Workflow($"jsonPath(parseJson(\"{source}\"), \"{path}\", \"Collection<Int>\")");

        TypedExpressionEvaluator.Evaluate(tree, Scopes.Evaluation("workflow"))
            .Should().BeAssignableTo<System.Collections.IList>().Which.Count.Should().Be(60 * 60 * 26);
    }

    [Fact]
    public void An_operation_failure_is_reported_at_the_node_that_failed_and_not_at_the_root()
    {
        var root = Workflow("1 + (${previous.n.big} + 1)");
        var inner = ((TypedOperation)root).Operands[1];

        var failure = Thrown(root, Scopes.Evaluation("workflow"));

        failure.Category.Should().Be("overflow");
        failure.Position.Should().Be(inner.Position).And.NotBe(root.Position);
    }

    [Fact]
    public void Operands_are_evaluated_left_to_right_and_before_the_operation_that_uses_them()
    {
        var failure = Thrown(Workflow("(5 % ${previous.n.zero}) + (${previous.n.big} + 1)"), Scopes.Evaluation("workflow"));

        failure.Category.Should().Be("division-by-zero");
    }

    [Theory]
    [InlineData("\"a\" == \"A\"", false)]
    [InlineData("\"a\" != \"A\"", true)]
    [InlineData("\"a\" == \"a\"", true)]
    [InlineData("1 == 1", true)]
    [InlineData("1 != 1", false)]
    [InlineData("2.5 == 2.5", true)]
    [InlineData("true == false", false)]
    [InlineData("true != false", true)]
    [InlineData("${previous.n.ki} == 5", true)]
    [InlineData("${previous.n.ni} == null", true)]
    public void Equality_compares_values_of_one_term(string expression, bool expected)
    {
        var tree = Workflow(expression);
        var scope = Scopes.Evaluation("workflow");

        TypedExpressionCompiler.Compile(tree).Evaluate(scope).Should().Be(expected);
        TypedExpressionEvaluator.Evaluate(tree, scope).Should().Be(expected);
    }

    public static TheoryData<string, string, string, string> Orderings()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var (kind, low, high) in new[] { ("Int", "1", "2"), ("Number", "1.5", "2.5"), ("String", "\"B\"", "\"a\"") })
        {
            foreach (var op in new[] { "<", "<=", ">", ">=" })
            {
                data.Add(kind, op, low, low);
                data.Add(kind, op, low, high);
                data.Add(kind, op, high, low);
            }
        }

        foreach (var op in new[] { "<", "<=", ">", ">=" })
            data.Add("DateTime", op, "${previous.n.d}", "${previous.n.d}");
        return data;
    }

    [Theory]
    [MemberData(nameof(Orderings))]
    public void A_comparison_holds_for_equal_and_for_ordered_operands_of_every_term(string kind, string op, string left, string right)
    {
        var order = Order(kind, left, right);
        var expected = op switch { "<" => order < 0, "<=" => order <= 0, ">" => order > 0, _ => order >= 0 };
        var tree = Workflow($"{left} {op} {right}");
        var scope = Scopes.Evaluation("workflow");

        TypedExpressionCompiler.Compile(tree).Evaluate(scope).Should().Be(expected, $"{left} {op} {right}");
        TypedExpressionEvaluator.Evaluate(tree, scope).Should().Be(expected, $"{left} {op} {right}");
    }

    private static int Order(string kind, string left, string right) => kind switch
    {
        "Int" => long.Parse(left).CompareTo(long.Parse(right)),
        "Number" => double.Parse(left, System.Globalization.CultureInfo.InvariantCulture).CompareTo(double.Parse(right, System.Globalization.CultureInfo.InvariantCulture)),
        "String" => string.CompareOrdinal(left, right),
        _ => left.CompareTo(right),
    };

    [Theory]
    [InlineData("contains(\"Hello\", \"ell\")", true)]
    [InlineData("contains(\"Hello\", \"ELL\")", false)]
    [InlineData("startsWith(\"Hello\", \"He\")", true)]
    [InlineData("startsWith(\"Hello\", \"he\")", false)]
    [InlineData("endsWith(\"Hello\", \"lo\")", true)]
    [InlineData("endsWith(\"Hello\", \"LO\")", false)]
    public void String_predicates_are_ordinal_and_case_sensitive(string expression, bool expected)
    {
        var tree = Workflow(expression);
        var scope = Scopes.Evaluation("workflow");

        TypedExpressionCompiler.Compile(tree).Evaluate(scope).Should().Be(expected);
        TypedExpressionEvaluator.Evaluate(tree, scope).Should().Be(expected);
    }

    [Theory]
    [InlineData("${previous.n.ni}")]
    [InlineData("${previous.n.nx}")]
    [InlineData("${previous.n.ns}")]
    [InlineData("${previous.n.nb}")]
    [InlineData("${previous.n.b} ? ${previous.n.ni} : 2")]
    [InlineData("${previous.n.ni} ?? ${previous.n.ni}")]
    public void A_nullable_scalar_expression_answers_null(string expression)
    {
        var tree = Workflow(expression);
        var scope = Scopes.Evaluation("workflow");

        TypedExpressionEvaluator.Evaluate(tree, scope).Should().BeNull();
        TypedExpressionCompiler.Compile(tree).Evaluate(scope).Should().BeNull();
    }

    [Fact]
    public void A_tree_nested_one_level_past_the_depth_limit_compiles_and_fails_when_it_is_evaluated()
    {
        var tree = Nested(TypedExpressionEvaluator.MaxEvaluationDepth + 1);
        var closure = FluentActions.Invoking(() => TypedExpressionCompiler.Compile(tree)).Should().NotThrow().Subject;

        FluentActions.Invoking(() => closure.Evaluate(Scopes.Evaluation("workflow")))
            .Should().Throw<ExpressionEvaluationException>().WithMessage("*too deeply nested*");
    }

    [Fact]
    public void A_tree_nested_exactly_to_the_depth_limit_is_still_evaluated()
    {
        var tree = Nested(TypedExpressionEvaluator.MaxEvaluationDepth);

        TypedExpressionCompiler.Compile(tree).Evaluate(Scopes.Evaluation("workflow")).Should().Be(true);
    }

    [Fact]
    public void A_now_field_that_does_not_exist_fails_when_the_read_is_evaluated_and_not_when_it_is_compiled()
    {
        var tree = new TypedRead(Terms.Int, "now", ["bogus"], 4);
        var closure = FluentActions.Invoking(() => TypedExpressionCompiler.Compile(tree)).Should().NotThrow().Subject;

        var failure = Thrown(tree, Scopes.Evaluation("workflow"));

        failure.Message.Should().Be("now has no field 'bogus'");
        failure.Position.Should().Be(4);
        FluentActions.Invoking(() => closure.Evaluate(Scopes.Evaluation("workflow"))).Should().Throw<ExpressionEvaluationException>();
    }

    [Fact]
    public void An_overload_the_compiler_has_no_builder_for_is_refused_when_the_tree_is_compiled_naming_it()
    {
        var tree = new TypedOperation("missing-overload", [], Terms.Int, 7);

        FluentActions.Invoking(() => TypedExpressionCompiler.Compile(tree)).Should().Throw<InvalidOperationException>().WithMessage("*missing-overload*");
    }

    [Fact]
    public void An_operation_given_the_wrong_number_of_operands_is_refused_when_the_tree_is_compiled()
    {
        var tree = new TypedOperation("int-add", [new TypedLiteral(Terms.Int, 1L, 0)], Terms.Int, 0);

        FluentActions.Invoking(() => TypedExpressionCompiler.Compile(tree)).Should().Throw<InvalidOperationException>().WithMessage("*int-add*2 operands*");
    }

    [Fact]
    public void An_overload_is_refused_before_the_operands_under_it_are_built()
    {
        var tree = new TypedOperation("missing-overload", [new TypedOperation("another-missing", [], Terms.Int, 1)], Terms.Int, 7);

        FluentActions.Invoking(() => TypedExpressionCompiler.Compile(tree)).Should().Throw<InvalidOperationException>().WithMessage("*missing-overload*");
    }

    [Fact]
    public void A_tree_is_compiled_once_and_the_same_closure_answers_every_later_request()
    {
        var tree = Workflow("1 + 2");

        TypedExpressionCompiler.For(tree).Should().BeSameAs(TypedExpressionCompiler.For(tree));
        TypedExpressionCompiler.Compile(tree).Should().NotBeSameAs(TypedExpressionCompiler.For(tree));
    }

    [Fact]
    public void Trees_with_the_same_term_and_position_but_different_operations_keep_their_own_closures()
    {
        var add = Workflow("1 + 2");
        var subtract = Workflow("1 - 2");
        add.Term.Should().Be(subtract.Term);
        add.Position.Should().Be(subtract.Position);
        var scope = Scopes.Evaluation("workflow");

        var addClosure = TypedExpressionCompiler.For(add);
        var subtractClosure = TypedExpressionCompiler.For(subtract);

        addClosure.Should().NotBeSameAs(subtractClosure);
        addClosure.Evaluate(scope).Should().Be(3L);
        subtractClosure.Evaluate(scope).Should().Be(-1L);
    }

    [Fact]
    public void A_cached_closure_answers_as_a_fresh_one_does()
    {
        var tree = Workflow("${previous.n.i} * 2 + len(${previous.n.s})");
        var scope = Scopes.Evaluation("workflow");

        TypedExpressionCompiler.For(tree).Evaluate(scope).Should().Be(TypedExpressionCompiler.Compile(tree).Evaluate(scope)).And.Be(17L);
    }

    [Fact]
    public void One_cached_closure_evaluated_on_many_threads_answers_each_scope_its_own_value()
    {
        var typing = new ExpressionTypingScope.Builder().Previous((_, _) => ReadLookup.Typed(Terms.Int)).Build();
        var closure = TypedExpressionCompiler.For(Typed("${previous.n.i} * 2", typing));
        var scopes = Enumerable.Range(0, 8).Select(input => (Input: (long)input,
            Scope: new ExpressionEvaluationScope.Builder().Previous((_, _) => (true, (long)input)).Build())).ToArray();
        var answers = new ConcurrentBag<(long Input, long Answer)>();

        Parallel.For(0, 4000, i =>
        {
            var (input, scope) = scopes[i % scopes.Length];
            answers.Add((input, (long)closure.Evaluate(scope)!));
        });

        answers.Should().HaveCount(4000).And.OnlyContain(pair => pair.Answer == pair.Input * 2);
    }

    private static TypedExpression Nested(int levels)
    {
        TypedExpression node = new TypedLiteral(Terms.Boolean, true, 0);
        for (var level = 1; level <= levels; level++)
            node = new TypedOperation("not", [node], Terms.Boolean, level);
        return node;
    }

    private sealed class Probe(string term, object? answer)
    {
        public static readonly object NotFound = new();

        public int Calls { get; set; }

        public (ExpressionTypingScope Typing, ExpressionEvaluationScope Evaluation) Scopes()
        {
            var resolver = new Resolver(this, term);
            var binding = new ExpressionNamespaceBinding(resolver, 1, resolver);
            var typing = new ExpressionTypingScope.Builder()
                .Previous((_, output) => ReadLookup.Typed(output is "ni" or "ki" ? Terms.Int.AsNullable() : Terms.Int))
                .Namespace(binding).Build();
            var evaluation = new ExpressionEvaluationScope.Builder()
                .Previous((_, output) => (true, output switch { "ni" => null, "ki" => 5L, _ => 0L }))
                .Namespace(binding).Build();
            return (typing, evaluation);
        }

        private sealed class Resolver(Probe probe, string term) : IExpressionNamespaceResolver, IExpressionNamespaceStaticCheck
        {
            public string Namespace => "ns";

            public ResolveResult Resolve(string[] segments)
            {
                probe.Calls++;
                if (probe.answer is Exception failure)
                    throw failure;
                return ReferenceEquals(probe.answer, NotFound) ? ResolveResult.Failed() : ResolveResult.Resolved(probe.answer);
            }

            public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, term);
        }

        private readonly object? answer = answer;
    }

    private static class ReadingScopes
    {
        public static ExpressionTypingScope Typing()
        {
            var binding = Binding(0);
            return new ExpressionTypingScope.Builder()
                .Previous((_, _) => ReadLookup.Typed(Terms.Int))
                .Parameters(_ => ReadLookup.Typed(Terms.Int))
                .Variables(_ => ReadLookup.Typed(Terms.Int))
                .Helper(new Dictionary<HelperPath, NamespacePathCheckAnswer> { [new HelperPath(["h"])] = new(NamespacePathCheck.Yes, "Int") })
                .Namespace(binding)
                .Build();
        }

        public static ExpressionEvaluationScope Evaluation(long value, int utcHour, int localHour) =>
            new ExpressionEvaluationScope.Builder()
                .Now(new DateTimeOffset(2026, 3, 5, utcHour, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 5, localHour, 0, 0, TimeSpan.Zero))
                .Previous((_, _) => (true, value))
                .Parameters(_ => (true, value))
                .Helper(new Dictionary<HelperPath, object?> { [new HelperPath(["h"])] = value })
                .Namespace(Binding(value))
                .Build()
                .WithVariables(new Dictionary<string, object?> { ["item"] = value });

        private static ExpressionNamespaceBinding Binding(long value)
        {
            var resolver = new Constant(value);
            return new ExpressionNamespaceBinding(resolver, 1, resolver);
        }

        private sealed class Constant(long value) : IExpressionNamespaceResolver, IExpressionNamespaceStaticCheck
        {
            public string Namespace => "ns";

            public ResolveResult Resolve(string[] segments) => ResolveResult.Resolved(value);

            public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "Int");
        }

        private sealed class Helper(long value) : IHelperResolver
        {
            public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.ToDictionary(p => p, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int")));
            public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(paths.ToDictionary(p => p, _ => (object?)value));
        }
    }
}
