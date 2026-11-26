import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor, CanvasFrame, DraftAnalysis, DraftPortAnalysis, WorkflowEdge, WorkflowNode } from '../index.js'
import {
  WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_INPUT_NODE_ID,
  buildPortTypeScopes, computeIncomingVariables, createPortTypeResolver, indexDraftAnalysis,
} from '../index.js'

const producer: BlockTypeDescriptor = {
  blockTypeId: 'Producer', displayName: 'Producer', inputs: [],
  outputs: [
    { name: 'Result', displayName: 'Result', isRequired: false, type: 'String' },
    { name: 'Count', displayName: 'Count', isRequired: false, type: 'Number' },
  ],
}
const rival: BlockTypeDescriptor = {
  blockTypeId: 'Rival', displayName: 'Rival', inputs: [],
  outputs: [{ name: 'Result', displayName: 'Result', isRequired: false, type: 'Boolean' }],
}
const consumer: BlockTypeDescriptor = { blockTypeId: 'Consumer', displayName: 'Consumer', inputs: [], outputs: [] }
const dynamic: BlockTypeDescriptor = { blockTypeId: 'Dynamic', displayName: 'Dynamic', inputs: [], outputs: [], hasDynamicPorts: true }
const entry: BlockTypeDescriptor = { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] }
const branch: BlockTypeDescriptor = {
  blockTypeId: 'Branch', displayName: 'Branch', inputs: [], outputs: [], subCanvasNames: ['Body'],
  subCanvasInputs: { Body: [{ name: 'Item', displayName: 'Item', isRequired: true, type: 'Device' }] },
}

const at = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })

const wire = (sourceNodeId: string, targetNodeId: string): WorkflowEdge =>
  ({ id: `${sourceNodeId}->${targetNodeId}`, sourceNodeId, targetNodeId, mappings: {} })

function incoming(args: {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  blockTypes?: BlockTypeDescriptor[]
  canvasStack?: CanvasFrame[]
  workflowInputParams?: { name: string; displayName: string; type: string; isRequired: boolean }[]
  targetId?: string
  analysis?: DraftAnalysis
  freshness?: 'none' | 'current' | 'stale'
}) {
  return offered(args).map(({ name, type }) => ({ name, type }))
}

function offered(args: Parameters<typeof incoming>[0]) {
  const blockTypes = args.blockTypes ?? [producer, rival, consumer, dynamic, entry, branch]
  const canvasStack = args.canvasStack ?? []
  const workflowInputParams = args.workflowInputParams ?? []
  const node = args.nodes.find((n) => n.id === (args.targetId ?? 'c'))!
  return computeIncomingVariables({
    node, nodes: args.nodes, edges: args.edges, blockTypes, canvasStack, workflowInputParams,
    analysis: indexDraftAnalysis(args.analysis ?? null), freshness: args.freshness,
    resolver: createPortTypeResolver({
      scopes: buildPortTypeScopes({ nodes: args.nodes, edges: args.edges, canvasStack }),
      blockTypes,
      workflowInputParams,
    }),
  })
}

describe('computeIncomingVariables', () => {
  it('lists the declared outputs of every wired source', () => {
    expect(incoming({
      nodes: [at('p', 'Producer'), at('c', 'Consumer')],
      edges: [wire('p', 'c')],
    })).toEqual([{ name: 'Result', type: 'String' }, { name: 'Count', type: 'Number' }])
  })

  it('ignores sources that are not wired to this node', () => {
    expect(incoming({
      nodes: [at('p', 'Producer'), at('c', 'Consumer'), at('other', 'Consumer')],
      edges: [wire('p', 'other')],
    })).toEqual([])
  })

  it('carries the forwarded variables of a source through, typed only where a wire gives them a type', () => {
    expect(incoming({
      nodes: [at('p', 'Producer', { passthrough: ['Carried'] }), at('c', 'Consumer')],
      edges: [wire('p', 'c')],
    })).toEqual([
      { name: 'Result', type: 'String' }, { name: 'Count', type: 'Number' }, { name: 'Carried', type: undefined },
    ])
  })

  it('reads the ports of a dynamic source out of its configuration', () => {
    expect(incoming({
      nodes: [
        at('p', 'Dynamic', { configuration: { OutputPorts: JSON.stringify([{ name: 'Alpha', type: 'Boolean' }]) } }),
        at('c', 'Consumer'),
      ],
      edges: [wire('p', 'c')],
    })).toEqual([{ name: 'Alpha', type: 'Boolean' }])
  })

  it('lists each name once, typed by the first source to offer it', () => {
    expect(incoming({
      nodes: [at('p1', 'Producer'), at('p2', 'Rival'), at('c', 'Consumer')],
      edges: [wire('p1', 'c'), wire('p2', 'c')],
    })).toEqual([{ name: 'Result', type: 'String' }, { name: 'Count', type: 'Number' }])
  })

  it('offers the workflow parameters at the root entry node', () => {
    expect(incoming({
      nodes: [at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID), at('c', 'Consumer')],
      edges: [wire(WORKFLOW_INPUT_NODE_ID, 'c')],
      workflowInputParams: [{ name: 'Trigger', displayName: 'Trigger', type: 'Device', isRequired: true }],
    })).toEqual([{ name: 'Trigger', type: 'Device' }])
  })

  it('offers the branch inputs at a sub-canvas entry node, keeping their declared types', () => {
    const parent = at('b', 'Branch', { passthrough: ['Extra'] })
    expect(incoming({
      nodes: [at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID), at('c', 'Consumer')],
      edges: [wire(WORKFLOW_INPUT_NODE_ID, 'c')],
      canvasStack: [{ parentNodeId: 'b', subCanvasName: 'Body', nodes: [parent], edges: [] }],
    })).toEqual([{ name: 'Item', type: 'Device' }, { name: 'Extra', type: undefined }])
  })

  it('offers nothing at a sub-canvas entry whose parent block type is unknown', () => {
    const parent = at('b', 'Missing', { passthrough: ['Extra'] })
    expect(incoming({
      nodes: [at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID), at('c', 'Consumer')],
      edges: [wire(WORKFLOW_INPUT_NODE_ID, 'c')],
      canvasStack: [{ parentNodeId: 'b', subCanvasName: 'Body', nodes: [parent], edges: [] }],
    })).toEqual([])
  })

  it('names the wire each variable came in on, so a forwarded name can be mapped on it', () => {
    expect(offered({
      nodes: [at('p1', 'Producer'), at('p2', 'Rival'), at('c', 'Consumer')],
      edges: [wire('p1', 'c'), wire('p2', 'c')],
    }).map((v) => [v.name, v.edgeId])).toEqual([['Result', 'p1->c'], ['Count', 'p1->c']])
  })

  it('skips a source whose block type the catalogue does not carry', () => {
    expect(incoming({
      nodes: [at('p', 'Unknown', { passthrough: ['Carried'] }), at('c', 'Consumer')],
      edges: [wire('p', 'c')],
    })).toEqual([])
  })

  describe('a name only a passthrough entry gives', () => {
    const feed: WorkflowEdge = {
      id: 'p->f', sourceNodeId: 'p', targetNodeId: 'f',
      mappings: { Result: { type: 'direct', sourceOutputName: 'Result' } },
    }
    const nodes = [at('p', 'Producer'), at('f', 'Consumer', { passthrough: ['Result'] }), at('c', 'Consumer')]
    const edges = [feed, wire('f', 'c')]
    const given = (name: string, effective: string | null, state: DraftPortAnalysis['state'] = 'bound', declared: string | null = null): DraftPortAnalysis =>
      ({ name, declared, effective, state, unbound: [] })
    const answer = (nodeId: string, canvasPath: DraftAnalysis['nodes'][number]['canvasPath'], port: DraftPortAnalysis): DraftAnalysis => ({
      revision: 1, registryGeneration: 0, diagnostics: [], nodes: [{ canvasPath, nodeId, typeArguments: {}, inputs: [], outputs: [port] }],
    })

    it('has no type before an answer: the editor no longer traces the wire that feeds it', () => {
      expect(incoming({ nodes, edges })).toEqual([{ name: 'Result', type: undefined }])
    })

    it('has the term the server answers for that output of its source', () => {
      expect(incoming({ nodes, edges, analysis: answer('f', [], given('Result', 'Collection<Int>')) })).toEqual([{ name: 'Result', type: 'Collection<Int>' }])
    })

    it('flags the server term as stale when the answer is older than the draft, and only then', () => {
      const analysis = answer('f', [], given('Result', 'Collection<Int>'))
      expect(offered({ nodes, edges, analysis, freshness: 'stale' })[0]).toMatchObject({ name: 'Result', type: 'Collection<Int>', stale: true })
      expect(offered({ nodes, edges, analysis, freshness: 'current' })[0]?.stale).toBeUndefined()
    })

    it('has none where the server refused it', () => {
      expect(incoming({ nodes, edges, analysis: answer('f', [], given('Result', null, 'refused')) })).toEqual([{ name: 'Result', type: undefined }])
    })

    it('has the server term on the entry node of a sub-canvas, for a name the owner forwards', () => {
      const parent = at('b', 'Branch', { passthrough: ['Extra'] })
      const path = [{ nodeId: 'b', branchName: 'Body' }]
      expect(incoming({
        nodes: [at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID), at('c', 'Consumer')],
        edges: [wire(WORKFLOW_INPUT_NODE_ID, 'c')],
        canvasStack: [{ parentNodeId: 'b', subCanvasName: 'Body', nodes: [parent], edges: [] }],
        analysis: answer(WORKFLOW_INPUT_NODE_ID, path, given('Extra', 'String')),
      })).toEqual([{ name: 'Item', type: 'Device' }, { name: 'Extra', type: 'String' }])
    })

    it('does not read the server for a port that has a declared term', () => {
      expect(incoming({
        nodes: [at('p', 'Producer'), at('c', 'Consumer')], edges: [wire('p', 'c')],
        analysis: answer('p', [], given('Result', 'Int', 'bound', 'String')),
      })).toEqual([{ name: 'Result', type: 'String' }, { name: 'Count', type: 'Number' }])
    })
  })

  it.each(['$T', '$T?', 'Collection<$T?>'])('has no type for an output declared %s', (declared) => {
    const generic: BlockTypeDescriptor = {
      blockTypeId: 'Generic', displayName: 'Generic', inputs: [],
      outputs: [{ name: 'Echo', displayName: 'Echo', isRequired: false, type: declared }],
      typeParameters: [{ name: 'T' }],
    }
    const listed = incoming({
      nodes: [at('p', 'Generic'), at('c', 'Consumer')],
      edges: [wire('p', 'c')],
      blockTypes: [generic, consumer],
    })

    expect(listed).toEqual([{ name: 'Echo', type: undefined }])
    expect(listed[0]?.type).toBeUndefined()
  })
})
