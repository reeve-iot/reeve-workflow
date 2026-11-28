# Changelog

All notable changes to Reeve Workflow are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Four npm packages and twelve NuGet packages carry one
version number. Removing or renaming a public member is a major change.

## [1.1.1] - 2026-11-28

### Fixed

- `BlockNode` no longer cuts a port label at the node edge. A label that does not fit its share of the row is drawn as
  the port name without its type, and a name that still does not fit ends in an ellipsis, followed by the required
  mark of a required input. The whole label, type included, stays in the label's hover title.

### Upgrading

No code changes. A test that read a port's type from the drawn label text reads it from the label's `<title>`
instead.

## [1.1.0] - 2026-11-26

### Breaking changes

This release removes what was left of the document forms and SDK shapes from before 1.0.0. Public members that a
1.0.0 host or plugin may use are removed. A host or plugin that calls a removed member, or implements one of the debug
interfaces below, must be rebuilt against this release.

- Stored documents: the reader reports a canvas `connections` key, a node `config` key and `sourceOutput` inside a
  mapping as `stored-key-unknown`; 1.0.0 reported them as `definition-unreadable`. Rule `L6` is gone from the reader
  and from `StoredDocumentRule`, in C# and TypeScript; `path-not-migrated` left `MESSAGE_ONLY_VALIDATION_CODES` and
  `LIBRARY_VALIDATION_CODES`. Array-form `mappings` are still refused under `L7`, but the detail changed from
  "array-form mappings must be migrated to the object form" to "an array where an object is required".
- Stored documents: `JsonSerializer` and `WorkflowDefinitionParse` no longer bind `connections` or `config` onto
  `WorkflowDefinition`; a document that uses them deserialises with no edges and no configuration.
  `WorkflowGraphHelpers.ReadMappings` reads `type`, `literalValue` and `sourceOutputName` by their exact spelling, reads
  no mappings from an array, and no longer falls back to `sourceOutput`. In `@reeve-iot/workflow-core`,
  `VariableMapping` loses `sourceOutput`, `readMapping` reads `sourceOutputName` alone, and `updateEdgeMappings`
  refuses a `sourceOutput` key as unlisted and refuses an array.
- Compiler: rule `path-not-migrated` and the constant `CompilationRules.PathNotMigrated` are gone. A `Path`
  configuration key on a `ParseJson` node is no longer refused; a wire into `Path` is refused as `wire-port-unknown`.
- Port types: the hint that rewrote an old concatenated spelling (`CollectionFoo`) is gone. `CollectionFoo` and a
  bare `Collection` both hint `write Collection<T> with the type of its elements`. `Any` and `Float` are refused as
  reserved names, with "reserved" in place of "retired" in the message.
- `ReeveWorkflow.Abstractions`: `TriggerContext` loses `FireAsync(CancellationToken)`,
  `FireAsync(string?, CancellationToken)` and the two constructors that fed them; the payload constructor stays.
- `ReeveWorkflow.Core` and `ReeveWorkflow.Debug`: `IWorkflowDebugHook.KillToken`,
  `IWorkflowDebugSessions.ReleaseHookAsync`, `IWorkflowDebugNotifier.NotifyCompletedAsync` and
  `IWorkflowDebugSessionService.EndSessionForExecutionAsync` have no default body; an implementation must provide
  them. `IWorkflowDebugSessions.ReleaseHook` is removed; `ReleaseHookAsync` drops the hook as well.
- `ReeveWorkflow.Core`: `WorkflowDefinition` no longer implements `IJsonOnDeserialized`.

### Upgrading

- A store that holds documents in the forms 1.0.0 already refused needs them rewritten. In
  `automation.workflow_versions` (`definition_json`, including every `subCanvases` canvas, and both parameter
  lists), apply these rewrites:
  - `connections` becomes `edges`, and `config` becomes `configuration`.
  - Each array entry `{"sourceOutput": s, "targetInput": t}` of `mappings` becomes the member
    `"t": {"type": "direct", "sourceOutputName": s}` of an object; entries with a blank source or target are dropped.
  - `CollectionFoo` becomes `Collection<Foo>`, and `CollectionCollectionFoo` becomes `Collection<Collection<Foo>>`.
  - On a `ParseJson` node, drop `Path` and read `Value` through a projection.
- Rebuild hosts and plugins that call the removed `TriggerContext` or `IWorkflowDebugSessions` members, and give the
  four debug members above their own bodies.

### Fixed

- `ReeveWorkflow.Debug`: `OrphanedSessionTimeoutSeconds` is held to 30 to 3 600 seconds, the range its validation
  attribute states, and the orphan timeout runs on the configured `TimeProvider`.
- `ReeveWorkflow.Editor`: a workflow's `Mode` and `OverflowBehavior` are read by name only, in any case; a number
  such as `"7"` is refused like any other unknown value.
- `ReeveWorkflow.EntityFrameworkCore.PostgreSql`: the package ships only the library, without a `runtimeconfig.json`.
- npm packages ship without source maps, which pointed at sources the packages do not contain.

### Changed

- NuGet packages carry the README, package tags and symbol packages (`.snupkg`) with Source Link.
- `ReeveWorkflow.Npgsql` depends on `Reeve.EntityFrameworkCore.AdvisoryLocks` 1.0.1.
- Each npm package links to its own README as its homepage.
- `examples/dispatch-host` mounts the expression API at `api/expressions`, the path `@reeve-iot/workflow-client`
  calls.
- Documentation: corrected the READMEs, the operations guides and the validation reference against the code, added
  the per-workflow concurrency settings, the HTTP status codes and the recovery audit events, and rewrote the
  port-type reference.

## 1.0.0 - 2026-11-25

First release. The NuGet packages target `net11.0`. React 18 or 19 is a peer dependency of `core`, `mui` and
`adapters`; `client` has no peer dependencies.

### Added

npm packages:

- `@reeve-iot/workflow-core`: the headless graph model (nodes, edges, ports, mappings, sub-canvases), the open port-type
  registry (`createPortTypeRegistry`, aliases, widening, `Collection<T>`, `Map<String,V>`, nullable types), the
  editor store, validation with host-supplied rules, and the stored-document reader. No design system and no fetching.
- `@reeve-iot/workflow-mui`: Material UI canvas, block nodes, palette, port tables, mapping and parameter dialogs and
  configuration panel over `core`; re-exports `core`.
- `@reeve-iot/workflow-client`: typed calls to a `ReeveWorkflow.AspNetCore` and `ReeveWorkflow.Editor` server, generated
  from the surface the server publishes.
- `@reeve-iot/workflow-adapters`: the seams `core` declares (port-type vocabulary, block catalogue, workflow
  documents and picker options, workflow references, draft analysis), implemented over `client`. They read from the
  server; nothing here saves.

NuGet packages:

- `ReeveWorkflow.Abstractions`: the block SDK, the whole dependency closure for a block author.
- `ReeveWorkflow.Core`: the compiler and its persisted verdicts, the expression language, the graph walk, admission
  with per-workflow concurrency modes (`Single`, `Parallel(n)`, `Queued`, `Restart`), checkpoints and crash recovery,
  durable sleeps and parked waits with a waker, and the resilience policies (timeout, retry).
- `ReeveWorkflow.DependencyInjection`: the builder an add-on hangs its own `AddX()` on.
- `ReeveWorkflow.EntityFrameworkCore` and `ReeveWorkflow.EntityFrameworkCore.PostgreSql`: the `DbContext`, the stores,
  `AddWorkflows` and `AddWorkflowEngine`, and the PostgreSQL schema (migrations, history table, TimescaleDB
  hypertable for execution steps) with `AddPostgreSqlSchema()` and `IWorkflowSchemaMigrator`.
- `ReeveWorkflow.AspNetCore` and `ReeveWorkflow.Editor`: the executor's HTTP surface (read, run, blocks, expressions)
  and the write side (create, update, delete, version, draft, publish, roll back, port-type vocabulary, draft
  analysis). Every mapped group states its authorization, and workflows are private to their owner.
- `ReeveWorkflow.History` and `ReeveWorkflow.Debug`: scheduled sweeping of run history and superseded versions;
  breakpoints, stepping and state snapshots over a running graph.
- `ReeveWorkflow.Blocks`: twenty-three opt-in standard blocks (branches, loops, Try/Catch, conversions, arithmetic,
  JSON, HTTP request, sub-workflow, waits), each replaceable by id.
- `ReeveWorkflow.Redis` and `ReeveWorkflow.Npgsql`: cluster seams over Redis (run-slot budget, fire de-duplication,
  signal transport) and over PostgreSQL advisory locks (run lock and leader gate). A host that declares peers must
  fill every cluster seam.

Across both halves:

- A compilation sweep lists every current workflow version that does not compile under the running catalogue.
- Contract files under `contracts/` pin the validation, port-type, expression and stored-document rules that the C#
  compiler and the TypeScript editor must agree on.
- `examples/dispatch-host` is a complete .NET host, tested as several replicas against real PostgreSQL and Redis.

[1.1.1]: https://github.com/reeve-iot/reeve-workflow/releases/tag/v1.1.1
[1.1.0]: https://github.com/reeve-iot/reeve-workflow/releases/tag/v1.1.0
