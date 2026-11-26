import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import { readStoredDefinitionText } from '@reeve-iot/workflow-core'
import type { StoredDefinitionRead, WorkflowEditorStore } from '@reeve-iot/workflow-core'
import { GRAMMAR_BLOCK_TYPES, generateDocuments } from '../../../core/src/editor/documentGrammar.testkit.js'
import { NodeConfigPanel } from '../config/NodeConfigPanel.js'
import { MappingDialog } from '../mapping/MappingDialog.js'
import { renderInEditor } from '../testUtils.js'
import { WorkflowCanvas } from './WorkflowCanvas.js'

const documents = generateDocuments(21_105, 120).map((d) => ({ ...d, read: readStoredDefinitionText(d.text) }))
const preload = (read: StoredDefinitionRead) => ({
  preload: (store: WorkflowEditorStore) => {
    store.getState().setBlockTypes(GRAMMAR_BLOCK_TYPES)
    store.getState().loadStoredWorkflow(read)
  },
})

describe('every generated document the store opens is drawn, and every one it refuses says so', () => {
  it('draws some and refuses some', () => {
    expect(documents.filter((d) => !d.read.ok).length).toBeGreaterThan(10)
    expect(documents.filter((d) => d.read.ok).length).toBeGreaterThan(60)
  })

  it.each(documents.map((d, i) => [i, d] as const))('document %i', (_i, d) => {
    const view = renderInEditor(<><WorkflowCanvas onCanvasClick={() => {}} /><NodeConfigPanel /></>, preload(d.read))
    const store = view.editor()
    if (!d.read.ok) {
      expect(screen.getByRole('alert').textContent).toContain('cannot be read, so it is shown empty')
      view.unmount()
      return
    }
    const visit = () => {
      for (const node of store.getState().nodes) view.drive((s) => { s.getState().setSelectedNode(node.id) })
    }
    visit()
    for (const node of store.getState().nodes.filter((n) => n.subCanvases?.['Body'])) {
      view.drive((s) => { s.getState().enterSubCanvas(node.id, 'Body') })
      visit()
      view.drive((s) => { s.getState().exitToLevel(0) })
    }
    const edgeIds = store.getState().edges.flatMap((e) => (typeof e.id === 'string' ? [e.id] : []))
    view.unmount()
    for (const edgeId of edgeIds) renderInEditor(<MappingDialog edgeId={edgeId} onClose={() => {}} />, preload(d.read)).unmount()
  })
})
