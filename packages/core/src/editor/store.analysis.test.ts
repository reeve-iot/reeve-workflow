import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DraftAnalysis, DraftAnalysisSource } from '../analysis/draftAnalysis.js'
import { draftAnalysisFreshness } from '../analysis/draftAnalysis.js'
import type { WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import type { WorkflowEditorStore } from './store.js'
import { createWorkflowEditorStore } from './store.js'

const DELAY = 300
const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES])

const node = (id: string, x = 0): WorkflowNode => ({ id, blockTypeId: 'Widget', position: { x, y: 0 }, configuration: {} })
const answer = (revision: number, registryGeneration = 0): DraftAnalysis => ({ revision, registryGeneration, diagnostics: [], nodes: [] })

interface Call { revision: number; signal: AbortSignal; resolve: (value: DraftAnalysis) => void; reject: (error: unknown) => void }

function source(): { source: DraftAnalysisSource; calls: Call[] } {
  const calls: Call[] = []
  return {
    calls,
    source: {
      analyze: (_document, revision, signal) => new Promise<DraftAnalysis>((resolve, reject) => { calls.push({ revision, signal, resolve, reject }) }),
    },
  }
}

const settle = async (): Promise<void> => { await vi.advanceTimersByTimeAsync(0) }

beforeEach(() => { vi.useFakeTimers() })
afterEach(() => { vi.useRealTimers() })

function editor(extra: Partial<Parameters<typeof createWorkflowEditorStore>[0]> = {}): WorkflowEditorStore {
  return createWorkflowEditorStore({ portTypes, ...extra })
}

describe('the revision', () => {
  it('counts every edit that changes the document, and a load', () => {
    const store = editor()
    expect(store.getState().revision).toBe(0)
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    const loaded = store.getState().revision
    expect(loaded).toBeGreaterThan(0)
    store.getState().addNode('Widget', { x: 1, y: 1 })
    expect(store.getState().revision).toBe(loaded + 1)
    store.getState().updateNodeConfig('a', { Depot: 'WAW' })
    expect(store.getState().revision).toBe(loaded + 2)
  })

  it('does not move for a node being dragged, a selection, or going into a sub-canvas and back', () => {
    const store = editor()
    store.getState().loadWorkflow({
      nodes: [{ ...node('loop'), subCanvases: { Body: { nodes: [node('inner')], edges: [] } } }],
      edges: [],
    })
    const before = store.getState().revision
    store.getState().updateNodePosition('loop', { x: 400, y: 90 })
    store.getState().setSelectedNode('loop')
    store.getState().enterSubCanvas('loop', 'Body')
    store.getState().exitSubCanvas()
    expect(store.getState().revision).toBe(before)
  })

  it('does not write the document out while a node is only being dragged', () => {
    const store = editor()
    store.getState().loadWorkflow({ nodes: [node('a'), node('b')], edges: [] })
    const written = store.getState().getWorkflowDefinition
    const reads = vi.fn(written)
    store.setState({ getWorkflowDefinition: reads })

    for (let x = 1; x <= 30; x++) store.getState().updateNodePosition('a', { x, y: x })
    expect(reads).not.toHaveBeenCalled()

    store.getState().updateNodeConfig('a', { Depot: 'WAW' })
    expect(reads).toHaveBeenCalled()
  })

  it('counts a change that moves a node and edits its configuration at once', () => {
    const store = editor()
    store.getState().loadWorkflow({ nodes: [node('a'), node('b')], edges: [] })
    const before = store.getState().revision

    store.setState((state) => ({
      nodes: state.nodes.map((n) => (n.id === 'a' ? { ...n, position: { x: 40, y: 40 }, configuration: { Depot: 'WAW' } } : n)),
    }))

    expect(store.getState().revision).toBe(before + 1)
  })
})

describe('asking the server', () => {
  it('posts the draft once the edits have been quiet for the delay, at the revision then current', async () => {
    const { source: draftAnalysis, calls } = source()
    const store = editor({ draftAnalysis, draftAnalysisDelayMs: DELAY })
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    store.getState().addNode('Widget', { x: 1, y: 1 })
    await vi.advanceTimersByTimeAsync(DELAY - 1)
    expect(calls).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(1)
    expect(calls).toHaveLength(1)
    expect(calls[0]?.revision).toBe(store.getState().revision)
  })

  it('posts what a save would write, with the workflow parameters', async () => {
    const posted: unknown[] = []
    const store = editor({
      draftAnalysis: { analyze: (document) => { posted.push(document); return new Promise<DraftAnalysis>(() => undefined) } },
      draftAnalysisDelayMs: DELAY,
    })
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    store.getState().setWorkflowParams([{ name: 'p', displayName: 'p', type: 'String', isRequired: true }], [])
    await vi.advanceTimersByTimeAsync(DELAY)
    expect(posted).toHaveLength(1)
    expect(posted[0]).toMatchObject({
      definition: store.getState().getWorkflowDefinition(),
      inputParameters: [{ name: 'p', type: 'String' }],
      outputParameters: [],
    })
  })

  it('aborts a request an edit supersedes and applies only the answer for the revision on screen', async () => {
    const { source: draftAnalysis, calls } = source()
    const store = editor({ draftAnalysis, draftAnalysisDelayMs: DELAY })
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    await vi.advanceTimersByTimeAsync(DELAY)
    const first = calls[0]!
    store.getState().addNode('Widget', { x: 1, y: 1 })
    expect(first.signal.aborted).toBe(true)
    first.resolve(answer(first.revision))
    await settle()
    expect(store.getState().analysis).toBeNull()

    await vi.advanceTimersByTimeAsync(DELAY)
    const second = calls[1]!
    second.resolve(answer(second.revision))
    await settle()
    expect(store.getState().analysis?.revision).toBe(store.getState().revision)
  })

  it('does not apply an answer that names another revision than the one it was asked at', async () => {
    const { source: draftAnalysis, calls } = source()
    const store = editor({ draftAnalysis, draftAnalysisDelayMs: DELAY })
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    await vi.advanceTimersByTimeAsync(DELAY)
    calls[0]!.resolve(answer(calls[0]!.revision + 7))
    await settle()
    expect(store.getState().analysis).toBeNull()
  })

  it('asks nothing for a document that could not be read', async () => {
    const { source: draftAnalysis, calls } = source()
    const store = editor({ draftAnalysis, draftAnalysisDelayMs: DELAY })
    store.getState().loadWorkflow({ nodes: [{ ...node('a'), tags: ['x'] } as unknown as WorkflowNode], edges: [] })
    await vi.advanceTimersByTimeAsync(DELAY * 3)
    expect(store.getState().loadedDocumentFault).not.toBeNull()
    expect(calls).toHaveLength(0)
  })

  it('asks nothing when the host gives no source', async () => {
    const store = editor()
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    await vi.advanceTimersByTimeAsync(DELAY * 3)
    expect(store.getState().analysis).toBeNull()
  })
})

describe('the three states', () => {
  it('is none, then current for the answered revision, then stale after the next edit, and the answer is kept', async () => {
    const { source: draftAnalysis, calls } = source()
    const store = editor({ draftAnalysis, draftAnalysisDelayMs: DELAY })
    const freshness = () => draftAnalysisFreshness(store.getState().analysis, store.getState().revision)
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    expect(freshness()).toBe('none')

    await vi.advanceTimersByTimeAsync(DELAY)
    const given = answer(calls[0]!.revision)
    calls[0]!.resolve(given)
    await settle()
    expect(freshness()).toBe('current')

    store.getState().addNode('Widget', { x: 1, y: 1 })
    expect(freshness()).toBe('stale')
    expect(store.getState().analysis).toBe(given)
  })

  it('drops the answer for another document when a new one is loaded', async () => {
    const { source: draftAnalysis, calls } = source()
    const store = editor({ draftAnalysis, draftAnalysisDelayMs: DELAY })
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    await vi.advanceTimersByTimeAsync(DELAY)
    calls[0]!.resolve(answer(calls[0]!.revision))
    await settle()
    expect(store.getState().analysis).not.toBeNull()
    store.getState().loadWorkflow({ nodes: [node('b')], edges: [] })
    expect(store.getState().analysis).toBeNull()
  })

  it('keeps the last answer and reports a request that failed', async () => {
    const { source: draftAnalysis, calls } = source()
    const onDraftAnalysisError = vi.fn()
    const store = editor({ draftAnalysis, draftAnalysisDelayMs: DELAY, onDraftAnalysisError })
    store.getState().loadWorkflow({ nodes: [node('a')], edges: [] })
    await vi.advanceTimersByTimeAsync(DELAY)
    calls[0]!.resolve(answer(calls[0]!.revision))
    await settle()
    store.getState().addNode('Widget', { x: 1, y: 1 })
    await vi.advanceTimersByTimeAsync(DELAY)
    const failure = new Error('503')
    calls[1]!.reject(failure)
    await settle()
    expect(onDraftAnalysisError).toHaveBeenCalledWith(failure)
    expect(store.getState().analysisError).toBe(failure)
    expect(store.getState().analysis).not.toBeNull()
  })
})
