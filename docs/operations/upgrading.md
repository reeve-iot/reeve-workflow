# Upgrading

How to move a host from one version of reeve-workflow to another. What each release changes, and any steps it
needs, are in [../../CHANGELOG.md](../../CHANGELOG.md) under that version's **Upgrading** heading. This page is
the general procedure.

## Version policy

- Every package has the same version. The four npm packages and the twelve NuGet packages are released together
  under one version, taken from the release tag. Use one version for all of them in a host.
- Versions follow semantic versioning. Renaming or removing an export, changing a signature, narrowing an
  accepted type, and renaming a persisted name, a wire name or a refusal id are breaking changes and ship only in
  a major version. A minor version adds, a patch fixes.
- Prereleases (`X.Y.Z-rc.N`) are published under the npm dist-tag `next` and never resolve from a plain install.

## Order of an upgrade

1. Read the **Upgrading** notes in [../../CHANGELOG.md](../../CHANGELOG.md) for every version between the one you
   run and the one you are moving to, in order. Steps are not repeated in later versions.
2. Make the code changes they ask for, and move every reeve-workflow package to the new version in one change.
3. Back up the database if any of those versions changes the schema or rewrites stored data.
4. Migrate, then deploy: apply the library's migrations before a replica on the new version serves (see
   [Applying migrations](#applying-migrations)). A version's notes say when a migration may also run after the
   deploy.
5. Roll the replicas, following the [rolling-upgrade rules](#rolling-upgrades) and any note that says the
   versions must not overlap.
6. Run the [compilation sweep](#after-the-upgrade) to list the workflow versions that do not compile under the new version.

## Schema ownership

The library owns its PostgreSQL schema. `ReeveWorkflow.EntityFrameworkCore.PostgreSql` carries the migrations,
and `.AddPostgreSqlSchema()` points the workflow context at them, at the history table
`automation.__WorkflowMigrationsHistory` and at the TimescaleDB migration generator. The tables live in the
schema `automation`. A host scaffolds no workflow migration and adds no workflow table to its own migrations
assembly. A context the host builds by hand calls `UseWorkflowPostgreSqlSchema()` after `UseNpgsql`.

A database built some other way (other migration ids, another history table, or no TimescaleDB) is not upgraded
by the library's migrations: it fails on `CREATE TABLE` or `CREATE EXTENSION`, and an existing plain
`execution_node_log` table is not converted into a hypertable. Recreate it on a server with TimescaleDB.

On a provider other than PostgreSQL, `ReeveWorkflow.EntityFrameworkCore` ships no migrations: the host scaffolds
them into the migrations assembly its context options name, and owns them.

### TimescaleDB

- The migration creates the `timescaledb` extension and makes `automation.execution_node_log` (one row per node a
  run enters) a hypertable on `StartedAt` with seven-day chunks.
- The library sets no compression and no retention policy on it. Turning either on is a deployment decision, and
  the deployment then owns it. History retention (`AddWorkflowRetention`), where registered, deletes expired rows
  from this table either way.
- The `NodeId` and `NodeType` columns are 100 characters wide, and the compiler refuses a longer node id or block
  type id (`node-id-too-long`, `block-type-id-too-long`) rather than letting a run's write fail. The columns are
  not widened because some TimescaleDB versions refuse `ALTER COLUMN ... TYPE` on a compressed hypertable. Expect
  the same of any change to that table if you turn compression on.

### Applying migrations

| Call | Does |
| --- | --- |
| `IWorkflowSchemaMigrator.MigrateAsync()` (scoped service) | Applies every outstanding migration. Throws `WorkflowDatabaseNotReadyException` first when the model has a change no migration covers. |
| `services.EnsureWorkflowsDatabaseAsync(WorkflowDatabaseMode.MigrateThenVerify)` | The model check, the migrations, then a check that none is left unapplied. |
| `services.EnsureWorkflowsDatabaseAsync(WorkflowDatabaseMode.VerifyOnly)` | The checks only; refuses to start against an unmigrated database. For a deployment where a separate job applies migrations. |
| `WorkflowDatabaseMode.None` | Neither. For a host with its own handling of this schema. |

The library takes no lock around a migration. EF Core locks its history table only inside one migration's
transaction and rereads the history only on the first acquisition, so two replicas that each start several
migrations behind interleave, and one fails on a duplicate history row or on DDL that already exists. With
exactly one migration outstanding they serialise. Migrate under a boot lock of your own: one migrating job, or an
advisory lock on a dedicated unpooled connection around the call. The example host does the second with
`pg_try_advisory_lock` in a hosted service that runs before the others
(`examples/dispatch-host/Dispatch.Host/Engine/WorkflowSchemaMigrationService.cs`).

The usual cause of "the workflow model has changes that no migration covers" on PostgreSQL is a missing
`.AddPostgreSqlSchema()` after `AddWorkflows`. The compilation sweep asks the same question before it runs and
does not run while the database is not ready.

## A host that writes the workflow tables itself

A host that writes `automation.workflows` or `automation.workflow_versions` without the Editor (an import, a
seed, a re-render) must keep two rules, or a publish can promote over its write, or it can make current a version
that does not compile:

1. Before setting a current version, call `WorkflowCompilationService.EnsureAsync(version, null,
   cancellationToken)` and do not set it when the verdict is invalid. `AddWorkflows` registers the service.
2. In the transaction that moves `current_version_id` or `draft_version_id`, changes a version's documents, or
   writes a field an update carries (name, description, host metadata, mode, concurrency, timeout, policies),
   run `UPDATE automation.workflows SET revision = revision + 1 WHERE id = @workflow` as the first write to that
   workflow's rows, before any write to its versions. A publish compares the revision it read before it
   promotes, and an update that names the revision its caller read is refused once it moved.

Version retention moves no revision: it deletes only versions no workflow names current or draft, in the
statement that deletes them.

## Releases that rewrite stored data

Some releases change what a stored document may say, and their notes carry a one-time data step. The procedure
that always works:

1. Optionally let runs that should finish do so on the old version. They are the
   `automation.workflow_executions_state` rows whose status is `Pending`, `Running`, `Sleeping`, `Waiting` or
   `Suspended`.
2. If the notes say the versions must not overlap, stop every old replica. Nothing checks this: an old replica
   left running can write to rows the step changes.
3. Back up the database.
4. Start one new replica that migrates (`MigrateThenVerify`) and runs the step the notes name. Read the report
   the step returns.
5. Start the other replicas.
6. Run the compilation sweep, with your own blocks and namespace checks registered.

## After the upgrade

A start compiles the version it runs and is refused when it does not compile, so a version that compiled under
the old release and not under the new one stops starting. To list them, run the compilation sweep from a process
that registers the same blocks and expression-namespace checks as the serving replicas:

```csharp
var runner = app.Services.GetRequiredService<WorkflowCompilationSweepRunner>();
var run = await runner.RunOnceAsync(cancellationToken);
```

`AddWorkflows` registers the runner whether or not `CompilationSweep.Enabled` is set. `run` is null when this
replica did not lead the sweep's gate or the database was not ready (logged at `Warning`). Otherwise `run.Report`
holds:

- `Invalid`: each current version that does not compile, with diagnostics naming the rule and, where they have
  them, the node and the port. Fix what they name, or decide that those workflows stop running.
- `Failed`: versions the sweep could not answer because the compilation service threw (a resolver, the store, or
  a `CompilationFaultException`). Sweep again.
- `Current`, `Unsaved`, `Shared`: every version answered, verdicts the store did not save, and versions more than
  one workflow names.

`GET {root}/compilation/invalid` reads only the caller's own workflows, so on a host where several users create
workflows it is not a substitute.

## Rolling upgrades

During a rolling upgrade, replicas on two versions share the database, Redis and the signal channels. Within one
major version:

- Wire messages and persisted payloads may gain fields, never rename or remove one. A replica ignores what it
  does not know: a field a newer replica added, or a debugger command name from a newer version. Run
  checkpoints, parked payloads and admission outcome strings follow the same rule.
- Contracts between replicas do not move in a mixed rollout. Channel names (`Defaults.SignalChannels`), the Redis
  key prefixes, the advisory-lock `KeyPrefix` and `LockId` are read by every replica. Two that disagree do not
  fail: they stop sharing a budget or stop hearing each other. Change one only in a rollout with no mixed
  versions.
- Schema changes are additive before the deploy. A migration a new replica needs runs before it serves, and an
  old replica keeps working against the migrated schema unless the version's notes say otherwise.
- Features that write new kinds of row are turned on after every replica can read them. Durable-sleep parking is
  the standing example; see [parking.md](parking.md#turning-parking-on-and-off). Turning such a feature off, or
  rolling back below the version that introduced it, means draining its rows first.
- A release that cannot overlap with the previous one says so in its Upgrading notes. Then stop the replicas
  instead of rolling them.

Rolling back follows the same rules in reverse. A migration's `Down` is not needed to run an older binary unless
that release's notes say so, and data written by a feature the older version does not know must be drained first.
