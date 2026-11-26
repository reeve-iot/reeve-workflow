import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor, WorkflowDefinition } from '@reeve-iot/workflow-core'
import { WorkflowCanvas } from './WorkflowCanvas.js'
import { NODE_WIDTH, NODE_CONFIG_PANEL_WIDTH } from './geometry.js'
import { NODE_RIGHT_OVERHANG } from './portGeometry.js'
import { BLOCK_DRAG_MIME } from '../palette/BlockPalette.js'
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
  edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: { In: { type: 'direct', sourceOutputName: 'Out' } } }],
}

function mount(props: Partial<Parameters<typeof WorkflowCanvas>[0]> = {}, definition: WorkflowDefinition = twoNodes) {
  const view = renderInEditor(<WorkflowCanvas onCanvasClick={props.onCanvasClick ?? (() => {})} {...props} />)
  view.seed([widget], definition)
  return view
}

const dataTransfer = (blockTypeId: string) => ({
  getData: (type: string) => (type === BLOCK_DRAG_MIME ? blockTypeId : ''),
  dropEffect: '',
  setData: () => {},
})

function dropAt(canvas: HTMLElement, blockTypeId: string, x: number, y: number) {
  const event = new Event('drop', { bubbles: true, cancelable: true })
  Object.defineProperties(event, {
    dataTransfer: { value: dataTransfer(blockTypeId) },
    clientX: { value: x },
    clientY: { value: y },
  })
  fireEvent(canvas, event)
}

describe('WorkflowCanvas', () => {
  beforeAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    proto.createSVGPoint = function createSVGPoint() {
      return { x: 0, y: 0, matrixTransform() { return { x: this.x, y: this.y } } }
    }
    proto.getScreenCTM = function getScreenCTM() { return { inverse: () => null } }
  })
  afterAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    delete proto.createSVGPoint
    delete proto.getScreenCTM
  })

  it('stops drawing a wire to nothing once the author removes it from the finding that names it', () => {
    const branch: BlockTypeDescriptor = { blockTypeId: 'Branch', displayName: 'Branch', inputs: [], outputs: [], subCanvasNames: ['Body'] }
    const view = mount({}, {
      nodes: [
        { id: 'a', blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {} },
        { id: 'loop', blockTypeId: 'Branch', position: { x: 0, y: 200 }, configuration: {}, subCanvases: { Body: {
          nodes: [{ id: 'c', blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {} }],
          edges: [{ id: 'inner', sourceNodeId: 'c', targetNodeId: 'gone', mappings: {} }],
        } } },
      ],
      edges: [{ id: 'w', sourceNodeId: 'ghost', targetNodeId: 'a', mappings: {} }],
    })
    view.drive((s) => { s.getState().setBlockTypes([widget, branch]) })
    view.drive((s) => { s.getState().enterSubCanvas('loop', 'Body') })
    expect(screen.getByTestId('workflow-edge-c-gone')).toBeInTheDocument()
    const wires = () => view.editor().getState().validateStructureFindings().filter((f) => f.edgeId !== undefined)
    const findings = wires()
    expect(findings.map((f) => f.edgeId)).toEqual(['w', 'inner'])
    view.drive((s) => { for (const f of findings) s.getState().removeEdge(f.edgeId ?? '', f.canvasPath) })
    expect(screen.queryByTestId('workflow-edge-c-gone')).not.toBeInTheDocument()
    view.drive((s) => { s.getState().exitToLevel(0) })
    expect(screen.queryByTestId('workflow-edge-ghost-a')).not.toBeInTheDocument()
    expect(wires()).toEqual([])
  })

  it('draws every node and wire the store holds', () => {
    mount()
    expect(screen.getAllByTestId('workflow-node-Widget')).toHaveLength(2)
    expect(screen.getByTestId('workflow-edge-a-b')).toBeInTheDocument()
  })

  it('shows a stored document it cannot read as an alert over an empty canvas, and draws no nodes', () => {
    const unlisted = { nodes: twoNodes.nodes, connections: twoNodes.edges } as unknown as WorkflowDefinition
    mount({}, unlisted)
    expect(screen.getByRole('alert')).toHaveTextContent(
      "The stored workflow cannot be read, so it is shown empty and cannot be saved: unlisted key 'connections' on canvas",
    )
    expect(screen.queryAllByTestId('workflow-node-Widget')).toHaveLength(0)
  })

  it('shows no alert for a document it read', () => {
    mount()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('reports a click that landed on empty canvas, not on a node', () => {
    const onCanvasClick = vi.fn()
    mount({ onCanvasClick })
    fireEvent.mouseDown(document.querySelector('svg')!, { button: 0 })
    expect(onCanvasClick).toHaveBeenCalledOnce()
  })

  it('adds the dropped block to the graph', () => {
    const view = mount()
    fireEvent.drop(screen.getByTestId('workflow-canvas'), { dataTransfer: dataTransfer('Widget') })
    expect(view.editor().getState().nodes).toHaveLength(4)
  })

  it('clamps a right-edge drop inside the panel-open canvas and centres a middle drop', () => {
    const view = mount({}, { nodes: [], edges: [] })
    const canvas = screen.getByTestId('workflow-canvas')
    const width = 760
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: width, bottom: 700, width, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    dropAt(canvas, 'Widget', 740, 400)
    const added = view.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(added.position).toEqual({ x: 233, y: 360 })
    expect(added.position.x + NODE_WIDTH + NODE_RIGHT_OVERHANG).toBeLessThanOrEqual(width - NODE_CONFIG_PANEL_WIDTH)
    const centeredView = mount({}, { nodes: [], edges: [] })
    const centeredCanvas = screen.getAllByTestId('workflow-canvas')[1]!
    vi.spyOn(centeredCanvas, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: width, bottom: 700, width, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    dropAt(centeredCanvas, 'Widget', 330, 400)
    const middle = centeredView.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(middle.position.x).toBe(330 - NODE_WIDTH / 2)
  })

  it('uses the current canvas width and converts drop placement through pan and zoom', () => {
    const view = mount({}, { nodes: [], edges: [] })
    const existingNodeId = view.editor().getState().nodes[0]!.id
    view.drive((store) => { store.getState().setSelectedNode(existingNodeId) })
    view.drive((store) => { store.setState({ zoom: 2, pan: { x: 20, y: 10 } }) })
    const canvas = screen.getByTestId('workflow-canvas')
    const width = 760
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: width, bottom: 700, width, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    dropAt(canvas, 'Widget', 600, 600)
    const added = view.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(added.position).toEqual({ x: 143, y: 255 })
    expect(added.position.x * 2 + 20 + (NODE_WIDTH + NODE_RIGHT_OVERHANG) * 2).toBeLessThanOrEqual(width)
  })

  it('keeps a right-edge drop within a narrow canvas without flooring its clamp width', () => {
    const view = mount({}, { nodes: [], edges: [] })
    const canvas = screen.getByTestId('workflow-canvas')
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 560, bottom: 700, width: 560, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    dropAt(canvas, 'Widget', 550, 400)
    const added = view.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(added.position.x).toBe(33)
    expect(added.position.x + NODE_WIDTH + NODE_RIGHT_OVERHANG).toBeLessThanOrEqual(560 - NODE_CONFIG_PANEL_WIDTH)
  })

  it('centres a zoomed drop, clamps its left overhang, and stacks repeated drops down', () => {
    const view = mount({}, { nodes: [], edges: [] })
    const canvas = screen.getByTestId('workflow-canvas')
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 760, bottom: 700, width: 760, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    view.drive((s) => { s.getState().setSelectedNode(s.getState().nodes[0]!.id) })
    view.drive((s) => { s.setState({ zoom: 2, pan: { x: 20, y: 10 } }) })
    dropAt(canvas, 'Widget', 400, 700)
    const first = view.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(first.position).toEqual({ x: 80, y: 305 })

    const leftView = mount({}, { nodes: [], edges: [] })
    const leftCanvas = screen.getAllByTestId('workflow-canvas')[1]!
    vi.spyOn(leftCanvas, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 760, bottom: 700, width: 760, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    dropAt(leftCanvas, 'Widget', 50, 400)
    const left = leftView.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(left.position).toEqual({ x: 7, y: 360 })

    const narrowView = mount({}, { nodes: [], edges: [] })
    const narrow = screen.getAllByTestId('workflow-canvas')[2]!
    vi.spyOn(narrow, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 760, bottom: 700, width: 760, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    dropAt(narrow, 'Widget', 740, 400)
    vi.spyOn(narrow, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 460, bottom: 700, width: 460, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    dropAt(narrow, 'Widget', 440, 400)
    const widgets = narrowView.editor().getState().nodes.filter((n) => n.blockTypeId === 'Widget')
    expect(widgets[1]?.position).toEqual({ x: 233, y: 520 })
    expect(widgets[1]!.position.x + NODE_WIDTH + NODE_RIGHT_OVERHANG).toBeLessThanOrEqual(460)
  })

  it('ignores a drop while read-only', () => {
    const view = mount({ readOnly: true })
    const before = view.editor().getState().nodes.length
    fireEvent.drop(screen.getByTestId('workflow-canvas'), { dataTransfer: dataTransfer('Widget') })
    expect(view.editor().getState().nodes).toHaveLength(before)
  })

  it('ends a palette drag once the block lands', () => {
    const view = mount()
    view.drive((store) => store.getState().setPaletteDragActive(true))
    fireEvent.drop(screen.getByTestId('workflow-canvas'), { dataTransfer: dataTransfer('Widget') })
    expect(view.editor().getState().isPaletteDragActive).toBe(false)
  })

  it('zooms out on a downward wheel and clamps rather than inverting', () => {
    const view = mount()
    const canvas = screen.getByTestId('workflow-canvas')
    for (let i = 0; i < 60; i += 1) fireEvent.wheel(canvas, { deltaY: 120 })
    expect(view.editor().getState().zoom).toBeGreaterThanOrEqual(0.1)
    expect(view.editor().getState().zoom).toBeLessThan(1)
  })

  it('deletes the selected node on Delete', async () => {
    const view = mount()
    view.drive((store) => store.getState().setSelectedNode('a'))
    screen.getByTestId('workflow-canvas').focus()
    await userEvent.keyboard('{Delete}')
    expect(view.editor().getState().nodes.find((n) => n.id === 'a')).toBeUndefined()
  })

  it('deletes the selected wire on Delete', async () => {
    const view = mount()
    view.drive((store) => store.getState().setSelectedEdge('e'))
    screen.getByTestId('workflow-canvas').focus()
    await userEvent.keyboard('{Delete}')
    expect(view.editor().getState().edges).toHaveLength(0)
  })

  it('does not delete while read-only', async () => {
    const view = mount({ readOnly: true })
    view.drive((store) => store.getState().setSelectedNode('a'))
    screen.getByTestId('workflow-canvas').focus()
    await userEvent.keyboard('{Delete}')
    expect(view.editor().getState().nodes.find((n) => n.id === 'a')).toBeDefined()
  })

  it('treats Backspace in a text field as editing, not as deleting the block', async () => {
    const view = mount()
    view.drive((store) => store.getState().setSelectedNode('a'))
    const field = document.createElement('input')
    document.body.appendChild(field)
    field.focus()
    await userEvent.keyboard('{Backspace}')
    expect(view.editor().getState().nodes.find((n) => n.id === 'a')).toBeDefined()
    field.remove()
  })

  it('selects the wire that was clicked', async () => {
    const view = mount()
    await userEvent.click(screen.getByTestId('workflow-edge-a-b'))
    expect(view.editor().getState().selectedEdgeId).toBe('e')
  })

  it('tints a node the host highlighted, without knowing what the status means', () => {
    mount({ highlights: [{ nodeId: 'a', status: 'error' }] })
    expect(screen.getByTestId('workflow-node-body-a')).toHaveAttribute('stroke-width', '2')
  })

  it('shows the values a host attached to a node as its tooltip', () => {
    mount({ highlights: [{ nodeId: 'a', status: 'success', outputsJson: '{"Out":1}' }] })
    expect(document.querySelector('[data-node-id="a"] title')?.textContent).toContain('{"Out":1}')
  })
})

describe('WorkflowCanvas — dragging a wire between ports', () => {
  beforeAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    proto.createSVGPoint = function createSVGPoint() {
      return { x: 0, y: 0, matrixTransform() { return { x: this.x, y: this.y } } }
    }
    proto.getScreenCTM = function getScreenCTM() { return { inverse: () => null } }
  })
  afterAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    delete proto.createSVGPoint
    delete proto.getScreenCTM
  })

  it('ends a pan released over an input port', () => {
    const view = mount()
    const canvas = screen.getByTestId('workflow-canvas')
    fireEvent.mouseDown(canvas, { button: 2 })
    fireEvent.mouseUp(screen.getByTestId('workflow-port-b-input-In'), { button: 2 })
    fireEvent.mouseMove(canvas, { movementX: 15, movementY: 0 })
    fireEvent.mouseUp(window)
    expect(view.editor().getState().pan.x).toBe(0)
  })

  it('drags a node by the world-space cursor delta at zoom two', async () => {
    const view = mount()
    view.drive((s) => { s.setState({ zoom: 2, pan: { x: 0, y: 0 } }) })
    const body = screen.getByTestId('workflow-node-body-a')
    fireEvent.mouseDown(body, { button: 0, clientX: 100, clientY: 100 })
    fireEvent.mouseMove(screen.getByTestId('workflow-canvas'), { clientX: 200, clientY: 100, movementX: 100, movementY: 0 })
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()))
    fireEvent.mouseUp(window)
    expect(view.editor().getState().nodes.find((n) => n.id === 'a')?.position.x).toBe(50)
  })

  it.each([1, 2])('pans on a button %i press over a node without selecting or dragging it', (button) => {
    const view = mount()
    const canvas = screen.getByTestId('workflow-canvas')
    const body = screen.getByTestId('workflow-node-body-a')
    fireEvent.mouseDown(body, { button })
    fireEvent.mouseMove(canvas, { movementX: 12, movementY: 7 })
    fireEvent.mouseUp(window)
    expect(view.editor().getState().selectedNodeId).toBeNull()
    expect(view.editor().getState().nodes.find((n) => n.id === 'a')?.position).toEqual({ x: 0, y: 0 })
    expect(view.editor().getState().pan).toEqual({ x: 12, y: 7 })
  })

  it('keeps the gesture alive when the cursor leaves the canvas mid-drag', () => {
    const view = mount()
    view.drive((s) => { s.getState().setSelectedNode('a') })
    fireEvent.mouseDown(screen.getByTestId('workflow-port-a-output-Out'))
    fireEvent.mouseLeave(screen.getByTestId('workflow-canvas'))
    fireEvent.mouseUp(screen.getByTestId('workflow-port-b-input-In'))
    expect(view.editor().getState().selectedEdgeId).toBe('e')
    expect(view.editor().getState().edges.filter((e) => e.sourceNodeId === 'a')).toHaveLength(1)
  })

  it('drops the gesture when it ends on empty canvas rather than on a port', () => {
    const view = mount()
    const before = view.editor().getState().edges.length
    fireEvent.mouseDown(screen.getByTestId('workflow-port-a-output-Out'))
    fireEvent.mouseLeave(screen.getByTestId('workflow-canvas'))
    fireEvent.mouseUp(screen.getByTestId('workflow-canvas'))
    expect(view.editor().getState().edges).toHaveLength(before)
  })

  it.each([1, 2])('does not start an output connection with button %i', (button) => {
    const view = mount()
    const port = screen.getByTestId('workflow-port-a-output-Out')
    fireEvent.mouseDown(port, { button })
    fireEvent.mouseMove(screen.getByTestId('workflow-canvas'), { movementX: 20, movementY: 0 })
    fireEvent.mouseUp(window)
    expect(view.editor().getState().edges).toHaveLength(1)
    expect(view.editor().getState().pan.x).toBe(20)
  })
})

describe('WorkflowCanvas — quick-adding a block', () => {
  beforeAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    proto.createSVGPoint = function createSVGPoint() {
      return { x: 0, y: 0, matrixTransform() { return { x: this.x, y: this.y } } }
    }
    proto.getScreenCTM = function getScreenCTM() { return { inverse: () => null } }
  })
  afterAll(() => {
    const proto = SVGSVGElement.prototype as unknown as Record<string, unknown>
    delete proto.createSVGPoint
    delete proto.getScreenCTM
  })

  const narrowCanvas = (width: number) => {
    const canvas = screen.getByTestId('workflow-canvas')
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({
      left: 0, top: 0, right: width, bottom: 700, width, height: 700, x: 0, y: 0, toJSON: () => ({}),
    } as DOMRect)
    return canvas
  }

  it('places a block clear of the panel its own selection is about to open', () => {
    const view = mount({ }, { nodes: [], edges: [] })
    const width = 760
    narrowCanvas(width)
    view.drive((s) => { s.getState().requestQuickAddBlock('Widget') })
    const added = view.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(added.position.x + NODE_WIDTH + NODE_RIGHT_OVERHANG).toBeLessThanOrEqual(width - NODE_CONFIG_PANEL_WIDTH)
  })

  it('does not stack the second block on top of the first', () => {
    const view = mount({ }, { nodes: [], edges: [] })
    narrowCanvas(760)
    view.drive((s) => { s.getState().requestQuickAddBlock('Widget') })
    view.drive((s) => { s.getState().requestQuickAddBlock('Widget') })
    const [n1, n2] = view.editor().getState().nodes.filter((n) => n.blockTypeId === 'Widget')
    const overlaps = Math.abs(n1!.position.x - n2!.position.x) < NODE_WIDTH
      && Math.abs(n1!.position.y - n2!.position.y) < 120
    expect(overlaps).toBe(false)
  })

  it('leaves both blocks wirable to each other with the panel open', () => {
    const view = mount({ }, { nodes: [], edges: [] })
    const canvas = narrowCanvas(760)
    view.drive((s) => { s.getState().requestQuickAddBlock('Widget') })
    view.drive((s) => { s.getState().requestQuickAddBlock('Widget') })
    const [n1, n2] = view.editor().getState().nodes.filter((n) => n.blockTypeId === 'Widget')
    fireEvent.mouseDown(screen.getByTestId(`workflow-port-${n1!.id}-output-Out`))
    fireEvent.mouseLeave(canvas)
    fireEvent.mouseUp(screen.getByTestId(`workflow-port-${n2!.id}-input-In`))
    expect(view.editor().getState().edges.some((e) => e.sourceNodeId === n1!.id && e.targetNodeId === n2!.id)).toBe(true)
  })

  it('centres quick-add in world coordinates under pan and zoom', () => {
    const panned = mount({}, { nodes: [], edges: [] })
    vi.spyOn(screen.getByTestId('workflow-canvas'), 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 1000, bottom: 700, width: 1000, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    panned.drive((s) => { s.setState({ pan: { x: -600, y: 0 }, zoom: 1 }) })
    panned.drive((s) => { s.getState().requestQuickAddBlock('Widget') })
    const a = panned.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(a.position.x).toBe(840)
    expect(a.position.x + NODE_WIDTH + NODE_RIGHT_OVERHANG - 600).toBeLessThanOrEqual(700)

    const zoomed = mount({}, { nodes: [], edges: [] })
    vi.spyOn(screen.getAllByTestId('workflow-canvas')[1]!, 'getBoundingClientRect').mockReturnValue({ left: 0, top: 0, right: 1000, bottom: 700, width: 1000, height: 700, x: 0, y: 0, toJSON: () => ({}) } as DOMRect)
    zoomed.drive((s) => { s.setState({ pan: { x: 0, y: 0 }, zoom: 2 }) })
    zoomed.drive((s) => { s.getState().requestQuickAddBlock('Widget') })
    const b = zoomed.editor().getState().nodes.find((n) => n.blockTypeId === 'Widget')!
    expect(b.position.x).toBe(65)
    expect((b.position.x + NODE_WIDTH + NODE_RIGHT_OVERHANG) * 2).toBeLessThanOrEqual(700)
  })
})

describe('WorkflowCanvas — flashing a node', () => {
  it('pulses the node it was asked for, and only that one', () => {
    const view = mount()
    expect(screen.queryByTestId('node-flash-ring')).not.toBeInTheDocument()
    view.drive((s) => { s.getState().flashNode('b') })
    const rings = screen.getAllByTestId('node-flash-ring')
    expect(rings).toHaveLength(1)
    expect(rings[0]?.closest('[data-node-id]')?.getAttribute('data-node-id')).toBe('b')
  })

  it('re-flashes the same node when asked again, so a repeated click still shows', () => {
    const view = mount()
    view.drive((s) => { s.getState().flashNode('b') })
    const first = view.editor().getState().flashSeq
    view.drive((s) => { s.getState().flashNode('b') })
    expect(view.editor().getState().flashSeq).toBeGreaterThan(first)
  })
})
