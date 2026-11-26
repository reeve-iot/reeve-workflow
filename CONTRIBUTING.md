# Contributing

Thanks for looking. This is a small library with a narrow purpose, so the fastest way to get a change merged is to
know what belongs here.

## Scope

In scope: the graph (nodes, edges, ports, mappings, sub-canvases), validation, editor state, the Material UI
components that render them, and the engine that compiles and runs a graph.

Out of scope: anything that assumes a particular product. Devices, rooms, scenes, users, permissions and tenants
belong to the host application, which registers whatever it needs through the port-type registry and the host seams.
A pull request that adds a domain concept to `packages/core` will be declined. The rules a change must keep are in
[docs/architecture/README.md](docs/architecture/README.md).

## Prerequisites

- Node.js as pinned in `.nvmrc`, with npm.
- The .NET 11 SDK.
- Docker, for the example host's PostgreSQL and multi-replica tests and the editor end-to-end suite (both use
  Testcontainers).
- Python 3, only if you change the port-type name corpus (`python3 tools/grammar/port-type-names.py --check` checks
  it; `--write` regenerates the rows below its marker line).

## Layout

| Path | Contains |
|---|---|
| `packages/*` | The four npm packages. Tests sit beside the code as `*.test.ts(x)`. |
| `dotnet/src`, `dotnet/tests` | The twelve NuGet packages and their test projects. |
| `dotnet/benchmarks` | The executor benchmarks. |
| `examples/*` | Private example projects that use the packages from outside: `adopter`, `dispatch`, `dispatch-host`, `playground`. |
| `contracts/` | Data files the C# and TypeScript tests read, so the two halves agree on validation, port types and expressions. |
| `tools/` | The stored-document grammar checks, the release scripts and an ESLint rule for the end-to-end suite. |

## Building

```bash
npm install
npm run build                       # tsc -b, all four packages
dotnet build dotnet/ReeveWorkflow.slnx
```

Every .NET project targets `net11.0`, set once in `dotnet/Directory.Build.props`.

## Testing

```bash
npm test                            # vitest: packages, examples and tools
npx tsc -p tsconfig.tests.json      # type-check the test sources
dotnet test dotnet/ReeveWorkflow.slnx --filter "Category!=Docker" -m:1
```

`-m:1` runs the .NET test projects one at a time. At default parallelism a loaded machine can kill the run with an
internal CLR error, no test output and exit code 0, so read the pass counts and not only the exit code.

The filter leaves out the tests that need a Docker daemon: the example host's tests against PostgreSQL and Redis from
Testcontainers, several of which start the host as two or three real processes. Run them with:

```bash
dotnet test examples/dispatch-host/Dispatch.Host.Tests/Dispatch.Host.Tests.csproj -c Release --filter "Category=Docker"
```

React is a peer dependency spanning 18 and 19, so CI runs the vitest suite a second time with React 18 installed
over the same tree. To do the same locally (this changes `node_modules`; `npm ci` restores it):

```bash
npm install --no-save react@18.3.1 react-dom@18.3.1
npx vitest run
```

Tests assert what the code decides, not that a constant equals itself. A test for a refusal pins the specific
failure (a refusal code, `toThrow(/Duplicate/)`), not just that something threw.

### Contract files

`contracts/` holds data files that pin the rules the two halves share: the validation corpus, the stored-document
grammar, the port-type name corpus, the expression typing rules, the message catalogue and others. Most are read by
both the C# and the TypeScript tests, so changing a rule in one half without the other fails a test. The `$note` at
the top of a file names the tests that read it.

The stored-document grammar has one row per path, field and rule of a stored workflow, and each row says how it is
covered. A release is refused while a row is still marked `gap`.

### The client's generated types

`packages/client/surface.json` and `surface.editor.json` are copies of the routing-table descriptions the server
packages publish (`dotnet/src/ReeveWorkflow.AspNetCore/workflow-api-surface.json` and
`dotnet/src/ReeveWorkflow.Editor/workflow-editor-api-surface.json`), and `src/generated/types.ts` and `editor.ts` are
generated from them and committed. A test regenerates the files and compares, so a stale one fails the suite. When
an endpoint changes, copy the new description over the surface file, run the tests once, move the `*.actual` file the
failure names over the generated module, and fix whatever fails to compile.

### Editor end-to-end tests

The Playwright suite in `examples/playground/e2e` runs the editor in a browser against the example host, with
PostgreSQL and Redis from Testcontainers. To run it locally:

```bash
dotnet publish examples/dispatch-host/Dispatch.Host/Dispatch.Host.csproj \
  -c Release -r linux-x64 --self-contained true -o artifacts/dispatch-host
npm ci
(cd examples/playground && npx playwright install --with-deps chromium)   # first time only
npm run build --workspace @reeve-iot/workflow-playground
npm run e2e --workspace @reeve-iot/workflow-playground
```

Set `E2E_HOST_ENTRYPOINT` to the path of the published host executable if it is not
`artifacts/dispatch-host/Dispatch.Host`.

Send every request the editor makes through the helpers in `examples/playground/e2e/support/canvas.ts` (opening the
page, creating, saving, publishing, running, rolling back, opening a file) instead of clicking the control yourself.
A helper returns once the host has answered, so an assertion after it only waits for rendering. Do not raise an
assertion's timeout to cover a slow host. `npm run lint` rejects a spec that makes a request any other way.

## Lint

```bash
npm run lint                        # eslint, zero warnings allowed
```

Lint is not a CI job, so run it before you open a pull request.

## Code style

Source code carries no comments. Say it with names, types and tests. The exception is documentation on public API:
XML doc comments in C# and TSDoc in TypeScript. A reason, rule or design decision belongs in `docs/`.

## Benchmarks

`dotnet/benchmarks/ReeveWorkflow.Benchmarks` measures the executor's own overhead (the graph walk, node input
binding, expressions and the waker). CI builds it but never runs it. From that directory:

```bash
dotnet run -c Release -- --filter '*'
```

Compare rows from one run only: results on a loaded machine move by tens of percent.

## Pull requests

Open pull requests against `main`. The CI workflow (`.github/workflows/ci.yml`) runs on every push to `main` and
every pull request:

- `node`: `npm ci`, `tsc -b`, the test type-check, vitest, `npm pack --dry-run` for the four packages, and vitest
  again on React 18.
- `dotnet`: build, pack and the tests that need no Docker.
- `dotnet-docker`: the example host's Docker tests.
- `e2e-editor`: the editor end-to-end suite.

Keep a pull request to one concern: a refactor and a behaviour change in the same diff are two reviews. A breaking
change (renaming an export, changing a signature, renaming a canonical port type) needs a major version and a
`CHANGELOG.md` entry with the steps an adopter must take. Add a `CHANGELOG.md` entry for any user-visible change.

## Dependency updates

[Renovate](https://github.com/apps/renovate) reads `renovate.json` and opens grouped pull requests for npm, NuGet and
GitHub Actions dependencies. This repository's own packages and the `Reeve.*` libraries are excluded. Major versions
of the .NET release train (`Microsoft.Extensions.*`, `Microsoft.AspNetCore.*`, EF Core, Npgsql, `System.*`), of the
TimescaleDB EF provider, and of the `dotnet-version` in the workflows are bumped by hand together with the target
framework.

## Releasing

All npm and NuGet packages carry one version number.

1. Move the changes under a new version heading in `CHANGELOG.md`, with the steps an adopter must take under
   `### Upgrading`.
2. Set `<Version>` in `dotnet/Directory.Build.props`, the `version` of each `packages/*/package.json` and
   `examples/*/package.json`, and every exact `@reeve-iot/workflow-*` pin between them to the same version, then run
   `npm install` to refresh `package-lock.json`.
3. Merge to `main`, then push the tag `vX.Y.Z`.

The release workflow (`.github/workflows/release.yml`) takes the version from the tag. The `npm` job sets the package
versions, builds, runs the tests (a release is refused while the stored-document grammar has a `gap` row) and
publishes `core`, `mui`, `client` and `adapters` with npm trusted publishing and provenance, skipping a package
already published at that version. A prerelease tag such as `v1.1.0-rc.1` publishes under the npm dist-tag `next`,
any other tag under `latest`. The `nuget` job then builds, tests and packs with the tag's version and pushes to
nuget.org with trusted publishing, skipping packages already there. The workflow also creates the GitHub Release for
the tag, with the matching section of `CHANGELOG.md` as its notes.

## Reporting a bug

Include the graph that reproduces it. A `WorkflowDefinition` in JSON plus the registry you built is almost always
enough. Security issues go through [SECURITY.md](SECURITY.md), not the public tracker.
