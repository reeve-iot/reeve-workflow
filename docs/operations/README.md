# Operations

This guide is for whoever installs reeve-workflow into a host, configures it, runs it on one or several
replicas, and upgrades it. reeve-workflow is a library: there is no server to deploy, only packages a host
references and a schema the library owns in the host's database.

| Page | What it covers |
| --- | --- |
| This page | Requirements, installing the packages, wiring a host, the HTTP surface, what an operator can observe. |
| [configuration.md](configuration.md) | Every options class, each setting with its default and effect, and the values refused at startup. |
| [clustering.md](clustering.md) | Running several replicas: the cluster seams, Redis and PostgreSQL backends, failure behaviour, handover. |
| [parking.md](parking.md) | Durable sleep and parked waits: when a sleep leaves the process, turning parking on and off. |
| [upgrading.md](upgrading.md) | Versioning, schema migrations, rolling upgrades, the compilation sweep. |
| [../faq.md](../faq.md) | Known problems (symptom, cause, fix) and common questions. |

[../architecture/README.md](../architecture/README.md) gives an overview of how the engine runs a workflow
(admission, checkpoints, recovery, parking, the waker). A complete example host, run as several replicas against
real PostgreSQL and Redis, is in [../../examples/dispatch-host/README.md](../../examples/dispatch-host/README.md).

## Requirements

| Component | Requirement |
| --- | --- |
| NuGet packages | .NET 11. Each package carries `lib/net11.0` only, so the host targets `net11.0` or later and runs on the .NET 11 runtime (ASP.NET Core for `ReeveWorkflow.AspNetCore` and `ReeveWorkflow.Editor`). The packages are built against EF Core 11 and Npgsql 11, so the host's own EF Core and Npgsql references are 11.x too. |
| `ReeveWorkflow.EntityFrameworkCore.PostgreSql` | PostgreSQL with the TimescaleDB extension available (the test suites run PostgreSQL 18 with TimescaleDB). The library's migration runs `CREATE EXTENSION` for TimescaleDB and makes the execution-step table a hypertable, so the migrating role must be allowed to create the extension. A server without TimescaleDB fails the first migration. |
| `ReeveWorkflow.Npgsql` | PostgreSQL (advisory locks), reachable on a connection of its own. Depends on `Reeve.EntityFrameworkCore.AdvisoryLocks`, which comes from nuget.org. |
| `ReeveWorkflow.Redis` | A Redis server (the test suites run Redis 7) and an `IConnectionMultiplexer` (StackExchange.Redis 3.x) registered by the host. |
| `@reeve-iot/workflow-core`, `@reeve-iot/workflow-adapters` | React 18 or 19 as a peer dependency. |
| `@reeve-iot/workflow-mui` | React and React DOM 18 or 19, `@mui/material` and `@mui/icons-material` 9, `@emotion/react` and `@emotion/styled` 11, all as peer dependencies. |
| `@reeve-iot/workflow-client` | No peer dependencies. |

The npm packages ship ES modules with type declarations; the consuming application bundles them.

## Installation

Every npm and NuGet package carries the same version number; install them at one version (see
[upgrading.md](upgrading.md#version-policy)). All packages are on npmjs.com and nuget.org; no extra registry or
package source is needed.

### npm

```bash
npm install @reeve-iot/workflow-core        # model, editor store, port types, validation; no design system
npm install @reeve-iot/workflow-mui         # a working editor on MUI; depends on core and re-exports it
npm install @reeve-iot/workflow-client      # typed calls to a ReeveWorkflow.AspNetCore server
npm install @reeve-iot/workflow-adapters    # core's seams implemented over client
```

A prerelease (`X.Y.Z-rc.N`) is published under the dist-tag `next` and a release under `latest`, so a plain
`npm install` never resolves to a release candidate. Install a prerelease with `@next` or its exact version.

### NuGet

| Package | Reference it when |
| --- | --- |
| `ReeveWorkflow.Abstractions` | You write a block. It is the whole dependency closure for one. |
| `ReeveWorkflow.Core` | You want the engine alone: the expression language and the graph walk. No dependency injection, no database. |
| `ReeveWorkflow.EntityFrameworkCore` | You want the tables, `AddWorkflows` and `AddWorkflowEngine`. Provider-agnostic; ships no migrations. |
| `ReeveWorkflow.EntityFrameworkCore.PostgreSql` | Your database is PostgreSQL with TimescaleDB. Carries the library's migrations and `AddPostgreSqlSchema()`. |
| `ReeveWorkflow.AspNetCore` | You serve the Executor's HTTP surface (read and run workflows, blocks, expressions). |
| `ReeveWorkflow.Editor` | You serve the write side: create, version, draft, publish, roll back. |
| `ReeveWorkflow.DependencyInjection` | You write an add-on that hangs its own `AddX()` on the workflow builder. |
| `ReeveWorkflow.History` | You want run history and superseded versions swept on a schedule. |
| `ReeveWorkflow.Debug` | You want breakpoints, stepping and state snapshots over running graphs. |
| `ReeveWorkflow.Blocks` | You want the standard blocks (branches, loops, Try/Catch, conversions, arithmetic, JSON, HTTP). |
| `ReeveWorkflow.Npgsql` | You run several replicas and want the run lock (and the leader gate derived from it) on PostgreSQL advisory locks. |
| `ReeveWorkflow.Redis` | You run several replicas and want the run-slot budget, fire de-duplication and the cluster signal on Redis. |

```bash
dotnet add package ReeveWorkflow.EntityFrameworkCore
dotnet add package ReeveWorkflow.EntityFrameworkCore.PostgreSql
dotnet add package ReeveWorkflow.AspNetCore
dotnet add package ReeveWorkflow.Editor
dotnet add package ReeveWorkflow.Blocks      # optional: the standard blocks
```

## Wiring a host

A minimal single-replica host on PostgreSQL:

```csharp
using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Editor;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Workflows")!;

builder.Services.AddStandardWorkflowBlocks();
builder.Services.AddWorkflows(workflows =>
    {
        workflows.ConfigureDbContext(db => db.UseNpgsql(connectionString));
        workflows.Defaults.ReplicaId = Environment.MachineName;
    })
    .AddPostgreSqlSchema()      // the library's migrations and hypertable; needs TimescaleDB
    .AddWorkflowEditor()
    .AddWorkflowEngine(engine => engine.Recovery.Enabled = true);

var app = builder.Build();

await app.Services.EnsureWorkflowsDatabaseAsync(WorkflowDatabaseMode.MigrateThenVerify);

app.MapWorkflowApi("api/workflows").RequireAuthorization();
app.MapWorkflowEditorApi("api/workflows").RequireAuthorization();
app.MapWorkflowBlockApi("api/workflow-blocks").RequireAuthorization();
app.MapWorkflowExpressionApi("api/expressions").RequireAuthorization();

app.Run();
```

What each call does:

- `AddWorkflows` registers the `DbContext` and answers every host seam the library can answer for itself: the
  stores over its own tables, and defaults for metrics, audit, authorization, the block catalogue and the graph
  host. It refuses a call with no database provider. A host supplies its own implementation of a seam in the
  same call, in the seam's slot: `workflows.Seams.Metrics.Use<MyMetrics>()`.
- `AddPostgreSqlSchema()` points the context at the library's migrations and history table. Without it the
  schema check fails with `WorkflowDatabaseNotReadyException` (see [../faq.md](../faq.md)).
- `AddWorkflowEditor()` registers the write side. Nothing in the Executor references it.
- `AddWorkflowEngine` composes the engine: the runner, a coordinator per workflow, the resumer, the recovery
  sweep and the suspension waker. Its options are in [configuration.md](configuration.md#workflowengineoptions).
- `AddStandardWorkflowBlocks()` registers the standard blocks. A host with its own rule about one of them
  (where an HTTP request may go, who may run a sub-workflow) replaces it by id:
  `options.Override<SsrfGuardedHttpRequestBlockFactory>("HttpRequest")`. The `HttpRequest` block applies no
  restriction on target hosts; outbound policy is the host's, through such a replacement or its `HttpClient`
  configuration.
- `EnsureWorkflowsDatabaseAsync` applies the library's migrations and verifies the schema. The library takes no
  lock around it; with several replicas, run it under a boot lock of your own (see
  [upgrading.md](upgrading.md#applying-migrations)).

Optional packages add their own registration: `services.AddWorkflowRetention(...)` (History) and
`services.AddWorkflowDebug(...)` (Debug). A host's own blocks are registered as `IWorkflowBlockFactory`
singletons. A host that keeps no run-state store runs graphs correctly but cannot carry a run across a restart
(see [../faq.md](../faq.md)). A host running several replicas also sets `IsMultiReplica` and fills the cluster
seams; see [clustering.md](clustering.md).

### Authorization

The default authorization seam grants no capability. Until the host fills `workflows.Defaults.GrantedCapabilities`
(capability names from `WorkflowCapabilities`, granted to everyone) or registers its own `IWorkflowAuthorization`
for per-principal answers, a workflow that needs a capability, such as one with a scripting block, is refused.

Every route group returned by `MapWorkflowApi`, `MapWorkflowEditorApi`, `MapWorkflowBlockApi` and
`MapWorkflowExpressionApi` must end in `.RequireAuthorization(...)` or `.AllowAnonymous()`. A group with neither
stops the host once it has started. A host that gates at its edge mounts the groups with `.AllowAnonymous()`,
but the Editor still refuses a write from an unauthenticated principal or one with no subject claim. Workflows
are private to their creator: "not there" and "not yours" both answer 404.

## HTTP status codes

| Status | When |
| --- | --- |
| 200 with empty outputs | `POST execute` of a run whose node failed under `FailWorkflow`, or a Try/Catch `Rethrow`. The engine recorded the failure; read it from `/executions`. A run that parked on a sleep also answers 200 with `{}`. |
| 400 | A publish, rollback or execute of a version that does not compile (the body carries the diagnostics), a save the Editor refuses, or a body the endpoint cannot read. |
| 401 | The caller is not authenticated, or has no `NameIdentifier` or `sub` claim holding a GUID. |
| 403 | The workflow contains a privileged block (scripting) and its owner lacks the `Scripting` capability, or the host's admission policy refused the start (the body carries its `code`). |
| 404 | The workflow, version or execution does not exist, or the workflow belongs to someone else. |
| 409 from `execute` | The workflow's concurrency budget is exhausted and its overflow behaviour is `Error`. |
| 409 from the Editor | The save lost to another write (the `revision` sent is stale, or the draft changed or the version number was taken meanwhile), a delete while the workflow has active runs, a publish with no draft, or a rollback to the version that is already current. |
| 422 | A version diff over a stored definition that cannot be read. |
| 499 | The run was superseded (a `Restart` trigger replaced it) before it finished, while the request was still open. |
| 500 | A publish or rollback called inside a host transaction (`WorkflowPublishInHostTransactionException`). |
| 503 with `Retry-After: 5` | A resolver, an admission policy or a store did not answer and may answer later. The five seconds are fixed. |

A `Location` header on a created resource is the surface's own address.

## Observing a running host

Metrics go through `IWorkflowMetrics`. The default implementation records on a meter named `ReeveWorkflow`
(`AddMeter("ReeveWorkflow")`); spans go to the activity source `ReeveWorkflow` (`AddSource("ReeveWorkflow")`).
Every instrument name is settable; see [configuration.md](configuration.md#metrics-and-tracing).

- A workflow id is never a metric tag. The trigger type on `workflow.runs.completed` reads `unknown` unless the
  host records its own.
- The recovery sweep reports a claim on every pass, zero included, so a missing series means the sweep is not
  running. It also reports each run it ended without re-entering, by reason (`block_not_idempotent_at_reentry`,
  `not_admitted`, `replayed_by_recovery`, and the others in `WorkflowRecoveryEndingReasons`). The node id is in
  the audit event, not the label.
- The waker reports what became of every wake a host published: `taken`, `not_ready`, `not_mine`, `lost`,
  `held`, `dropped` or `failed`. A take that lost a race and a take the store failed are counted apart.
- The recovery claim, the recovery endings and the wake outcomes are calls on `IWorkflowMetrics`
  (`RecordRecoveryClaim`, `RecordRecoveryEnding`, `RecordSuspensionWake`) with empty default bodies: the default
  meter does not export them. A host that wants them implements those three methods in its own metrics seam.
- Recovery writes audit events for each run it handles: `execution.claimed_by_recovery` when it claims the row,
  `execution.reentered_by_recovery` when it re-enters the run at its checkpoint, and `execution.ended_by_recovery`
  when it ends the run instead (`Data.reason` carries the reason). The subject is the run's journal entry, or its
  run-state row when it has no checkpoint; `Data.subjectIs` says which. A waker that takes over a wait its taker
  abandoned writes `suspension.retaken_by_waker`, with the execution log id as subject. A fleet whose takers keep
  dying shows up in these events.
- The engine logs under the category `ReeveWorkflow.Engine` (settable through `LoggerCategory`). A leaf node's
  block failure is logged under a category of its own, so it can be silenced separately. A leader-gated sweep
  logs why it did or did not run.
