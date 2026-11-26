import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { ValidationFinding, WorkflowDefinition, WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { createWorkflowEditorStore } from './store.js'
import type { NodeValidationRule } from './validationRules.js'
import { buildValidationSummary } from './validationSummary.js'

const portTypes = createPortTypeRegistry(BUILTIN_PORT_TYPES)
const block = (o: Partial<BlockTypeDescriptor>): BlockTypeDescriptor =>
  ({ blockTypeId: 'noop', displayName: 'Noop', inputs: [], outputs: [], subCanvasNames: [], subCanvasInputs: {}, subCanvasOutputs: {}, ...o })
const NOTIFY = block({
  blockTypeId: 'notify',
  displayName: 'Notify',
  inputs: [{ name: 'Message', displayName: 'Message', type: 'String', isRequired: true }],
  outputs: [{ name: 'Text', displayName: 'Text', type: 'String', isRequired: false }],
})
const LOOP = block({ blockTypeId: 'loop.for', displayName: 'For Loop', subCanvasNames: ['Body'], subCanvasInputs: { Body: [] } })
const IN = '__workflow_input__'
const OUT = '__workflow_output__'
const node = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })
const edge = (id: string, sourceNodeId: string, targetNodeId: string, mappings: WorkflowEdge['mappings'] = {}): WorkflowEdge =>
  ({ id, sourceNodeId, targetNodeId, mappings })
const BODY = [{ nodeId: 'loop', branchName: 'Body' }]

const unfedInBody = (bodyEdges: WorkflowEdge[] = []): WorkflowDefinition => ({
  nodes: [node(IN, 'WorkflowInput'), node('loop', 'loop.for', {
    subCanvases: { Body: { nodes: [node(IN, 'WorkflowInput'), node('n1', 'notify')], edges: [edge('b0', IN, 'n1'), ...bodyEdges] } },
  })],
  edges: [edge('e0', IN, 'loop')],
})

function storeWith(definition: WorkflowDefinition, blocks: BlockTypeDescriptor[] = [NOTIFY, LOOP], rules: NodeValidationRule[] = []) {
  const store = createWorkflowEditorStore({ portTypes, nodeErrorRules: rules, nodeWarningRules: rules })
  store.getState().setBlockTypes(blocks)
  store.getState().loadWorkflow(definition)
  return store
}

describe('the store validates every canvas, whichever is open', () => {
  it('reports a required input left unfed inside a branch while the root is open, naming the branch', () => {
    const store = storeWith(unfedInBody())
    const expected: ValidationFinding[] = [
      { nodeId: 'n1', canvasPath: BODY, code: 'required-input-unfed', params: { nodeLabel: 'Notify', portName: 'Message', portLabel: 'Message' }, ruleId: 'required-input-unfed', portName: 'Message', message: "Node 'Notify' is missing required mapping for 'Message'" },
    ]
    expect(store.getState().validateWorkflowFindings()).toEqual(expected)
    store.getState().enterSubCanvas('loop', 'Body')
    expect(store.getState().validateWorkflowFindings()).toEqual(expected)
  })

  it('reports a dangling wire anywhere while a sub-canvas is open, so a save guard on it still holds there', () => {
    const store = storeWith(unfedInBody([edge('bad', 'ghost', 'n1')]))
    const atRoot = store.getState().validateStructureFindings()
    expect(atRoot).toEqual([{
      nodeId: null,
      edgeId: 'bad',
      code: 'edge-endpoint-unknown',
      ruleId: 'edge-endpoint-unknown',
      params: { edgeId: 'bad', location: " in branch 'Body' of 'For Loop'", end: 'source', nodeId: 'ghost' },
      canvasPath: BODY,
      message: "Connection 'bad' in branch 'Body' of 'For Loop' names source node 'ghost', which does not exist on its canvas",
    }])
    store.getState().enterSubCanvas('loop', 'Body')
    expect(store.getState().validateStructureFindings()).toEqual(atRoot)
    expect(store.getState().validateStructure()).toEqual(atRoot)
  })

  it('checks the workflow outputs on the root alone, never against an open branch exit node', () => {
    const store = storeWith({
      nodes: [node(IN, 'WorkflowInput'), node('loop', 'loop.for', {
        subCanvases: { Body: { nodes: [node(IN, 'WorkflowInput'), node(OUT, 'WorkflowOutput')], edges: [edge('b0', IN, OUT)] } },
      }), node(OUT, 'WorkflowOutput')],
      edges: [edge('e0', IN, 'loop'), edge('e1', IN, OUT, { result: { type: 'literal', literalValue: 'x' } })],
    })
    store.getState().setWorkflowParams([], [{ name: 'result', displayName: 'Result', type: 'String', isRequired: true }])
    expect(store.getState().validateWorkflowFindings()).toEqual([])
    store.getState().enterSubCanvas('loop', 'Body')
    expect(store.getState().validateWorkflowFindings()).toEqual([])
  })

  it('walks the branches of a block the catalogue does not know yet, as it loads, grading what needs no ports', () => {
    const store = storeWith(unfedInBody([edge('bad', 'n1', 'ghost')]), [NOTIFY, block({ blockTypeId: 'cfg', configurationSchema: [{ name: 'C', label: 'C', type: 'string', required: true }] })])
    expect(store.getState().validateStructureFindings().map((f) => [f.edgeId, f.canvasPath])).toEqual([['bad', BODY]])
    expect(store.getState().validateWorkflowFindings()).toEqual([])
    const withConfig = unfedInBody()
    withConfig.nodes[1]?.subCanvases?.['Body']?.nodes.push(node('c', 'cfg'))
    withConfig.nodes[1]?.subCanvases?.['Body']?.edges.push(edge('b1', IN, 'c'))
    const configStore = storeWith(withConfig, [NOTIFY, block({ blockTypeId: 'cfg', configurationSchema: [{ name: 'C', label: 'C', type: 'string', required: true }] })])
    expect(configStore.getState().validateWorkflowFindings().map((f) => [f.ruleId, f.nodeId, f.canvasPath])).toEqual([['required-config-missing', 'c', BODY]])
  })

  it('runs a host error rule on every canvas and says which canvas each finding is on', () => {
    const rule: NodeValidationRule = ({ node: n }) => (n.blockTypeId === 'notify' ? [{ nodeId: n.id, code: 'host-refusal', message: 'host says no' }] : [])
    const store = storeWith({
      ...unfedInBody(),
      nodes: [...unfedInBody().nodes, node('n1', 'notify', { configuration: { Message: 'x' } })],
      edges: [...unfedInBody().edges, edge('e1', IN, 'n1')],
    }, [NOTIFY, LOOP], [rule])
    store.getState().enterSubCanvas('loop', 'Body')
    const found = store.getState().validateWorkflowFindings().filter((f) => f.message === 'host says no')
    expect(found.map((f) => [f.nodeId, f.canvasPath])).toEqual([['n1', []], ['n1', BODY]])
    const warned = store.getState().validateWorkflowWarningFindings().filter((f) => f.message === 'host says no')
    expect(warned.map((f) => [f.nodeId, f.canvasPath])).toEqual([['n1', []], ['n1', BODY]])
  })

  it('gives a host rule the same types through a branch entry node whichever canvas is open', () => {
    const probe: NodeValidationRule = ({ node: n, resolveOutputType }) => n.blockTypeId === 'noop'
      ? [{ nodeId: n.id, code: 'host-types', message: `Item is ${resolveOutputType(IN, 'Item')}; Seed is ${resolveOutputType(IN, 'Seed')}` }]
      : []
    const loop = block({
      blockTypeId: 'loop.x', displayName: 'Loop', inputs: [{ name: 'Seed', displayName: 'Seed', type: 'Any', isRequired: false }],
      subCanvasNames: ['Body'], subCanvasInputs: { Body: [{ name: 'Item', displayName: 'Item', type: 'Number', isRequired: false }] },
    })
    const store = storeWith({
      nodes: [node(IN, 'WorkflowInput'), node('loop', 'loop.x', {
        passthrough: ['Seed'],
        subCanvases: { Body: { nodes: [node(IN, 'WorkflowInput'), node('p', 'noop')], edges: [edge('b', IN, 'p')] } },
      })],
      edges: [edge('r', IN, 'loop', { Seed: { type: 'literal', literalValue: 'text' } })],
    }, [block({}), loop], [probe])
    const read = () => store.getState().validateWorkflowFindings().filter((f) => f.nodeId === 'p').map((f) => f.message)
    const atRoot = read()
    store.getState().enterSubCanvas('loop', 'Body')
    expect({ atRoot, inside: read() }).toEqual({ atRoot: ['Item is Number; Seed is Any'], inside: ['Item is Number; Seed is Any'] })
  })

  it('refuses a resilience number JSON cannot spell once, as the unreadable value it is, not also as resilience', () => {
    const store = storeWith({ nodes: [node(IN, 'WorkflowInput'), node('n1', 'notify', { configuration: { Message: 'm' } })], edges: [edge('e', IN, 'n1')] })
    store.getState().patchNodeConfig('n1', { timeoutSeconds: Number.NaN, retryCount: Number.POSITIVE_INFINITY })
    expect(store.getState().validateWorkflowFindings().map((f) => f.ruleId)).toEqual(['definition-unreadable'])
  })
})

describe('the summary keeps two canvases apart', () => {
  it('groups one node id on two canvases as two groups, each labelled for its own canvas', () => {
    const summary = buildValidationSummary(
      [
        { nodeId: 'n1', code: 'test', message: 'boom' },
        { nodeId: 'n1', canvasPath: BODY, code: 'test', message: 'boom' },
        { nodeId: 'n1', canvasPath: [], code: 'test-again', message: 'again' },
      ],
      [],
      (id, path) => `${id ?? ''}@${path.map((s) => s.branchName).join('/')}`,
    )
    expect(summary.groupedByNode).toEqual([
      { nodeId: 'n1', canvasPath: [], nodeLabel: 'n1@', findings: [
        { nodeId: 'n1', code: 'test', message: 'boom' },
        { nodeId: 'n1', canvasPath: [], code: 'test-again', message: 'again' },
      ] },
      { nodeId: 'n1', canvasPath: BODY, nodeLabel: 'n1@Body', findings: [{ nodeId: 'n1', canvasPath: BODY, code: 'test', message: 'boom' }] },
    ])
    expect(summary.uniqueErrorCount).toBe(3)
  })
})

describe('a wired mapping naming only blanks is one the run drops', () => {
  it('neither feeds an input nor counts towards a doubly fed one, as ReadMappings drops it', () => {
    const store = storeWith({
      nodes: [node(IN, 'WorkflowInput'), node('src', 'notify', { configuration: { Message: 'x' } }), node('n1', 'notify')],
      edges: [
        edge('e0', IN, 'src'),
        edge('e1', IN, 'n1', { Message: { type: 'direct', sourceOutputName: '\t' } }),
        edge('e2', 'src', 'n1', { Message: { type: 'direct', sourceOutputName: ' ' } }),
      ],
    })
    expect(store.getState().validateStructure()).toEqual([])
    expect(store.getState().validateWorkflow().map((finding) => finding.message)).toEqual(["Node 'Notify (n1)' is missing required mapping for 'Message'"])
  })
})
