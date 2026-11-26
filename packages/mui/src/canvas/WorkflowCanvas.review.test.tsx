import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, fireEvent, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor, WorkflowDefinition, WorkflowEditorState } from '@reeve-iot/workflow-core'
import { WorkflowCanvas } from './WorkflowCanvas.js'
import { renderInEditor } from '../testUtils.js'

const widget: BlockTypeDescriptor = {
  blockTypeId: 'Widget', displayName: 'Widget', category: 'Test',
  inputs: [{ name: 'In', displayName: 'In', isRequired: false, type: 'String' }],
  outputs: [{ name: 'Out', displayName: 'Out', isRequired: false, type: 'String' }],
}

const twoNodes: WorkflowDefinition = {
  nodes: [
    { id: 'a', blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {} },
    { id: 'b', blockTypeId: 'Widget', position: { x: 400, y: 0 }, configuration: {} },
  ],
  edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }],
}

function mount(definition: WorkflowDefinition = twoNodes) {
  const view = renderInEditor(<WorkflowCanvas onCanvasClick={() => {}} />)
  view.seed([widget], definition)
  return view
}

const rect = (width: number, height: number): DOMRect =>
  ({ left: 0, top: 0, right: width, bottom: height, width, height, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('WorkflowCanvas — two editors on one page (review claims 1 and 2)', () => {
  it('Delete removes the selection of the editor the user is working in, and no other', async () => {
    const first = mount()
    const second = mount()
    const [firstCanvas] = screen.getAllByTestId('workflow-canvas')
    second.drive((s) => s.getState().setSelectedNode('b'))

    fireEvent.mouseDown(within(firstCanvas!).getByTestId('workflow-node-body-a'), { button: 0 })
    fireEvent.mouseUp(window)
    expect(first.editor().getState().selectedNodeId).toBe('a')
    await userEvent.keyboard('{Delete}')

    expect(first.editor().getState().nodes.find((n) => n.id === 'a')).toBeUndefined()
    expect(second.editor().getState().nodes.find((n) => n.id === 'b')).toBeDefined()
  })

  it('clicking inside a canvas gives it keyboard focus, so the shortcut has an owner', () => {
    mount()
    const canvas = screen.getByTestId('workflow-canvas')
    fireEvent.mouseDown(screen.getByTestId('workflow-node-body-a'), { button: 0 })
    fireEvent.mouseUp(window)
    expect(document.activeElement).toBe(canvas)
  })

  it('paints no two SVG elements with the same id', () => {
    mount()
    mount()
    const ids = [...document.querySelectorAll('svg [id]')].map((el) => el.id)
    const duplicated = [...new Set(ids.filter((id, index) => ids.indexOf(id) !== index))]
    expect(duplicated).toEqual([])
  })

  it('has every grid and clip reference resolve inside its own canvas', () => {
    mount()
    mount()
    for (const svg of document.querySelectorAll('svg')) {
      const own = new Set([...svg.querySelectorAll('[id]')].map((el) => el.id))
      const references = [...svg.querySelectorAll('*')].flatMap((el) =>
        [el.getAttribute('fill'), el.getAttribute('clip-path')]
          .filter((value): value is string => value !== null && value.startsWith('url(#'))
          .map((value) => value.slice(5, -1)),
      )
      expect(references.length).toBeGreaterThan(0)
      for (const id of references) expect(own.has(id)).toBe(true)
    }
    const grids = [...document.querySelectorAll('pattern')].map((p) => p.id)
    expect(new Set(grids).size).toBe(grids.length)
  })
})

describe('WorkflowCanvas — the Delete shortcut has an owner (review claims 1 and 2)', () => {
  it('leaves the graph alone when Delete is pressed with the focus outside every canvas', async () => {
    const view = mount()
    const outside = document.createElement('button')
    document.body.appendChild(outside)
    view.drive((s) => s.getState().setSelectedNode('a'))
    outside.focus()

    await userEvent.keyboard('{Delete}')

    expect(view.editor().getState().nodes.find((n) => n.id === 'a')).toBeDefined()
    outside.remove()
  })
})

describe('WorkflowCanvas — panning (review claim 4)', () => {
  it('applies every mouse movement that arrives within one animation frame, not only the last', () => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] })
    const view = mount()
    const canvas = screen.getByTestId('workflow-canvas')
    fireEvent.mouseDown(canvas, { button: 1 })

    fireEvent.mouseMove(canvas, { movementX: 10, movementY: 4 })
    fireEvent.mouseMove(canvas, { movementX: 10, movementY: 4 })
    act(() => { vi.advanceTimersToNextFrame() })

    expect(view.editor().getState().pan).toEqual({ x: 20, y: 8 })
  })

  it('applies a pan gesture that ended before its frame ever arrived', () => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] })
    const view = mount()
    const canvas = screen.getByTestId('workflow-canvas')
    fireEvent.mouseDown(canvas, { button: 1 })
    fireEvent.mouseMove(canvas, { movementX: 15, movementY: 6 })

    fireEvent.mouseUp(canvas)
    act(() => { vi.advanceTimersToNextFrame() })

    expect(view.editor().getState().pan).toEqual({ x: 15, y: 6 })
  })
})

describe('WorkflowCanvas — wheel zoom (review claim 5)', () => {
  const canvasPointUnder = (s: WorkflowEditorState, clientX: number, clientY: number) => ({
    x: (clientX - s.pan.x) / s.zoom,
    y: (clientY - s.pan.y) / s.zoom,
  })

  it('keeps the canvas point under the cursor where it is while zooming', () => {
    const view = mount()
    const canvas = screen.getByTestId('workflow-canvas')
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue(rect(800, 600))
    view.drive((s) => { s.getState().setPan({ x: 50, y: 20 }) })
    const before = canvasPointUnder(view.editor().getState(), 400, 300)

    fireEvent.wheel(canvas, { deltaY: -120, clientX: 400, clientY: 300 })

    const state = view.editor().getState()
    expect(state.zoom).toBeGreaterThan(1)
    const after = canvasPointUnder(state, 400, 300)
    expect(after.x).toBeCloseTo(before.x, 6)
    expect(after.y).toBeCloseTo(before.y, 6)
  })

  it('registers its wheel listener as non-passive, so preventDefault can stop the page scrolling', () => {
    const spy = vi.spyOn(HTMLElement.prototype, 'addEventListener')
    mount()
    const nonPassiveWheel = spy.mock.calls.some(
      ([type, , options]) => type === 'wheel' && typeof options === 'object' && options !== null && options.passive === false,
    )
    expect(nonPassiveWheel).toBe(true)
  })
})
