using FluentAssertions;
using ReeveWorkflow.Npgsql;

namespace ReeveWorkflow.Npgsql.Tests;

public sealed class NpgsqlWorkflowLockIdTests
{
    private const string BudgetKey = "WorkflowRunBudget_0a4b1c2d3e4f5061728394a5b6c7d8e9";
    private const string NeighbouringKey = "WorkflowRunBudget_0a4b1c2d3e4f5061728394a5b6c7d8ea";

    [Fact]
    public void A_lock_name_derives_one_number_that_every_replica_has_to_arrive_at()
    {
        NpgsqlWorkflowLockOptions.DeriveLockId(BudgetKey).Should().Be(-42371777404804019L);
        NpgsqlWorkflowLockOptions.DeriveLockId(NeighbouringKey).Should().Be(4511810133247674927L);
    }

    [Fact]
    public void Two_lock_names_that_differ_do_not_share_an_advisory_lock()
    {
        NpgsqlWorkflowLockOptions.DeriveLockId(NeighbouringKey)
            .Should().NotBe(NpgsqlWorkflowLockOptions.DeriveLockId(BudgetKey),
                "two workflows sharing one advisory lock would make each of them block the other's runs");
    }

    [Fact]
    public void The_derivation_is_the_one_the_options_hand_out_by_default()
    {
        new NpgsqlWorkflowLockOptions().LockId(BudgetKey)
            .Should().Be(NpgsqlWorkflowLockOptions.DeriveLockId(BudgetKey));
    }
}
