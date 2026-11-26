using Dispatch.Host.Freight;
using FluentAssertions;

namespace Dispatch.Host.Tests;

public sealed class BlockRepeatDeclarationTests
{
    [Fact]
    public void The_block_with_an_effect_the_desk_will_not_repeat_says_so_itself()
    {
        new PrintLabelBlockFactory().IsIdempotent.Should().BeFalse(
            "a label print that timed out and was retried is a second label on the pallet, and "
            + "this declaration is what keeps the retry away — the README says so in the host's own words");
    }

    [Fact]
    public void The_blocks_it_is_content_to_repeat_declare_that_and_keep_their_retries()
    {
        new AssignCarrierBlockFactory().IsIdempotent.Should().BeTrue();
        new HoldUntilBlockFactory().IsIdempotent.Should().BeTrue(
            "silence would now read as 'do not repeat', which for these two would drop the retries this "
            + "desk relies on for a transient carrier API failure");
    }
}
