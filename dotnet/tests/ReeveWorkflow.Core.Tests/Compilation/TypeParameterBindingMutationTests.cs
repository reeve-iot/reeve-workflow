using FluentAssertions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class TypeParameterBindingMutationTests
{
    public static TheoryData<string> Mutants() =>
        [.. Enum.GetValues<BindingMutant>().Where(m => m != BindingMutant.None).Select(m => m.ToString())];

    [Theory]
    [MemberData(nameof(Mutants))]
    public void A_vector_kills_the_mutant(string mutant)
    {
        TypeParameterBindingContractTests.Failures(Enum.Parse<BindingMutant>(mutant))
            .Should().NotBeEmpty($"no vector tells {mutant} from the compiler's own decision");
    }
}
