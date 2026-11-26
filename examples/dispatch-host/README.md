# Dispatch host

A complete .NET host for Reeve Workflow: a freight desk that runs workflows. Its domain is shipments, carriers and
loading bays, the same invented desk that `examples/dispatch` builds an editor for on the TypeScript side. It is an
example, not a package, and is never published.

It shows what an adopter of the NuGet packages writes, and its tests run it as several replicas against real PostgreSQL
and Redis.

## What it shows

The registration is one method, `DispatchHost.Build`: `AddWorkflows`, `AddPostgreSqlSchema()`, `AddWorkflowEditor()`
and `AddWorkflowEngine()`, plus the route mapping. The rest of the desk is its domain:

- `Freight/`: four blocks, a port vocabulary, a loading bay, a depot feed and a customs desk.
- `Engine/`: the migration service, the codec that hides header values, and the key ring it reads.
- `DispatchHost.cs`, `DispatchHostOptions.cs`, `Program.cs`: the registration and the deployment's settings, read from
  the environment.

What the desk leaves to the library:

- The engine, the per-workflow coordinators and the recovery sweep come from `AddWorkflowEngine()`. The desk states its
  own recovery policy (cutoff, interval, batch size). Recovery is opt-in because a recovered run repeats the block it
  died in; a block that declares itself not idempotent is never re-entered.
- The write side is `AddWorkflowEditor()` and `MapWorkflowEditorApi(...)`.
- Parking uses the library's tables. `Freight/HoldUntilBlock.cs` is all the desk writes to hold a shipment across a
  deploy.
- The schema comes from `ReeveWorkflow.EntityFrameworkCore.PostgreSql` through one `AddPostgreSqlSchema()` line, on a
  server with TimescaleDB. There are no migrations or design-time factory in the host.
- Metrics, node observer, audit and authorization use the library defaults. The metrics default is a real
  `System.Diagnostics.Metrics` meter.
- Cluster coordination is `Seams.RunLockProvider.UseNpgsqlLocks()`, `Seams.FireDedup.UseRedis()`,
  `Seams.RunSlotGate.UseRedis()` and `Seams.ClusterSignal.UseRedis()`. The desk names the key prefixes
  (`dispatch:fire-dedup:`, `dispatch:run-slots:`) and the signal channel (`dispatch:workflow-restart-cancel`), which
  every replica must agree on.
- `Defaults.IsMultiReplica` is read from `DISPATCH_HAS_PEER_REPLICAS`, because whether a deployment has peers is a
  fact about the deployment.
- `PrintLabelBlock` declares itself not idempotent, so a label print that times out is not retried. The other blocks
  declare `true`. A host whose block factories have not been audited sets `RetryOnlyIdempotentBlocks = false` only
  after auditing them.

### The migration lock

`Engine/WorkflowSchemaMigrationService.cs` takes a session advisory lock before it migrates. EF Core releases its own
migration lock at the commit of each migration, so two replicas starting together with several migrations pending can
both apply the same one and the loser fails. The service asks for the lock repeatedly rather than blocking, because a
blocked replica holds a transaction open, which a concurrent index build waits for. It needs a real session, so it
migrates over a direct connection, not through a pooler in transaction mode.

## Running it

The PostgreSQL server needs the TimescaleDB extension (for example the `timescale/timescaledb` image).

```bash
DISPATCH_POSTGRES='Host=localhost;Database=dispatch;Username=postgres;Password=postgres' \
DISPATCH_REDIS='localhost:6379' \
DISPATCH_REPLICA_ID='desk-1' \
DISPATCH_HAS_PEER_REPLICAS=true \
dotnet run --project examples/dispatch-host/Dispatch.Host
```

Then `POST /api/workflows` to create a workflow and `POST /api/workflows/{id}/execute` to run it, with an
`X-Dispatch-Operator` header holding the owning operator's id.

### Hidden headers and the key ring

A header the editor hides is stored as ciphertext under a Data Protection key. The keys live in the table
`dispatch_data_protection_keys` of the database `DISPATCH_POSTGRES` names, and the application name is fixed, so every
replica reveals what another protected, after any restart or container recreation. The table is not in the workflow
model: the desk creates it at start-up, under the same advisory lock, when it is absent. A runtime role that may not
create tables needs an operator to create it once:

```sql
CREATE TABLE dispatch_data_protection_keys (id bigserial PRIMARY KEY, friendly_name text NOT NULL, xml text NOT NULL);
```

The keys are stored as plain XML beside the ciphertext they open, so the database is the trust boundary: anyone who
can read it can reveal every hidden header. Treat every dump and backup as holding secrets and keep the table in every
restore; without it every hidden header is lost and its node fails with `secret-unresolved`. A deployment that must
keep the keys from a reader of the database wraps them (`ProtectKeysWith…`) with a certificate or a key service. A
failed reveal logs a warning naming the key id that was not found.

## Tests

`Dispatch.Host.Tests` starts the desk as several real operating-system processes against one PostgreSQL and one Redis
from Testcontainers, so the tests can kill a replica the way a crash does. They need a Docker daemon and carry
`[Trait("Category", "Docker")]`:

```bash
dotnet test examples/dispatch-host/Dispatch.Host.Tests/Dispatch.Host.Tests.csproj -c Release --filter "Category=Docker"
```

The solution-wide test command filters them out with `--filter "Category!=Docker"`.

What they cover:

- Single mode across replicas: one run for a workflow dispatched on both, and the lock is released afterwards.
- Recovery: a run whose replica is killed mid-block is finished by the survivor from its checkpoint, under the same
  journal entry; exactly one of several survivors claims it; a block that declares itself not idempotent fails the run
  instead of re-running; a graceful stop hands the run over.
- Restart mode: a newer trigger on one replica cancels the run in flight on the other, with and without the signal.
- Event de-duplication: one depot event delivered to both replicas becomes one run.
- Parking: a held shipment parks in the library's table and resumes once, even when the replica that parked it is
  killed first.
- The editor and executor together: a workflow written through the editor API runs, a draft does not run until it is
  published, and a definition row written by something else runs the same.
- Cluster configuration: a replica that declares peers without a cluster backend does not start, and the message names
  the missing seams.
- Migrations: two replicas bringing an empty schema up to date at once both finish, and the generated plain and
  idempotent scripts each build the schema.
- Database behaviour: the run-lock, run-slot and suspension queries against real PostgreSQL and Redis, the waker's
  paging by index, in-place run row writes, and the history sweep's budget.
