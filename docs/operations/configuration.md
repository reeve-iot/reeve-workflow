# Configuration reference

Every setting a host gives the library is a property of a public options class, passed to the registration
call that owns it. There are no configuration-file keys: a host that wants settings from `IConfiguration`
reads them itself and assigns them in the callback.

| Registration | Options it takes | Package |
| --- | --- | --- |
| `services.AddWorkflows(o => ...)` | [`WorkflowPersistenceOptions`](#workflowpersistenceoptions), with `o.Defaults` ([`WorkflowHostDefaultsOptions`](#workflowhostdefaultsoptions)), `o.Seams` and `o.CompilationSweep` ([`WorkflowCompilationSweepOptions`](#workflowcompilationsweepoptions)) | `ReeveWorkflow.EntityFrameworkCore` |
| `.AddPostgreSqlSchema()` | none | `ReeveWorkflow.EntityFrameworkCore.PostgreSql` |
| `.AddWorkflowEngine(e => ...)` | [`WorkflowEngineOptions`](#workflowengineoptions), with `e.Recovery`, `e.DurableSleep` and `e.Waker` | `ReeveWorkflow.EntityFrameworkCore` |
| `.AddWorkflowEditor(e => ...)` | [`WorkflowEditorOptions`](#workfloweditoroptions) | `ReeveWorkflow.Editor` |
| `services.AddStandardWorkflowBlocks(b => ...)` | [`StandardWorkflowBlockOptions`](#standardworkflowblockoptions) | `ReeveWorkflow.Blocks` |
| `services.AddWorkflowRetention(r => ...)` | [`WorkflowRetentionOptions`](#workflowretentionoptions) | `ReeveWorkflow.History` |
| `services.AddWorkflowDebug(d => ...)`, `AddWorkflowDebugRetention(d => ...)` | [`WorkflowDebugOptions`](#workflowdebugoptions) | `ReeveWorkflow.Debug` |
| `o.Seams.RunLockProvider.UseNpgsqlLocks(l => ...)` | [`NpgsqlWorkflowLockOptions`](#npgsqlworkflowlockoptions) | `ReeveWorkflow.Npgsql` |
| `o.Seams.RunSlotGate.UseRedis(r => ...)` | [`RedisWorkflowOptions`](#redisworkflowoptions) | `ReeveWorkflow.Redis` |
| `o.Seams.FireDedup.UseRedis(r => ...)` | [`RedisFireDedupOptions`](#redisfirededupoptions) | `ReeveWorkflow.Redis` |
| `o.Seams.ClusterSignal.UseRedis()` | none; channel names are in [`Defaults.SignalChannels`](#workflowclustersignalchannels) | `ReeveWorkflow.Redis` |
| `services.AddSingleton(new WorkflowSuspensionOptions { ... })` | [`WorkflowSuspensionOptions`](#workflowsuspensionoptions), read from the container | `ReeveWorkflow.EntityFrameworkCore` |

`AddWorkflows`, `AddWorkflowEngine` and the seam extensions read their options when they are called; a change
made to an options object after the call has no effect. History and Debug read theirs through
`IOptions<T>`.

## Values refused at startup

A misconfiguration that would give a silent wrong answer is refused when the host is built or started, with a
message naming the setting.

| Where | Refused when |
| --- | --- |
| `AddWorkflows` | No database provider was given (`ConfigureDbContext` never called). |
| `AddWorkflows` | `Defaults.ReplicaId` is longer than 100 characters (`WorkflowHostDefaultsOptions.ReplicaIdColumnWidth`, the width of `owned_by_replica_id`). |
| `AddWorkflows` | `Defaults.IsMultiReplica` is true and the run-slot gate, fire de-duplication, run lock or Restart ordering has no cross-replica answer. See [clustering.md](clustering.md#the-cluster-seams). |
| `AddWorkflows` | A `CompilationSweep` value is out of the bounds listed [below](#workflowcompilationsweepoptions). |
| `AddWorkflowEngine` | `Recovery.AbandonedAfter` is zero or negative. |
| `AddWorkflowEngine` | `RunHeartbeatInterval` is zero or negative (turn it off with null), or `Recovery.AbandonedAfter` is less than three heartbeats. |
| `AddWorkflowEngine` | `RunStateWriteMode` is null; a mode other than `EveryBlock` with the heartbeat off; a `Heartbeat` interval not shorter than `Recovery.AbandonedAfter`. |
| `AddWorkflowEngine` | `MaxRecordedSteps` is zero or negative. |
| `AddWorkflowEngine` | `MaxCheckpointBytes` is zero or negative. |
| `AddWorkflowEngine` | `Recovery.Enabled` with `Recovery.SweepInterval` zero or negative. |
| `AddWorkflowEngine` | `DurableSleep.ParkWhenAtLeast` is zero or negative, or is set while `Waker.Enabled` is false or `Waker.Kinds` leaves out `Sleep`. |
| `AddWorkflowEngine` | `Waker.Kinds` is an empty list (null means every kind). |
| `AddWorkflowEngine` | `DefaultContainerTimeoutSeconds` is not between 1 and 86 400. |
| Engine resolution | `Recovery.Enabled` and `Defaults.ReplicaId` is not set: every claim would write no owner, and the renewal that fences on the owner would match every peer's rows. |
| Engine resolution | `Recovery.Enabled`, `Seams.RunStateStore` is a host store, and `Seams.RunRecoveryStore` is the library's default, which reads a table that store does not write. |
| Engine resolution | `DurableSleep.ParkWhenAtLeast` is set and the registered `IWorkflowEngine` is not the library's `WorkflowEngine` and no `IWorkflowRunCoordinatorPool` is registered. A host that wraps the engine registers the inner engine's `Coordinators` as `IWorkflowRunCoordinatorPool`. |
| Waker construction | `ExpressionNamespaces` is null, or non-empty without a `NamespaceScopeFactory`. |
| `UseNpgsqlLocks` | `ConnectionString` is empty, when the lock provider is built. |
| `FireDedup.UseRedis()`, `ClusterSignal.UseRedis()` | No `IConnectionMultiplexer` is registered, when the seam is resolved. `RunSlotGate.UseRedis()` is the exception: without a multiplexer it admits on the in-process permit alone. |
| Leader-gated sweeps | The run-lock provider the leader gate is derived from was registered with a scoped lifetime. |
| After the host started | A `Map*Api` route group has neither `.RequireAuthorization(...)` nor `.AllowAnonymous()`. |

A host whose waker is off, or whose waker's `Kinds` leave out a kind that has rows waiting, is not refused: it
logs a warning (once at startup, then at most once a minute) and keeps running. See
[parking.md](parking.md#turning-parking-on-and-off).

## WorkflowPersistenceOptions

What `AddWorkflows` is given.

| Member | Effect |
| --- | --- |
| `ConfigureDbContext(Action<DbContextOptionsBuilder>)` | Adds one mutation of the workflow context's options: the provider, the connection string, timeouts. Callable more than once; every mutation runs in the order added, when the container builds the context. Required at least once. |
| `Defaults` | [`WorkflowHostDefaultsOptions`](#workflowhostdefaultsoptions). |
| `Seams` | Where a host names its own implementation of each seam (`Seams.X.Use<T>()`, `Use<T>(lifetime)`, or a factory). A slot left alone keeps the library's answer. A seam registered in the container after `AddWorkflows`, or under a key, is not seen by the startup checks; name it in its slot instead. |
| `CompilationSweep` | [`WorkflowCompilationSweepOptions`](#workflowcompilationsweepoptions). |

## WorkflowHostDefaultsOptions

`AddWorkflows(o => o.Defaults...)`: the few facts the library's default seams cannot guess. Leaving all of them
at their defaults is a supported single-replica configuration.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `ReplicaId` | `string?` | null | This process's name. Written on every run-state row it owns (so a peer can tell a live run from an abandoned one), and its identity on the cluster signal. Must differ on every replica of one deployment: two that share it read each other's signals as their own echoes and drop them. A machine name is not enough where two replicas share a machine. At most 100 characters. Null writes no owner on any row, and the signal transport invents an id for the process when it is null or blank. Do not set a blank name: rows store it as written. Required when recovery is enabled. |
| `IsMultiReplica` | `bool` | false | Whether this deployment runs as more than one process. True makes the library refuse to start while any cluster seam is on its single-process answer, and turns on the cross-replica checks (the run-slot gate, fire de-duplication and the Restart watch are consulted only when this is true). See [clustering.md](clustering.md). |
| `TimeZone` | `TimeZoneInfo` | UTC | The zone a workflow's local wall-clock expression fields (`${now.local*}`) are read in. |
| `ResolveTimeZoneAsync` | `Func<IServiceProvider, CancellationToken, Task<TimeZoneInfo>>?` | null | The zone as a lookup (a home, a tenant). Called once per graph walk inside the run's scope (a walk that gets no zone fails at a local-time read), and once per parked template that reads local time, from a scope of its own (a failure there leaves the row waiting for its timeout). `Waker.ResolveTimeZone` answers the second from the row instead. Also once per expression preview that reads the clock, from the request's scope (a failure there is a preview error). |
| `ExpressionNamespaces` | `IList<WorkflowExpressionNamespace>` | empty | Host expression namespaces, shared by compilation, runs, parked waits and preview. Also reachable as `Seams.ExpressionNamespaces`. |
| `AuditTransitions` | `bool` | false | Whether entering a node is also an audit event. One audit write per node per run; turn it on only where the trail is required. |
| `DurableSleepBlockTypeIds` | `IList<string>` | empty | Block types whose delay the engine runs instead of the block, so a restart mid-wait resumes rather than losing the run. A named block's inputs must carry the duration and its outputs are the runner's; name only blocks that wait. See [parking.md](parking.md). |
| `SignalChannels` | [`WorkflowClusterSignalChannels`](#workflowclustersignalchannels) | | Wire names of the cross-replica channels. |
| `Metrics` | [`WorkflowMetricsOptions`](#metrics-and-tracing) | | Meter and instrument names. |
| `Tracing` | [`WorkflowTracingOptions`](#metrics-and-tracing) | | Activity source name. |
| `GrantedCapabilities` | `IList<string>` | empty | Capability names (`WorkflowCapabilities`) the default authorization grants to everyone. Empty denies all of them. A host with per-principal answers registers its own `IWorkflowAuthorization` instead. |

## WorkflowClusterSignalChannels

`Defaults.SignalChannels`. Every replica must use the same names: replicas that disagree do not fail, they stop
hearing each other on that channel. Change one only in a rollout with no mixed versions. Redis pub/sub channels
are instance-wide (not scoped by database index), so two deployments sharing one Redis must use distinct names.

| Property | Default | Carries |
| --- | --- | --- |
| `RestartCancel` | `workflow_restart_cancel` | Restart supersession: the workflow id, with the trigger's generation as payload. |
| `DebugCommands` | `workflow_debug_commands` | A debugger command for whichever replica holds the paused run. |
| `DebugCommandAcks` | `workflow_debug_command_acks` | The holder's confirmation that it applied a command. |
| `DebugBreakpoints` | `workflow_debug_breakpoints` | The whole replacement breakpoint set of an execution, as a JSON array. |

## WorkflowCompilationSweepOptions

`AddWorkflows(o => o.CompilationSweep...)`. The sweep compiles every workflow's current version and stores
the verdicts; `WorkflowCompilationSweepRunner` can be called on demand whatever these say. It runs on the
leader only (see [clustering.md](clustering.md#leader-gated-jobs)) and refuses to run while the model has a
change no migration covers or a migration is unapplied (the database is "not ready"). When its timer's wait
itself fails, the timer stops rather than spinning, and logs an error; an on-demand call still works.

| Property | Type | Default | Bounds (refused outside) | Effect |
| --- | --- | --- | --- | --- |
| `Enabled` | `bool` | false | | Whether a hosted service runs the sweep: once after the host has started, then every `Interval`. |
| `Interval` | `TimeSpan` | 6 hours | 1 minute to about 49.7 days | Time from one run to the next. |
| `ReadinessRetry` | `TimeSpan` | 1 minute | 1 minute to `Interval` | Wait after a run that found the database not ready, in place of `Interval`. |
| `Retention` | `TimeSpan` | 30 days | at least 1 hour | How long stored verdicts are kept. |
| `PageSize` | `int` | 500 | 1 to 10 000 | Workflows read per page of the listing. |

## WorkflowEngineOptions

`.AddWorkflowEngine(e => ...)`. Which replica this is and whether it has peers are not here: they are read
from `Defaults`.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `TraceMode` | `WorkflowTraceMode` | `None` | How much of a run's steps to record: `None`, `Errors` or `Full`. |
| `MaxPayloadValueBytes` | `int` | 8 KB | Cap on one serialised value in a persisted step payload, past which it is replaced by a note. Zero truncates every value. |
| `MaxRecordedSteps` | `int` | 10 000 | Most steps, and most node log entries, one segment of a run's walk records; a segment runs from a start or a resume to the walk's end or next park, so a resumed run counts afresh. Both lists are held in memory until the segment ends and are then written in one batch; past this number the first ones are kept, no later one is recorded, and the run goes on. A step or log entry carrying an error is still recorded up to twice the number, so the step that failed the run is in its history. Must be positive. |
| `MaxCheckpointBytes` | `int` | 256 KB | Largest checkpoint a run writes. Past it the row says so, and a recovery replays the run from its start. Must be positive. |
| `RunStateWriteMode` | `RunStateWriteMode` | `EveryBlock` | How often a run writes its row (checkpoint and fence): `EveryBlock` (a recovery repeats at most the block in flight), `BeforeNonIdempotentOnly` (writes before the first block, before every non-idempotent block and the block after it), or `Heartbeat(interval)` (writes when the last write is at least that old; a recovery re-enters only if every block it would repeat is idempotent). Anything but `EveryBlock` needs `RunHeartbeatInterval`. A workflow can override it, see [per-workflow settings](#per-workflow-settings). |
| `RunHeartbeatInterval` | `TimeSpan?` | 10 seconds | How often a live run renews its row and re-asserts its cluster lock or slot, so a block longer than `Recovery.AbandonedAfter` is not reclaimed from a replica still running it. Each beat is one owner-fenced update, plus a lock check or slot renewal. `Recovery.AbandonedAfter` must be at least three of these. Null turns it off (a Restart run on a replica with peers still looks for a newer trigger every ten seconds). |
| `SleepPollInterval` | `TimeSpan` | 5 seconds | How often an in-process durable sleep wakes to check for cancellation; also its row's renewal interval, so keep it well under `Recovery.AbandonedAfter`. Read only by the blocks in `Defaults.DurableSleepBlockTypeIds`. |
| `RetryOnlyIdempotentBlocks` | `bool` | true | Whether a node that says nothing about retries inherits its block's `IsIdempotent` answer instead of being retried. A node's own `retryEnabled` wins either way. `IsIdempotent` is false unless a factory says otherwise, so silence means "do not retry"; a non-idempotent block retried after a timeout runs its effect twice. |
| `DefaultContainerTimeoutSeconds` | `int?` | null | Deadline for a container whose node names none. Null means the run's own deadline only. A number on the node wins; leaf blocks keep their own 30-second default. A nested container gets the ceiling too, so an outer one must cover its inner ones. 1 to 86 400. |
| `HandOverRunsOnStop` | `bool?` | null (reads `Recovery.Enabled`) | Whether a run this host stops mid-walk is left with no owner, `Running` (or `Sleeping` with its wake-up time), for a sweep to carry on, instead of ending `Cancelled`. A handover leaves the run's journal entry open for whoever takes the row. A host that sweeps such rows with its own sweep turns this on with `Recovery` off, and must close the entry (`WorkflowRunResumer.CloseEntryAsync`) after ending the row; a host with neither says `false`. See [clustering.md](clustering.md#graceful-stop-and-handover). |
| `CoordinatorIdleTimeout` | `TimeSpan` | 30 minutes | How long an idle per-workflow coordinator stays cached. |
| `FireDedupWindow` | `TimeSpan` | 10 seconds | How long one physical trigger event stays claimed in fire de-duplication: long enough for every replica's copy to arrive, short enough that a genuine repeat is not swallowed. |
| `LoggerCategory` | `string` | `ReeveWorkflow.Engine` | Category of the engine's own log lines. |
| `Recovery` | [`WorkflowRunRecoveryOptions`](#workflowrunrecoveryoptions) | | Picking up runs whose replica died. |
| `DurableSleep` | [`WorkflowDurableSleepOptions`](#workflowdurablesleepoptions) | | When a sleep parks. |
| `Waker` | [`WorkflowSuspensionWakeOptions`](#workflowsuspensionwakeoptions) | | Moving parked runs. |

### WorkflowRunRecoveryOptions

`e.Recovery`. The recovery sweep claims runs whose replica died and re-enters, replays or ends them; see
[clustering.md](clustering.md#when-a-replica-dies).

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `Enabled` | `bool` | false | Whether this host sweeps. A recovered run repeats the block it died in (the whole run from its start when there is no checkpoint to re-enter); read the two limits below before turning it on. Needs `Defaults.ReplicaId`. |
| `SweepInterval` | `TimeSpan` | 30 seconds | How often this replica looks for abandoned runs. Must be positive. |
| `AbandonedAfter` | `TimeSpan` | 1 minute | How long a run's row may go unrenewed before its owner is presumed dead. With the heartbeat on it must outlast a few beats (at least three, enforced), not the longest block. With the heartbeat off it must exceed the longest block any workflow contains, or a live run is reclaimed and runs twice. A block's write stamps the row with the writing replica's clock and the claim compares against the database clock, so leave room for clock drift. Also the default for `Waker.RetakeAbandonedTakesAfter`. |
| `BatchSize` | `int` | 20 | The most abandoned runs one sweep claims, so a replica coming back after an outage does not take the whole backlog at once. A sweep claims fewer when fewer of its `MaxConcurrentResumes` places are free. |
| `MaxConcurrentResumes` | `int` | 8 | How many recovered runs this replica carries on at the same time, across all its sweeps. A sweep claims only into free places and nothing while every place is taken; the runs it claimed carry on in the background, so one that sleeps or waits on a slow block holds only its own place and the next sweep claims into the rest. The rows of one claim start oldest first but do not end in that order. Below 1 counts as 1. |
| `GiveUpOnRefusalAfter` | `TimeSpan?` | 1 hour | How long after a run started a sweep keeps retrying a replay that admission refuses, before ending it `not_admitted`. Every refusal counts: a permit held by a peer, a full run-slot gate, a collapsed duplicate fire. A cluster that stays saturated longer than this ends the runs it could not fit. Null keeps such runs for ever, re-dispatched every `SweepInterval`. With the heartbeat off a sweep can claim a run a live peer is executing, so this can end a run that is fine. Measured from the run's start to the claim, both on the database clock. Not applied while the execution gate reports safe mode. |

### WorkflowDurableSleepOptions

`e.DurableSleep`.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `ParkWhenAtLeast` | `TimeSpan?` | null | The shortest durable sleep that parks its run as a suspension row instead of waiting in the process. Null keeps every sleep in the process; zero or less is refused. Needs the waker running with `Sleep` among its kinds. Read [parking.md](parking.md#turning-parking-on-and-off) before setting or unsetting it. |

### WorkflowSuspensionWakeOptions

`e.Waker` (a record: assign `e.Waker = e.Waker with { ... }`). The waker runs on every replica and moves
parked runs: due timeouts and sleeps, template and predicate waits, children that ended, and wakes the host
publishes.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `Enabled` | `bool` | true | Whether the waker runs at all. False leaves the rows to whatever else the host wakes them with; parked sleeps then never wake (a startup warning says so). |
| `PollInterval` | `TimeSpan` | 5 seconds | How often a pass runs. A parked run waits at most this long past the moment it is ready, longer under a backlog. |
| `BatchSize` | `int` | 50 | Parked rows one pass reads: due timeouts and sleeps oldest timeout first, then templates and sub-workflow parks; the same cap bounds the abandoned takes it looks at and the held wakes it retries. The next pass still waits its `PollInterval` (a park or a host wake starts it sooner), so an overdue backlog drains at most this many rows per interval. A pass also stops taking once every resume worker (`MaxConcurrentResumes`) is busy. A due row a pass reads but leaves parked keeps its timeout, so it is read first again on the next pass. |
| `SettleFor` | `TimeSpan` | 2 seconds | How long a new suspension row is left alone, because it exists before its run has finished parking. Delays only the first look at a freshly parked run. |
| `MaxConcurrentResumes` | `int` | 8 | Taken runs one pass re-enters at once. |
| `MaxConcurrentWakes` | `int` | 4 | Host wakes this replica re-enters runs for at once, a budget separate from the pass so a stream of wakes cannot starve due timeouts and sleeps. |
| `MaxHeldWakes` | `int` | 1024 | Wakes kept while waiting for their row to land; past it the oldest is dropped. Zero holds nothing. |
| `HoldWakesFor` | `TimeSpan?` | null | How long a held wake waits for its row before it is dropped. Null derives the longer of twice `SettleFor` and three `PollInterval`s (15 seconds with the defaults). |
| `RetakeAbandonedTakesAfter` | `TimeSpan?` | `Recovery.AbandonedAfter` | How long a taken wait may go without its run being re-entered before a pass takes it over (its taker died between the take and the re-entry). Only runs with a state row are retaken. Null on a hand-composed waker never retakes. |
| `Kinds` | `IReadOnlyCollection<WorkflowSuspensionKind>?` | null (every kind) | Which kinds of parked row this waker moves: `WaitForTrigger`, `WaitTemplate`, `Sleep`, `SubWorkflow`, `Predicate`. Empty is refused. A host that wakes its own templates and triggers names `Sleep` alone. A kind nothing moves stays parked for ever. |
| `ResolveTimeZone` | `Func<WorkflowSuspensionCandidate, CancellationToken, Task<TimeZoneInfo?>>?` | from `Defaults.ResolveTimeZoneAsync` | The zone of a parked template, read from its row. |
| `TypeVocabulary` | `Func<WorkflowTypeVocabulary>?` | the compile's vocabulary | The port-type vocabulary a host namespace's value is held to while a template waits. |
| `TimeZone`, `ExpressionNamespaces`, `NamespaceScopeFactory` | | filled by `AddWorkflowEngine` | Set from `Defaults`; a host sets them only on a waker it composes by hand. |

## WorkflowSuspensionOptions

Registered by the host as a singleton (`services.AddSingleton(new WorkflowSuspensionOptions { ... })`); read by
the library's suspension store and by `AddWorkflowEngine`.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `MaxStatePayloadBytes` | `int` | 256 KB | Cap on one parked run's state payload. A wait past it fails the run at the wait (measured before the insert); a sleep past it waits in the process instead of parking. |

## WorkflowEditorOptions

`.AddWorkflowEditor(e => ...)`.

| Property | Default | Effect |
| --- | --- | --- |
| `Transaction` | the library's transaction on its own context | Seam for the host's unit of work, so an import writes the host's rows and the library's as one. A host transaction must answer `JoinsAnotherTransaction`; inside one, create and update save a draft and defer the publish, and publish and rollback are refused, because the compiler cannot read what that transaction wrote. |

## StandardWorkflowBlockOptions

`AddStandardWorkflowBlocks(b => ...)`.

| Member | Effect |
| --- | --- |
| `Override<TFactory>(string blockTypeId)` | Registers `TFactory` (built through the container) instead of the standard block with that id, not beside it. An id that is not a standard block throws at the call. |

## WorkflowRetentionOptions

`AddWorkflowRetention(r => ...)`. Two sweeps, both on the leader only (see
[clustering.md](clustering.md#leader-gated-jobs)). Values outside the ranges are clamped by the sweeps, not
refused.

`AddWorkflowRetention` takes a second argument, `clock` (`Func<IServiceProvider, TimeProvider>`), for a host that
owns time and does not want to register `TimeProvider` for everything it runs. It defaults to the container's
`TimeProvider`, else the system clock.

The members of `r.Versions` and `r.ExecutionHistory` are init-only: assign a whole object, as in
`r.Versions = new() { RetentionDays = 30 }` (and the same for `r.ExecutionHistory`).

`r.Versions` (`WorkflowVersionRetentionOptions`):

| Property | Default | Range | Effect |
| --- | --- | --- | --- |
| `MaxVersionsPerWorkflow` | 50 | 1 to 10 000 | A superseded version goes once this many newer superseded versions exist, or once it is older than `RetentionDays`, whichever comes first. Current and draft versions are never counted or deleted, nor is a version any run references, or that a run which has not ended pins in its manifest. A manifest that does not read refuses the whole sweep (logged at `Error`) until its row ends. |
| `RetentionDays` | 365 | 1 to 36 500 | Age after which a superseded version goes regardless of count. |
| `SweepIntervalHours` | 24 | 1 to 8 760 | Hours between sweeps. |

`r.ExecutionHistory` (`WorkflowExecutionHistoryRetentionOptions`): execution logs, their entries and node
steps, wait suspensions, and ended run-state rows.

| Property | Default | Range | Effect |
| --- | --- | --- | --- |
| `RetentionDays` | 90 | 1 to 3 650 | Days a finished run stays queryable. |
| `DeleteBatchSize` | 1 000 | 50 to 50 000 | Rows per delete statement; bounds the lock footprint of one delete. |
| `MaxSweepSeconds` | 60 | 1 to 3 600 | Seconds one sweep deletes from each table before it lets the database go. A sweep that stops here (or at `MaxDeletesPerSweep`) with expired rows left comes back after a pause as long, instead of after `SweepIntervalHours`. |
| `MaxDeletesPerSweep` | 5 000 000 | at least `DeleteBatchSize` | Ceiling per table per sweep. |
| `SweepIntervalHours` | 6 | 1 to 168 | Hours between sweeps. |

## WorkflowDebugOptions

`AddWorkflowDebug(d => ...)`. Values outside the ranges are clamped when the debug service is created.

`AddWorkflowDebug` also takes three optional arguments after the options callback:

| Argument | Effect |
| --- | --- |
| `clock` | A `Func<IServiceProvider, TimeProvider>` for the session service and the retention sweep. Defaults to the container's `TimeProvider`, else the system clock. |
| `auditSink` | A `Func<IServiceProvider, IWorkflowAuditSink>` that receives the `debug.*` audit events. Defaults to the container's audit sink, opened in a scope per write. |
| `seams` | An `Action<WorkflowDebugSeams>` that fills `Notifier` (how a paused run reaches whoever watches it; the default notifies nobody), `LeaderGate` (which replica runs the session sweep; the default is derived from the run lock) and `BlobStore` (where snapshot bytes live; the default is the workflow database, which a host with object storage should replace). |

`AddWorkflowDebugRetention(d => ..., clock)` registers the session sweep alone.

| Property | Default | Range | Effect |
| --- | --- | --- | --- |
| `SnapshotRetentionDays` | 7 | 1 to 365 | Days after which finished sessions and their snapshots are swept (on the leader). |
| `OrphanedSessionTimeoutSeconds` | 300 | 30 to 3 600 | Inactivity after which a paused session is abandoned and its run killed; a half-stepped run holds its slot and its locks. |
| `CommandAckTimeoutSeconds` | 5 | 1 to 60 | How long a command sent to the replica holding the run waits for its confirmation; past it the answer is `Unconfirmed` (the command may still have been applied). |
| `KillWaitTimeoutSeconds` | 30 | 1 to 600 | How long a delete waits, after a kill was applied, for the run to end before it gives up and keeps the session. |

## NpgsqlWorkflowLockOptions

`Seams.RunLockProvider.UseNpgsqlLocks(l => ...)`. Every replica of one deployment must agree on `KeyPrefix`
and `LockId`.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `ConnectionString` | `string?` | required | The connection the package opens its own session on, held open for the life of the process. Its own, not the application's pool: an advisory lock belongs to the session that took it. The package turns pooling off and drops `Multiplexing` from the string, and sets a 30-second keep-alive when the string sets none, so a session killed by an idle timeout or a failover is noticed. |
| `KeyPrefix` | `string` | empty | Prepended to every lock key. Set it when two deployments share one database: advisory locks have one namespace per database and the library's keys are constants, so two unprefixed deployments take each other's locks and leadership without noticing. |
| `LockId` | `Func<string, long>` | `DeriveLockId` (SHA-256 of the key, first eight bytes, little-endian) | Turns a key into the 64-bit advisory-lock number. A cross-replica and cross-application contract. |

## RedisWorkflowOptions

`Seams.RunSlotGate.UseRedis(r => ...)`. Every replica of one deployment must agree on these values.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `RunSlotKeyPrefix` | `string` | `wfslots:` | Prefix of the per-workflow sorted set of held slots; the workflow id follows in `N` format. Distinct per deployment on a shared Redis. |
| `RunSlotTtl` | `TimeSpan` | 5 minutes | How long a held slot survives without renewal or release before it is treated as a crashed owner and pruned. Live runs renew their slot on the heartbeat. It must stay far longer than any healthy block run and the heartbeat interval, or a live slot is pruned mid-execution and the budget over-admits. |

## RedisFireDedupOptions

`Seams.FireDedup.UseRedis(r => ...)`.

| Property | Type | Default | Effect |
| --- | --- | --- | --- |
| `KeyPrefix` | `string` | `reeveworkflow:fire-dedup:` | Prefix of the claim keys. Two replicas that disagree on it both claim and both fire. A host that already holds claims under another prefix states it here. |

## Metrics and tracing

`Defaults.Metrics` (`WorkflowMetricsOptions`) and `Defaults.Tracing` (`WorkflowTracingOptions`). Every
instrument name is settable.

| Property | Default | Instrument |
| --- | --- | --- |
| `MeterName` | `ReeveWorkflow` | The meter (`AddMeter`). |
| `RunsStarted` | `workflow.runs.started` | Runs admitted. |
| `RunsCompleted` | `workflow.runs.completed` | Runs ended, tagged `status` and `trigger_type`. |
| `RunDuration` | `workflow.run.duration` | Run duration in seconds, same tags. For a parked run it covers the part after the wake. |
| `NodeErrors` | `workflow.node.errors` | Node errors, tagged `block_type`, `policy`, `caught`, `exception_type`. |
| `RunAdmissions` | `workflow.run.admissions` | Admission decisions, tagged `mode` and `outcome` (`started`, `skipped`, `restarted`, `queued`, `dropped`, `errored`). |
| `DebugSessions` | `workflow.debug.sessions` | Debug sessions ended, tagged `outcome`: `completed`, `failed`, `cancelled`, `killed` or `abandoned`. |
| `DebugPauseDuration` | `workflow.debug.pause.duration` | Seconds paused at a breakpoint. |
| `SuspensionsStarted` | `workflow.suspensions.started` | Runs parked, tagged `kind`. |
| `SuspensionsResolved` | `workflow.suspensions.resolved` | Parked runs resolved, tagged `kind` and `outcome`. |
| `SuspensionDuration` | `workflow.suspension.duration` | Seconds parked, same tags. |
| `Tracing.ActivitySourceName` | `ReeveWorkflow` | The activity source (`AddSource`). |

## Per-workflow settings

Stored on the workflow row and set through the Editor. Values the run would read as something other than what
is written are refused at create, update and import.

| Setting | Effect | Refused |
| --- | --- | --- |
| `ExecutionTimeoutSeconds` | The run's deadline. | Zero or negative (`execution-timeout-not-positive`), above 4 294 967 seconds, about 49.7 days (`execution-timeout-above-timer-limit`). |
| Retry policy (`WorkflowRetryPolicy`): `MaxAttempts`, `BackoffStrategy` (`fixed` or `exponential`), `InitialDelayMs` | Attempts of the whole run. | Malformed JSON (`retry-policy-malformed`), another strategy (`retry-backoff-other`), a written `maxAttempts` below 1 (`retry-max-attempts-below-1`), a written `initialDelayMs` below 1 (`retry-initial-delay-below-1`). |
| `Mode` | How runs of this workflow are admitted: `Single` (the default), `Restart`, `Parallel` or `Queued`. See [clustering.md](clustering.md#the-cluster-seams) for what each needs across replicas. | A name that is not one of the four. |
| `MaxConcurrentRuns` | Runs at once in `Parallel` and `Queued` mode. Default 1. | Below 1. |
| `MaxQueuedRuns` | Triggers that may wait for a place in `Queued` mode. Default 10. The queue is in memory on each replica (see [clustering.md](clustering.md#the-queued-queue-is-per-replica)). | Below 0. |
| `OverflowBehavior` | What a `Parallel` or `Queued` workflow does with a trigger that finds no place: `Drop` (the default; the trigger is skipped), `Log` (skipped, and a warning is logged) or `Error` (the start throws, and `execute` answers 409). | A name that is not one of the three. |
| Checkpoint policy (`WorkflowCheckpointPolicy`): `Mode` (`EveryBlock`, `BeforeNonIdempotentOnly`, `Heartbeat`), `HeartbeatSeconds` | Overrides the engine's `RunStateWriteMode` for this workflow. | The Editor refuses a policy the host's settings cannot honour (a mode that needs the heartbeat while it is off; a `Heartbeat` interval not shorter than `Recovery.AbandonedAfter`); a run falls back to the engine's mode for such a policy. |

A run with a deadline or with more than one attempt cannot park a durable sleep; see [parking.md](parking.md).

## Limits a host or an author meets

| Limit | Value | Behaviour |
| --- | --- | --- |
| Per-node timeout and retry delay | at most one day (86 400 s) | Refused by the compiler at save. A stored version carrying a larger or unreadable value fails the run when the walk reaches the node (see [../faq.md](../faq.md)). |
| Per-node retry backoff | | The first retry waits about the stated delay; each later one about twice the previous (exponential, with jitter). |
| Leaf node deadline | 30 s unless the node names one | A container has none unless `DefaultContainerTimeoutSeconds` is set. |
| Run deadline | about 49.7 days | See per-workflow settings. |
| HTTP response body | 4 MiB (`HttpRequestBlock.MaxResponseBodyBytes`) | Past it the request fails as a network failure does: `Success` false with the reason in `Body`, or a throw under `ThrowOnFailure`. A failure message quotes at most the first 1 024 characters of a body. A derived block overrides the number. |
| `WaitForDuration`, HTTP request deadline | about 49.7 days (the longest `Task.Delay` / `CancelAfter`) | An overflow fails the node; it is not clamped. |
| Loop iterations, items | `ForLoop` 10 000 and `WhileLoop` 100 iterations unless the node's `MaxIterations` says otherwise; `IterateOver` 10 000 items unless its `MaxItems` does | The loop node fails past its cap. |
| Sub-workflow call depth | 10 | Deeper calls are refused. |
| Node id, block type id | 100 characters | Refused at compile (`node-id-too-long`, `block-type-id-too-long`): every entered node is written to the node log, whose columns are that wide. |
| Expression | depth 32, 1 000 syntax nodes | Refused when parsed. |
| Traced step value | `MaxPayloadValueBytes` (8 KB) | Replaced by a note saying so. |
| Recorded steps and node log entries per walk segment | `MaxRecordedSteps` (10 000 of each; twice that for ones carrying an error) | Later ones are not recorded; the run goes on. |
| Checkpoint | `MaxCheckpointBytes` (256 KB) | The row records that it did not fit; a recovery replays from the start. |
| Parked payload | `WorkflowSuspensionOptions.MaxStatePayloadBytes` (256 KB) | A wait fails the run at the wait; a sleep stays in the process. |
| Replica name | 100 characters | Refused by `AddWorkflows`. |

The library's expression preview service reads local-time fields in the zone a run reads them in:
`Defaults.ResolveTimeZoneAsync`, asked from the request's scope, when it is set, and `Defaults.TimeZone` otherwise. A
preview context pinned with its own `LocalNow` keeps it. A host that registers its own `ExpressionPreviewService`
gets this only through the constructor that takes the zone; built with another constructor, it reads UTC.

## Options of a hand-composed engine

`AddWorkflowEngine` builds these from the options above. A host that constructs the engine, runner, resumer,
sleeper or recorder itself passes them directly.

| Class | Holds |
| --- | --- |
| `WorkflowEngineSettings` | Everything `WorkflowEngineOptions` and `Defaults` say, flattened, plus `TimeProvider` (the clock for coordinator retention, node deadlines, retry backoff and in-process sleeps; persisted instants stay on wall-clock time) and `RestartCancelChannel`. Its `SleepPollInterval` defaults to 1 second, and `HandOverRunsOnStop` and `RecoveryReentersRuns` to false. With `IsMultiReplica` true, a null run-slot gate, fire de-duplication, run lock or Restart ordering is refused. |
| `WorkflowGraphExecutorOptions` | What the walk reads; `WorkflowGraphExecutorOptions.For(settings)` maps it. `MaxResilienceSeconds` (86 400) is the longest node deadline or retry delay. |
| `WorkflowRunnerOptions`, `WorkflowResumeOptions` | What a run or a resumed segment needs about its host: replica id, handover, heartbeat, write mode, the recovery store the heartbeat renews. `WorkflowResumeOptions.StateRowTimeout` (30 s) bounds each read or write of a sleep park. |
| `WorkflowSleepOptions` | `CancellationPollInterval` (required), `ReplicaId`, `TimeProvider` for an in-process durable sleep. |
| `WorkflowTransitionOptions` | `ReplicaId` and `AuditTransitions` for the node-entry recorder. |
| `WorkflowAdmissionOptions` | `FireDedupWindowSeconds` (10). |
| `CompilationOptions` | The type registry, namespace registrations and helper resolver the compiler reads. `AddWorkflows` registers it. |
