using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowSuspensionWakerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_run_past_its_deadline_is_moved_with_the_outcome_its_block_has_a_branch_for()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1)));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.TimedOut);
        world.Resumer.Resumed.Should().ContainSingle()
            .Which.OutcomeJson.Should().Contain(WorkflowWaitOutcomes.TimedOut);
    }

    [Fact]
    public async Task A_template_that_has_become_true_moves_its_run_as_satisfied()
    {
        var world = new World(Template("${parameter.threshold} > 5"));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        world.Resumer.Resumed.Should().ContainSingle()
            .Which.OutcomeJson.Should().Contain(WorkflowWaitOutcomes.Satisfied);
    }

    [Fact]
    public async Task One_waker_pass_reads_each_templates_helper_paths_in_one_batch()
    {
        var resolver = new CountingHelperResolver();
        var world = new World(
            Template("${helper.first} + ${helper.first} > 0"),
            Template("${helper.second} > 0")) { Helper = resolver };

        await world.Waker().WakeOnceAsync(default);

        resolver.ReadBatches.Should().HaveCount(2);
        resolver.ReadBatches.Select(batch => batch.Select(path => string.Join('.', path.Segments)).Single())
            .Should().BeEquivalentTo("first", "second");
    }

    [Fact]
    public async Task A_waker_candidate_without_helper_paths_does_not_check_or_read_helpers()
    {
        var resolver = new CountingHelperResolver();
        var world = new World(Template("1 > 0")) { Helper = resolver };

        await world.Waker().WakeOnceAsync(default);

        resolver.CheckCalls.Should().Be(0);
        resolver.ReadBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_helper_check_that_times_out_leaves_its_row_parked_and_does_not_abort_the_pass()
    {
        var world = new World(Template("${helper.first} > 0"), Template("1 > 0")) { Helper = new TimingOutCheckResolver() };

        var woken = await world.Waker().WakeOnceAsync(default);

        woken.Should().Be(1);
        world.Resumer.Resumed.Should().ContainSingle();
    }

    [Fact]
    public async Task A_throwing_waker_helper_read_faults_the_candidate_instead_of_deferring_it()
    {
        var world = new World(Template("${helper.first} > 0")) { Helper = new ThrowingReadResolver() };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Resumer.Resumed.Should().ContainSingle().Which.OutcomeJson
            .Should().Contain(WorkflowWaitOutcomes.Faulted).And.Contain(WorkflowErrorCodes.ExpressionRuntimeError);
    }

    [Fact]
    public async Task Rows_pinned_to_one_version_load_its_program_once_in_a_pass()
    {
        var first = Template("1 == 1");
        var second = Parked(kind: WorkflowSuspensionKind.WaitTemplate) with { WorkflowId = first.WorkflowId };
        second = ParkedTemplates.PinToSameVersion(second, "1 == 1", first.ExecutionLogId);
        var versionId = ParkedTemplates.VersionIdFor(first.ExecutionLogId);
        var before = ParkedTemplates.VersionLoadCount(versionId);
        var world = new World(first, second);
        var waker = world.Waker();

        (await waker.WakeOnceAsync(default)).Should().Be(2);

        (ParkedTemplates.VersionLoadCount(versionId) - before).Should().Be(1,
            "both rows read the same pinned version during this pass");
        world.Store.Taken.Select(take => take.Id).Should().Contain([first.SuspensionId, second.SuspensionId]);
    }

    [Fact]
    public async Task Rows_with_different_versions_resolve_each_program()
    {
        var first = Template("1 == 1");
        var second = Template("1 == 1");
        var firstVersion = ParkedTemplates.VersionIdFor(first.ExecutionLogId);
        var secondVersion = ParkedTemplates.VersionIdFor(second.ExecutionLogId);
        var beforeFirst = ParkedTemplates.VersionLoadCount(firstVersion);
        var beforeSecond = ParkedTemplates.VersionLoadCount(secondVersion);
        var world = new World(first, second);
        var waker = world.Waker();

        (await waker.WakeOnceAsync(default)).Should().Be(2);

        (ParkedTemplates.VersionLoadCount(firstVersion) - beforeFirst).Should().Be(1);
        (ParkedTemplates.VersionLoadCount(secondVersion) - beforeSecond).Should().Be(1);
        world.Store.Taken.Select(take => take.Id).Should().Contain([first.SuspensionId, second.SuspensionId]);
    }

    [Fact]
    public async Task Rows_of_one_version_with_different_manifests_resolve_separately()
    {
        var first = Template("1 == 1");
        var second = ParkedTemplates.PinToSameVersion(
            Parked(kind: WorkflowSuspensionKind.WaitTemplate) with { WorkflowId = first.WorkflowId },
            "1 == 1", first.ExecutionLogId);
        var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(second.StatePayloadJson)!;
        second = second with
        {
            StatePayloadJson = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
            {
                VariablesJson = payload.VariablesJson,
                PreviousOutputsJson = payload.PreviousOutputsJson,
                TriggerEnvelopeJson = payload.TriggerEnvelopeJson,
                ExecutionStateId = payload.ExecutionStateId,
                InputNodeId = payload.InputNodeId,
                PinnedVersionsJson = JsonSerializer.Serialize(new Dictionary<Guid, Guid>
                {
                    [Guid.NewGuid()] = Guid.NewGuid()
                })
            })
        };
        var versionId = ParkedTemplates.VersionIdFor(first.ExecutionLogId);
        var before = ParkedTemplates.VersionLoadCount(versionId);
        var world = new World(first, second);

        (await world.Waker().WakeOnceAsync(default)).Should().Be(2);

        (ParkedTemplates.VersionLoadCount(versionId) - before).Should().Be(2,
            "the resume manifest is part of the program resolution key");
    }

    [Fact]
    public async Task A_version_resolution_failure_stays_with_rows_of_that_version()
    {
        var failed = Template("1 == 1");
        var failedPeer = ParkedTemplates.PinToSameVersion(
            Parked(kind: WorkflowSuspensionKind.WaitTemplate) with { WorkflowId = failed.WorkflowId },
            "1 == 1", failed.ExecutionLogId);
        var good = Template("1 == 1");
        var failedVersion = ParkedTemplates.VersionIdFor(failed.ExecutionLogId);
        var beforeFailed = ParkedTemplates.VersionLoadCount(failedVersion);
        ParkedTemplates.ForgetVersion(failedVersion);
        var world = new World(failed, failedPeer, good);

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Store.Taken.Select(take => take.Id).Should().ContainSingle().Which.Should().Be(good.SuspensionId);
        world.Store.Taken.Select(take => take.Id).Should().NotContain([failed.SuspensionId, failedPeer.SuspensionId]);
        world.Resumer.Resumed.Should().ContainSingle();
        (ParkedTemplates.VersionLoadCount(failedVersion) - beforeFailed).Should().Be(1,
            "both failed rows share one resolution attempt in the pass");
    }

    [Fact]
    public async Task A_later_pass_resolves_the_version_again()
    {
        var row = Template("1 == 2");
        var versionId = ParkedTemplates.VersionIdFor(row.ExecutionLogId);
        var before = ParkedTemplates.VersionLoadCount(versionId);
        var world = new World(row);
        var waker = world.Waker();

        (await waker.WakeOnceAsync(default)).Should().Be(0);
        (await waker.WakeOnceAsync(default)).Should().Be(0);

        (ParkedTemplates.VersionLoadCount(versionId) - before).Should().Be(2,
            "a later pass must observe a fresh version and dependency fingerprint");
    }

    [Fact]
    public async Task A_resumed_wait_is_told_why_it_woke_and_what_the_template_said()
    {
        var world = new World(Template("2 + 3 == 5"));

        await world.Waker().WakeOnceAsync(default);

        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
        outcome.RootElement.GetProperty("_resumeReason").GetString().Should().Be("poll");
        outcome.RootElement.GetProperty("lastEvaluation").GetBoolean().Should().BeTrue("a condition is Boolean and answered true");
    }

    [Fact]
    public async Task A_wait_that_expired_says_so_and_carries_no_evaluation()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1)));

        await world.Waker().WakeOnceAsync(default);

        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("_resumeReason").GetString().Should().Be("timeout");
        outcome.RootElement.TryGetProperty("lastEvaluation", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_template_that_is_still_false_is_left_where_it_is()
    {
        var world = new World(Template("1 == 2"));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);

        world.Store.Taken.Should().BeEmpty("nothing has happened to this run yet");
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_reads_the_scope_the_run_parked_with()
    {
        var world = new World(Template("${parameter.threshold} > 5"));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);
        JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson).RootElement
            .GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
    }

    [Fact]
    public async Task A_template_that_became_true_after_its_deadline_still_missed_it()
    {
        var world = new World(Template("1 == 1") with { TimeoutAtUtc = Now.AddSeconds(-1) });

        await world.Waker().WakeOnceAsync(default);

        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.TimedOut);
    }

    [Fact]
    public async Task A_row_a_peer_took_first_is_not_resumed_here_as_well()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1))) { TakenByAPeer = true };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);

        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_this_pass_cannot_read_waits_for_its_timeout_rather_than_guessing()
    {
        var unreadable = Parked(kind: WorkflowSuspensionKind.WaitTemplate) with { ConfigJson = "{ not json" };
        var world = new World(unreadable);

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0, "it is not satisfied and not yet late");

        world.Store.Candidates = [unreadable with { TimeoutAtUtc = Now.AddSeconds(-1) }];
        (await world.Waker().WakeOnceAsync(default)).Should().Be(1, "its deadline is what it still has");
    }

    [Fact]
    public async Task A_run_that_could_not_be_re_entered_does_not_stop_the_ones_behind_it()
    {
        var world = new World(
            Parked(timeoutAtUtc: Now.AddSeconds(-2)),
            Parked(timeoutAtUtc: Now.AddSeconds(-1)))
        {
            Resumer = { FailFirst = true }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Store.Taken.Should().HaveCount(2, "both were ready, and both were taken before either re-entered");
    }

    [Fact]
    public async Task A_pass_that_cannot_read_the_table_says_so_and_moves_nothing()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1))) { FindThrows = true };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);

        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_that_has_not_finished_parking_yet_is_left_for_the_next_pass()
    {
        var world = new World(Template("1 == 1") with { SuspendedAtUtc = Now.AddMilliseconds(-100) });

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);

        world.Store.Taken.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_past_its_deadline_is_older_than_that_window_by_construction()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1)));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1,
            "waiting for a settle window here would delay every timeout by it, for nothing");
    }

    private static WorkflowSuspensionCandidate Parked(
        DateTimeOffset? timeoutAtUtc = null, WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitForTrigger) =>
        new(
            SuspensionId: Guid.NewGuid(),
            ExecutionLogId: Guid.NewGuid(),
            WorkflowId: Guid.NewGuid(),
            BlockId: "wait-1",
            Kind: kind,
            ConfigJson: "{}",
            StatePayloadJson: Payload,
            SuspendedAtUtc: Now.AddMinutes(-5),
            TimeoutAtUtc: timeoutAtUtc ?? Now.AddMinutes(5));

    [Fact]
    public async Task A_run_the_resume_would_not_take_is_put_back_rather_than_spent()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1)))
        {
            Resumer = new RecordingResumer { Answer = WorkflowResumeOutcome.Deferred }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0, "nothing moved");

        world.Store.Released.Should().ContainSingle()
            .Which.TakenAs.Should().Be(WorkflowSuspensionStatus.TimedOut,
                "the status this pass wrote is what it hands back, so a peer's cancel is not undone");
    }

    [Fact]
    public async Task A_run_the_resume_refuses_for_good_keeps_the_row_it_was_taken_with()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1)))
        {
            Resumer = new RecordingResumer { Answer = WorkflowResumeOutcome.Abandoned }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);

        world.Store.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_the_resume_finds_somebody_else_holding_is_neither_put_back_nor_counted()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1)))
        {
            Resumer = new RecordingResumer { Answer = WorkflowResumeOutcome.HeldElsewhere }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);

        world.Store.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_release_that_fails_does_not_take_the_pass_down_with_it()
    {
        var world = new World(
            Parked(timeoutAtUtc: Now.AddSeconds(-1)),
            Template("1 == 1"))
        {
            Resumer = new RecordingResumer { Answer = WorkflowResumeOutcome.Deferred }
        };
        world.Store.ReleaseThrows = true;

        var act = async () => await world.Waker().WakeOnceAsync(default);

        await act.Should().NotThrowAsync();
        world.Store.Taken.Should().HaveCount(2, "the second row is still looked at");
    }

    [Fact]
    public async Task A_template_is_read_against_the_instant_the_deadline_was_read_against()
    {
        var candidate = Template($"${{now.epoch}} >= {Now.ToUnixTimeSeconds()}") with
        {
            TimeoutAtUtc = Now.AddSeconds(1)
        };

        var world = new World(candidate);

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);
        ShouldBeSatisfied(world);
        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
    }

    [Fact]
    public async Task The_zone_a_template_reads_local_time_in_is_the_host_s_to_answer_per_run()
    {
        var world = new World(Template("${now.localhour} == 14"))
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                ResolveTimeZone = (_, _) =>
                    Task.FromResult<TimeZoneInfo?>(TimeZoneInfo.CreateCustomTimeZone(
                        "test/plus-two", TimeSpan.FromHours(2), "test/plus-two", "test/plus-two"))
            }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1, "noon UTC is 14:00 two hours east");
        ShouldBeSatisfied(world);
    }

    [Fact]
    public async Task A_template_whose_zone_cannot_be_answered_waits_rather_than_guessing()
    {
        var world = new World(Template("${now.localhour} == 12"))
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                ResolveTimeZone = (_, _) => throw new InvalidOperationException("no tenant for this run")
            }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);
        world.Store.Taken.Should().BeEmpty();
    }

    [Fact]
    public async Task A_template_that_never_reads_local_time_does_not_ask_the_host_for_a_zone()
    {
        var asked = false;
        var world = new World(Template("1 == 1"))
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                ResolveTimeZone = (_, _) =>
                {
                    asked = true;
                    return Task.FromResult<TimeZoneInfo?>(TimeZoneInfo.Utc);
                }
            }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);
        ShouldBeSatisfied(world);
        asked.Should().BeFalse("a lookup a host pays for belongs where it changes the answer");
    }

    [Fact]
    public async Task A_template_reads_the_host_s_own_vocabulary_while_it_waits()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var world = new World(Template("${device.kitchen.temperature} > 20"))
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                ExpressionNamespaces =
                [
                    new WorkflowExpressionNamespace("device", 2, new SimpleCheck("device", "Number"),
                        (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new KitchenDevices()))
                ],
                NamespaceScopeFactory = services.GetRequiredService<IServiceScopeFactory>()
            }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);
        ShouldBeSatisfied(world);
    }

    [Fact]
    public async Task A_host_namespace_that_cannot_be_opened_leaves_the_template_parked_for_the_next_pass()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var opens = 0;
        var world = new World(Template("${device.kitchen.temperature} > 20"))
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                ExpressionNamespaces =
                [
                    new WorkflowExpressionNamespace("device", 2, new SimpleCheck("device", "Number"),
                        (_, _) => Interlocked.Increment(ref opens) == 1
                            ? throw new InvalidOperationException("redis down")
                            : Task.FromResult<IExpressionNamespaceResolver>(new KitchenDevices()))
                ],
                NamespaceScopeFactory = services.GetRequiredService<IServiceScopeFactory>()
            }
        };
        var waker = world.Waker();

        (await waker.WakeOnceAsync(default)).Should().Be(0);
        world.Store.Taken.Should().BeEmpty("a resolver that cannot open says nothing about the condition");
        world.Resumer.Resumed.Should().BeEmpty();

        (await waker.WakeOnceAsync(default)).Should().Be(1, "the next pass reads the condition once the resolver opens");
        ShouldBeSatisfied(world);
    }

    [Fact]
    public async Task A_type_vocabulary_that_throws_leaves_the_template_parked()
    {
        var world = new World(Template("1 > 0"))
        {
            Options = new WorkflowSuspensionWakeOptions { TypeVocabulary = () => throw new InvalidOperationException("registry down") }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);
        world.Store.Taken.Should().BeEmpty();
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_shared_registration_receives_the_parked_trigger_scope()
    {
        var triggerId = Guid.NewGuid();
        var envelope = new TriggerEnvelope(DateTimeOffset.UnixEpoch, new Dictionary<string, object?> { ["source"] = "parked" })
        {
            Terms = new Dictionary<string, ReeveWorkflow.Abstractions.ClosedPortType>
            {
                ["source"] = ReeveWorkflow.Core.Compilation.WorkflowTypeVocabulary.Builtin.ClosedTypeOf(ReeveWorkflow.Abstractions.PortTerm.Atom("String"))!
            }
        };
        var payload = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
        {
            VariablesJson = WorkflowVariablesEncoder.Encode(new Dictionary<string, object?>()),
            PreviousOutputsJson = JsonSerializer.Serialize(new Dictionary<string, object?>()),
            TriggerEnvelopeJson = PersistedValueCodec.WriteTriggerEnvelope(triggerId, envelope),
            ExecutionStateId = null
        });
        ExpressionNamespaceScope? scopeSeen = null;
        using var services = new ServiceCollection().BuildServiceProvider();
        var world = new World(Template("${host.ready}") with { StatePayloadJson = payload })
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                ExpressionNamespaces =
                [
                    new WorkflowExpressionNamespace("host", 1, new ReadyCheck(), (scope, _) =>
                    {
                        scopeSeen = scope;
                        return Task.FromResult<IExpressionNamespaceResolver>(new FixedResolver("host", true));
                    })
                ],
                NamespaceScopeFactory = services.GetRequiredService<IServiceScopeFactory>()
            }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);
        ShouldBeSatisfied(world);
        scopeSeen.Should().NotBeNull();
        scopeSeen!.TriggerId.Should().Be(triggerId);
        scopeSeen.TriggerEnvelope!.Payload.Should().NotContainKey("source",
            "no trigger namespace accepts the key, so admission keeps none and the codec reads none back");
    }

    [Fact]
    public async Task A_template_on_a_namespace_no_host_registered_is_unevaluable()
    {
        var world = new World(Template("${device.kitchen.temperature} > 20"));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);
        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Unevaluable);
        outcome.GetProperty("reason").GetString().Should().Be(WorkflowAbandonmentReasons.PinnedVersionUncompilable);
    }

    [Theory]
    [InlineData("threshold-not-a-number")]
    [InlineData("stale-input")]
    public async Task A_template_whose_parked_values_do_not_read_against_its_pinned_node_is_unevaluable(string fault)
    {
        var row = Template("${parameter.threshold} > 5");
        row = fault == "stale-input"
            ? row with { ConfigJson = JsonSerializer.Serialize(new WorkflowStoredTemplateConfig("Other", null, "true")) }
            : row with
            {
                StatePayloadJson = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
                {
                    VariablesJson = "{}", PreviousOutputsJson = """{"start":{"threshold":"nine"}}""", InputNodeId = "start",
                    TriggerEnvelopeJson = "{}", ExecutionStateId = null
                })
            };
        var world = new World(row);

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Unevaluable);
        outcome.GetProperty("reason").GetString().Should().Be(WorkflowAbandonmentReasons.ResumeValueInvalid);
    }

    [Fact]
    public async Task A_template_whose_pinned_version_cannot_be_read_stays_parked()
    {
        var row = Template("1 == 1");
        var waker = new WorkflowSuspensionWaker(new RecordingResumeStore { Candidates = [row] }, new RecordingResumer(),
            NullLogger.Instance, ParkedTemplates.Compiler(), scopes: new ScopesThatCannotOpen());

        (await waker.WakeOnceAsync(default)).Should().Be(0);
    }

    private sealed class ScopesThatCannotOpen : IWorkflowRunScopeFactory
    {
        public IWorkflowRunScope CreateScope() => throw new InvalidOperationException("the database is away");
    }

    private static void ShouldBeSatisfied(World world) =>
        JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson).RootElement
            .GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);

    private sealed class KitchenDevices : IExpressionNamespaceResolver
    {
        public string Namespace => "device";

        public ResolveResult Resolve(string[] segments) =>
            segments is ["kitchen", "temperature"]
                ? ResolveResult.Resolved(21.5)
                : ResolveResult.Failed();
    }

    private sealed record FixedResolver(string Namespace, bool Value) : IExpressionNamespaceResolver
    {
        public ResolveResult Resolve(string[] segments) => ResolveResult.Resolved(Value);
    }

    private sealed class TriggerPayloadNamespace(TriggerEnvelope? envelope) : IExpressionNamespaceResolver
    {
        public string Namespace => "trigger";
        public ResolveResult Resolve(string[] segments) =>
            segments is ["box"] && envelope?.Payload.TryGetValue("box", out var value) == true
                ? ResolveResult.Resolved(value is null ? null : JsonSerializer.SerializeToElement(value))
                : ResolveResult.Failed();
    }

    private sealed record ReadyCheck() : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "host";
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "Boolean");
    }

    private sealed record SimpleCheck(string Namespace, string Term = "Json?") : IExpressionNamespaceStaticCheck
    {
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, Term);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public async Task Each_pass_reads_on_from_where_the_last_one_stopped()
    {
        var world = new World(Template("1 == 2"));
        world.Store.NextPosition = 50;
        var waker = world.Waker();

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        world.Store.ScannedAfter.Should().Equal(0, 50);
    }

    [Fact]
    public async Task A_sleep_whose_wake_time_has_come_is_resumed_not_timed_out()
    {
        var world = new World(Sleep(wakeAtUtc: Now.AddSeconds(-1)));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("_resumeReason").GetString().Should().Be("sleep");
        outcome.RootElement.GetProperty("durationMs").GetInt64().Should().Be(600_000);
    }

    [Fact]
    public async Task A_sleep_that_is_not_due_is_left_alone()
    {
        var world = new World(Sleep(wakeAtUtc: Now.AddSeconds(1)));

        (await world.Waker().WakeOnceAsync(default)).Should().Be(0);

        world.Store.Taken.Should().BeEmpty("nothing but its clock can end a sleep");
    }

    [Fact]
    public async Task A_pass_limited_to_sleeps_leaves_every_other_kind_to_the_host()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddSeconds(-1)), Sleep(wakeAtUtc: Now.AddSeconds(-1)))
        {
            Options = new WorkflowSuspensionWakeOptions { Kinds = [WorkflowSuspensionKind.Sleep] }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Resumer.Resumed.Should().ContainSingle().Which.Kind.Should().Be(WorkflowSuspensionKind.Sleep);
        world.Store.Taken.Should().ContainSingle();
    }

    [Fact]
    public async Task A_pass_limited_to_sleeps_is_not_starved_by_a_full_batch_of_other_overdue_rows()
    {
        var world = new World(Parked(timeoutAtUtc: Now.AddMinutes(-3)), Parked(timeoutAtUtc: Now.AddMinutes(-2)),
            Sleep(wakeAtUtc: Now.AddSeconds(-1)))
        {
            Options = new WorkflowSuspensionWakeOptions { BatchSize = 2, Kinds = [WorkflowSuspensionKind.Sleep] }
        };

        (await world.Waker().WakeOnceAsync(default)).Should().Be(1);

        world.Resumer.Resumed.Should().ContainSingle().Which.Kind.Should().Be(WorkflowSuspensionKind.Sleep);
    }

    [Fact]
    public void A_waker_limited_to_no_kind_at_all_is_refused()
    {
        var build = () => new WorkflowSuspensionWaker(new RecordingResumeStore(), new RecordingResumer(),
            NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions { Kinds = [] });

        build.Should().Throw<ArgumentException>().WithMessage("*Kinds*");
    }

    [Fact]
    public void A_waker_given_null_namespace_registrations_is_refused()
    {
        var build = () => new WorkflowSuspensionWaker(new RecordingResumeStore(), new RecordingResumer(),
            NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions { ExpressionNamespaces = null! });

        build.Should().ThrowExactly<ArgumentException>()
            .WithMessage("*WorkflowSuspensionWakeOptions.ExpressionNamespaces is null*Use an empty list*")
            .WithParameterName("options");
    }

    [Fact]
    public void A_waker_given_host_namespaces_and_no_scope_factory_is_refused_rather_than_read_them_as_null()
    {
        var build = () => new WorkflowSuspensionWaker(new RecordingResumeStore(), new RecordingResumer(),
            NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions
            {
                ExpressionNamespaces =
                [
                    new WorkflowExpressionNamespace("trigger", 1, new SimpleCheck("trigger"),
                        (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new TriggerPayloadNamespace(null)))
                ],
            });

        build.Should().Throw<ArgumentException>().WithMessage("*NamespaceScopeFactory*");
    }

    private static WorkflowSuspensionCandidate Sleep(DateTimeOffset wakeAtUtc) =>
        Parked(timeoutAtUtc: wakeAtUtc, kind: WorkflowSuspensionKind.Sleep) with
        {
            SuspendedAtUtc = wakeAtUtc.AddMinutes(-10),
            ConfigJson = JsonSerializer.Serialize(new WorkflowSleepConfig(600_000, wakeAtUtc))
        };

    [Fact]
    public async Task Each_pass_hands_the_store_back_the_cursor_its_last_pass_wrote()
    {
        var world = new World(Template("1 == 2"));
        var key = new WorkflowSuspensionScanCursor(0)
        {
            AfterSuspendedAtUtc = Now.AddMinutes(-5),
            AfterSuspensionId = Guid.NewGuid()
        };
        world.Store.NextCursor = key;
        var waker = world.Waker();

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        world.Store.ScannedFrom.Should().Equal(WorkflowSuspensionScanCursor.Start, key);
    }

    [Fact]
    public async Task A_pass_that_ran_out_of_workers_reads_its_page_again_rather_than_stepping_past_it()
    {
        var world = new World(Template("1 == 1"), Template("1 == 1"))
        {
            Options = new WorkflowSuspensionWakeOptions { MaxConcurrentResumes = 1 },
            Resumer = new RecordingResumer { Hold = new TaskCompletionSource<WorkflowResumeOutcome>() }
        };
        var key = new WorkflowSuspensionScanCursor(0) { AfterSuspendedAtUtc = Now.AddMinutes(-5), AfterSuspensionId = Guid.NewGuid() };
        world.Store.NextCursor = key;
        var waker = world.Waker();

        (await waker.TakeOnceAsync(default)).Should().Be(1);
        world.Resumer.Hold!.SetResult(WorkflowResumeOutcome.Resumed);
        await waker.WaitForResumesAsync();
        await waker.TakeOnceAsync(default);

        world.Store.ScannedFrom.Should().Equal(WorkflowSuspensionScanCursor.Start, WorkflowSuspensionScanCursor.Start);
    }

    private static WorkflowSuspensionCandidate Template(string expression) =>
        ParkedTemplates.Pin(Parked(kind: WorkflowSuspensionKind.WaitTemplate), expression);

    private static readonly string Payload = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
    {
        VariablesJson = WorkflowVariablesEncoder.Encode(new Dictionary<string, object?> { ["threshold"] = 9 }),
        PreviousOutputsJson = """{"start":{"threshold":9}}""",
        InputNodeId = "start",
        TriggerEnvelopeJson = "{}",
        ExecutionStateId = null
    });

    private sealed class World(params WorkflowSuspensionCandidate[] parked)
    {
        public RecordingResumeStore Store { get; } = new() { Candidates = parked };
        public RecordingResumer Resumer { get; init; } = new();
        public bool TakenByAPeer { get; init; }
        public bool FindThrows { get; init; }
        public WorkflowSuspensionWakeOptions? Options { get; init; }
        public IHelperResolver? Helper { get; init; }

        public WorkflowSuspensionWaker Waker()
        {
            Store.TakenByAPeer = TakenByAPeer;
            Store.FindThrows = FindThrows;
            IWorkflowSuspensionResumeStore store = Store.NextCursor is null ? Store : new PagingByKey(Store);
            return new WorkflowSuspensionWaker(store, Resumer, NullLogger.Instance, ParkedTemplates.Compiler(Options?.ExpressionNamespaces, Helper), Options,
                helperResolver: Helper, scopes: ParkedTemplates.Scopes());
        }
    }

    private sealed class CountingHelperResolver : IHelperResolver
    {
        public List<IReadOnlyCollection<HelperPath>> ReadBatches { get; } = [];
        public int CheckCalls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            CheckCalls++;
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int")));
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            ReadBatches.Add(paths.ToArray());
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(paths.ToDictionary(path => path, _ => (object?)1L));
        }
    }

    private sealed class TimingOutCheckResolver : IHelperResolver
    {
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromException<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(new TaskCanceledException("helper store timed out"));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private sealed class ThrowingReadResolver : IHelperResolver
    {
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int")));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromException<IReadOnlyDictionary<HelperPath, object?>>(new InvalidOperationException("read failed"));
    }

    private sealed class PagingByKey(RecordingResumeStore inner) : IWorkflowSuspensionResumeStore
    {
        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a waker reads a store that pages by key by its cursor");

        public async Task<WorkflowSuspensionScan> ScanAsync(
            WorkflowSuspensionScanCursor after, int limit, CancellationToken cancellationToken)
        {
            inner.ScannedFrom.Add(after);
            var scan = await inner.ScanAsync(after.Position, limit, cancellationToken);
            return scan with { NextCursor = inner.NextCursor! };
        }

        public async Task<WorkflowSuspensionScan> ScanAsync(WorkflowSuspensionScanCursor after, int limit,
            IReadOnlyCollection<WorkflowSuspensionKind>? kinds, CancellationToken cancellationToken)
        {
            inner.ScannedFrom.Add(after);
            var scan = await inner.ScanAsync(after, limit, kinds, cancellationToken);
            return scan with { NextCursor = inner.NextCursor! };
        }

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) =>
            inner.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(suspensionId, takenAs, cancellationToken);
    }

    private sealed class RecordingResumeStore : IWorkflowSuspensionResumeStore
    {
        public IReadOnlyList<WorkflowSuspensionCandidate> Candidates { get; set; } = [];
        public List<(Guid Id, WorkflowSuspensionStatus Status)> Taken { get; } = [];
        public List<(Guid Id, WorkflowSuspensionStatus TakenAs)> Released { get; } = [];
        public List<int> ScannedAfter { get; } = [];
        public bool TakenByAPeer { get; set; }
        public bool FindThrows { get; set; }
        public bool ReleaseThrows { get; set; }

        public Task<WorkflowSuspensionScan> ScanAsync(
            int afterPosition, int limit, CancellationToken cancellationToken)
        {
            ScannedAfter.Add(afterPosition);
            return FindThrows
                ? Task.FromException<WorkflowSuspensionScan>(
                    new InvalidOperationException("the database is away"))
                : Task.FromResult(new WorkflowSuspensionScan(Now, Candidates.Take(limit).ToList(), NextPosition));
        }

        public Task<WorkflowSuspensionScan> ScanAsync(WorkflowSuspensionScanCursor after, int limit,
            IReadOnlyCollection<WorkflowSuspensionKind>? kinds, CancellationToken cancellationToken)
        {
            ScannedAfter.Add(after.Position);
            return FindThrows
                ? Task.FromException<WorkflowSuspensionScan>(new InvalidOperationException("the database is away"))
                : Task.FromResult(new WorkflowSuspensionScan(Now,
                    Candidates.Where(c => kinds is null || kinds.Contains(c.Kind)).Take(limit).ToList(), NextPosition));
        }

        public int NextPosition { get; set; }

        public WorkflowSuspensionScanCursor? NextCursor { get; set; }

        public List<WorkflowSuspensionScanCursor> ScannedFrom { get; } = [];

        public Task<bool> ReleaseAsync(
            Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
        {
            if (ReleaseThrows)
                return Task.FromException<bool>(new InvalidOperationException("the database is away"));

            Released.Add((suspensionId, takenAs));
            return Task.FromResult(true);
        }

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId,
            WorkflowSuspensionStatus status,
            string outcomeJson,
            DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            Taken.Add((suspensionId, status));
            if (TakenByAPeer)
                return Task.FromResult<ResumeContext?>(null);

            var candidate = Candidates.Single(c => c.SuspensionId == suspensionId);
            return Task.FromResult<ResumeContext?>(new ResumeContext
            {
                SuspensionId = candidate.SuspensionId,
                ExecutionLogId = candidate.ExecutionLogId,
                WorkflowId = candidate.WorkflowId,
                BlockId = candidate.BlockId,
                Kind = candidate.Kind,
                OutcomeJson = outcomeJson,
                StatePayloadJson = candidate.StatePayloadJson,
                SuspendedAtUtc = candidate.SuspendedAtUtc
            });
        }
    }

    private sealed class RecordingResumer : IWorkflowRunResumer
    {
        public List<ResumeContext> Resumed { get; } = [];
        public bool FailFirst { get; set; }
        public WorkflowResumeOutcome Answer { get; set; } = WorkflowResumeOutcome.Resumed;

        public TaskCompletionSource<WorkflowResumeOutcome>? Hold { get; init; }

        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(
            ResumeContext ctx, CancellationToken cancellationToken)
        {
            if (FailFirst)
            {
                FailFirst = false;
                return Task.FromException<WorkflowResumeOutcome>(
                    new InvalidOperationException("the workflow could not be loaded"));
            }

            Resumed.Add(ctx);
            return Hold?.Task ?? Task.FromResult(Answer);
        }
    }
}
