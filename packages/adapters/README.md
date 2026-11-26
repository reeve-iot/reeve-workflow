# @reeve-iot/workflow-adapters

The data sources [`@reeve-iot/workflow-core`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/core) declares, implemented against
[`@reeve-iot/workflow-client`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/client). Wire these and the editor loads workflows, blocks and port types over HTTP, with
no fetch layer, cache keys or mapping from the server's shapes to the editor's. Part of [Reeve Workflow](https://github.com/reeve-iot/reeve-workflow).

## Installation

```bash
npm install @reeve-iot/workflow-adapters
```

Requires React 18 or 19. This is the only package that depends on both `core` and `client`, and it is optional: a host
with its own data layer (auth interceptors, React Query keys, an offline cache) implements the same seams itself.

## Usage

Build the sources once, outside render. Each holds a cache, and one rebuilt per render drops it every time.

```tsx
import {
  createBlockCatalogueSource,
  createWorkflowDocumentSource,
  createWorkflowOptionsSource,
  createWorkflowReferenceSource,
} from '@reeve-iot/workflow-adapters'
import { createWorkflowClient } from '@reeve-iot/workflow-client'

const client = createWorkflowClient({ baseUrl: 'https://workflows.example.com' })

const catalogue = createBlockCatalogueSource(client)
const document = createWorkflowDocumentSource(client)
const options = createWorkflowOptionsSource(client)
const reference = createWorkflowReferenceSource(client)
```

Then read them inside the editor:

```tsx
function EditorBody({ workflowId }: { workflowId: string | null }) {
  const blockTypes = catalogue.useBlockTypes()
  const workflow = document.useWorkflow(workflowId)

  const setBlockTypes = useWorkflowEditorStore((s) => s.setBlockTypes)
  const loadStoredWorkflow = useWorkflowEditorStore((s) => s.loadStoredWorkflow)
  const setWorkflowParams = useWorkflowEditorStore((s) => s.setWorkflowParams)
  const syncSpecialNodes = useWorkflowEditorStore((s) => s.syncSpecialNodes)

  useEffect(() => { setBlockTypes([...blockTypes]) }, [blockTypes, setBlockTypes])
  useEffect(() => {
    if (workflow === null) return
    setWorkflowParams(workflow.inputParams, workflow.outputParams)
    loadStoredWorkflow(workflow.definition)
    syncSpecialNodes()
  }, [workflow, loadStoredWorkflow, setWorkflowParams, syncSpecialNodes])

  return <WorkflowCanvas onCanvasClick={() => undefined} />
}
```

`workflow.definition` is the stored-document reader's answer (`StoredDefinitionRead`): `{ ok: true, definition, sites }`,
or `{ ok: false, fault }` for a definition or parameter list it refuses. Pass it to `loadStoredWorkflow`. A refused
answer loads an empty canvas that cannot be saved, with the fault on `loadedDocumentFault`, so a save never overwrites
the stored workflow with an empty canvas. Do not pass it to `loadWorkflow`, which takes a graph.

`reference.resolver` goes into `WorkflowReferenceResolverContext`. It is a hook, so its identity must stay stable for
the editor's lifetime.

[`examples/adopter`](https://github.com/reeve-iot/reeve-workflow/tree/main/examples/adopter) is a complete host (palette, canvas, configuration panel,
workflow picker); its test renders it against a fake server.

## What each source reads

| Source | Reads | Gives |
|---|---|---|
| `createBlockCatalogueSource` | `GET /api/workflow-blocks` | `BlockTypeDescriptor[]` for `setBlockTypes` |
| `createWorkflowDocumentSource` | `GET /api/workflows/{id}`, and `GET /api/workflows/{id}/versions/{n}` when the workflow has a draft whose documents the first answer does not already carry | the draft's graph where there is one, else the current one, as `StoredDefinitionRead`, and its declared parameters |
| `createWorkflowOptionsSource` | `GET /api/workflows` | `{ id, name }` for a picker |
| `createWorkflowReferenceSource` | `GET /api/workflows/{id}` | the ports a referenced child workflow declares |

Port types arrive as whatever string the server spelled them with, so each is normalised through the registry of the
surrounding `WorkflowEditorStoreProvider`; call these hooks inside the provider.

A block type the editor cannot read, such as one with a port that declares a `typeFrom` relation, is left out of the
palette and reported to `onError` once per load; the rest loads. `outputSatisfyingBranchSets` is not on the wire, so a
host that knows its own catalogue sets it through the store's `adaptBlockType`.

## The port-type vocabulary

The server's compiler checks every wire against one widening table: the built-ins plus whatever the host registered
through `Seams.PortTypes`. `getPortTypes()` on the editor client publishes it, and `readPortTypeVocabulary` turns it
into definitions for `createPortTypeRegistry`, so the editor answers as the compiler does:

```ts
import { readPortTypeConstructors, readPortTypeVocabulary } from '@reeve-iot/workflow-adapters'
import { createWorkflowEditorClient } from '@reeve-iot/workflow-client'
import { createPortTypeRegistry } from '@reeve-iot/workflow-core'

const editor = createWorkflowEditorClient({ baseUrl: 'https://workflows.example.com' })

async function loadPortTypes() {
  const vocabulary = await editor.getPortTypes()
  return createPortTypeRegistry(readPortTypeVocabulary(vocabulary), readPortTypeConstructors(vocabulary), {
    secretCodec: vocabulary.secretCodec,
  })
}
```

Load it before the editor mounts: a registry is immutable and the editor reads it once, so a type registered later is
offered after a page reload. `getPortTypes` throws `WorkflowApiError` for an error status (and what `fetch` throws
when there is no answer), and `readPortTypeVocabulary` throws, naming the type, for a name that does not parse, a
capability the editor does not know or a literal form it cannot grade. A `Collection<…>` row contributes only its
aliases: its `widensTo` is ignored. Until a load succeeds, show the editor as unavailable with a way to retry rather
than mounting it over `BUILTIN_PORT_TYPES`, which would pass wires the server refuses and refuse ones it passes.

The endpoint sits in the editor group, behind the policy the host states on `MapWorkflowEditorApi`'s group, so a caller
that policy refuses cannot seed from it. A host that shows such a caller the canvas serves
`PortTypeVocabularyResponse.From(options.Vocabulary, services)` from a route of its own. The vocabulary carries no
display names; map the definitions to add one. A type the editor offers must be registered on the server
(`Seams.PortTypes.Add(new WorkflowTypeRecord(…))`), not only in TypeScript. A record cannot express an alias onto a
`Collection<…>` name, so a server built on records publishes none.

## The server's types for a draft

The editor does not bind type parameters or type expressions itself: the compiler does. `POST {root}/analysis` answers
for a draft nobody saved, with the declared and effective term of every port, and `createDraftAnalysisSource` wires it
into the store:

```ts
import { createDraftAnalysisSource } from '@reeve-iot/workflow-adapters'
import { createWorkflowEditorClient } from '@reeve-iot/workflow-client'
import { createWorkflowEditorStore } from '@reeve-iot/workflow-core'

const editor = createWorkflowEditorClient({ baseUrl: 'https://workflows.example.com' })
const store = createWorkflowEditorStore({
  portTypes: await loadPortTypes(),
  draftAnalysis: createDraftAnalysisSource(editor, { workflowId: () => openWorkflowId }),
})
```

The store asks for an analysis after each edit, once it has been quiet for `draftAnalysisDelayMs`, and aborts a request
an edit overtakes. An answer is shown as current only for the revision it was asked at. A failed request leaves the
last answer in place and reaches `onDraftAnalysisError` and the store's `analysisError`. `readDraftAnalysis` refuses an
answer it cannot hold (an unknown port state, or a bound port with no effective term). Without the analysis seam a
port shows its declared term and the editor grades only a literal against a closed one.

## Loading, failure and refresh

The block catalogue, workflow document, workflow options and workflow reference sources are read-through and safe to
call during render: an unknown key returns the empty value and starts one load, and the same key asked again while
that load is in flight starts nothing. The first render is therefore empty; there is no suspense boundary and no
thrown promise.

A failed load is remembered as empty rather than retried, so one unreachable workflow cannot turn into an unbounded
stream of requests. Pass `onError` to see it; without one it is silent, which matches the editor's contract that an
unresolvable reference draws no ports rather than breaking the canvas.

`refresh()` drops everything one of these sources holds, so the next render loads again. The reference resolver has
one too: a child workflow that has just gained a parameter is stale on its parent until the resolver is refreshed, and
a child that failed once is otherwise never asked for again. A load in flight when `refresh()` is called is discarded
rather than written.

## Documentation

See the [main README](https://github.com/reeve-iot/reeve-workflow#readme) and [docs](https://github.com/reeve-iot/reeve-workflow/tree/main/docs).

## Licence

MIT.
