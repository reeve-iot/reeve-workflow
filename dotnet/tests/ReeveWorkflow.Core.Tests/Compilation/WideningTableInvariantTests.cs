using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class WideningTableInvariantTests
{
    [Fact]
    public void An_alias_that_would_retarget_an_already_declared_collection_shaped_name_is_refused()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Origins"), PortValueType.Of("Collection<Origin>"));

        var alias = () => table.WithAlias(PortValueType.Of("Origin"), PortValueType.Of("StartPoint"));

        alias.Should().Throw<ArgumentException>().WithMessage("*Collection<Origin>*Collection<StartPoint>*")
            .Which.ParamName.Should().Be("alias");
    }

    [Fact]
    public void The_same_two_calls_in_the_opposite_order_also_refuse()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Origin"), PortValueType.Of("StartPoint"));

        var alias = () => table.WithAlias(PortValueType.Of("Origins"), PortValueType.Of("Collection<Origin>"));

        alias.Should().Throw<ArgumentException>().WithMessage("*'Collection<StartPoint>'*'StartPoint'*another depth*")
            .Which.ParamName.Should().Be("canonical");
    }

    [Fact]
    public void WithAlias_still_accepts_an_alias_that_touches_no_declared_names_chain()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Widget"));

        var alias = () => table.WithAlias(PortValueType.Of("wid"), PortValueType.Of("Widget"));

        alias.Should().NotThrow();
    }

    [Fact]
    public void WithAlias_accepts_a_case_variant_identity_alias_of_an_already_declared_name()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Point"));

        var alias = () => table.WithAlias(PortValueType.Of("point"), PortValueType.Of("point"));

        alias.Should().NotThrow();
    }

    [Fact]
    public void WithAlias_refuses_when_its_own_target_would_stop_canonicalising_to_itself()
    {
        var alias = () => PortTypeWideningTable.Builtin.WithAlias(
            PortValueType.Of("Origin"), PortValueType.CollectionOf(PortValueType.Of("Origin")));

        alias.Should().Throw<ArgumentException>().WithMessage("*Collection<Origin>*Collection<Collection<Origin>>*");
    }

    private static readonly string[] NamePool =
    [
        "Point", "point", "Collection<Point>", "s", "bool", "int", "BOOLEAN", "Origin", "Waypoint",
        "Zed", "Any", "Collection<Nested>",
    ];

    private readonly record struct Op(string Label, OpKind Kind, string A, string B)
    {
        public PortTypeWideningTable? Apply(PortTypeWideningTable table)
        {
            try
            {
                return Kind switch
                {
                    OpKind.Declare => table.Declaring(PortValueType.Of(A)),
                    OpKind.With => table.With(PortValueType.Of(A), [PortValueType.Of(B)]),
                    OpKind.WithAlias => table.WithAlias(PortValueType.Of(A), PortValueType.Of(B)),
                    _ => throw new InvalidOperationException(),
                };
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private enum OpKind { Declare, With, WithAlias }

    private static List<Op> BuildOps()
    {
        var ops = new List<Op>();
        foreach (var name in NamePool)
            ops.Add(new Op($"Declaring({name})", OpKind.Declare, name, ""));

        foreach (var a in NamePool)
            foreach (var b in NamePool)
            {
                if (a == b)
                    continue;
                ops.Add(new Op($"With({a},[{b}])", OpKind.With, a, b));
                ops.Add(new Op($"WithAlias({a},{b})", OpKind.WithAlias, a, b));
            }

        return ops;
    }

    private static List<Op[]> Sequences()
    {
        var ops = BuildOps();
        var sequences = new List<Op[]>(ops.Count + ops.Count * ops.Count + 5000);

        foreach (var op in ops)
            sequences.Add([op]);

        foreach (var op1 in ops)
            foreach (var op2 in ops)
                sequences.Add([op1, op2]);

        var random = new Random(20260923);
        for (var i = 0; i < 5000; i++)
            sequences.Add([ops[random.Next(ops.Count)], ops[random.Next(ops.Count)], ops[random.Next(ops.Count)]]);

        return sequences;
    }

    [Fact]
    public void WithAlias_never_breaks_the_canonical_form_invariant_across_a_generated_corpus()
    {
        var sequences = Sequences();
        var acceptedSteps = 0;
        var refusedSteps = 0;

        foreach (var sequence in sequences)
        {
            var table = PortTypeWideningTable.Builtin;
            var trace = new List<string>();

            foreach (var op in sequence)
            {
                trace.Add(op.Label);
                var result = op.Apply(table);

                if (result is null)
                {
                    refusedSteps++;
                    continue;
                }

                acceptedSteps++;
                table = result;
                foreach (var known in table.KnownTypes)
                {
                    table.Canonicalize(known).Should().Be(known,
                        because: $"{string.Join(" -> ", trace)} must keep every declared name canonical");
                    table.Canonicalize(table.Canonicalize(known)).Should().Be(table.Canonicalize(known),
                        because: "Canonicalize must be ordinally idempotent");
                }
            }
        }

        sequences.Count.Should().Be(81_452);
        acceptedSteps.Should().Be(71_327);
        refusedSteps.Should().Be(96_301);
    }
}
