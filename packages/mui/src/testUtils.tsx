import type { ReactElement, ReactNode } from 'react'
import { act, render } from '@testing-library/react'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import type {
  BlockTypeDescriptor,
  WorkflowDefinition,
  WorkflowEditorStore,
  WorkflowEditorStoreOptions,
} from '@reeve-iot/workflow-core'
import {
  BUILTIN_PORT_TYPES,
  WorkflowEditorStoreContext,
  WorkflowEditorStoreProvider,
  createPortTypeRegistry,
} from '@reeve-iot/workflow-core'
import { useContext, useState } from 'react'

export interface RenderInEditorOptions extends Partial<WorkflowEditorStoreOptions> {
  /** Runs against the store before `ui` mounts. A component that reads the store into `useState` only
   * ever sees what was there at mount, exactly as it would in a host that loads the workflow first. */
  preload?: (store: WorkflowEditorStore) => void
}

export const testPortTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Device', aliases: ['device'], literal: { gradedAs: 'String' } }])

const theme = createTheme()

/** The editor's providers alone, for a test that changes a mounted component's props: `renderInEditor`'s
 * rerender replaces the root element, so a rerender through it would drop the store. */
export function editorProviders(ui: ReactNode): ReactElement {
  return (
    <ThemeProvider theme={theme}>
      <WorkflowEditorStoreProvider options={{ portTypes: testPortTypes }}>{ui}</WorkflowEditorStoreProvider>
    </ThemeProvider>
  )
}

/** Renders a component inside a fresh editor store and a plain MUI theme, handing back the store so a
 * test can drive it the way the surrounding application would. */
export function renderInEditor(ui: ReactElement, options: RenderInEditorOptions = {}) {
  const { preload, ...storeOptions } = options
  let store: WorkflowEditorStore | null = null

  function Capture({ children }: { children: ReactNode }) {
    store = useContext(WorkflowEditorStoreContext)
    const captured = store
    useState(() => {
      if (captured) preload?.(captured)
      return true
    })
    return <>{children}</>
  }

  const result = render(
    <ThemeProvider theme={theme}>
      <WorkflowEditorStoreProvider options={{ portTypes: testPortTypes, ...storeOptions }}>
        <Capture>{ui}</Capture>
      </WorkflowEditorStoreProvider>
    </ThemeProvider>,
  )

  const editor = (): WorkflowEditorStore => {
    if (!store) throw new Error('The editor store was never mounted.')
    return store
  }

  return {
    ...result,
    editor,
    drive: (action: (store: WorkflowEditorStore) => void) => act(() => action(editor())),
    seed: (blockTypes: BlockTypeDescriptor[], definition: WorkflowDefinition) =>
      act(() => {
        editor().getState().setBlockTypes(blockTypes)
        editor().getState().loadWorkflow(definition)
      }),
  }
}
