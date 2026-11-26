using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowSleepScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(15);

    [Fact]
    public void ALongSleepIsBrokenIntoPolls() =>
        WorkflowSleepSchedule.NextWait(Now, Now.AddHours(6), Poll).Should().Be(Poll,
            "a run cancelled in the database has no way to reach a replica that is asleep for six hours");

    [Fact]
    public void TheLastStretchIsShorterThanAPollAndIsNotRoundedUp() =>
        WorkflowSleepSchedule.NextWait(Now, Now.AddSeconds(4), Poll).Should().Be(TimeSpan.FromSeconds(4),
            "waiting the full poll would overshoot the wake-up the caller asked for");

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public void AWakeUpAlreadyPassedEndsTheSleep(int secondsFromNow) =>
        WorkflowSleepSchedule.NextWait(Now, Now.AddSeconds(secondsFromNow), Poll).Should().Be(TimeSpan.Zero);

    [Fact]
    public void AWaitExactlyOnePollLongIsOnePollAndNotTwo() =>
        WorkflowSleepSchedule.NextWait(Now, Now + Poll, Poll).Should().Be(Poll);

    [Fact]
    public void RenewalIsDueOnTheIntervalAndNotBefore()
    {
        var renew = TimeSpan.FromSeconds(15);

        WorkflowSleepSchedule.ShouldRenew(Now, Now.AddSeconds(-14), renew).Should().BeFalse();
        WorkflowSleepSchedule.ShouldRenew(Now, Now.AddSeconds(-15), renew).Should().BeTrue(
            "the reclaim cutoff runs on a clock that does not wait for strictly-greater");
        WorkflowSleepSchedule.ShouldRenew(Now, Now.AddSeconds(-600), renew).Should().BeTrue();
    }

    [Fact]
    public void ARenewalJustTakenIsNotDueAgain() =>
        WorkflowSleepSchedule.ShouldRenew(Now, Now, TimeSpan.FromSeconds(15)).Should().BeFalse();
}
