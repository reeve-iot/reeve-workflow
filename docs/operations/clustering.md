# Running several replicas

A host can run the engine as several processes over one database. At their defaults, every cluster seam
answers for one process only: two replicas each hold their own in-process permit, so a `Single` workflow
triggered on both runs twice, and `Parallel(n)` admits up to `n` per replica. Nothing about that fails loudly.
A clustered host therefore declares its peers and supplies the seams below. A declared deployment with peers
refuses to start unless every cluster seam has a cross-replica answer.

## Declaring peers

```csharp
builder.Services.AddWorkflows(workflows =>
{
    workflows.ConfigureDbContext(db => db.UseNpgsql(postgres));
    workflows.Defaults.ReplicaId = replicaId;          // distinct on every replica, at most 100 characters
    workflows.Defaults.IsMultiReplica = true;

    workflows.Seams.RunLockProvider.UseNpgsqlLocks(locks => locks.ConnectionString = postgres);
    workflows.Seams.RunSlotGate.UseRedis();
    workflows.Seams.FireDedup.UseRedis();
    workflows.Seams.ClusterSignal.UseRedis();
})
.AddPostgreSqlSchema()
.AddWorkflowEngine(engine => engine.Recovery.Enabled = true);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
```

- `Defaults.ReplicaId` names the process on every run-state row it owns and on the cluster signal. Every replica
  needs its own: two that share one drop each other's signals as their own echoes, and recovery cannot tell their
  runs apart. A machine name is not enough where two replicas share a machine.
- `Defaults.IsMultiReplica = true` makes `AddWorkflows` refuse to start while a cluster seam is on its
  single-process answer, and switches on the cross-replica checks: the run-slot gate, fire de-duplication and the
  Restart watch are consulted only when it is true. A multi-replica deployment that leaves it false runs with
  in-process budgets even if Redis is wired.
- Recovery carries a run across a replica that died. It is off unless enabled; see
  [When a replica dies](#when-a-replica-dies).

The example host in [../../examples/dispatch-host/README.md](../../examples/dispatch-host/README.md) is wired this
way and tested as several replicas against real PostgreSQL and Redis.

## The cluster seams

| Seam (`Seams.X`) | Question it answers | Backend package | Refused on a declared cluster when left alone |
| --- | --- | --- | --- |
| `RunLockProvider` | "At most one run" for the one-permit modes (`Single`, `Restart`). | `ReeveWorkflow.Npgsql` (`UseNpgsqlLocks`) | yes |
| `RunSlotGate` | A shared count of held slots for the N-permit modes (`Parallel`, `Queued`). | `ReeveWorkflow.Redis` (`UseRedis`) | yes |
| `FireDedup` | One physical trigger event that reached every replica becomes one run (modes other than `Single`, which the run lock already covers). | `ReeveWorkflow.Redis` (`UseRedis`) | yes |
| `RestartGenerations` | Which `Restart` trigger is the newest, read by every replica from one place. | `ReeveWorkflow.EntityFrameworkCore` fills it by default, over two of its tables | yes, if replaced by the in-process answer |
| `ClusterSignal` | The transport for restart supersession and the debugger's commands, acknowledgements and breakpoints. | `ReeveWorkflow.Redis` (`UseRedis`) | no: its absence loses a takeover or a debugger step, not correctness |

The startup check sees seams named in their slot, including ones given as a factory, and seams registered in the
container before `AddWorkflows` without a key. A seam registered after `AddWorkflows`, or under a key, is not
visible to it (the refusal message says so); name it in its slot.

A host may implement any seam itself instead of using the packages:

- `IWorkflowRunLockProvider.TryAcquireAsync` returns null only when a peer demonstrably holds the key, and
  throws on a fault, so the caller can tell "contended" from "unknown".
- A run-lock provider must be a singleton: the leader gate is derived from it and runs on a timer with no scope.
- `IWorkflowClusterSignal` must drop this replica's own broadcasts and must never throw on a transport fault.

### PostgreSQL advisory locks (`ReeveWorkflow.Npgsql`)

The run lock is a PostgreSQL advisory lock on one session the process keeps open on its own connection. There is
no TTL and nothing to renew: the lock lives as long as the session, so a killed replica frees its locks at once
and a stalled one keeps them until its connection actually goes. The provider probes the session periodically; a
session lost to an idle timeout or a failover is a new session, and the leases taken on the old one report
themselves invalid. Dropping the session releases every lock the process holds.

Advisory locks have one namespace per database. Two deployments sharing a database set distinct
[`KeyPrefix`](configuration.md#npgsqlworkflowlockoptions) values, or they take each other's locks and leadership.

### Redis (`ReeveWorkflow.Redis`)

The host registers an `IConnectionMultiplexer`. `FireDedup.UseRedis()` and `ClusterSignal.UseRedis()` refuse to
resolve without one. `RunSlotGate.UseRedis()` does not: without a multiplexer it admits on the in-process permit
alone, so register the multiplexer on every replica that declares peers.

- **Run-slot gate.** A sorted set per workflow, scored by Redis server time and admitted by one Lua script, so
  two racing replicas cannot both take the last slot. A live run renews its slot on the run heartbeat. A slot not
  renewed or released within [`RunSlotTtl`](configuration.md#redisworkflowoptions) is treated as a crashed owner
  and pruned on the next acquire; until then the budget under-admits by that slot. Keep `RunSlotTtl` far longer
  than any healthy block and the heartbeat interval.
- **Fire de-duplication.** `SET NX` with an expiry of `FireDedupWindow` (10 seconds) on the fire key. Keys are
  logged as fingerprints, never raw, because a key can carry a person's data.
- **Cluster signal.** Redis pub/sub, which is at-most-once: a message published while a subscriber is
  disconnected is lost, and nothing replays it. Every signal is therefore a hint, never a fact. A lost
  restart-cancel is caught by the running `Restart` run's own periodic look for a newer trigger (every heartbeat,
  or every ten seconds when the heartbeat is off). A lost debugger command is reported `Unconfirmed`. Channels
  are instance-wide, not scoped by database index: two deployments on one Redis use distinct
  [channel names](configuration.md#workflowclustersignalchannels), slot prefixes and fire-dedup prefixes.

A host that already publishes its own messages on one of these channels changes the message body by adopting this
transport; its replicas cannot read each other for the length of that rollout.

## The `Queued` queue is per replica

In `Queued` mode a trigger that finds every place taken waits in memory on the replica that received it, up to
`MaxQueuedRuns`. Nothing about a waiting trigger is written down, with Redis or without: a crash or a stop loses
it, and a peer's queue is a different queue, so a fleet of N replicas can hold N times `MaxQueuedRuns` waiters. Only
the places (`MaxConcurrentRuns`) are counted across replicas, by the run-slot gate, and a waiter asks for one when it
reaches the front of its queue. If there is none then, it is refused as the workflow's `OverflowBehavior` says. A
trigger that must not be lost belongs in something durable, such as an outbox or a broker, before it reaches the
engine.

## Failure behaviour

Coordination backends fail open: when one cannot answer, the run proceeds on the in-process permit alone and the
fault is logged. Over-admission across replicas after a blip is accepted; stopping every workflow on a Redis blip
is not.

| Outage | What happens |
| --- | --- |
| Redis, slot gate | Acquire fails: admitted on the in-process permit (logged `degrade-open`). Renewal fails: the run keeps its place until the next beat. Release fails: the slot is left for the TTL prune. |
| Redis, fire de-duplication | The fire is admitted, so one event may run once per replica. |
| Redis, cluster signal | Publishing or subscribing fails quietly; peers are not heard until Redis is. Restart supersession falls back to the store-side check. |
| Advisory-lock session | A lock fault admits on the in-process permit. A lease whose session died reports itself invalid; the run stops and its row is released for recovery once the budget allows (`handed_over_lease_lost`). |
| Parked-sleep lookup | A `Single` or `Restart` admission that cannot ask whether a run is parked on a sleep admits. |
| Workflow database | Starts fail. Resumes are deferred and stay parked for a later pass; claimed rows stay claimed until they age back past `AbandonedAfter`. |

Budgets are therefore best-effort during an outage. A deployment with a hard cap on a rate-limited external side
effect must enforce that cap where the side effect happens, not rely on the workflow mode alone.

Misconfiguration is not an outage and is not failed open: a declared cluster with a seam left on its
single-process answer is refused at startup (see
[configuration.md](configuration.md#values-refused-at-startup)).

## The database clock

Every judgement that compares an instant written by one replica with "now" on another reads "now" from the
database (`CURRENT_TIMESTAMP`): the recovery claim and abandonment age, the recovery giving-up bound, Restart
waiter liveness, suspension deadlines, the waker's settle window and template evaluation. Replica clock skew and
clock steps therefore do not decide which runs are abandoned.

Replicas still stamp two things with their own clock: a block's write of the run's row, and a parked sleep's wake
time (set from the parking replica's clock and compared with the database's). Keep replica clocks reasonably
synchronised and leave room for drift in `Recovery.AbandonedAfter`; a parked sleep on a replica whose clock is
ahead wakes early by that amount. `Restart` supersession of a parked sleep compares instants from replica clocks,
so a fire within the clock skew of the park can be misjudged.

A host that supplies its own store implementations must answer instants from the store's clock, not the caller's.

## Leader-gated jobs

The history retention sweeps, debug-session retention and the compilation sweep run on one replica per tick.
Their leader gate is derived from `Seams.RunLockProvider`: whoever takes the lock for the sweep's key runs it,
and everyone else skips that tick (logged at `Debug`). The lock is held for one run of the work and released
after it, so a replica that dies mid-sweep loses the lock with its session and the next tick has a leader again.
Nothing renews or fences it: a replica that keeps its session but is wedged inside a sweep holds the leadership
until it dies.

With no run-lock provider (a single replica), every sweep runs locally. A host can name a leader gate of its own
by registering an `IWorkflowLeaderGate` in the container: the history sweeps and the compilation sweep take it from
there. `AddWorkflowDebug` also accepts one through its `seams` argument (`LeaderGate`).

## When a replica dies

A live run renews its row on the run heartbeat (`RunHeartbeatInterval`, 10 seconds). The recovery sweep on any
replica claims a row that has gone unrenewed for `Recovery.AbandonedAfter` (1 minute), or that names no replica
because a replica handed it over (a graceful stop, or a lost cluster lease). A replica carries on at most
`Recovery.MaxConcurrentResumes` (8) recovered runs at once; each sweep claims only into the places that are free,
at most `Recovery.BatchSize`, and decides from each row's checkpoint:

| Row's checkpoint | What recovery does |
| --- | --- |
| Names the block in flight, and that block may run again | Re-enters the run at that block; earlier outputs come from the checkpoint. |
| Names a block that may not run twice (not idempotent) | Ends the run `Failed` with `block_not_idempotent_during_recovery: <block>`. |
| Does not read | Abandons the run. |
| None (or did not fit `MaxCheckpointBytes`) | Replays the run from its start. |
| A run started by another run's block | Ends it (`dispatched_by_another_run`): its parent is recovered and calls it again. |

A replay that admission keeps refusing (a peer holds the permit, the slot gate is full, the fire was a
duplicate) is retried every sweep until `GiveUpOnRefusalAfter` (1 hour, from the run's start) has passed, then
ended `not_admitted`. A run replayed hours late can do more damage than a lost one; the reason on the row tells
the operator what the run still owes. A host in safe mode (`Seams.ExecutionGate` reports execution suspended)
refuses starts and defers resumes for the whole boot, and the giving-up bound is not applied under it.

Parked runs belong to no process: any replica's waker resumes them. A parked run whose persisted payload does not
read back is abandoned with `resume_value_invalid`. A take whose taker died before re-entering the run is
taken over after `Waker.RetakeAbandonedTakesAfter`, which defaults to `Recovery.AbandonedAfter`. A wake for a
trigger wait that was held and then dropped is a lost event: it is logged as a warning and noted in the run's own
history.

A node's timeout while the run's own token is still live is an ordinary node fault handled by its error policy,
not a cancellation. A `Restart` sleeper superseded by a newer run is ended when it wakes.

A host that keeps no run state (no run-state store) has nothing to recover: a restart mid-run loses the run.

## Graceful stop and handover

On a graceful stop (`IHostApplicationLifetime.ApplicationStopping`), a run this host stops mid-walk is handed
over when `HandOverRunsOnStop` is true (the default whenever recovery is enabled). Its row keeps its status,
position and wake time, its owner is cleared (fenced on this replica's name), and its journal entry stays open.
The next sweep on any replica picks it up without waiting out `AbandonedAfter` (a sleeping run once its wake-up
time has passed). The journal entry starts with `handed_over_on_stop`, so a history can tell a deploy from an
operator's cancel. A run started by another run's block is not replayed; a sweep ends it instead. Without
handover a stopped run ends `Cancelled`. From the same signal on, this replica's own sweep claims nothing, and
the rows it had claimed but not yet carried to an ending are released the same way, so they do not come back to
it under its own name.

A host whose container has no stop signal (tests, console tools) never hands over. Give the host a shutdown
timeout long enough for in-flight writes to land.

Rolling restarts are safe in this model: stop a replica, let its peers take its runs, start it again. Replicas
on two versions at once are covered in [upgrading.md](upgrading.md#rolling-upgrades).
