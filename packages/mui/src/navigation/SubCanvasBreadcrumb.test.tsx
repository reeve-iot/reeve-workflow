import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor } from '@reeve-iot/workflow-core'
import { SubCanvasBreadcrumb } from './SubCanvasBreadcrumb.js'
import { renderInEditor } from '../testUtils.js'

const branching: BlockTypeDescriptor = {
  blockTypeId: 'IfElse',
  displayName: 'If / Else',
  inputs: [],
  outputs: [],
  subCanvasNames: ['Then', 'Else'],
}

function mount() {
  const view = renderInEditor(<SubCanvasBreadcrumb />)
  view.seed([branching], {
    nodes: [
      {
        id: 'if1',
        blockTypeId: 'IfElse',
        position: { x: 0, y: 0 },
        configuration: {},
        subCanvases: { Then: { nodes: [], edges: [] }, Else: { nodes: [], edges: [] } },
      },
    ],
    edges: [],
  })
  return view
}

describe('SubCanvasBreadcrumb', () => {
  it('shows only the root while the editor is at the top level', () => {
    mount()
    expect(screen.getByText('Main Workflow')).toBeInTheDocument()
    expect(screen.queryByText('(editing sub-canvas)')).not.toBeInTheDocument()
  })

  it('lets the host rename the root, since not every product edits a "workflow"', () => {
    renderInEditor(<SubCanvasBreadcrumb rootLabel="Recipe" />)
    expect(screen.getByText('Recipe')).toBeInTheDocument()
  })

  it('names the descended block by its display name, not by its id', () => {
    const view = mount()
    view.drive((store) => store.getState().enterSubCanvas('if1', 'Then'))
    expect(screen.getByText('If / Else')).toBeInTheDocument()
    expect(screen.getByText('(editing sub-canvas)')).toBeInTheDocument()
  })

  it('offers every sibling branch of the block it descended into', () => {
    const view = mount()
    view.drive((store) => store.getState().enterSubCanvas('if1', 'Then'))
    expect(screen.getByRole('tab', { name: 'Then' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Else' })).toBeInTheDocument()
  })

  it('switches branch without leaving the sub-canvas', async () => {
    const view = mount()
    view.drive((store) => store.getState().enterSubCanvas('if1', 'Then'))
    await userEvent.click(screen.getByRole('tab', { name: 'Else' }))
    expect(view.editor().getState().canvasStack).toHaveLength(1)
    expect(view.editor().getState().canvasStack[0]?.subCanvasName).toBe('Else')
  })

  it('returns to the root when the root chip is clicked', async () => {
    const view = mount()
    view.drive((store) => store.getState().enterSubCanvas('if1', 'Then'))
    await userEvent.click(screen.getByText('Main Workflow'))
    expect(view.editor().getState().canvasStack).toHaveLength(0)
  })
})
