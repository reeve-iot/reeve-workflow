# @reeve-iot/workflow-core

The headless part of the Reeve Workflow editor: the workflow-graph model, editor state, port-type system and
validation. No design system, no network, no domain concepts: you bring the components and the data. Part of
[Reeve Workflow](https://github.com/reeve-iot/reeve-workflow).

## Installation

```bash
npm install @reeve-iot/workflow-core
```

Requires React 18 or 19. If you would rather not build the UI, install
[`@reeve-iot/workflow-mui`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/mui) instead; it depends on this package and re-exports it.

## Usage

### Port types

The set of port types is open. The package ships the types every host shares and nothing else; you register your own
at start-up.

```ts
import { BUILTIN_PORT_TYPES, createPortTypeRegistry } from '@reeve-iot/workflow-core'

const portTypes = createPortTypeRegistry([
  ...BUILTIN_PORT_TYPES,
  { name: 'Device', aliases: ['device'], displayName: 'Device' },
])

portTypes.normalize('Collection<Device>')   // 'Collection<Device>'
portTypes.has('Device')                     // true
```

For autocomplete on your own names, merge them into the vocabulary interface. This affects autocomplete only.

```ts
declare module '@reeve-iot/workflow-core' {
  interface PortTypeVocabulary { Device: true }
}
```

The full contract is in [docs/reference/port-types.md](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/reference/port-types.md).

### Editor store

The editor state (nodes, edges, canvas stack, selection, validation) lives in one store per editor.
`createWorkflowEditorStore(options)` makes one, outside React or inside it. `WorkflowEditorStoreProvider` creates a
store from the same `options` and shares it with its children; `useWorkflowEditorStore(selector)` reads it, and
throws outside a provider. There is no global store, so two editors on a page do not share a canvas.

```tsx
import {
  WorkflowEditorStoreProvider,
  readStoredDefinitionText,
  useWorkflowEditorStore,
} from '@reeve-iot/workflow-core'

function OpenButton({ definitionJson }: { definitionJson: string }) {
  const loadStoredWorkflow = useWorkflowEditorStore((s) => s.loadStoredWorkflow)
  return <button onClick={() => loadStoredWorkflow(readStoredDefinitionText(definitionJson))}>Open</button>
}

<WorkflowEditorStoreProvider options={{ portTypes }}>
  <OpenButton definitionJson={saved} />
</WorkflowEditorStoreProvider>
```

A stored workflow is read by the stored-document reader (`readStoredDefinitionText`, or `readStoredDefinition` for a
value you already hold), which never throws. If it refuses the document, `loadStoredWorkflow` shows an empty canvas
that cannot be saved and puts the reason on `loadedDocumentFault`, so a save never replaces the stored workflow with
an empty one. `loadWorkflow` takes a graph you built yourself.

### Validation findings

`validateStructure()`, `validateWorkflow()` and `validateWorkflowWarnings()` return `ValidationFinding[]`. `code` is
the stable rule identifier, `message` is its English text, and `params` carries the named values a translation needs.
Host rules return their own `code`, `params` and English `message`.

```ts
import { createWorkflowEditorStore, validationMessage } from '@reeve-iot/workflow-core'

const store = createWorkflowEditorStore({ portTypes })
const finding = store.getState().validateWorkflow()[0]
if (finding) console.log(validationMessage(finding))
```

Supply translations through the editor provider's `validationMessages` option, keyed by code. A translation receives
the finding's params and the finding. A library code is translated only for a finding that carries every param the
English template reads; any other finding shows its own `message`. A publish diagnostic carries no params, so it
shows the server's message unless its code's template reads none.

See the [validation findings guide](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/reference/validation-findings.md) for provider and UI examples.

## Documentation

See the [main README](https://github.com/reeve-iot/reeve-workflow#readme) and [docs](https://github.com/reeve-iot/reeve-workflow/tree/main/docs).

## Licence

MIT.
