# Durable sleep and parked waits

A run that waits on something outside the process (a trigger, a template, a host predicate, a run it started)
parks: it is written as a suspension row, its process lets it go, and the suspension waker on any replica
resumes it when the wait is answered. A sleep can park too, when the host asks for it. This page says when a
sleep parks, how to turn parking on and off safely, and what differs for an operator or an author. The
running model is summarised in the [architecture overview](../architecture/README.md).

The kinds of parked row (`WorkflowSuspensionKind`, the `kind` column of `automation.workflow_suspensions`):

| Kind | Value | Parked by | Resumed when |
| --- | --- | --- | --- |
| `WaitForTrigger` | 0 | A wait-for-trigger block | The host publishes the trigger, or the timeout passes. |
| `WaitTemplate` | 1 | A wait on a template | The template reads true on a waker pass (or a host wake), or the timeout passes. |
| `Sleep` | 2 | A durable sleep at or above the park threshold | Its wake time passes. |
| `SubWorkflow` | 3 | A run waiting for a run it started | That run reaches an ending. |
| `Predicate` | 4 | A host wait on a node's `Expression<Boolean>` input | The host wakes the row and the expression reads true, or the timeout passes. |

A kind is moved only by a waker whose `Kinds` include it (null means every kind). A kind no waker moves stays
parked for ever.

## Durable sleep

A block named in `Defaults.DurableSleepBlockTypeIds` does not run its own delay: the engine sleeps for it. By
default it sleeps in the process: the walk waits, renewing the run's row every `SleepPollInterval`, so a cancel
or a lost claim ends the wait and, with recovery enabled, a restart mid-wait resumes rather than losing the run.
The run, its outputs and its concurrency slot stay in memory.

With a threshold set, a long sleep leaves the process instead:

```csharp
.AddWorkflowEngine(engine =>
{
    engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(5);
});
```

A sleep at least that long parks the run as a `Sleep` row whose timeout is the wake time. The waker resolves the
row when it is due, and the resumed node reports the same outputs as an in-process sleep
(`{ Completed: true, DurationMs }`). `AddWorkflowEngine` refuses a threshold while the waker is disabled or its
`Kinds` leave out `Sleep`.

### When a sleep parks

A sleep at or above the threshold parks only when all of these hold. Otherwise it sleeps in the process, which
is slower but not wrong.

| Condition | Why |
| --- | --- |
| The workflow's mode is `Single` or `Restart` | A resumed sleep never waits for a slot, and under a steady `Queued` or `Parallel` queue it would never get one. |
| The run is a root run, not a sub-workflow | A parent waits for its child's outputs in memory. |
| No debugger is attached, the run records no log entries, and the node has no passthrough entries | A resume cannot restore them. |
| The workflow has no `ExecutionTimeoutSeconds` and no retry policy of more than one attempt | The run's deadline and its retry loop live in the process. |
| No node has failed under a `Skip` error policy so far | A resume would not know the node was skipped, and would run it and what follows. |
| The run has a state row, and the suspension store implements `IWorkflowParkedSleepStore` | Without them there is nothing to park, and `Single` and `Restart` could not see parked runs. The library's EF store does. |
| The sleep is on the root canvas, or inside containers that all declare `OnlyDrivesItsSubCanvases` (the standard loops, branches and Try/Catch do), with the run's position fitting the payload cap | A resume re-enters at the recorded position. A container that does something of its own cannot be re-entered. |
| The run's outputs serialise to JSON, and the payload fits `WorkflowSuspensionOptions.MaxStatePayloadBytes` (256 KB) | An output of infinity, NaN or a cycle exists only in the process; this is logged once. |
| The duration resolves and the wake time does not overflow | A bad duration fails the node through its error policy, as an in-process sleep does. |
| Writing the row succeeds | A failed pre-park is withdrawn and the walk sleeps in the process. |

### What a parked sleep keeps

A parked sleep keeps its workflow's mode. A `Single` run is skipped while another run is parked on a sleep, and
a `Restart` fire cancels parked sleepers. A woken sleep takes a lease from the engine's coordinators without
waiting, and goes back to the table while the budget is held. A `Restart` sleeper whose workflow has a run
started after it parked has been superseded, and ends when it wakes, whether the budget was held or free. Both
instants come from replica clocks, so a fire within the replicas' clock skew of the park can be misjudged. While
a `Restart` fire is waiting, a sleep does not resume on that replica or on a peer that reads the same
generations.

A row the waker has taken still counts as parked while the run's state row is `Suspended`; the resume changes it
once it holds the lease. A take the resume never followed, because the waker died, is taken over after
`Waker.RetakeAbandonedTakesAfter` (by default `Recovery.AbandonedAfter`), whether or not sleeps park. A resumer
built without the engine's coordinators leaves these sleeps parked and logs an error. A host whose
`IWorkflowEngine` wraps the library's registers the inner engine's `Coordinators` as
`IWorkflowRunCoordinatorPool`.

A host that wakes its own templates and triggers can still leave sleeps to the library: run the library waker
with `Kinds = [WorkflowSuspensionKind.Sleep]` and keep its own sweep off `Sleep` rows.

### Turning parking on and off

A parked sleep is data that outlives the configuration that wrote it, and only the library waker wakes it.

**Turning parking on.** Set `DurableSleep.ParkWhenAtLeast` on every replica in the same rollout. A replica
without the threshold does not look for parked sleeps at admission (which keeps admission free of that query on
hosts that never park): it admits a `Single` run beside a parked one, and a `Restart` fire on it leaves a parked
sleeper to run its rest later.

**Turning parking off: drain first.**

1. Unset `DurableSleep.ParkWhenAtLeast` on every replica. New sleeps stay in the process.
2. Keep the waker enabled with `Sleep` among its `Kinds` on at least one replica until no `Sleep` rows remain
   parked: no row of `automation.workflow_suspensions` with `kind = 2` whose `status` (a string column) is
   `'Suspended'`, `'Resumed'` or `'TimedOut'` and whose run has not ended. A parked sleep lasts as long as it was
   set to. A take whose waker died is taken over by the waker's own look for abandoned takes, which needs no
   extra setting.
3. Only then narrow `Kinds` or disable the waker.

```sql
SELECT s.id, s.workflow_id, s.status, s.timeout_at_utc
FROM automation.workflow_suspensions s
JOIN automation.workflow_execution_logs l ON l.id = s.execution_log_id
WHERE s.kind = 2
  AND s.status IN ('Suspended', 'Resumed', 'TimedOut')
  AND l.completed_at_utc IS NULL;
```

The same holds for any change that turns the waker off or leaves a kind out of its `Kinds` while rows of that
kind are parked, not only for sleeps: a `SubWorkflow` or `Predicate` row that no waker moves never resumes
either. Add the kind back to `Waker.Kinds` until no rows of it remain.

Two checks warn about this instead of leaving rows parked silently. Neither stops the host:

- With `Waker.Enabled = false`, a startup check looks for an overdue parked `Sleep` and logs a warning ("A
  parked Sleep is overdue, and Waker.Enabled is false ...") unless the host wakes `Sleep` rows itself.
- A running waker whose `Kinds` leave out `Sleep`, `SubWorkflow` or `Predicate` looks, at most once a minute, for
  a waiting row of each missing kind and logs a warning ("A parked Sleep is waiting, and this waker's Kinds leave
  out Sleep ...") when it finds one.

### What differs from a sleep in the process

The sleep node's own outputs are the same either way: the same keys and value types. The differences are around
it.

The nodes before the sleep are the main one. Their outputs were stored as JSON and read back, so a node after the
wake reads a `DateTimeOffset` or a `Guid` as a string, an `int` as a `long`, a record as a dictionary, and `2.0`
as the `long` `2`. A block that reads an earlier output by type has to accept both shapes.

| In the process | Parked |
| --- | --- |
| A node after the sleep reads earlier outputs as their blocks returned them | It reads them in JSON's shapes (above) |
| `ExecuteAsync` returns the run's outputs when the run finishes | It returns at once with empty outputs; the HTTP execute endpoint answers `200` with `{}` |
| The sleep's node-completed notice carries its inputs | It carries only `DurationMs` as inputs |
| One journal step for the sleep node | Two: `suspended` when it parks, and its outputs when it wakes |
| Wakes by this process's clock | Wake time set from the parking replica's clock, compared with the database's |
| Wakes on time | Up to one `Waker.PollInterval` late, later when the waker has a backlog |
| State row `Sleeping` | State row `Suspended` |
| The run-duration metric covers the whole run | It covers the part after the wake; the parked part is the suspension duration (`workflow.suspension.duration`) |
| A cancelled sleep's node gets no terminal notice | A run ended while parked announces the node as failed, with a reason starting `Cancelled:` |
| The node's sleep advances on a host's `TimeProvider` | The waker compares the wake time with the database clock; advancing a fake clock does not wake it |
| Nothing is read back from the store | A row whose persisted outcome does not read (not an object, or a duration that is not an integer) abandons the run with `resume_value_invalid` |

## Parked waits

Waits other than sleeps always park. The payload a parked run carries (its outputs so far, its position, its
variables) is capped by `WorkflowSuspensionOptions.MaxStatePayloadBytes`. A wait whose run would not fit fails
the run at the wait, measured before the insert, rather than losing part of the run.

A host can tell the waker that a parked run may be ready now through `Seams.SuspensionWakeSource`, so waits on
host state are event-driven instead of polled. A wake is a hint about when to look, not an answer: the waker
still reads the row and evaluates its condition, except for a trigger, whose wait has no condition. A wake that
arrives before its row can be read is held (`MaxHeldWakes`, `HoldWakesFor`). A trigger wake held and then
dropped is a lost event, logged as a warning and noted in the run's own history. The waker's settings are in
[configuration.md](configuration.md#workflowsuspensionwakeoptions); what happens to parked runs when a replica
dies is in [clustering.md](clustering.md#when-a-replica-dies).
