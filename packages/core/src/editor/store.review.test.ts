import { beforeEach, describe, expect, it } from 'vitest'
import type { WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import type { WorkflowEditorStore } from './store.js'
import { createWorkflowEditorStore } from './store.js'

const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES])
let store: WorkflowEditorStore
let nextId = 0

beforeEach(() => {
  nextId = 0
  store = createWorkflowEditorStore({ portTypes, createId: () => `id-${++nextId}` })
})

const node = (id: string, extra: Record<string, unknown> = {}): WorkflowNode =>
  ({ id, blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {}, ...extra }) as WorkflowNode

describe('loadWorkflow — unlisted fields are refused (review claim 3)', () => {
  it('refuses an unknown node field rather than silently stripping it', () => {
    store.getState().loadWorkflow({ nodes: [node('n1', { tags: ['ops'], retry: { max: 3 } })], edges: [] })
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'stored-key-unknown', location: '/nodes/0/tags' })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(/unlisted key 'tags'/)
  })

  it('refuses an unknown node field inside a sub-canvas as well', () => {
    store.getState().loadWorkflow({
      nodes: [node('parent', { subCanvases: { Then: { nodes: [node('child', { colour: 'teal' })], edges: [] } } })],
      edges: [],
    })

    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'stored-key-unknown', location: '/nodes/0/subCanvases/Then/nodes/0/colour' })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(/unlisted key 'colour'/)
  })

  it('refuses an unlisted `config` key rather than folding it into `configuration`', () => {
    store.getState().loadWorkflow({ nodes: [node('n1', { config: { Depot: 'WAW' }, configuration: undefined })], edges: [] })

    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'stored-key-unknown', location: '/nodes/0/config' })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(/cannot be read, so it cannot be saved: unlisted key 'config'/)
  })

  it('refuses an unknown edge field rather than silently stripping it', () => {
    store.getState().loadWorkflow({
      nodes: [node('a'), node('b')],
      edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {}, label: 'yes' } as unknown as never],
    })
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'stored-key-unknown', location: '/edges/0/label' })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(/unlisted key 'label'/)
  })
})

describe('removeNode (review claim 6)', () => {
  it('clears the selection of a wire that went with the removed node', () => {
    store.getState().loadWorkflow({
      nodes: [node('a'), node('b')],
      edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }],
    })
    store.getState().setSelectedEdge('e')

    store.getState().removeNode('a')

    expect(store.getState().edges.find((e) => e.id === 'e')).toBeUndefined()
    expect(store.getState().selectedEdgeId).toBeNull()
  })

  it('keeps a selected wire that did not touch the removed node', () => {
    store.getState().loadWorkflow({
      nodes: [node('a'), node('b'), node('c')],
      edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }],
    })
    store.getState().setSelectedEdge('e')
    store.getState().removeNode('c')
    expect(store.getState().selectedEdgeId).toBe('e')
  })
})

describe('addEdge (review claim 6)', () => {
  beforeEach(() => {
    store.getState().loadWorkflow({ nodes: [node('a'), node('b')], edges: [] })
  })

  it('selects the wire a pair already has instead of adding a second one on top of it', () => {
    store.getState().addEdge('a', 'b')
    const [existing] = store.getState().edges.filter((e) => e.sourceNodeId === 'a')
    store.getState().setSelectedNode('a')

    store.getState().addEdge('a', 'b')

    expect(store.getState().edges.filter((e) => e.sourceNodeId === 'a' && e.targetNodeId === 'b')).toHaveLength(1)
    expect(store.getState().selectedEdgeId).toBe(existing!.id)
  })

  it('refuses a wire from a node to itself', () => {
    store.getState().addEdge('a', 'a')
    expect(store.getState().edges).toHaveLength(0)
  })

  it('accepts a wire that closes a cycle, and leaves reporting it to validateStructure', () => {
    store.getState().addEdge('a', 'b')
    store.getState().addEdge('b', 'a')
    expect(store.getState().edges).toHaveLength(2)
    expect(store.getState().validateStructureFindings().some((e) => e.code === 'cycle')).toBe(true)
  })
})

describe('updateNodePosition (review claim 6)', () => {
  it('marks the workflow dirty after a move, and each frame re-asserts it', () => {
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    store.getState().markSaved()
    store.getState().updateNodePosition('a', { x: 1, y: 1 })
    expect(store.getState().isDirty).toBe(true)
    const before = store.getState()
    store.getState().updateNodePosition('a', { x: 2, y: 2 })
    expect(store.getState()).not.toBe(before)
  })
})
