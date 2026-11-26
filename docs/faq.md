# Troubleshooting and FAQ

Known problems (the symptom or exact error, then cause and fix), then common questions. The settings named here are
described in [operations/configuration.md](operations/configuration.md).

## Starting a host

### `Defaults.IsMultiReplica is true, so this deployment has peers, but N cluster seam(s) have no cross-replica answer`

**Cause.** The host said it has peers but left a cluster seam on its single-process default: the run-slot gate,
fire de-duplication, the run lock, or Restart ordering. Each would degrade to in-process behaviour and run more
than configured without saying so. A seam registered in the container after `AddWorkflows`, or under a key,
is not visible to the check.

**Fix.** Fill each named seam in its slot (`Seams.RunLockProvider.UseNpgsqlLocks(...)`,
`Seams.RunSlotGate.UseRedis()`, `Seams.FireDedup.UseRedis()`), or set `Defaults.IsMultiReplica = false` if this
really is one process. See [operations/clustering.md](operations/clustering.md).

### `Recovery.Enabled is true but Defaults.ReplicaId is not set`

**Cause.** A recovery claim would write no owner on the row, and the renewal that fences on the owner would
match every peer's rows, so two replicas could run one recovered run.

**Fix.** Set `Defaults.ReplicaId` to a name unique to this replica (at most 100 characters), or turn recovery off.

### `Defaults.ReplicaId is N characters long, and the owned_by_replica_id column holds 100`

**Cause.** The replica name is written on every run-state row the process owns; a longer one would fail every
park, heartbeat and recovery claim once a run is under way.

**Fix.** Shorten the name.

### `DurableSleep.ParkWhenAtLeast is set, but the waker is disabled or its Kinds leave out Sleep`

**Cause.** Nothing would wake a parked sleep.

**Fix.** Enable the waker with `Sleep` among its `Kinds`, or unset the threshold. See
[operations/parking.md](operations/parking.md).

### `Seams.FireDedup.UseRedis() was called, but no IConnectionMultiplexer is registered in the container`

**Cause.** The Redis seams need the host's Redis connection; the same holds for `ClusterSignal`.

**Fix.** Register `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(...))`.
`RunSlotGate.UseRedis()` does not refuse a missing connection: it admits on the in-process permit alone, so
register the multiplexer even if only the slot gate uses Redis.

### `The ... API was mapped without an authorisation intent`

**Cause.** A `Map*Api` route group was mapped with neither `.RequireAuthorization(...)` nor `.AllowAnonymous()`.
The check runs once the host has started.

**Fix.** Add one of the two to the group the `Map*Api` call returns.

### `WorkflowDatabaseNotReadyException: The workflow model has changes that no migration covers`

**Cause.** On PostgreSQL, almost always a missing `.AddPostgreSqlSchema()` after `AddWorkflows`, so the context
does not see the library's migrations. On another provider, the host has not scaffolded a migration for the
model.

**Fix.** Call `.AddPostgreSqlSchema()`; on another provider, scaffold the migration into the assembly the
context options name.

### `WorkflowDatabaseNotReadyException: The workflow schema has N unapplied migration(s)`

**Cause.** The host started in `VerifyOnly` against a database nobody migrated.

**Fix.** Run the migrating job first, or start with `WorkflowDatabaseMode.MigrateThenVerify`. See
[operations/upgrading.md](operations/upgrading.md#applying-migrations).

### The first migration fails on `CREATE EXTENSION` or `CREATE TABLE`

**Cause.** The server has no TimescaleDB, the migrating role may not create the extension, or the database was
built by something other than the library's migrations (its own migration ids, another history table).

**Fix.** Use a server with TimescaleDB and a role that may create it. A database the library's migrations did
not build is not upgraded: recreate it.

### Two replicas migrating at once fail on a duplicate history row or on existing DDL

**Cause.** The library takes no lock around migrations, and EF Core's own lock does not serialise callers that
are several migrations behind.

**Fix.** Migrate under a boot lock of your own (one migrating job, or an advisory lock around the call). See
[operations/upgrading.md](operations/upgrading.md#applying-migrations).

## Running workflows

### A save or start is refused with `resilience-config-invalid`

**Cause.** A node's timeout or retry setting cannot be read: outside the 32-bit range, the wrong kind, a delay or
timeout above one day, or a boolean the engine refuses.

**Fix.** Correct the node's settings and publish again. Also check values the editor flags as clamped: a timeout
below one second, or a negative backoff or retry count, runs something other than what is written.

### Why was my workflow definition refused for a duplicate key, a number, or a type name?

Stored documents have one reading, so anything a save would rewrite or resolve silently is refused:

- A repeated member name in a JSON object is refused rather than resolved to the last one.
- A number must be a finite double whose decimal value equals the value of its double's shortest round-trip
  form. `1.0` and `1e3` are accepted (their values are exact); a number with more precision than a double holds
  (such as `0.30000000000000001`, which reads back as `0.3`) or one outside the double range is refused.
- An unpaired surrogate in a string or a property name is refused, not repaired.
- The port-type names `Any` and `Float` are reserved and refused as `port-type-alias`. The concatenated collection
  spelling (`CollectionInt`) is refused with a hint to write `Collection<T>` with the type of its elements.

### Why did my sleeping workflow not park, or why did a parked sleep never wake?

**Cause.** A sleep stays in the process when any of these holds:

- the workflow's mode is `Queued` or `Parallel`;
- the run is a sub-workflow;
- the run has its own deadline, a retry policy of more than one attempt, or a recorded debug log;
- a node was skipped under a `Skip` policy;
- the sleep sits inside a container that cannot be re-entered;
- the run's outputs cannot be serialised (logged once) or do not fit the payload cap;
- writing the row failed.

A parked sleep is woken only by the library waker. If parking was turned off before the parked sleeps drained, the
waker is disabled, or its `Kinds` leave out `Sleep` (or `SubWorkflow` or `Predicate` for those waits), the rows stay
parked for ever.

**Fix.** Re-enable the waker with the missing kinds until no such rows remain. A startup check and a
once-a-minute warning name the case. See [operations/parking.md](operations/parking.md#turning-parking-on-and-off).

### Why does a node after a parked sleep read a string where it got a `Guid` before?

Earlier outputs of a parked run are stored as JSON and read back: a `DateTimeOffset` or `Guid` becomes a string,
an `int` a `long`, a record a dictionary, `2.0` the `long` `2`. A block that reads earlier outputs by type must
accept both. See [operations/parking.md](operations/parking.md#what-differs-from-a-sleep-in-the-process).

### Why does `POST execute` answer 200 with `{}` when the run failed?

A node that failed under `FailWorkflow`, or a Try/Catch `Rethrow`, is recorded by the engine and answered with
empty outputs; read the outcome from `/executions`. A run that parked on a sleep answers the same way. A run
superseded by a `Restart` trigger while the request was open answers 499.

### Why does the HTTP request block refuse a header?

Headers the block, its client or the connection write themselves (content length, host, transfer coding and
similar) are refused. Keys must be RFC 9110 tokens: a pasted field line such as `Authorization: Bearer x` is not
one, and the message names only the part before the first bad character. Values may hold only HTAB, SP and
visible ASCII. The message names the key, never the value.

### Why does the API return 404 for a workflow that exists?

Workflows are private to their creator, and the API answers the same for "does not exist" and "owned by someone
else", so it cannot be used to discover another user's workflow. Separately, the Editor refuses a write by an
unauthenticated principal, or one that has no subject (a GUID in the `NameIdentifier` or `sub` claim), even
where the host mounts it with `AllowAnonymous()`.

### The Editor answers 503 with `Retry-After: 5`

**Cause.** A compile could not be answered: a host expression-namespace check, a resolver or the workflow store
threw. Nothing was changed. The five seconds are fixed; the library cannot know when that dependency
answers again.

**Fix.** Look for the fault in the log (compiler faults are logged at `Error`) and retry once the dependency
answers. A namespace check that throws fails every publish that reaches it.

### `WorkflowPublishInHostTransactionException`

**Cause.** A publish or rollback was called inside a host transaction (`IWorkflowEditorTransaction` that joins
another), where the compiler cannot read what that transaction wrote.

**Fix.** Publish after the host transaction commits, a callee before its caller.

## Clusters, outages and recovery

### Why did a `Single` workflow run twice, or one event run once per replica?

Either `Defaults.IsMultiReplica` is false on a deployment that has peers (the run-slot gate, fire
de-duplication and the Restart watch are consulted only when it is true), or a coordination backend was
unreachable. Redis and advisory-lock outages fail open: the run proceeds on the in-process permit, which can
over-admit across replicas, and each such admission logs `degrade-open`. A slot whose owner did not release it
(a crash, a Redis error on release) is held until `RunSlotTtl` prunes it. Treat the run budget as best-effort
during an outage. See [operations/clustering.md](operations/clustering.md#failure-behaviour).

### Why was a run ended `not_admitted`?

Recovery reclaimed it from a dead replica, and admission kept refusing the replay (a peer held the permit, the
slot gate was full, the fire was a duplicate) until `Recovery.GiveUpOnRefusalAfter` (one hour from the run's
start) had passed. Set the bound to null to keep such runs for ever instead.

### Why was a run ended `block_not_idempotent_during_recovery`?

Its replica died inside a block that says it may not run twice, so recovery refused to re-run it. The reason
names the block. Mark the block idempotent if it is, or accept that such runs end when their replica dies.

### Why did a run disappear when the host restarted?

A run lives in its process unless something persists where it is. A host with no run-state store, or with
recovery off and no handover, cannot carry a run across a restart mid-run: the run is lost (or ended
`Cancelled` on a graceful stop). Keep the library's run-state store, enable `Recovery`, and set
`Defaults.ReplicaId`. Parked waits survive any restart because they live in the database.

### `WorkflowRunOwnershipLostException`

**Cause.** The run's row stopped being this replica's while it was running or sleeping the run. The reason says
how: `ReclaimedByPeer` (a peer presumed this replica dead and took the run over), `Released` (the row names no
replica: handed over on a stop, or written by a host that names none), `StatusMoved` (cancelled or ended by
something else), `RowMissing` (the row is gone), or `Unknown`. `ReclaimedByPeer` usually means this replica's
heartbeat stalled past `Recovery.AbandonedAfter`.

**Fix.** Nothing on the run; check why the heartbeat stalled (a blocked process, a slow database), and that
`Recovery.AbandonedAfter` leaves room for clock drift.

## Design questions

### Why is there no workflow runtime in JavaScript?

The editor edits; execution is the server's. A second implementation of the graph walk would be a second set of
semantics to keep in step.

### Why do the npm packages ship no blocks?

The editor renders whatever block catalogue the host declares and executes nothing, so it holds no
implementations. The .NET half ships the standard blocks in `ReeveWorkflow.Blocks`, opt-in, with every id
replaceable by name.

### Why is there no debugger or version-history view?

`ReeveWorkflow.Debug` and the Editor serve that state, and `@reeve-iot/workflow-client` types those endpoints
without rendering them. A renderer would be a package of its own; adding a view to `client` would make it depend
on a design system.

### Why is there only an MUI editor?

`@reeve-iot/workflow-core` carries everything that is not presentation; `@reeve-iot/workflow-mui` is one presentation of
it. Another design system belongs in a package of its own, against the same `core`.

### Why does the editor not offer an expression mode for a port?

The library owns the `expr:` protocol (the prefix, mode switching, a lossless round-trip) but no expression
editor. `PortValueEditor` offers the expression mode only when the host supplies `renderExpressionEditor`.

### Why is `${device...}` (or another namespace of mine) refused as `expression-unknown-namespace`?

The engine supplies only `now`, `parameter`, `previous` and `helper`. Every other namespace belongs to the host,
which registers it with `WorkflowExpressionNamespace` (a runtime resolver paired with its static check) in
`Defaults.ExpressionNamespaces`. A namespace with no registration is refused at compile.

### Why can recovery not undo what a half-finished run did?

The library knows only whether a block may be repeated, not how to take back what it did, so recovery either
repeats a block, ends the run, or replays it. Compensation (a block that declares how to undo itself, a saga log
of what to compensate) is not implemented.
