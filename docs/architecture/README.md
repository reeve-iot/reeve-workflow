# Architecture

How reeve-workflow is built, for contributors and for people who want to embed it. What an operator
configures and sees is in [operations/](../operations/README.md); the types and findings the library reports are in
[reference/](../reference/port-types.md).

## What it is

reeve-workflow is a workflow-graph engine in two halves that ship separately. Four npm packages model and edit a
graph. Twelve NuGet packages (.NET 11) check and execute one. Either half works without the other: the editor
runs against any server that speaks its HTTP surface, and the engine runs with no editor at all.

It is a library, not an application. It has no server of its own, no design system and no domain. A host supplies
the domain: port types, blocks, pickers, authorisation, persistence choices and cluster backends. Devices, rooms,
users, tenants and the choice of database cluster are out of scope. It is the workflow engine of the Reeve
home-automation platform, but nothing in it is specific to home automation.

## The two halves

The TypeScript packages:

| Package | What it holds |
|---|---|
| `@reeve-iot/workflow-core` | Graph model, port-type registry, editor store, validation, the stored-document reader. Headless: no design system, no network calls. |
| `@reeve-iot/workflow-mui` | The canvas, block nodes, palette, port tables and dialogs, built on MUI. Re-exports `core`. |
| `@reeve-iot/workflow-client` | Typed calls to the HTTP surface, generated from the surface the server publishes. Holds no state. |
| `@reeve-iot/workflow-adapters` | Implements the seams `core` declares (vocabulary, catalogue, persistence, references) over `client`. |

`core` imports neither `client` nor `adapters`, and `mui` imports neither of them either. `client` is the only
package that talks to a server. `adapters` is the only one that depends on both halves of the editor stack.

The .NET packages:

| Package | What it holds |
|---|---|
| `ReeveWorkflow.Abstractions` | The block SDK: block factories and contexts, port types and terms. A plugin that contributes blocks references this alone. |
| `ReeveWorkflow.Core` | The engine: compiler, expression language, graph walk, admission, recovery, parking, the waker. |
| `ReeveWorkflow.DependencyInjection` | The builder an add-on hangs its own `AddX()` on. |
| `ReeveWorkflow.EntityFrameworkCore` | The library's own `DbContext`, the stores, `AddWorkflows`, the hosted service that resumes runs. Provider-agnostic. |
| `ReeveWorkflow.EntityFrameworkCore.PostgreSql` | The PostgreSQL schema and migrations, with a TimescaleDB hypertable for execution steps. |
| `ReeveWorkflow.AspNetCore` | The executor's HTTP surface: reading and running workflows. |
| `ReeveWorkflow.Editor` | The write side: create, version, draft, publish, roll back. The executor does not reference it. |
| `ReeveWorkflow.History` | Scheduled sweeping of run history and superseded versions. |
| `ReeveWorkflow.Debug` | Breakpoints, stepping and state snapshots over a running graph. |
| `ReeveWorkflow.Blocks` | The opt-in standard blocks: branches, loops, Try/Catch, conversions, arithmetic, JSON, HTTP, sub-workflow, waits. |
| `ReeveWorkflow.Redis` | Cluster seams over Redis: run-slot gate, fire de-duplication, signal transport. |
| `ReeveWorkflow.Npgsql` | Cluster seams over PostgreSQL advisory locks: the run lock and the leader gate. |

Dependencies point one way, from the packages that do the work toward the ones that only describe it. `Abstractions`
depends on logging abstractions only, `DependencyInjection` on the dependency-injection abstractions only, `Core` on
`Abstractions`, and everything else on `Core` or the EF Core package. The build rejects a reference a project is not
allowed to have.

Both halves live in one repository because the server's published endpoints are the generator input for
`@reeve-iot/workflow-client`, and a repository boundary would let the two drift. Most files in `contracts/`
(validation corpus, stored-document grammar, port-type assignability, refusal codes and others) are read by both
test suites, so the editor's rules and the engine's rules are measured against the same vectors. The `$note` at the
top of a file names the tests that read it.

## The life of a workflow

**Edit.** The editor store holds one definition per editor instance and validates it with a mirror of the engine's
rules, so it never decides a rule of its own. The host saves the definition through the
Editor package, directly or through the typed client.

**Store.** The Editor writes the workflow and its versions (draft and current) as stored documents. One reader, in
.NET and in TypeScript, checks every stored document and returns the first rule it breaks. It never throws and it
never rewrites stored data; a field spelling the grammar does not allow is refused with a message naming what
to write.

**Compile.** One compiler answers at three points. A save compiles the version it wrote and lets the write
through with its diagnostics. A publish or rollback makes a version current only if it compiles. A run's start
compiles the version and every version its pin manifest names, and is refused if one does not compile. The verdict
(valid or not, with diagnostics) is stored. The compiled program is not stored; it is rebuilt when needed. The
compiler never refuses a definition the run would execute as drawn, and it refuses what a run would silently
ignore, such as a wire to a missing node.

**Admit.** Admission checks the workflow's concurrency mode, the run budget, de-duplication and the host's
admission policy, and writes the run's row. The four modes are:

| Mode | Behaviour |
|---|---|
| `Single` | One run at a time. A second start is skipped. |
| `Restart` | One run at a time. A new start supersedes the running one. |
| `Parallel` | Up to `MaxConcurrentRuns` runs at once. |
| `Queued` | Up to `MaxConcurrentRuns` runs, then a queue of up to `MaxQueuedRuns`. |

On one process the budget is exact with no broker or lock service. A trigger that finds `Parallel` or `Queued` full is
handled as the workflow's `OverflowBehavior` says. The `Queued` queue lives in memory on each replica; see
[operations/clustering.md](../operations/clustering.md#the-queued-queue-is-per-replica).

**Walk.** The runner executes the compiled program, never the stored definition, canvas by canvas in topological
order. A canvas is the root graph or a sub-graph held by a container block such as a loop or Try/Catch. The runner
writes checkpoints as it goes, so a run that loses its process can continue from the last one.

**Recover.** With recovery enabled (it is off by default), a recovery sweep on another replica takes over the runs of
a replica that died. Runs carry on in the background, and a sleep that was in progress ends when the dead replica said
it would, not after a fresh duration.

**Sleep and wait.** A run that waits on a trigger, a template, a predicate or a long sleep can park: it is written
as a suspension row and releases its process. The waker, a background service on every replica, resumes it when
the wait is answered or the time arrives, compiling the pinned version again first. A durable sleep parks only
when the host sets a threshold, and only in `Single` and `Restart` workflows. Turning parking on and off is
described in [operations/parking.md](../operations/parking.md).

**Read.** The HTTP surface, the history package and the debug package read what the run wrote: the run row, its
steps, its outputs and its findings.

## More than one replica

On one replica nothing beyond the database is needed. With several, the library needs two things the host
chooses: a place to count run slots and de-duplicate fires and signals (`ReeveWorkflow.Redis`), and a lock service
for the `Single` and `Restart` modes and for electing the replica that runs singleton jobs
(`ReeveWorkflow.Npgsql`, on PostgreSQL advisory locks). A host that declares peers without these seams is refused at
startup rather than falling back to single-process behaviour. Setup, failure behaviour and the example host that
runs as several replicas are in [operations/clustering.md](../operations/clustering.md).

## HTTP and the editor write side

The executor and the editor are separate products over the same tables. A workflow written into the database by
something else is legitimate input to the executor.

| Mapping call | Package | What it serves |
|---|---|---|
| `MapWorkflowApi` | `ReeveWorkflow.AspNetCore` | List and read workflows, run them, executions and their replay, versions and diffs, import validation. |
| `MapWorkflowBlockApi` | `ReeveWorkflow.AspNetCore` | The block catalogue. |
| `MapWorkflowExpressionApi` | `ReeveWorkflow.AspNetCore` | Expression preview. |
| `MapWorkflowEditorApi` | `ReeveWorkflow.Editor` | Create, update, delete, publish, roll back, compilation reads, port types, draft analysis. |

The library does not decide who may call these. Each `Map*Api` call refuses to build its endpoints unless the host
has stated who may reach them (`RequireAuthorization` or `AllowAnonymous`), so a missing decision fails the deploy
rather than the first request. Workflows are private to their creator unless the host's policy says otherwise.
The published surfaces are `workflow-api-surface.json` and `workflow-editor-api-surface.json` beside each package's
sources, and the typed client is generated from them.

The editor's write side saves a draft, publishes it as the current version, and rolls back to an earlier version.
Publish and rollback only succeed for a version that compiles. Every successful write returns the verdict, so an
editor can show findings as soon as a save completes.

## What a host supplies

To the engine, through the `Seams` slots of the options:

- blocks (`Seams.Blocks`, optionally `AddStandardWorkflowBlocks` for the standard set);
- port types (`Seams.PortTypes`), expression namespaces and helpers;
- policies: authorisation, audit, block sandbox, time zone, an admission policy;
- stores, when the EF stores do not fit; and
- for more than one replica, the cluster seams.

`AddWorkflows` answers every seam the library can answer itself: stores, read side, metrics, audit and the graph
host. The cluster seams and the policy seams have no default on purpose, and an empty policy slot admits or finds
nothing instead of allowing everything. Options and defaults are in
[operations/configuration.md](../operations/configuration.md).

To the editor: a port-type registry, a block catalogue, persistence of definitions (or `adapters` over the client),
optional per-node validation rules, port and configuration customisation, a workflow reference resolver, and
domain pickers for typed values. The port-type vocabulary is open: no domain type is built in, and unknown types
pass through the editor so a graph written by a newer host round-trips through an older editor. See
[reference/port-types.md](../reference/port-types.md).

The library has its own `DbContext`, never an extension of the host's. No foreign key leaves its schema, and author
and owner ids are uninterpreted values. No transaction spans the library's tables and the host's, so a host may point
the library at a separate database.

## Compatibility

Every exported symbol is public API, because hosts pin exact versions. Renaming or removing an export, changing a
signature or narrowing an accepted type needs a major version and a note in `CHANGELOG.md`. Port-type names are
stored inside saved graphs, so renaming a canonical name would break saved graphs. Refusal messages name what was
found, never the value, because messages reach logs and run failure text.

## Where to read next

- [operations/](../operations/README.md): [configuration](../operations/configuration.md),
  [clustering](../operations/clustering.md), [parking](../operations/parking.md) and
  [upgrading](../operations/upgrading.md).
- [reference/port-types.md](../reference/port-types.md) and
  [reference/validation-findings.md](../reference/validation-findings.md), including the stored-document rules.
- [faq.md](../faq.md) for known problems and fixes.
- [examples/dispatch-host](../../examples/dispatch-host/README.md): a complete .NET host, run as several replicas.
- [CONTRIBUTING.md](../../CONTRIBUTING.md): building, testing and proposing a change.
