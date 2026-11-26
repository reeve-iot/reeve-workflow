import { describe, expect, it, vi } from 'vitest'
import { act } from '@testing-library/react'
import type { BlockTypeDescriptor, WorkflowDefinition } from '@reeve-iot/workflow-core'
import type * as NodePortsModule from './nodePorts.js'
import { WorkflowCanvas } from './WorkflowCanvas.js'
import { renderInEditor } from '../testUtils.js'

const counter = vi.hoisted(() => ({ renders: 0 }))

vi.mock('./nodePorts.js', async (importOriginal) => {
  const actual = await importOriginal<typeof NodePortsModule>()
  return {
    ...actual,
    useNodePorts: (...args: Parameters<typeof actual.useNodePorts>) => {
      counter.renders += 1
      return actual.useNodePorts(...args)
    },
  }
})

const widget: BlockTypeDescriptor = {
  blockTypeId: 'Widget', displayName: 'Widget', category: 'Test',
  inputs: [{ name: 'In', displayName: 'In', isRequired: false, type: 'String' }],
  outputs: [{ name: 'Out', displayName: 'Out', isRequired: false, type: 'String' }],
}

function graph(size: number): WorkflowDefinition {
  const nodes = Array.from({ length: size }, (_, i) => ({
    id: `n${i}`, blockTypeId: 'Widget', position: { x: (i % 20) * 260, y: Math.floor(i / 20) * 140 }, configuration: {},
  }))
  const edges = nodes.slice(1).map((n, i) => ({
    id: `e${i}`, sourceNodeId: `n${i}`, targetNodeId: n.id, mappings: { In: { type: 'direct' as const, sourceOutputName: 'Out' } },
  }))
  return { nodes, edges }
}

function oneDragFrame(size: number) {
  const view = renderInEditor(<WorkflowCanvas onCanvasClick={() => {}} />)
  view.seed([widget], graph(size))
  counter.renders = 0
  const started = performance.now()
  act(() => { view.editor().getState().updateNodePosition('n0', { x: 7, y: 7 }) })
  const ms = performance.now() - started
  const renders = counter.renders
  view.unmount()
  return { renders, ms }
}

describe('WorkflowCanvas — cost of one drag frame', () => {
  it('re-renders the node being dragged, not every node on the canvas', () => {
    const sizes = [50, 100, 300, 600]
    const results = sizes.map((size) => ({ size, ...oneDragFrame(size) }))
    for (const r of results) console.log(`DRAG-FRAME nodes=${r.size} blockNodeRenders=${r.renders} ms=${r.ms.toFixed(1)}`)

    for (const r of results) expect(r.renders).toBeLessThanOrEqual(2)
  }, 30_000)
})
