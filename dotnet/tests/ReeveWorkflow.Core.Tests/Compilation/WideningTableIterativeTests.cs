using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class WideningTableIterativeTests
{
    private const int DeepStackSize = 256 * 1024;
    private const int DeepMargin = 10_000;

    private static string DeepBracketName(int depth, string atom) =>
        string.Concat(Enumerable.Repeat("Collection<", depth)) + atom + new string('>', depth);

    private static void OnASmallStack(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        }, maxStackSize: DeepStackSize);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }

    [Fact]
    public void Aliasing_onto_a_10000_deep_collection_name_succeeds_on_a_256KB_stack()
    {
        var deepName = PortValueType.Of(DeepBracketName(DeepMargin, "Point"));

        OnASmallStack(() =>
        {
            var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Deep"), deepName);
            table.KnownTypes.Should().Contain(deepName, because: "WithAlias must actually declare the deep target");
        });
    }

    [Fact]
    public void Declaring_a_10000_deep_collection_name_is_refused_on_a_256KB_stack()
    {
        var deepName = PortValueType.Of(DeepBracketName(DeepMargin, "Point"));

        OnASmallStack(() => PortTypeWideningTable.Builtin.Invoking(t => t.Declaring(deepName))
            .Should().Throw<ArgumentException>().WithMessage("*cannot be declared: only a plain name can be declared*"));
    }

    [Fact]
    public void Canonicalize_of_a_10000_deep_unaliased_name_answers_itself_unchanged_on_a_256KB_stack()
    {
        var deepName = PortValueType.Of(DeepBracketName(DeepMargin, "Point"));

        OnASmallStack(() => PortTypeWideningTable.Builtin.Canonicalize(deepName).Should().Be(deepName));
    }

    [Fact]
    public void Canonicalize_resolves_an_alias_at_the_innermost_element_of_a_10000_deep_name_on_a_256KB_stack()
    {
        var deepAliasName = PortValueType.Of(DeepBracketName(DeepMargin, "bool"));
        var expected = PortValueType.Of(DeepBracketName(DeepMargin, "Boolean"));

        OnASmallStack(() => PortTypeWideningTable.Builtin.Canonicalize(deepAliasName).Should().Be(expected));
    }

    [Fact]
    public void IsAssignable_widens_a_10000_deep_collection_of_Int_to_the_same_depth_of_Number_on_a_256KB_stack()
    {
        var deepInt = PortValueType.Of(DeepBracketName(DeepMargin, "Int"));
        var deepNumber = PortValueType.Of(DeepBracketName(DeepMargin, "Number"));

        OnASmallStack(() => PortTypeWideningTable.Builtin.IsAssignable(deepInt, deepNumber).Should().BeTrue());
    }

    [Fact]
    public void IsAssignable_refuses_a_10000_deep_collection_of_Int_against_the_same_depth_of_String_on_a_256KB_stack()
    {
        var deepInt = PortValueType.Of(DeepBracketName(DeepMargin, "Int"));
        var deepString = PortValueType.Of(DeepBracketName(DeepMargin, "String"));

        OnASmallStack(() => PortTypeWideningTable.Builtin.IsAssignable(deepInt, deepString).Should().BeFalse());
    }

    [Theory]
    [InlineData("bool")]
    [InlineData("Point")]
    public void Canonicalize_allocates_in_proportion_to_the_name_not_its_square(string atom)
    {
        var deep = PortValueType.Of(DeepBracketName(5_000, atom));
        PortTypeWideningTable.Builtin.Canonicalize(deep);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var canonical = PortTypeWideningTable.Builtin.Canonicalize(deep);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        canonical.Name.Should().EndWith(new string('>', 5_000));
        allocated.Should().BeLessThan(20L * sizeof(char) * deep.Name.Length,
            "one render of a 60,000-character name is a few copies of it, never one per level");
    }

    [Fact]
    public void IsAssignable_widens_a_100000_deep_collection_of_Int_to_the_same_depth_of_Number()
    {
        var deepInt = PortValueType.Of(DeepBracketName(100_000, "Int"));
        var deepNumber = PortValueType.Of(DeepBracketName(100_000, "Number"));

        PortTypeWideningTable.Builtin.IsAssignable(deepInt, deepNumber).Should().BeTrue();
    }
}
