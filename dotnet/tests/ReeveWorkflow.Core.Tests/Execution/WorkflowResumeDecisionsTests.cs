using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowResumeDecisionsTests
{
    private static readonly JsonSerializerOptions Json = WorkflowGraphHelpers.JsonOptions;

    private static Dictionary<string, object?> Outputs(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    [Theory]
    [InlineData(WorkflowSuspensionKind.WaitForTrigger, "suspension.resumed_by_trigger")]
    [InlineData(WorkflowSuspensionKind.WaitTemplate, "suspension.resumed_by_poll")]
    public void AWaitThatWasSatisfiedIsAttributedToWhatItWasWaitingOn(
        WorkflowSuspensionKind kind, string expected) =>
        WorkflowResumeDecisions.ResolveResumeAuditKind(kind, Outputs(("outcome", "satisfied")))
            .Should().Be(expected);

    [Theory]
    [InlineData(WorkflowSuspensionKind.WaitForTrigger)]
    [InlineData(WorkflowSuspensionKind.WaitTemplate)]
    public void AWaitThatExpiredIsATimeoutWhateverItWasWaitingOn(WorkflowSuspensionKind kind) =>
        WorkflowResumeDecisions.ResolveResumeAuditKind(kind, Outputs(("outcome", "timed_out")))
            .Should().Be(WorkflowAuditEventKinds.ResumedByTimeout,
                "a trigger wait that ran out of time did not have its trigger fire");

    [Fact]
    public void AHelperChangeIsIndistinguishableFromAPollUntilTheDispatcherSaysSo()
    {
        var looksLikeAPoll = Outputs(("outcome", "satisfied"));
        var saysHelperChange = Outputs(("outcome", "satisfied"), ("_resumeReason", "helper_change"));

        WorkflowResumeDecisions.ResolveResumeAuditKind(WorkflowSuspensionKind.WaitTemplate, looksLikeAPoll)
            .Should().Be(WorkflowAuditEventKinds.ResumedByPoll);
        WorkflowResumeDecisions.ResolveResumeAuditKind(WorkflowSuspensionKind.WaitTemplate, saysHelperChange)
            .Should().Be(WorkflowAuditEventKinds.ResumedByHelperChange,
                "the two arrive with identical kind and outcome, so only the dispatcher can tell them apart");
    }

    [Theory]
    [InlineData("trigger", "suspension.resumed_by_trigger")]
    [InlineData("helper_change", "suspension.resumed_by_helper_change")]
    [InlineData("poll", "suspension.resumed_by_poll")]
    [InlineData("timeout", "suspension.resumed_by_timeout")]
    public void TheDispatchersOwnWordBeatsEveryInference(string reason, string expected) =>
        WorkflowResumeDecisions.ResolveResumeAuditKind(
            WorkflowSuspensionKind.WaitForTrigger, Outputs(("outcome", "timed_out"), ("_resumeReason", reason)))
            .Should().Be(expected,
                "the outcome says it timed out, and the dispatcher is still the one that knows");

    [Fact]
    public void AReasonNobodyRecognisesFallsBackToTheInferenceRatherThanBlankingIt() =>
        WorkflowResumeDecisions.ResolveResumeAuditKind(
            WorkflowSuspensionKind.WaitTemplate, Outputs(("outcome", "timed_out"), ("_resumeReason", "sunspots")))
            .Should().Be(WorkflowAuditEventKinds.ResumedByTimeout);

    [Fact]
    public void OutputsThatSayNothingStillProduceAKind() =>
        WorkflowResumeDecisions.ResolveResumeAuditKind(
            WorkflowSuspensionKind.WaitForTrigger, new Dictionary<string, object?>())
            .Should().Be(WorkflowAuditEventKinds.ResumedByTrigger);

    [Fact]
    public void ANullOutcomeIsNotATimeout() =>
        WorkflowResumeDecisions.ResolveResumeAuditKind(
            WorkflowSuspensionKind.WaitTemplate, Outputs(("outcome", null)))
            .Should().Be(WorkflowAuditEventKinds.ResumedByPoll);

    [Fact]
    public void ARunWithNoRecordedManifestStaysPinnedToItsOwnVersion()
    {
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var manifest = WorkflowResumeDecisions.ResolveResumeManifest(null, workflowId, versionId, Json);

        manifest.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<Guid, Guid>(workflowId, versionId),
                "resuming on current would run a version the caller never chose");
    }

    [Fact]
    public void ARecordedManifestIsHonouredWholeAndNotJustForTheRootWorkflow()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        var rootVersion = Guid.NewGuid();
        var childVersion = Guid.NewGuid();
        var json = JsonSerializer.Serialize(
            new Dictionary<Guid, Guid> { [root] = rootVersion, [child] = childVersion }, Json);

        var manifest = WorkflowResumeDecisions.ResolveResumeManifest(json, root, Guid.NewGuid(), Json);

        manifest.Should().HaveCount(2);
        manifest[root].Should().Be(rootVersion, "the run's own pin must not overwrite the recorded one");
        manifest[child].Should().Be(childVersion, "a sub-workflow's pin is the reason a manifest exists");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("null")]
    public void AManifestThatCannotBeReadFallsBackInsteadOfFailingTheResume(string manifestJson)
    {
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var manifest = WorkflowResumeDecisions.ResolveResumeManifest(manifestJson, workflowId, versionId, Json);

        manifest.Should().ContainSingle().Which.Value.Should().Be(versionId,
            "the run's own version is known and perfectly resumable, so an unreadable manifest must not fail it");
    }

    private static readonly DateTimeOffset Parked = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    private static string SleepConfig(long durationMs) =>
        JsonSerializer.Serialize(new WorkflowSleepConfig(durationMs, Parked.AddMilliseconds(durationMs)), Json);

    [Fact]
    public void ASleepThatRanItsLengthIsASleepResumeNotATimeout()
    {
        var outputs = WorkflowDurableSleep.ResumedOutputs(
            WorkflowDurableSleep.OutcomeJson(SleepConfig(600_000), Parked, Parked.AddMinutes(10)));

        WorkflowResumeDecisions.ResolveResumeAuditKind(WorkflowSuspensionKind.Sleep, outputs)
            .Should().Be(WorkflowAuditEventKinds.ResumedBySleep);
    }

    [Fact]
    public void AResumedSleepReportsExactlyWhatASleepInThisProcessReports()
    {
        var outputs = WorkflowDurableSleep.ResumedOutputs(
            WorkflowDurableSleep.OutcomeJson(SleepConfig(600_000), Parked, Parked.AddMinutes(10)));

        outputs.Should().BeEquivalentTo(new Dictionary<string, object?> { ["Completed"] = true, ["DurationMs"] = 600_000 });
        outputs["DurationMs"].Should().BeOfType<long>("an Int is a long, as the in-memory sleep answers it");
    }

    [Fact]
    public void ASleepWhoseConfigCannotBeReadStillReportsTheLengthItWasParkedFor()
    {
        var outputs = WorkflowDurableSleep.ResumedOutputs(
            WorkflowDurableSleep.OutcomeJson("{ not json", Parked, Parked.AddSeconds(90)));

        outputs["DurationMs"].Should().Be(90_000);
    }

    [Theory]
    [InlineData("""{"outcome":"satisfied","durationMs":"600000"}""")]
    [InlineData("""{"outcome":"satisfied","durationMs":1.5}""")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("[]")]
    public void A_sleep_outcome_whose_duration_does_not_read_as_an_Int_is_refused_rather_than_read_as_zero(string outcomeJson)
    {
        var read = () => WorkflowDurableSleep.ResumedOutputs(outcomeJson);

        read.Should().Throw<JsonException>();
    }

    [Fact]
    public void TheOutcomeOfASleepSaysWhyItWoke()
    {
        using var outcome = JsonDocument.Parse(
            WorkflowDurableSleep.OutcomeJson(SleepConfig(5_000), Parked, Parked.AddSeconds(5)));

        outcome.RootElement.GetProperty("_resumeReason").GetString().Should().Be("sleep");
        outcome.RootElement.GetProperty("durationMs").GetInt64().Should().Be(5_000);
    }
}
