using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowParkedWaitKeysTests
{
    private static readonly JsonSerializerOptions JsonOptions = WorkflowGraphHelpers.JsonOptions;

    private static IReadOnlySet<string> KeysOf(WorkflowSuspensionKind kind, string configJson) =>
        WorkflowSuspensionWaker.DependencyKeysOf(
            kind, configJson, ExpressionDependencyWalker.Instance, JsonOptions);

    [Fact]
    public void A_registered_namespaces_key_ends_where_its_registration_says_and_the_field_path_is_not_in_it()
    {
        var registration = new WorkflowExpressionNamespace("trigger", 1, new TriggerCheck(),
            (_, _) => throw new InvalidOperationException("not needed"));

        var keys = WorkflowSuspensionWaker.DependencyKeysOf(
            WorkflowSuspensionKind.WaitTemplate, TemplateConfig("${trigger.payload.auth.user} == \"ada\""),
            ExpressionDependencyWalker.For([registration]), JsonOptions);

        keys.Should().BeEquivalentTo(["trigger:payload"]);
    }

    private sealed class TriggerCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "trigger";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "String");
    }

    private static string TemplateConfig(string expression) =>
        JsonSerializer.Serialize(new WorkflowStoredTemplateConfig("Condition", null, expression), JsonOptions);

    private static string TriggerConfig(params string[] triggerIds) =>
        JsonSerializer.Serialize(new WorkflowWaitForTriggerConfig(triggerIds), JsonOptions);

    [Fact]
    public void A_templates_keys_are_what_its_expression_reads()
    {
        KeysOf(
                WorkflowSuspensionKind.WaitTemplate,
                TemplateConfig("${helper.vacation_mode} || ${device.kitchen.temperature} > 22"))
            .Should().BeEquivalentTo(["helper:vacation_mode", "device:kitchen:temperature"]);
    }

    [Fact]
    public void A_trigger_waits_keys_are_its_trigger_ids()
    {
        KeysOf(WorkflowSuspensionKind.WaitForTrigger, TriggerConfig("door-opened", "sun-set"))
            .Should().BeEquivalentTo(["trigger-id:door-opened", "trigger-id:sun-set"]);
    }

    [Fact]
    public void A_trigger_id_cannot_collide_with_a_condition_that_reads_the_trigger_envelope()
    {
        var ids = KeysOf(WorkflowSuspensionKind.WaitForTrigger, TriggerConfig("doorOpened"));
        var envelope = KeysOf(WorkflowSuspensionKind.WaitTemplate, TemplateConfig("${trigger.doorOpened} == 1"));

        envelope.Should().BeEquivalentTo(["trigger:doorOpened"]);
        ids.Should().NotIntersectWith(envelope);
    }

    [Fact]
    public void A_trigger_id_is_the_id_the_row_spells_and_not_a_tidied_one()
    {
        KeysOf(WorkflowSuspensionKind.WaitForTrigger, TriggerConfig("door ", " door", "door"))
            .Should().BeEquivalentTo(["trigger-id:door ", "trigger-id: door", "trigger-id:door"]);
    }

    [Fact]
    public void A_kind_this_reading_has_not_been_taught_is_refused()
    {
        var reading = () => KeysOf((WorkflowSuspensionKind)99, TemplateConfig("${device.a.b}"));

        reading.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(WorkflowSuspensionKind.Sleep)]
    [InlineData(WorkflowSuspensionKind.SubWorkflow)]
    public void A_wait_no_event_ends_has_no_keys(WorkflowSuspensionKind kind)
    {
        KeysOf(kind, "{\"durationMs\":60000,\"wakeAtUtc\":\"2026-01-01T00:00:00+00:00\"}").Should().BeEmpty();
    }

    [Fact]
    public void A_template_that_reads_nothing_has_no_keys()
    {
        KeysOf(WorkflowSuspensionKind.WaitTemplate, TemplateConfig("1 == 1")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowSuspensionKind.WaitTemplate)]
    [InlineData(WorkflowSuspensionKind.WaitForTrigger)]
    public void A_config_that_cannot_be_read_has_no_keys_and_does_not_throw(WorkflowSuspensionKind kind)
    {
        KeysOf(kind, "not json at all").Should().BeEmpty();
        KeysOf(kind, "{}").Should().BeEmpty();
        KeysOf(kind, "").Should().BeEmpty();
    }

    [Fact]
    public void An_expression_that_cannot_be_parsed_has_no_keys_and_does_not_throw()
    {
        KeysOf(WorkflowSuspensionKind.WaitTemplate, TemplateConfig("${device.kitchen.")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_written_before_this_release_has_no_index_to_build()
    {
        IWorkflowSuspensionResumeStore store = new StoreWithoutTheScan();
        var cursor = new WorkflowSuspensionScanCursor(0) { AfterSuspendedAtUtc = DateTimeOffset.UtcNow };

        var read = await store.ScanParkedWaitsAsync(cursor, 50, null, default);

        read.Waits.Should().BeEmpty();
        read.NextCursor.Should().BeSameAs(WorkflowSuspensionScanCursor.Start,
            "an empty page is the end of the waits, and a host looping until the cursor comes back to the start "
            + "would spin for ever against a store that handed its own cursor back");
    }

    private sealed class StoreWithoutTheScan : IWorkflowSuspensionResumeStore
    {
        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, [], 0));

        public Task<bool> ReleaseAsync(
            Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<ResumeContext?>(null);
    }

    [Fact]
    public void A_wait_carries_its_keys_and_no_reading_of_them()
    {
        KeysOf(WorkflowSuspensionKind.WaitTemplate, TemplateConfig("${helper.evening} && ${device.dock.free}"))
            .Should().BeEquivalentTo(["helper:evening", "device:dock:free"], "the walker's spelling, passed through");

        typeof(WorkflowParkedWait).GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(
                [nameof(WorkflowParkedWait.SuspensionId), nameof(WorkflowParkedWait.ExecutionLogId),
                    nameof(WorkflowParkedWait.Kind), nameof(WorkflowParkedWait.DependencyKeys),
                    nameof(WorkflowParkedWait.SuspendedAtUtc)],
                "a wait carries the row and its keys; anything read off those keys belongs to whoever indexes them");
    }
}
