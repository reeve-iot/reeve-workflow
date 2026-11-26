# @reeve-iot/workflow-mui

The Material UI editor for Reeve Workflow: canvas, block nodes, palette and configuration panels over
[`@reeve-iot/workflow-core`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/core). Part of [Reeve Workflow](https://github.com/reeve-iot/reeve-workflow).

## Installation

```bash
npm install @reeve-iot/workflow-mui
```

Peer dependencies: `react` and `react-dom` (18 or 19), `@mui/material` and `@mui/icons-material` 9, `@emotion/react`
and `@emotion/styled` 11.

This package depends on `core` and re-exports it, so one dependency is enough. To style the editor yourself, depend
on `@reeve-iot/workflow-core` directly and MUI never enters your bundle.

## What it contains

The workflow canvas, block nodes, the block palette, the node configuration panel (with Resiliency, Error Handling,
Passthrough and Type Arguments sections), `WorkflowExecutionSection` for a workflow's deadline, retry and checkpoint
policy, `ConcurrencySection` for its concurrency mode and queue, the mapping dialog, port value editors, navigation
and validation display. Every component reads and writes through the editor store of `core`; none fetches. To load and
save over HTTP, add
[`@reeve-iot/workflow-client`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/client) and
[`@reeve-iot/workflow-adapters`](https://github.com/reeve-iot/reeve-workflow/tree/main/packages/adapters).

## Usage

Components render inside `WorkflowEditorStoreProvider`:

```tsx
import { WorkflowCanvas, WorkflowEditorStoreProvider, type PortTypeRegistry } from '@reeve-iot/workflow-mui'

export function Editor({ portTypes }: { portTypes: PortTypeRegistry }) {
  return (
    <WorkflowEditorStoreProvider options={{ portTypes }}>
      <WorkflowCanvas onCanvasClick={() => undefined} />
    </WorkflowEditorStoreProvider>
  )
}
```

### Customising

A host reaches into the editor through React contexts. Without a provider each one is empty and the editor draws only
what the block catalogue declares.

| Context | What the host supplies |
|---|---|
| `NodeConfigCustomizationContext` | Renderers for configuration fields, extra sections in the configuration panel, a type-argument picker and a control for `Json` expressions. |
| `NodePortCustomizationContext` | Ports to hide, input types that depend on the node's configuration, and ports discovered at runtime. |
| `MappingCustomizationContext` | The projection control in the mapping dialog. |
| `EditorColorsContext` | Literal colours for the canvas. By default `deriveEditorColors(theme)` builds them from the active MUI theme; a theme in MUI's CSS-variables mode needs literal colours here, because the canvas paints with SVG attributes that do not resolve `var(--…)`. |

### Validation findings

The `ValidationFindings` component displays editor findings and warnings, or the diagnostics a publish attempt
returned. It must be rendered inside `WorkflowEditorStoreProvider`. It renders through `validationMessage` with the
`validationMessages` catalogue given to the provider; its `catalogue` prop overrides that for one list. A code the
library has no template for, a host rule's code without a translation, and a publish diagnostic whose code's template
reads params show the finding's own `message`. Every row carries `data-code`; a row that names a node opens that
node's canvas and selects it, then calls `onSelectNode`.

See the [validation findings guide](https://github.com/reeve-iot/reeve-workflow/blob/main/docs/reference/validation-findings.md) for an end-to-end example.

## Licence

MIT.
