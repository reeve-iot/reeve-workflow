# @reeve-iot/workflow-client

A typed HTTP client for the workflow endpoints that `ReeveWorkflow.AspNetCore` and `ReeveWorkflow.Editor` mount. The
types are not written by hand: the server publishes a description of its routing table and this package generates its
operations from it, so an endpoint that changes shape is a compile error here, not a surprise at run time. Part of
[Reeve Workflow](https://github.com/reeve-iot/reeve-workflow).

## Installation

```bash
npm install @reeve-iot/workflow-client
```

No peer dependencies. The package holds no graph state, no editor and no design system, and
[`@reeve-iot/workflow-core`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/core) does not depend on it.

## Usage

```ts
import { createWorkflowClient, WorkflowApiError } from '@reeve-iot/workflow-client'

const client = createWorkflowClient({
  baseUrl: 'https://workflows.example.com',
  headers: () => ({ Authorization: `Bearer ${token()}` }),
})

const workflows = await client.getWorkflows()
const detail = await client.getWorkflow({ id: workflows[0]!.id })
const page = await client.getExecutions({ id: detail.id, take: 25, hasErrors: true })
```

The typed paths assume the server mounts `api/workflows`, `api/workflow-blocks` and `api/expressions`, as the
`Map*Api` calls in the [main README](https://github.com/reeve-iot/reeve-workflow#engine) do. `baseUrl` only supplies
the origin (and any path) in front of them.

The client has no auth, caching or retry policy of its own. `headers` is where a host puts its token (as a function if
the token expires), and `fetch` is where it wraps anything else.

`createWorkflowEditorClient` is the same for the write side: create, update, delete, publish, roll back, draft
analysis, a version's compilation, the compilation sweep and the port-type vocabulary. Reading versions and their
diffs is on the executor client. `getPortTypes()` on the editor client answers the port-type vocabulary the server's
compiler enforces; `readPortTypeVocabulary` in
[`@reeve-iot/workflow-adapters`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/adapters) seeds an
editor registry from it.

## Errors

Every non-2xx answer throws a `WorkflowApiError` carrying the status, the parsed body and the response headers: the
`ProblemDetails` the server sent, or the conflict shape for a 409. A body that is not JSON, which is what a gateway
tends to answer with, comes through as the raw text with its status intact.

- A 400 from publish, rollback or execute is a `CompilationProblemDetails` whose `diagnostics` are the compiler's; it
  is empty when something else refused.
- A 503 carries `Retry-After` (`error.headers.get('Retry-After')`). From publish, rollback or `getVersionCompilation` it
  means the server could not tell whether a version compiles: nothing was done and the same request may succeed
  later. From execute it means the same and nothing ran; the refused start is kept as one failed run whose
  `failureReason` is `compilation-unanswered`, which `hasErrors=true` lists.
- A 503 from a create, or from an update that asked to publish, means the save was stored but publishing could not be
  finished. Its body (`WorkflowSaveUnpublishedProblemDetails`) carries the workflow and the draft the save left, so
  publish that draft rather than send the create again.

A create or update that does not compile answers with the workflow and its `draftVersionId` set;
`getVersionCompilation({ versionId })` on the editor client gives that draft's diagnostics.

## Compilation sweep

`getInvalidCompilations({ skip, take })` checks a page of up to `take` (at most 50) of the caller's workflows that
have a current version; `invalid` is the subset that does not compile. Page by `totalCount` and never stop on a short
or empty `invalid`: the page compiles only when `failed` is empty too. It reads only the caller's own workflows; the
check over every workflow before a deploy is the server's `WorkflowCompilationSweepRunner.RunOnceAsync` (see
[docs/operations/upgrading.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/operations/upgrading.md)).

## Where the types come from

`surface.json` and `surface.editor.json` are copies of what the server packages publish, and the typed operations in
`src/generated` are generated from them and committed. A test fails when the generated files and the surfaces
disagree.

## Documentation

See the [main README](https://github.com/reeve-iot/reeve-workflow#readme) and [docs](https://github.com/reeve-iot/reeve-workflow/tree/main/docs).

## Licence

MIT.
