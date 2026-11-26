import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it, vi } from 'vitest'
import type { DraftAnalysisResponse, WorkflowEditorClient } from '@reeve-iot/workflow-client'
import type { DraftAnalysisDocument, SavedWorkflowDefinition } from '@reeve-iot/workflow-core'
import { createDraftAnalysisSource, readDraftAnalysis } from './draftAnalysis.js'

const response: DraftAnalysisResponse = {
  revision: 3,
  registryGeneration: 7,
  diagnostics: [{ blockSource: null, canvasPath: [], message: 'Nothing feeds Items', nodeId: 'a', portName: 'Items', ruleId: 'required-input-unfed' }],
  nodes: [{
    canvasPath: [{ nodeId: 'loop', branchName: 'Body' }],
    nodeId: 'a',
    typeArguments: { T: null },
    inputs: [{ name: 'Items', declared: 'Collection<$T>', effective: null, state: 'unbound', unbound: ['T'], expression: null, projection: null }],
    outputs: [{ name: 'Item', declared: '$T', effective: 'Int', state: 'bound', unbound: [], expression: 'Int', projection: 'String' }],
  }],
}

const at = { x: 0, y: 0 }
const definition: SavedWorkflowDefinition = {
  nodes: [{ id: 'loop', blockTypeId: 'ForLoop', position: at, subCanvases: { Body: { nodes: [{ id: 'a', blockTypeId: 'Filter', position: at }] } } }],
}

describe('readDraftAnalysis', () => {
  it('reads every member the server answers, and no expression or projection where it answers none', () => {
    const read = readDraftAnalysis(response, definition)
    expect(read.revision).toBe(3)
    expect(read.registryGeneration).toBe(7)
    expect(read.diagnostics).toEqual([{ ruleId: 'required-input-unfed', message: 'Nothing feeds Items', nodeId: 'a', portName: 'Items', canvasPath: [] }])
    const node = read.nodes[0]
    expect(node?.canvasPath).toEqual([{ nodeId: 'loop', branchName: 'Body' }])
    expect(node?.typeArguments).toEqual({ T: null })
    expect(node?.inputs[0]).toEqual({ name: 'Items', declared: 'Collection<$T>', effective: null, state: 'unbound', unbound: ['T'] })
    expect(node?.outputs[0]).toEqual({ name: 'Item', declared: '$T', effective: 'Int', state: 'bound', unbound: [], expression: 'Int', projection: 'String' })
  })

  it('refuses a state it does not know, naming the node and the port', () => {
    const bad = { ...response, nodes: [{ ...response.nodes[0]!, inputs: [{ ...response.nodes[0]!.inputs[0]!, state: 'guessed' }] }] }
    expect(() => readDraftAnalysis(bad, definition)).toThrow(/port state 'guessed' at node 'a' input\.Items/)
  })

  it('refuses a bound port that has no effective term', () => {
    const bad = { ...response, nodes: [{ ...response.nodes[0]!, inputs: [{ ...response.nodes[0]!.inputs[0]!, state: 'bound' }] }] }
    expect(() => readDraftAnalysis(bad, definition)).toThrow(/bound port with no effective term at node 'a' input\.Items/)
  })
})

describe('readDraftAnalysis — a port with no declared term', () => {
  const nameless = (name: string, state: string, effective: string | null) =>
    ({ name, declared: null, effective, state, unbound: [], expression: null, projection: null })
  const answer = (nodeId: string, canvasPath: DraftAnalysisResponse['nodes'][number]['canvasPath'], port: ReturnType<typeof nameless>): DraftAnalysisResponse => ({
    revision: 1, registryGeneration: 0, diagnostics: [],
    nodes: [{ canvasPath, nodeId, typeArguments: {}, inputs: [port], outputs: [port] }],
  })
  const passing = (passthrough: string[]): SavedWorkflowDefinition => ({
    nodes: [{
      id: 'it', blockTypeId: 'IterateOver', position: at, passthrough,
      subCanvases: { Body: { nodes: [{ id: 'entry', blockTypeId: 'WorkflowInput', position: at }, { id: 'inner', blockTypeId: 'Filter', position: at, passthrough: ['Tag'] }] } },
    }],
  })

  it('keeps null for a name the node lists in passthrough, as an input and as an output', () => {
    const read = readDraftAnalysis(answer('it', [], nameless('Title', 'bound', 'String')), passing(['Title']))
    expect(read.nodes[0]?.inputs[0]).toEqual({ name: 'Title', declared: null, effective: 'String', state: 'bound', unbound: [] })
    expect(read.nodes[0]?.outputs[0]?.declared).toBeNull()
  })

  it('keeps null on the entry node of a sub-canvas for a name its owner lists, and on a node of that canvas for its own', () => {
    const body = [{ nodeId: 'it', branchName: 'Body' }]
    expect(readDraftAnalysis(answer('entry', body, nameless('Title', 'bound', 'String')), passing(['Title'])).nodes[0]?.outputs[0]?.declared).toBeNull()
    expect(readDraftAnalysis(answer('inner', body, nameless('Tag', 'refused', null)), passing([])).nodes[0]?.inputs[0]?.state).toBe('refused')
  })

  it('refuses null for a name no passthrough entry of that node lists, naming the place', () => {
    expect(() => readDraftAnalysis(answer('it', [], nameless('Other', 'bound', 'String')), passing(['Title'])))
      .toThrow(/no declared term for node 'it' input\.Other, which no passthrough entry names/)
    expect(() => readDraftAnalysis(answer('entry', [{ nodeId: 'it', branchName: 'Body' }], nameless('Tag', 'bound', 'String')), passing(['Title'])))
      .toThrow(/no declared term for node 'entry' input\.Tag/)
    expect(() => readDraftAnalysis(answer('gone', [], nameless('Title', 'bound', 'String')), passing(['Title']))).toThrow(/node 'gone' input\.Title/)
  })

  it('refuses an unbound port with no declared term, which names no parameter', () => {
    expect(() => readDraftAnalysis(answer('it', [], nameless('Title', 'unbound', null)), passing(['Title'])))
      .toThrow(/unbound port with no declared term at node 'it' input\.Title/)
  })
})

describe('createDraftAnalysisSource', () => {
  const document: DraftAnalysisDocument = {
    definition: { nodes: [], edges: [] },
    inputParameters: [{ name: 'p', displayName: 'p', type: 'String', isRequired: true }],
    outputParameters: [],
  }

  it('posts the draft as the update body writes it, with the revision, the open workflow and the abort signal', async () => {
    const analyzeDraft = vi.fn().mockResolvedValue(response)
    const source = createDraftAnalysisSource({ analyzeDraft } as unknown as Pick<WorkflowEditorClient, 'analyzeDraft'>, { workflowId: () => 'w-1' })
    const controller = new AbortController()
    const read = await source.analyze(document, 3, controller.signal)
    expect(analyzeDraft).toHaveBeenCalledWith({
      body: {
        workflowId: 'w-1',
        revision: 3,
        definitionJson: '{"nodes":[],"edges":[]}',
        inputParametersJson: '[{"name":"p","displayName":"p","type":"String","isRequired":true}]',
        outputParametersJson: '[]',
      },
    }, { signal: controller.signal })
    expect(read.revision).toBe(3)
  })

  it('posts a null workflow for one not yet created', async () => {
    const analyzeDraft = vi.fn().mockResolvedValue(response)
    await createDraftAnalysisSource({ analyzeDraft } as unknown as Pick<WorkflowEditorClient, 'analyzeDraft'>)
      .analyze(document, 1, new AbortController().signal)
    expect(analyzeDraft.mock.calls[0]?.[0].body.workflowId).toBeNull()
  })
})

interface ContractCase { name: string; request: { definition: SavedWorkflowDefinition }; response: DraftAnalysisResponse }

const contract = JSON.parse(readFileSync(resolve(__dirname, '../../../contracts/draft-analysis.json'), 'utf8')) as { cases: ContractCase[] }

describe('contracts/draft-analysis.json', () => {
  it.each(contract.cases.map((c) => [c.name, c] as const))('%s: the response reads, and keeps every port as the server wrote it', (_name, row) => {
    const read = readDraftAnalysis(row.response, row.request.definition)
    expect(read.revision).toBe(row.response.revision)
    expect(read.registryGeneration).toBe(row.response.registryGeneration)
    expect(read.diagnostics.map((d) => [d.ruleId, d.nodeId, d.portName])).toEqual(row.response.diagnostics.map((d) => [d.ruleId, d.nodeId, d.portName]))
    expect(read.nodes.map((n) => [n.nodeId, n.canvasPath, n.typeArguments])).toEqual(row.response.nodes.map((n) => [n.nodeId, n.canvasPath, n.typeArguments]))
    const ports = (nodes: DraftAnalysisResponse['nodes']) => nodes.flatMap((n) => [...n.inputs, ...n.outputs]).map((p) => ({
      name: p.name, declared: p.declared, effective: p.effective, state: p.state, unbound: p.unbound, expression: p.expression ?? undefined, projection: p.projection ?? undefined,
    }))
    expect(ports(read.nodes as unknown as DraftAnalysisResponse['nodes'])).toEqual(ports(row.response.nodes))
  })

  it('holds a case of every port state, so the reader is graded on each', () => {
    const states = new Set(contract.cases.flatMap((c) => c.response.nodes.flatMap((n) => [...n.inputs, ...n.outputs].map((p) => p.state))))
    expect([...states].sort()).toEqual(['bound', 'refused', 'unbound'])
  })

  it('holds a bound and a refused port with no declared term, so the reader is graded on both', () => {
    const nameless = contract.cases.flatMap((c) => c.response.nodes.flatMap((n) => [...n.inputs, ...n.outputs].filter((p) => p.declared === null)))
    expect(new Set(nameless.map((p) => p.state))).toEqual(new Set(['bound', 'refused']))
  })
})
