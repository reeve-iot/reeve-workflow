# Reeve Workflow

Reeve Workflow is a workflow-graph engine in two halves that ship separately. Four npm packages model and edit a
graph; twelve NuGet packages execute one. Either half works without the other: the editor runs against any server
that speaks the same HTTP surface, and the engine runs with no editor at all.

All packages carry one version number and are MIT licensed.

## Which package do I need?

### Editor (npm)

| Package | Install when |
|---|---|
| [`@reeve-iot/workflow-core`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/core) | You want the graph model, editor state, port-type system and validation, and bring your own components. No design system. |
| [`@reeve-iot/workflow-mui`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/mui) | You want a working Material UI editor. Depends on `core` and re-exports it, so one dependency is enough. |
| [`@reeve-iot/workflow-client`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/client) | You talk to a `ReeveWorkflow.AspNetCore` and `ReeveWorkflow.Editor` server and want its endpoints typed. Depends on neither of the above. |
| [`@reeve-iot/workflow-adapters`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/adapters) | You want the data sources `core` declares already implemented over `client`. The only package that depends on both. |

### Engine (NuGet)

| Package | Install when |
|---|---|
| `ReeveWorkflow.Abstractions` | You are writing a block. This is the whole dependency closure for one. |
| `ReeveWorkflow.Core` | You want the engine: the expression language and the graph walk. Knows nothing about dependency injection or a database. |
| `ReeveWorkflow.EntityFrameworkCore` | You want the tables. Ships its own `DbContext`; provider-agnostic, with no migrations. |
| `ReeveWorkflow.EntityFrameworkCore.PostgreSql` | Your database is PostgreSQL with TimescaleDB. Ships the library's migrations and makes the execution-step table a hypertable. |
| `ReeveWorkflow.AspNetCore` | You want the Executor's HTTP surface (reading and running workflows), which `@reeve-iot/workflow-client` is generated from. |
| `ReeveWorkflow.Editor` | You want the write side: creating, versioning, drafting and publishing definitions, and the API over it. |
| `ReeveWorkflow.DependencyInjection` | You are writing an add-on that hangs its own `AddX()` on the builder. |
| `ReeveWorkflow.History` | You want run history swept on a schedule instead of growing forever. |
| `ReeveWorkflow.Debug` | You want breakpoints, stepping and state snapshots over a running graph. |
| `ReeveWorkflow.Blocks` | You want branches, loops, Try/Catch, conversions, arithmetic, JSON and an HTTP request without writing them. Opt-in; every block is replaceable by id. |
| `ReeveWorkflow.Redis` | You run several replicas and want the run budget, fire de-duplication and the cluster signal over Redis. |
| `ReeveWorkflow.Npgsql` | You run several replicas and want the run lock (and the leader gate derived from it) as PostgreSQL advisory locks. |

## Requirements

- npm packages: `core`, `mui` and `adapters` need React 18 or 19 as a peer dependency. `@reeve-iot/workflow-mui` also
  needs `react-dom`, `@mui/material` and `@mui/icons-material` 9, and `@emotion/react` and `@emotion/styled` 11.
  `@reeve-iot/workflow-client` has no peer dependencies.
- NuGet packages: .NET 11 (`net11.0`) and, for the EF Core packages, EF Core 11.
- `ReeveWorkflow.EntityFrameworkCore.PostgreSql`: PostgreSQL with the TimescaleDB extension.
- `ReeveWorkflow.Redis`: a Redis server.

[docs/operations/README.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/operations/README.md) has the full list.

## Installation

```bash
npm install @reeve-iot/workflow-mui         # editor with Material UI (pulls in core)
npm install @reeve-iot/workflow-core        # or: headless, bring your own components
npm install @reeve-iot/workflow-client      # typed calls to the server
npm install @reeve-iot/workflow-adapters    # core's data sources, wired to client
```

```bash
dotnet add package ReeveWorkflow.Core
dotnet add package ReeveWorkflow.EntityFrameworkCore
dotnet add package ReeveWorkflow.EntityFrameworkCore.PostgreSql
dotnet add package ReeveWorkflow.AspNetCore
dotnet add package ReeveWorkflow.Editor
dotnet add package ReeveWorkflow.Blocks      # optional: the standard blocks
```

## Usage

### Editor

The port-type vocabulary is open. `core` ships the types every host shares (`String`, `Number`, `Int`, `Boolean`,
`DateTime`, `Json`, `Collection<T>`, `Map<String,V>`) and you register your own:

```ts
import { BUILTIN_PORT_TYPES, createPortTypeRegistry } from '@reeve-iot/workflow-core'

const portTypes = createPortTypeRegistry([
  ...BUILTIN_PORT_TYPES,
  { name: 'Device', aliases: ['device'], displayName: 'Device' },
  { name: 'Room', aliases: ['room'] },
])

portTypes.normalize('Collection<Device>')   // 'Collection<Device>': companion types are derived
portTypes.has('Thermostat')                 // false: not registered
portTypes.normalize('TypeFromANewerHost')   // unchanged: an unknown type never breaks a saved graph
```

The registry goes to `createWorkflowEditorStore({ portTypes })` or to `WorkflowEditorStoreProvider`. See
[docs/reference/port-types.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/reference/port-types.md)
for the rules and the [`client`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/client) and
[`adapters`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/adapters) READMEs for talking to a server.

### Engine

```csharp
builder.Services.AddStandardWorkflowBlocks();

builder.Services.AddWorkflows(options =>
{
    options.ConfigureDbContext(db => db.UseNpgsql(connectionString));
    options.Defaults.ReplicaId = Environment.MachineName;   // unique per process; recovery needs it

    // Anything not named here keeps the library's own answer.
    options.Seams.Metrics.Use<MyMetrics>();
    options.Seams.Authorization.Use<MyAuthorization>(ServiceLifetime.Scoped);
})
.AddPostgreSqlSchema()      // the library's migrations and hypertable; needs TimescaleDB
.AddWorkflowEditor()
.AddWorkflowEngine();

var app = builder.Build();

await app.Services.EnsureWorkflowsDatabaseAsync(WorkflowDatabaseMode.MigrateThenVerify);

app.MapWorkflowApi("api/workflows").RequireAuthorization();
app.MapWorkflowEditorApi("api/workflows").RequireAuthorization();
app.MapWorkflowBlockApi("api/workflow-blocks").RequireAuthorization();
app.MapWorkflowExpressionApi("api/expressions").RequireAuthorization();
```

`AddWorkflows` registers the `DbContext` and answers every seam the library can answer itself. Authorization is
restrictive by default: it grants nothing until you tell it to. `AddWorkflowEngine` composes the engine (the runner,
a coordinator per workflow, the resumer, the recovery sweep and the waker). `EnsureWorkflowsDatabaseAsync` applies the
schema and verifies it. The library takes no lock around it, so with several replicas run it under a lock of your own.

Recovery of runs whose replica died is off by default. Turn it on with `.AddWorkflowEngine(e => e.Recovery.Enabled =
true)`; it needs `Defaults.ReplicaId`. See
[docs/operations/clustering.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/operations/clustering.md#when-a-replica-dies).

[`examples/dispatch-host`](https://github.com/reeve-iot/reeve-workflow/tree/main/examples/dispatch-host) is a
complete host, tested as several replicas against real PostgreSQL and Redis.

## Documentation

- [docs/architecture/README.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/architecture/README.md): how the library is built and the rules it keeps.
- [docs/operations/README.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/operations/README.md): requirements, configuration, clustering and upgrading.
- [docs/reference/port-types.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/reference/port-types.md): the port-type contract.
- [docs/reference/validation-findings.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/reference/validation-findings.md): validation codes and how to render and translate them.
- [docs/faq.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/faq.md): troubleshooting and common questions.
- [examples/dispatch-host](https://github.com/reeve-iot/reeve-workflow/tree/main/examples/dispatch-host): a complete .NET host.
- [CHANGELOG.md](https://github.com/reeve-iot/reeve-workflow/blob/main/CHANGELOG.md): what each release changes.

## Contributing

See [CONTRIBUTING.md](https://github.com/reeve-iot/reeve-workflow/blob/main/CONTRIBUTING.md). To report a security
issue, see [SECURITY.md](https://github.com/reeve-iot/reeve-workflow/blob/main/SECURITY.md).

## Licence

MIT, see [LICENSE](https://github.com/reeve-iot/reeve-workflow/blob/main/LICENSE).
