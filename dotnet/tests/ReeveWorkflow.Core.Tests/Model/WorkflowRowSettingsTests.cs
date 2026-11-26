using FluentAssertions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Model;

public class WorkflowRowSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(WorkflowRowSettings.LongestTimeoutSeconds)]
    public void A_timeout_that_is_absent_or_a_positive_number_a_timer_holds_is_stored(int? seconds) =>
        WorkflowRowSettings.TimeoutFault(seconds).Should().BeNull();

    [Theory]
    [InlineData(0, WorkflowRowSettings.TimeoutNotPositive)]
    [InlineData(-5, WorkflowRowSettings.TimeoutNotPositive)]
    [InlineData(WorkflowRowSettings.LongestTimeoutSeconds + 1, WorkflowRowSettings.TimeoutAboveTimerLimit)]
    public void A_timeout_the_run_reads_as_none_or_a_timer_cannot_hold_is_refused(int seconds, string reason) =>
        WorkflowRowSettings.TimeoutFault(seconds)!.Reason.Should().Be(reason);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{}")]
    [InlineData("""{"maxAttempts":3,"backoffStrategy":"exponential","initialDelayMs":100}""")]
    [InlineData("""{"MaxAttempts":2,"BackoffStrategy":"fixed"}""")]
    [InlineData("""{"backoffStrategy":"exponential"}""")]
    public void A_retry_policy_the_run_reads_as_written_is_stored(string? json) =>
        WorkflowRowSettings.RetryPolicyFault(json).Should().BeNull();

    [Theory]
    [InlineData("not json", WorkflowRowSettings.RetryPolicyMalformed)]
    [InlineData("[]", WorkflowRowSettings.RetryPolicyMalformed)]
    [InlineData("null", WorkflowRowSettings.RetryPolicyMalformed)]
    [InlineData("""{"maxAttempts":"many"}""", WorkflowRowSettings.RetryPolicyMalformed)]
    [InlineData("""{"backoffStrategy":"linear"}""", WorkflowRowSettings.RetryBackoffOther)]
    [InlineData("""{"backoffStrategy":"Exponential"}""", WorkflowRowSettings.RetryBackoffOther)]
    [InlineData("""{"backoffStrategy":null}""", WorkflowRowSettings.RetryBackoffOther)]
    [InlineData("""{"maxAttempts":0}""", WorkflowRowSettings.RetryMaxAttemptsBelowOne)]
    [InlineData("""{"MAXATTEMPTS":-1}""", WorkflowRowSettings.RetryMaxAttemptsBelowOne)]
    [InlineData("""{"initialDelayMs":0}""", WorkflowRowSettings.RetryInitialDelayBelowOne)]
    [InlineData("""{"maxAttempts":2,"initialDelayMs":-1}""", WorkflowRowSettings.RetryInitialDelayBelowOne)]
    public void A_retry_policy_the_run_reads_as_other_than_written_is_refused(string json, string reason) =>
        WorkflowRowSettings.RetryPolicyFault(json)!.Reason.Should().Be(reason);

    [Fact]
    public void Refusal_names_the_timeout_before_the_policy() =>
        WorkflowRowSettings.Refusal(0, "not json").Should().Contain("ExecutionTimeoutSeconds");

    private static Workflow Row(int? timeout = null, string? policy = null) =>
        new(Guid.NewGuid(), "w", null, Guid.NewGuid(), DateTimeOffset.UnixEpoch, null, timeout, policy);

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(WorkflowRowSettings.LongestTimeoutSeconds + 1, null)]
    [InlineData(null, "not json")]
    [InlineData(null, """{"backoffStrategy":"linear"}""")]
    [InlineData(null, """{"backoffStrategy":"Exponential"}""")]
    [InlineData(null, """{"maxAttempts":0}""")]
    [InlineData(null, """{"initialDelayMs":0}""")]
    public void The_Workflow_constructor_refuses_a_setting_the_run_reads_as_other_than_written(int? timeout, string? policy) =>
        FluentActions.Invoking(() => Row(timeout, policy)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, "not json")]
    [InlineData(null, """{"maxAttempts":-2}""")]
    public void Workflow_Update_refuses_the_same_settings_and_leaves_the_row_as_it_was(int? timeout, string? policy)
    {
        var workflow = Row(30, """{"maxAttempts":2}""");

        FluentActions.Invoking(() => workflow.Update("renamed", null, DateTimeOffset.UnixEpoch, null, timeout, policy))
            .Should().Throw<ArgumentException>();

        workflow.Name.Should().Be("w");
        workflow.ExecutionTimeoutSeconds.Should().Be(30);
        workflow.RetryPolicyJson.Should().Be("""{"maxAttempts":2}""");
    }

    [Fact]
    public void The_Workflow_constructor_and_Update_store_a_setting_the_run_reads_as_written()
    {
        var workflow = Row(30, """{"maxAttempts":3,"backoffStrategy":"exponential","initialDelayMs":100}""");

        workflow.Update("w", null, DateTimeOffset.UnixEpoch, null, null, null);

        workflow.ExecutionTimeoutSeconds.Should().BeNull();
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("null", null)]
    [InlineData(null, "")]
    [InlineData(null, "null")]
    public void The_WorkflowVersion_constructor_refuses_a_blank_or_null_parameter_list(string? inputs, string? outputs) =>
        FluentActions.Invoking(() => new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{}", inputs, outputs, Guid.NewGuid(), DateTimeOffset.UnixEpoch))
            .Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(null, null)]
    [InlineData("[]", "[]")]
    [InlineData("""[{"name":"a","type":5}]""", """[{"name":"a","type":5}]""")]
    public void The_WorkflowVersion_constructor_stores_an_absent_empty_or_entry_unreadable_parameter_list(string? inputs, string? outputs) =>
        new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{}", inputs, outputs, Guid.NewGuid(), DateTimeOffset.UnixEpoch)
            .InputParametersJson.Should().Be(inputs);

    [Theory]
    [InlineData("", null, "inputParametersJson")]
    [InlineData("null", null, "inputParametersJson")]
    [InlineData(null, " ", "outputParametersJson")]
    [InlineData(null, "null", "outputParametersJson")]
    public void UpdateDefinition_refuses_a_blank_or_null_parameter_list_and_keeps_what_the_version_held(string? inputs, string? outputs, string parameter)
    {
        var version = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{}", "[]", "[]", Guid.NewGuid(), DateTimeOffset.UnixEpoch);

        FluentActions.Invoking(() => version.UpdateDefinition("{\"changed\":true}", inputs, outputs))
            .Should().Throw<ArgumentException>().WithParameterName(parameter);

        version.DefinitionJson.Should().Be("{}");
        version.InputParametersJson.Should().Be("[]");
        version.OutputParametersJson.Should().Be("[]");
    }
}
