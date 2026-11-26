import { renderHook, waitFor } from '@testing-library/react'
import { createElement } from 'react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkflowClient, WorkflowResponse } from '@reeve-iot/workflow-client'
import { BUILTIN_PORT_TYPES, PortTypeRegistryContext, createPortTypeRegistry, createWorkflowEditorStore } from '@reeve-iot/workflow-core'
import { createWorkflowDocumentSource, readWorkflowDefinition, readWorkflowDocument } from './workflowDocument.js'

const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES])

const wrapper = ({ children }: { children: ReactNode }) =>
  createElement(PortTypeRegistryContext.Provider, { value: portTypes }, children)

const GRAPH = JSON.stringify({
  nodes: [{ id: 'n1', blockTypeId: 'Delay', position: { x: 0, y: 0 }, configuration: {} }],
  edges: [{ id: 'e1', sourceNodeId: 'n1', targetNodeId: 'n1', mappings: {} }],
})

function response(overrides: Partial<WorkflowResponse> = {}): WorkflowResponse {
  return {
    id: 'w1',
    name: 'Evening lights',
    definitionJson: GRAPH,
    inputParametersJson: JSON.stringify([{ name: 'Room', type: 'string' }]),
    outputParametersJson: JSON.stringify([{ name: 'Applied', type: 'boolean' }]),
    ...overrides,
  } as WorkflowResponse
}

describe('readWorkflowDefinition', () => {
  it('reads the nodes and edges of a stored graph', () => {
    const read = readWorkflowDefinition(GRAPH)

    expect(read.ok).toBe(true)
    if (!read.ok) return
    expect(read.definition.nodes).toHaveLength(1)
    expect(read.definition.edges).toHaveLength(1)
  })

  it.each([
    ['{not json', 'L1', ''],
    ['null', 'L7', ''],
    ['[]', 'L7', ''],
    ['{"nodes":5}', 'L7', '/nodes'],
    ['{"edges":"no"}', 'L7', '/edges'],
    ['{"Nodes":[{"Id":"n1"}]}', 'L5', '/Nodes'],
    ['{"nodes":[],"connections":[]}', 'stored-key-unknown', '/connections'],
    ['{"nodes":[],"nodes":[{"id":"x"}]}', 'L3', '/nodes'],
  ])('refuses %s under %s at %j, never reading it as a canvas', (json, rule, location) => {
    expect(readWorkflowDefinition(json)).toMatchObject({ ok: false, fault: { rule, location } })
  })

  it('refuses a graph when the required edges field is absent', () => {
    expect(readWorkflowDefinition('{"nodes":[{"id":"n1","position":{"x":0,"y":0},"configuration":{}}]}'))
      .toMatchObject({ ok: false, fault: { rule: 'L7', location: '/edges' } })
  })
})

describe('readWorkflowDocument', () => {
  it('keeps the two parameter lists on the sides they were declared', () => {
    const document = readWorkflowDocument(response(), portTypes)

    expect(document.inputParams.map((p) => p.name)).toEqual(['Room'])
    expect(document.outputParams.map((p) => p.name)).toEqual(['Applied'])
  })

  it("makes an unreadable parameter list the document's fault, and reads its parameters as none", () => {
    const document = readWorkflowDocument(response({ outputParametersJson: '[{"name":""}]' }), portTypes)

    expect(document.definition).toMatchObject({
      ok: false,
      fault: { rule: 'L7', location: '/0/name', detail: 'the declared output parameters: an empty string where a name is required' },
    })
    expect(document.outputParams).toEqual([])
  })

  it('answers the first fault of the input list, the output list and the graph, in that order', () => {
    const inputs = readWorkflowDocument(response({ inputParametersJson: '[5]', outputParametersJson: '[6]', definitionJson: '[]' }), portTypes)
    const outputs = readWorkflowDocument(response({ outputParametersJson: '[6]', definitionJson: '[]' }), portTypes)

    expect(inputs.definition).toMatchObject({ ok: false, fault: { rule: 'L7', location: '/0', detail: expect.stringMatching(/^the declared input parameters: /) as unknown } })
    expect(outputs.definition).toMatchObject({ ok: false, fault: { rule: 'L7', location: '/0', detail: expect.stringMatching(/^the declared output parameters: /) as unknown } })
  })

  it('normalises parameter types through the registry', () => {
    const document = readWorkflowDocument(response(), portTypes)

    expect(document.inputParams[0]?.type).toBe('String')
    expect(document.outputParams[0]?.type).toBe('Boolean')
  })
})

describe('createWorkflowDocumentSource', () => {
  it('answers null for no workflow, without asking the server', () => {
    const getWorkflow = vi.fn()
    const source = createWorkflowDocumentSource({ getWorkflow } as unknown as WorkflowClient)

    const { result } = renderHook(() => source.useWorkflow(null), { wrapper })

    expect(result.current).toBeNull()
    expect(getWorkflow).not.toHaveBeenCalled()
  })

  it('loads the workflow and holds it stable across renders', async () => {
    const source = createWorkflowDocumentSource({
      getWorkflow: async () => response(),
    } as unknown as WorkflowClient)

    const { result, rerender } = renderHook(() => source.useWorkflow('w1'), { wrapper })
    await waitFor(() => { expect(result.current).not.toBeNull() })
    const first = result.current
    rerender()

    expect(result.current?.name).toBe('Evening lights')
    expect(result.current).toBe(first)
  })

  it('loads again after refresh, which is how a save becomes visible', async () => {
    let name = 'Before'
    const getWorkflow = vi.fn(async () => response({ name }))
    const source = createWorkflowDocumentSource({ getWorkflow } as unknown as WorkflowClient)

    const { result } = renderHook(() => source.useWorkflow('w1'), { wrapper })
    await waitFor(() => { expect(result.current?.name).toBe('Before') })
    name = 'After'
    source.refresh()

    await waitFor(() => { expect(result.current?.name).toBe('After') })
  })

  it('reports a failure and answers null rather than throwing into render', async () => {
    const onError = vi.fn()
    const source = createWorkflowDocumentSource(
      { getWorkflow: async () => { throw new Error('404') } } as unknown as WorkflowClient,
      { onError },
    )

    const { result } = renderHook(() => source.useWorkflow('gone'), { wrapper })
    await waitFor(() => { expect(onError).toHaveBeenCalledWith('gone', expect.any(Error)) })

    expect(result.current).toBeNull()
  })
})

describe('createWorkflowDocumentSource on a workflow with a draft', () => {
  const DRAFT_GRAPH = JSON.stringify({
    nodes: [
      { id: 'n1', blockTypeId: 'Delay', position: { x: 5, y: 5 }, configuration: { Seconds: 30 } },
      { id: 'out', blockTypeId: 'WorkflowOutput', position: { x: 0, y: 0 }, configuration: {} },
    ],
    edges: [{ id: 'e1', sourceNodeId: 'n1', targetNodeId: 'out', mappings: { Done: { type: 'direct', sourceOutputName: 'Done' } } }],
  })
  const draft = {
    id: 'v2', workflowId: 'w1', versionNumber: 2, isPublished: false, definitionJson: DRAFT_GRAPH,
    inputParametersJson: JSON.stringify([{ name: 'Zone', type: 'string' }]), outputParametersJson: null,
  }
  const withDraft = (documentsFrom: string | null) =>
    response({ draftVersionId: 'v2', draftVersionNumber: 2, currentVersionId: 'v1', currentVersionNumber: 1, documentsFrom })

  it.each([['Current'], [null]])("loads the draft's documents when the response's are %s, so a save writes the draft back", async (from) => {
    const getVersion = vi.fn(async () => draft)
    const source = createWorkflowDocumentSource({ getWorkflow: async () => withDraft(from), getVersion } as unknown as WorkflowClient)

    const { result } = renderHook(() => source.useWorkflow('w1'), { wrapper })
    await waitFor(() => { expect(result.current).not.toBeNull() })
    const store = createWorkflowEditorStore({ portTypes })
    store.getState().setWorkflowParams(result.current?.inputParams ?? [], result.current?.outputParams ?? [])
    const definition = result.current?.definition
    if (definition) store.getState().loadStoredWorkflow(definition)

    expect(getVersion).toHaveBeenCalledWith({ id: 'w1', versionNumber: 2 })
    expect(result.current?.name).toBe('Evening lights')
    expect(result.current?.inputParams.map((p) => p.name)).toEqual(['Zone'])
    expect(result.current?.outputParams).toEqual([])
    const saved = readWorkflowDefinition(JSON.stringify(store.getState().getWorkflowDefinition()))
    const expected = readWorkflowDefinition(DRAFT_GRAPH)
    if (!saved.ok || !expected.ok) throw new Error('unreadable')
    expect(saved.definition.nodes.filter((n) => n.id === 'n1')).toEqual(expected.definition.nodes.filter((n) => n.id === 'n1'))
    expect(saved.definition.edges).toEqual(expected.definition.edges)
  })

  it('reads the response as it is when its documents are the draft already, or there is no draft', async () => {
    const getVersion = vi.fn(async () => draft)
    for (const answer of [withDraft('Draft'), response({ draftVersionId: null, documentsFrom: 'Current' })]) {
      const source = createWorkflowDocumentSource({ getWorkflow: async () => answer, getVersion } as unknown as WorkflowClient)
      const { result } = renderHook(() => source.useWorkflow('w1'), { wrapper })
      await waitFor(() => { expect(result.current).not.toBeNull() })
      expect(result.current?.inputParams.map((p) => p.name)).toEqual(['Room'])
    }
    expect(getVersion).not.toHaveBeenCalled()
  })

  it('reports a draft it cannot reach and answers null, never the current documents in its place', async () => {
    const onError = vi.fn()
    const unnumbered = response({ draftVersionId: 'v2', draftVersionNumber: null, documentsFrom: 'Current' })
    const failing = createWorkflowDocumentSource({ getWorkflow: async () => withDraft('Current'), getVersion: async () => { throw new Error('503') } } as unknown as WorkflowClient, { onError })
    const noNumber = createWorkflowDocumentSource({ getWorkflow: async () => unnumbered, getVersion: vi.fn() } as unknown as WorkflowClient, { onError })

    const one = renderHook(() => failing.useWorkflow('w1'), { wrapper })
    const two = renderHook(() => noNumber.useWorkflow('w1'), { wrapper })
    await waitFor(() => { expect(onError).toHaveBeenCalledTimes(2) })

    expect(onError.mock.calls.map((c) => String(c[1]))).toEqual(expect.arrayContaining(['Error: 503', expect.stringMatching(/draft/) as unknown]))
    expect([one.result.current, two.result.current]).toEqual([null, null])
  })
})
