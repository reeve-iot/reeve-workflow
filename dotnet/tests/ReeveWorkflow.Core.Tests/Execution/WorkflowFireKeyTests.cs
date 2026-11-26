using FluentAssertions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowFireKeyTests
{
    [Fact]
    public void One_key_always_fingerprints_to_the_same_token()
    {
        const string key = "state:sensor-4|obs:2026-09-08T10:00:00.0000000Z";

        WorkflowFireKey.Fingerprint(key).Should().Be(WorkflowFireKey.Fingerprint(key));
    }

    [Fact]
    public void Two_keys_that_differ_anywhere_fingerprint_differently()
    {
        var first = WorkflowFireKey.Fingerprint("geo:sha:user-7|52.2297|21.0122");
        var second = WorkflowFireKey.Fingerprint("geo:sha:user-7|52.2297|21.0123");

        first.Should().NotBe(second);
    }

    [Fact]
    public void The_token_carries_nothing_of_the_key_it_came_from()
    {
        const string key = "geo:sha:8f14e45f-user|52.2297|21.0122";

        var fingerprint = WorkflowFireKey.Fingerprint(key);

        fingerprint.Should().NotContain("8f14e45f-user").And.NotContain("52.2297").And.NotContain("21.0122");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_caller_that_could_not_derive_a_key_still_has_something_to_log(string? key)
    {
        WorkflowFireKey.Fingerprint(key).Should().Be("-");
    }

    [Fact]
    public void The_token_is_the_one_the_first_adopter_already_writes()
    {
        WorkflowFireKey.Fingerprint("state:sensor-4|obs:2026-09-08T10:00:00.0000000Z")
            .Should().Be("745AC7F9FC668AFD");
    }

    [Fact]
    public void The_seam_answers_a_keyless_fire_the_same_way_the_helper_does()
    {
        new SingleReplicaFireDedup().Fingerprint(string.Empty).Should().Be("-");

        var fingerprintingNull = () => new SingleReplicaFireDedup().Fingerprint(null!);
        fingerprintingNull.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void The_seam_that_never_de_duplicates_fingerprints_the_same_way_as_the_ones_that_do()
    {
        const string key = "event:9|sha:abc";

        new SingleReplicaFireDedup().Fingerprint(key).Should().Be(WorkflowFireKey.Fingerprint(key));
    }
}
