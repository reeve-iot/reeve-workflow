import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { buildPortTypeScopes, createPortTypeResolver } from '../ports/portTypeResolver.js'
import { computeIncomingVariables } from './incomingVariables.js'
import { createWorkflowEditorStore } from './store.js'
import { validateBranchOutputs } from './validateBranchOutputs.js'
import { validateStructure } from './validateStructure.js'

const portTypes = createPortTypeRegistry(BUILTIN_PORT_TYPES)

const block = (overrides: Partial<BlockTypeDescriptor>): BlockTypeDescriptor => ({
  blockTypeId: 'noop',
  displayName: 'Noop',
  inputs: [],
  outputs: [],
  subCanvasNames: [],
  subCanvasInputs: {},
  subCanvasOutputs: {},
  ...overrides,
})

const NOTIFY = block({
  blockTypeId: 'notify',
  displayName: 'Notify',
  inputs: [{ name: 'Message', displayName: 'Message', type: 'String', isRequired: true }],
  outputs: [{ name: 'Text', displayName: 'Text', type: 'String', isRequired: false }],
})
const LOOP = block({ blockTypeId: 'loop.for', displayName: 'For Loop', subCanvasNames: ['Body'], subCanvasInputs: { Body: [] } })
const BLOCKS = [block({}), NOTIFY, LOOP]

const node = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })
const edge = (id: string, sourceNodeId: string, targetNodeId: string, mappings: WorkflowEdge['mappings'] = {}): WorkflowEdge =>
  ({ id, sourceNodeId, targetNodeId, mappings })
const START = node('__workflow_input__', 'WorkflowInput')

describe('validateStructure reports a wire to a node that is not on its canvas', () => {
  it('names the compile rule for two wires with the same source and target', () => {
    const findings = validateStructure({
      nodes: [START, node('a', 'noop')],
      edges: [edge('e1', START.id, 'a'), edge('e2', START.id, 'a')],
      blockTypes: BLOCKS,
    })
    expect(findings).toMatchObject([{ code: 'duplicate-edge-pair', ruleId: 'duplicate-edge-pair', edgeId: 'e2' }])
  })

  it('names the missing source, and only that: the reachability maths still skips the wire', () => {
    const errors = validateStructure({
      nodes: [START, node('a', 'noop')],
      edges: [edge('e1', '__workflow_input__', 'a'), edge('e2', 'ghost', 'a')],
      blockTypes: BLOCKS,
    })
    expect(errors.map((finding) => finding.message)).toEqual(["Connection 'e2' names source node 'ghost', which does not exist on its canvas"])
  })

  it('names the missing target, and only that', () => {
    const errors = validateStructure({
      nodes: [START, node('a', 'noop')],
      edges: [edge('e1', '__workflow_input__', 'a'), edge('e2', 'a', 'ghost')],
      blockTypes: BLOCKS,
    })
    expect(errors.map((finding) => finding.message)).toEqual(["Connection 'e2' names target node 'ghost', which does not exist on its canvas"])
  })

  it('reports a wire missing both ends once per end, as the compiler does', () => {
    const errors = validateStructure({
      nodes: [START, node('a', 'noop')],
      edges: [edge('e1', '__workflow_input__', 'a'), edge('e2', 'ghost1', 'ghost2')],
      blockTypes: BLOCKS,
    })
    expect(errors.map((finding) => finding.message)).toEqual([
      "Connection 'e2' names source node 'ghost1', which does not exist on its canvas",
      "Connection 'e2' names target node 'ghost2', which does not exist on its canvas",
    ])
  })

  it('writes an id a stored wire lacks as empty, as the compiler does, never as undefined', () => {
    const bare = { targetNodeId: 'a', mappings: {} } as unknown as WorkflowEdge
    expect(validateStructure({ nodes: [START, node('a', 'noop')], edges: [edge('e1', '__workflow_input__', 'a'), bare], blockTypes: BLOCKS }))
      .toMatchObject([{ code: 'edge-endpoint-unknown', message: "Connection '' names source node '', which does not exist on its canvas" }])
  })

  it('reports a wire on a canvas with no nodes at all', () => {
    expect(validateStructure({ nodes: [], edges: [edge('e1', 'x', 'y')], blockTypes: BLOCKS }).map((finding) => finding.message)).toEqual([
      "Connection 'e1' names source node 'x', which does not exist on its canvas",
      "Connection 'e1' names target node 'y', which does not exist on its canvas",
    ])
  })

  it('reports one inside a sub-canvas, and one two canvases down, naming where each sits', () => {
    const inner = node('loop2', 'loop.for', {
      subCanvases: { Body: { nodes: [node('c1', 'noop')], edges: [edge('ce1', 'c1', 'ghost-c')] } },
    })
    const loop = node('loop', 'loop.for', {
      subCanvases: { Body: { nodes: [node('b1', 'noop'), inner], edges: [edge('be1', 'ghost-b', 'b1')] } },
    })
    const errors = validateStructure({ nodes: [START, loop], edges: [edge('e1', '__workflow_input__', 'loop')], blockTypes: BLOCKS })
    expect(errors.map((finding) => finding.message)).toEqual([
      "Connection 'be1' in branch 'Body' of 'For Loop' names source node 'ghost-b', which does not exist on its canvas",
      "Connection 'ce1' in branch 'Body' of 'For Loop' in branch 'Body' of 'For Loop' names target node 'ghost-c', which does not exist on its canvas",
    ])
  })

  it('still calls a node whose only incoming wire names a missing source rootless', () => {
    const errors = validateStructure({
      nodes: [START, node('a', 'noop')],
      edges: [edge('e1', 'ghost', 'a')],
      blockTypes: BLOCKS,
    })
    expect(errors.map((finding) => finding.message)).toEqual([
      "Connection 'e1' names source node 'ghost', which does not exist on its canvas",
      "Node 'Noop' has no incoming connection — the workflow must start only from the Workflow Input node",
    ])
  })

  it('counts a literal on a wire from a missing node towards a doubly fed input', () => {
    const errors = validateStructure({
      nodes: [START, node('a', 'notify')],
      edges: [
        edge('e1', '__workflow_input__', 'a', { Message: { type: 'literal', literalValue: 'one' } }),
        edge('e2', 'ghost', 'a', { Message: { type: 'literal', literalValue: 'two' } }),
      ],
      blockTypes: BLOCKS,
    })
    expect(errors.map((finding) => finding.message)).toEqual([
      "Connection 'e2' names source node 'ghost', which does not exist on its canvas",
      "Node 'Notify' input 'Message' is mapped from multiple incoming connections — only one will apply",
    ])
  })

  it('leaves a sub-canvas node wired to nothing alone: only a wire naming a missing node is reported', () => {
    const loop = node('loop', 'loop.for', {
      subCanvases: { Body: { nodes: [node('b1', 'noop', { configuration: { Message: 'x' } })], edges: [] } },
    })
    expect(validateStructure({ nodes: [START, loop], edges: [edge('e1', '__workflow_input__', 'loop')], blockTypes: BLOCKS }))
      .toEqual([])
  })
})

describe('a mapping feeds an input only if the run would write it', () => {
  const loadRoot = (edges: WorkflowEdge[]) => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setBlockTypes(BLOCKS)
    store.getState().loadWorkflow({ nodes: [START, node('n1', 'notify')], edges: [edge('e0', '__workflow_input__', 'n1'), ...edges] })
    return store
  }

  it('reports a required input fed only by a wired mapping from a missing node, beside the wire itself', () => {
    const store = loadRoot([edge('e1', 'ghost', 'n1', { Message: { type: 'direct', sourceOutputName: 'Text' } })])
    expect(store.getState().validateStructure().map((finding) => finding.message)).toEqual([
      "Connection 'e1' names source node 'ghost', which does not exist on its canvas",
    ])
    expect(store.getState().validateWorkflowFindings()).toEqual([
      { canvasPath: [], nodeId: 'n1', code: 'required-input-unfed', params: { nodeLabel: 'Notify', portName: 'Message', portLabel: 'Message' }, ruleId: 'required-input-unfed', portName: 'Message', message: "Node 'Notify' is missing required mapping for 'Message'" },
    ])
  })

  it('counts a literal on a wire from a missing node as feeding the input, since the run writes it regardless', () => {
    for (const literalValue of ['hello']) {
      const store = loadRoot([edge('e1', 'ghost', 'n1', { Message: { type: 'literal', literalValue } })])
      expect(store.getState().validateStructure()).toHaveLength(1)
      expect(store.getState().validateWorkflowFindings(), String(literalValue)).toEqual([])
    }
  })

  it('still counts a wired mapping from a node that is there', () => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setBlockTypes(BLOCKS)
    store.getState().loadWorkflow({
      nodes: [START, node('src', 'notify', { configuration: { Message: 'x' } }), node('n1', 'notify')],
      edges: [edge('e0', '__workflow_input__', 'src'), edge('e1', 'src', 'n1', { Message: { type: 'direct', sourceOutputName: 'Text' } })],
    })
    expect([store.getState().validateStructure(), store.getState().validateWorkflowFindings()]).toEqual([[], []])
  })

  it('reports a workflow output fed only by a wired mapping from a missing node as not connected', () => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setBlockTypes(BLOCKS)
    store.getState().loadWorkflow({
      nodes: [START, node('__workflow_output__', 'WorkflowOutput')],
      edges: [
        edge('e0', '__workflow_input__', '__workflow_output__'),
        edge('e1', 'ghost', '__workflow_output__', { result: { type: 'direct', sourceOutputName: 'Text' } }),
      ],
    })
    store.getState().setWorkflowParams([], [{ name: 'result', displayName: 'Result', type: 'String', isRequired: true }])
    expect(store.getState().validateWorkflowFindings()).toEqual([
      { canvasPath: [], nodeId: '__workflow_output__', code: 'required-input-unfed.exit', params: { exitNoun: 'Workflow output', portName: 'result', portLabel: 'Result' }, ruleId: 'required-input-unfed', portName: 'result', message: "Workflow output 'Result' is not connected" },
    ])
  })

  it('warns that a branch leaves a declared output unset when only a wire from a missing node feeds it', () => {
    const branch = (mapping: WorkflowEdge['mappings'][string]) => ({
      nodes: [node('__workflow_input__', 'WorkflowInput'), node('__workflow_output__', 'WorkflowOutput')],
      edges: [edge('be1', 'ghost', '__workflow_output__', { value: mapping })],
    })
    const warningsFor = (mapping: WorkflowEdge['mappings'][string]) => validateBranchOutputs({
      definition: {
        nodes: [START, node('loop', 'loop.for', {
          outputPorts: [{ name: 'out', displayName: 'Out', type: 'String', sources: { Body: 'value' } }],
          subCanvases: { Body: branch(mapping) },
        })],
        edges: [edge('e1', '__workflow_input__', 'loop')],
      },
      blockTypes: BLOCKS,
      workflowInputParams: [],
      portTypes,
    })
    expect(warningsFor({ type: 'direct', sourceOutputName: 'Text' })).toEqual([
      { canvasPath: [], nodeId: 'loop', code: 'branch-output-unset', params: { branchName: 'Body', nodeLabel: 'For Loop', portName: 'out' }, message: "Branch 'Body' of 'For Loop' does not set output 'out' (returns null)" },
    ])
    expect(warningsFor({ type: 'literal', literalValue: 'x' })).toEqual([])
  })
})

describe('what a wire from a missing node offers', () => {
  it('offers no variable to the node it targets', () => {
    const nodes = [START, node('c', 'notify')]
    const edges = [edge('e0', '__workflow_input__', 'c'), edge('e1', 'ghost', 'c')]
    const offered = computeIncomingVariables({
      node: nodes[1] as WorkflowNode, nodes, edges, blockTypes: BLOCKS, canvasStack: [], workflowInputParams: [],
      resolver: createPortTypeResolver({ scopes: buildPortTypeScopes({ nodes, edges, canvasStack: [] }), blockTypes: BLOCKS, workflowInputParams: [] }),
    })
    expect(offered).toEqual([])
  })
})

describe('an author removes a reported wire', () => {
  const load = (definition: { nodes: WorkflowNode[]; edges: WorkflowEdge[] }) => {
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setBlockTypes(BLOCKS)
    store.getState().loadWorkflow(definition)
    return store
  }
  const body = (edges: WorkflowEdge[]) => node('loop', 'loop.for', {
    subCanvases: { Body: { nodes: [START, node('b1', 'noop'), node('loop2', 'loop.for', {
      subCanvases: { Body: { nodes: [START], edges: [edge('deep', 'ghost', 'nowhere')] } },
    })], edges: [edge('b0', '__workflow_input__', 'b1'), edge('b2', '__workflow_input__', 'loop2'), ...edges] } },
  })
  const document = (bodyEdges: WorkflowEdge[] = []) => ({
    nodes: [START, body(bodyEdges)],
    edges: [edge('e0', '__workflow_input__', 'loop'), edge('root', '__workflow_input__', 'ghost')],
  })
  const reported = (store: ReturnType<typeof load>) =>
    store.getState().validateStructureFindings().map((f) => [f.edgeId, (f.canvasPath ?? []).map((s) => s.nodeId)])

  it('removes a wire from the entry node to nothing, and one with neither end, from the open canvas', () => {
    const store = load({ nodes: [START, node('a', 'noop')], edges: [edge('e0', '__workflow_input__', 'a'), edge('w1', '__workflow_input__', 'ghost'), edge('w2', 'x', 'y')] })
    store.getState().removeEdge('w1')
    store.getState().removeEdge('w2')
    expect(store.getState().validateStructure()).toEqual([])
    expect(store.getState().edges.map((e) => e.id)).toEqual(['e0'])
  })

  it('removes the wire a finding names, on whichever canvas it is, from whichever canvas is open', () => {
    for (const open of [[], ['loop'], ['loop', 'loop2']]) {
      const store = load(document([edge('mid', 'b1', 'ghost')]))
      for (const container of open) store.getState().enterSubCanvas(container, 'Body')
      for (const f of store.getState().validateStructureFindings()) store.getState().removeEdge(f.edgeId ?? '', f.canvasPath)
      expect(reported(store), `open: ${open.join('/')}`).toEqual([])
      expect(store.getState().canvasStack.map((f) => f.parentNodeId)).toEqual(open)
      expect(store.getState().isDirty).toBe(true)
      store.getState().exitToLevel(0)
      const saved = store.getState().getWorkflowDefinition()
      expect(saved).toEqual({
        nodes: [START, body([])].map((n) => n.id === 'loop' ? { ...n, subCanvases: { Body: {
          ...n.subCanvases?.Body,
          nodes: n.subCanvases?.Body?.nodes.map((m) => m.id === 'loop2' ? { ...m, subCanvases: { Body: { nodes: [START], edges: [] } } } : m),
        } } } : n),
        edges: [edge('e0', '__workflow_input__', 'loop')],
      })
    }
  })

  it('removes from the branch of the node the path names, among nodes whose branches share a name', () => {
    const twin = (id: string, wire: string) => node(id, 'loop.for', { subCanvases: { Body: { nodes: [START], edges: [edge(wire, 'ghost', 'x')] } } })
    const store = load({ nodes: [START, twin('first', 'w1'), twin('second', 'w2')], edges: [edge('a', '__workflow_input__', 'first'), edge('b', '__workflow_input__', 'second')] })
    store.getState().removeEdge('w2', [{ nodeId: 'second', branchName: 'Body' }])
    expect(reported(store)).toEqual([['w1', ['first']], ['w1', ['first']]])
    expect(store.getState().isDirty).toBe(true)
  })

  it('changes nothing when the path names no canvas or the canvas has no such wire', () => {
    const store = load(document())
    const before = store.getState().getWorkflowDefinition()
    store.getState().removeEdge('deep', [{ nodeId: 'loop', branchName: 'Else' }])
    store.getState().removeEdge('deep', [{ nodeId: 'loop', branchName: 'Body' }])
    store.getState().removeEdge('root', [{ nodeId: 'nobody', branchName: 'Body' }])
    expect(store.getState().getWorkflowDefinition()).toEqual(before)
    expect(store.getState().isDirty).toBe(false)
  })

  it('changes nothing when the open canvas, named by its path or by none, has no such wire', () => {
    for (const open of [[], ['loop']]) {
      const store = load(document())
      for (const container of open) store.getState().enterSubCanvas(container, 'Body')
      const before = store.getState().getWorkflowDefinition()
      const edges = store.getState().edges
      store.getState().removeEdge('absent')
      store.getState().removeEdge('absent', open.map((nodeId) => ({ nodeId, branchName: 'Body' })))
      expect(store.getState().edges, `open: ${open.join('/')}`).toBe(edges)
      expect(store.getState().getWorkflowDefinition()).toEqual(before)
      expect(store.getState().isDirty).toBe(false)
    }
  })

  it('removes from the branch the path names, not the open sibling branch holding the same wire id', () => {
    const twoWay = block({ blockTypeId: 'fork', subCanvasNames: ['Then', 'Else'], subCanvasInputs: { Then: [], Else: [] } })
    const branch = { nodes: [START], edges: [edge('w', 'ghost', 'x')] }
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setBlockTypes([...BLOCKS, twoWay])
    store.getState().loadWorkflow({ nodes: [START, node('f', 'fork', { subCanvases: { Then: branch, Else: branch } })], edges: [edge('a', '__workflow_input__', 'f')] })
    store.getState().enterSubCanvas('f', 'Then')
    store.getState().removeEdge('w', [{ nodeId: 'f', branchName: 'Else' }])
    expect(store.getState().edges.map((e) => e.id)).toEqual(['w'])
    store.getState().exitToLevel(0)
    const f = store.getState().getWorkflowDefinition().nodes?.find((n) => n.id === 'f')
    expect([f?.subCanvases?.Then?.edges?.map((e) => e.id), f?.subCanvases?.Else?.edges?.map((e) => e.id)]).toEqual([['w'], []])
  })

  it('keeps the open canvas selection when a wire of the same id goes from another canvas', () => {
    const store = load(document([edge('root', 'b1', 'ghost')]))
    store.getState().setSelectedEdge('root')
    store.getState().removeEdge('root', [{ nodeId: 'loop', branchName: 'Body' }])
    expect(store.getState().selectedEdgeId).toBe('root')
    expect(store.getState().edges.map((e) => e.id)).toContain('root')
  })
})
