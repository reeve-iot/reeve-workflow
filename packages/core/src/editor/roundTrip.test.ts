import { describe, expect, it } from 'vitest'
import { readStoredDefinitionText } from '../model/storedDocument.js'
import type { SavedWorkflowDefinition, WorkflowDefinition } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { GRAMMAR_BLOCK_TYPES, generateDocuments } from './documentGrammar.testkit.js'
import { WorkflowDocumentUnreadableError, createWorkflowEditorStore } from './store.js'

const portTypes = createPortTypeRegistry(BUILTIN_PORT_TYPES)
const machineSpeedTestsEnabled = process.env['REEVE_RUN_MACHINE_SPEED_TESTS'] === '1'
const IN = '__workflow_input__'
const placed = (id: string, extra: Record<string, unknown> = {}) => ({ id, blockTypeId: 'Widget', position: { x: 1, y: 2 }, configuration: {}, ...extra })

function open(text: string) {
  const store = createWorkflowEditorStore({ portTypes })
  store.getState().setBlockTypes(GRAMMAR_BLOCK_TYPES)
  const parsed: unknown = JSON.parse(text)
  const complete = (value: unknown): void => {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return
    const canvas = value as Record<string, unknown>
    if (!Object.hasOwn(canvas, 'edges')) canvas['edges'] = []
    const nodes = canvas['nodes']
    if (Array.isArray(nodes)) for (const node of nodes) {
      if (!node || typeof node !== 'object' || Array.isArray(node)) continue
      const branches = (node as Record<string, unknown>)['subCanvases']
      if (branches && typeof branches === 'object' && !Array.isArray(branches)) Object.values(branches).forEach(complete)
    }
  }
  complete(parsed)
  const read = readStoredDefinitionText(JSON.stringify(parsed))
  store.getState().loadStoredWorkflow(read)
  return { store, read }
}

function reread(store: ReturnType<typeof open>['store']): WorkflowDefinition {
  const again = readStoredDefinitionText(JSON.stringify(store.getState().getWorkflowDefinition()))
  if (!again.ok) throw new Error(`the save is refused: ${again.fault.detail}`)
  return again.definition
}

const isPlain = (v: unknown): boolean => typeof v === 'object' && v !== null && !Array.isArray(v)
function heldInEditorForm(canvas: Pick<WorkflowDefinition, 'nodes' | 'edges'>): boolean {
  return canvas.nodes.every((n) => isPlain(n.configuration) && Object.values(n.subCanvases ?? {}).every(heldInEditorForm)) &&
    canvas.edges.every((e) => isPlain(e.mappings))
}

function modelOf(text: string): WorkflowDefinition {
  const read = readStoredDefinitionText(text)
  if (!read.ok) throw new Error(read.fault.detail)
  return read.definition
}

describe('B-m2: the reader refuses nodes without a position', () => {
  it.each([
    ['no position', { nodes: [{ id: 'a', blockTypeId: 'Widget', configuration: {} }] }, '/nodes/0/position'],
    ['no x', { nodes: [placed('a'), { id: 'b', blockTypeId: 'Widget', position: { y: 1 }, configuration: {} }] }, '/nodes/1/position/x'],
    ['no y', { nodes: [{ id: 'a', blockTypeId: 'Widget', position: { x: 1 }, configuration: {} }] }, '/nodes/0/position/y'],
    ['no position in a branch', {
      nodes: [placed('loop', { blockTypeId: 'Loop', subCanvases: { Body: { nodes: [{ id: 'deep', blockTypeId: 'Widget', configuration: {} }], edges: [] } } })],
      edges: [],
    }, '/nodes/0/subCanvases/Body/nodes/0/position'],
  ])('%s', (_name, document, location) => {
    const text = JSON.stringify(document)
    const { store, read } = open(text)
    expect(read).toMatchObject({ ok: false, fault: { rule: 'L7', location } })
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'L7', location })
    expect(store.getState().validateWorkflowFindings()).toMatchObject([{ ruleId: 'definition-unreadable' }])
    expect(() => store.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
  })
})

describe('B-m3: an unlisted key makes the stored document unreadable', () => {
  it.each([
    ['canvas', { nodes: [], zRoot: 1 }, '/zRoot'],
    ['nested canvas', { nodes: [placed('loop', { blockTypeId: 'Loop', subCanvases: { Body: { nodes: [], zBranch: 7 } } })] }, '/nodes/0/subCanvases/Body/zBranch'],
  ])('%s key is refused and the editor cannot save it', (_name, document, location) => {
    const { store, read } = open(JSON.stringify(document))
    expect(read).toMatchObject({ ok: false, fault: { rule: 'stored-key-unknown', location } })
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'stored-key-unknown', location })
    expect(store.getState().validateWorkflowFindings()).toMatchObject([{ code: 'stored-key-unknown', ruleId: 'stored-key-unknown' }])
    expect(() => store.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
  })
})

describe('an empty passthrough is absent when the edited document is written', () => {
  const passthroughNode = (passthrough: string[]) => placed('a', { passthrough })
  const savedNode = (initial: string[], next: string[]) => {
    const { store } = open(JSON.stringify({ nodes: [passthroughNode(initial)], edges: [] }))
    store.getState().updateNodePassthrough('a', next)
    const node = store.getState().getWorkflowDefinition().nodes?.find((candidate) => candidate.id === 'a')
    if (!node) throw new Error('The saved document is missing node a')
    return node
  }

  it('removes an emptied passthrough and keeps a non-empty one', () => {
    const empty = savedNode(['Carry'], [])
    expect(Object.hasOwn(empty, 'passthrough')).toBe(false)

    expect(savedNode([], ['Carry'])['passthrough']).toEqual(['Carry'])
  })
})

describe('E19: the object form is opened and drawn as a run reads it; the array form is refused', () => {
  const wired = (mappings: unknown) => JSON.stringify({ nodes: [placed(IN, { blockTypeId: 'WorkflowInput' }), placed('a'), placed('b')], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings }] })

  it('refuses the array form holding a mapping at the reader', () => {
    const fault = open(wired([{ sourceOutput: 'Out', targetInput: 'In' }])).store.getState().loadedDocumentFault

    expect(fault).toMatchObject({ rule: 'L7', location: '/edges/0/mappings', nodeId: null })
  })

  it.each([
    ['direct', { In: { type: 'direct', sourceOutputName: 'Out' } }],
    ['literal', { In: { type: 'literal', literalValue: { a: [1] } } }],
    ['an empty object', {}],
  ])('opens %s and writes it back as it was', (_name, mappings) => {
    const text = wired(mappings)
    const { store } = open(text)

    expect(store.getState().loadedDocumentFault).toBeNull()
    expect(reread(store)).toEqual(modelOf(text))
  })
})

const ENTRY = { id: IN, blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} }
const EXIT = { id: '__workflow_output__', blockTypeId: 'WorkflowOutput', position: { x: 800, y: 100 }, configuration: {} }
function freeId(nodes: readonly unknown[], reserved: string): string {
  const ids = new Set(nodes.map((n) => (n as { id?: unknown }).id))
  let id = reserved
  for (let i = 2; ids.has(id); i++) id = `${reserved}${String(i)}`
  return id
}
const holds = (nodes: readonly unknown[], blockTypeId: string) => nodes.some((n) => (n as { blockTypeId?: unknown }).blockTypeId === blockTypeId)

function withBoundaryNodes(model: WorkflowDefinition, exit: boolean): WorkflowDefinition {
  const nodes: unknown[] = Array.isArray(model.nodes) ? model.nodes : []
  const entry = holds(nodes, 'WorkflowInput') ? [] : [{ ...ENTRY, id: freeId(nodes, IN) }]
  const withEntry = [...entry, ...nodes]
  const added = [...withEntry, ...(exit && !holds(nodes, 'WorkflowOutput') ? [{ ...EXIT, id: freeId(withEntry, EXIT.id) }] : [])]
  return added.length === nodes.length ? model : { ...model, nodes: added as WorkflowDefinition['nodes'] }
}

function withBodiesOpened(model: WorkflowDefinition): WorkflowDefinition {
  const nodes = model.nodes.map((n) => {
    const body = n.blockTypeId === 'Loop' && typeof n.id === 'string' ? n.subCanvases?.['Body'] : undefined
    if (!body || typeof body !== 'object') return n
    const inner: unknown[] = Array.isArray(body.nodes) ? body.nodes : []
    const withEntry = holds(inner, 'WorkflowInput') ? inner : [{ ...ENTRY, id: freeId(inner, IN) }, ...inner]
    const exit = (n.outputPorts ?? []).some((p) => p.sources && Object.hasOwn(p.sources, 'Body') && !!p.sources['Body'])
    const withExit = exit && !holds(inner, 'WorkflowOutput') ? [...withEntry, { ...EXIT, id: freeId(withEntry, EXIT.id) }] : withEntry
    if (withExit === inner) return n
    return { ...n, subCanvases: { ...n.subCanvases, Body: { ...body, nodes: withExit as WorkflowDefinition['nodes'] } } }
  })
  return { ...model, nodes }
}

describe('the reader refuses a missing or null member', () => {
  it.each([
    ['no configuration', { nodes: [{ id: 'a', blockTypeId: 'Widget', position: { x: 0, y: 0 } }], edges: [] }, '/nodes/0/configuration'],
    ['a null configuration', { nodes: [placed('a', { configuration: null })], edges: [] }, '/nodes/0/configuration'],
    ['no nodes and no edges', {}, '/nodes'],
    ['null nodes and edges', { nodes: null, edges: null }, '/nodes'],
    ['a branch with no canvas members', { nodes: [placed('l', { blockTypeId: 'Loop', subCanvases: { Body: {} } })], edges: [] }, '/nodes/0/subCanvases/Body/nodes'],
    ['a branch with null nodes', { nodes: [placed('l', { blockTypeId: 'Loop', subCanvases: { Body: { nodes: null, edges: [] } } })], edges: [] }, '/nodes/0/subCanvases/Body/nodes'],
    ['null sub-canvases', { nodes: [placed('l', { blockTypeId: 'Loop', subCanvases: null })], edges: [] }, '/nodes/0/subCanvases'],
  ])('%s', (_name, document, location) => {
    const { read, store } = open(JSON.stringify(document))
    expect(read).toMatchObject({ ok: false, fault: { rule: 'L7', location } })
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'L7', location })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
  })

  it('refuses an absent edge mapping at the reader', () => {
    const text = JSON.stringify({
      nodes: [placed('l', { blockTypeId: 'Loop', subCanvases: { Body: { nodes: [placed('a'), placed('b')], edges: [{ id: 'x', sourceNodeId: 'a', targetNodeId: 'b' }] } } })],
      edges: [],
    })
    const { read, store } = open(text)
    expect(read).toMatchObject({ ok: false, fault: { rule: 'L7', location: '/nodes/0/subCanvases/Body/edges/0/mappings' } })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
  })

  it('edits a readable configuration and mapping set', () => {
    const { store } = open(JSON.stringify({ nodes: [placed('a'), placed('b')], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: {} }] }))
    store.getState().patchNodeConfig('a', { Mode: 'x' })
    store.getState().updateEdgeMappings('e', { In: { type: 'direct', sourceOutputName: 'Out' } })
    const saved = reread(store)
    expect(saved.nodes.find((n) => n.id === 'a')?.configuration).toEqual({ Mode: 'x' })
    expect(saved.edges[0]?.mappings).toEqual({ In: { type: 'direct', sourceOutputName: 'Out' } })
  })
})

describe('generated from the reader\'s grammar: a load then a save without an edit reads back to the same model, or the load refuses', () => {
  const documents = generateDocuments(21_005, 3000)

  it('covers each outcome', () => {
    const outcomes = { refused: 0, opened: 0, nested: 0 }
    for (const d of documents) {
      const read = readStoredDefinitionText(d.text)
      if (!read.ok) {
        expect(['duplicate-node-id', 'stored-key-unknown', 'L7'], d.text).toContain(read.fault.rule)
        outcomes.refused++
      } else if (d.undrawable.length > 0) outcomes.refused++
      else {
        outcomes.opened++
        if (read.definition.nodes?.some((n) => Object.values(n.subCanvases ?? {}).some((c) => (c?.nodes ?? []).length > 0))) outcomes.nested++
      }
    }
    expect(outcomes.refused).toBeGreaterThan(300)
    expect(outcomes.opened).toBeGreaterThan(1500)
    expect(outcomes.nested).toBeGreaterThan(150)
  })

  it.skipIf(!machineSpeedTestsEnabled).each([['parameters, then the load', true], ['the load, then parameters', false]])('%s', (_name, paramsFirst) => {
    for (const d of documents) {
      const store = createWorkflowEditorStore({ portTypes })
      store.getState().setBlockTypes(GRAMMAR_BLOCK_TYPES)
      const read = readStoredDefinitionText(d.text)
      if (paramsFirst) store.getState().setWorkflowParams(d.inputParams, d.outputParams)
      store.getState().loadStoredWorkflow(read)
      if (!paramsFirst) store.getState().setWorkflowParams(d.inputParams, d.outputParams)
      store.getState().syncSpecialNodes()
      const fault = store.getState().loadedDocumentFault

      if (!read.ok) {
        expect(fault, d.text).toMatchObject({ rule: read.fault.rule, nodeId: read.fault.nodeId, location: read.fault.location })
        const findingRule = read.fault.rule === 'duplicate-node-id' || read.fault.rule === 'stored-key-unknown' || read.fault.rule === 'error-policy-route-unread'
          ? read.fault.rule : 'definition-unreadable'
        expect(store.getState().validateWorkflowFindings(), d.text).toMatchObject([{ code: findingRule, ruleId: findingRule, nodeId: read.fault.nodeId }])
        expect(() => store.getState().getWorkflowDefinition(), d.text).toThrow(WorkflowDocumentUnreadableError)
        continue
      }

      if (d.undrawable.length > 0) {
        expect(fault?.rule, d.text).toBe('undrawable')
        expect(d.undrawable, d.text).toContain(fault?.location)
        const [finding, ...more] = store.getState().validateWorkflowFindings()
        expect(more, d.text).toEqual([])
        expect(finding, d.text).toEqual({
          code: 'definition-undrawable', ruleId: 'definition-undrawable', nodeId: fault?.nodeId, message: expect.stringMatching(/^The editor cannot draw this workflow: /) as unknown,
          params: { fault: expect.any(String) },
          ...(fault && 'edgeId' in fault && fault.edgeId !== null ? { edgeId: fault.edgeId } : {}),
        })
        expect(() => store.getState().getWorkflowDefinition(), d.text).toThrow(/^The workflow definition cannot be drawn by the editor, so it cannot be saved: /)
        continue
      }
      expect(fault, d.text).toBeNull()
      expect(heldInEditorForm(store.getState()), d.text).toBe(true)
      expect(() => store.getState().validateWorkflowFindings(), d.text).not.toThrow()
      const opened = withBoundaryNodes(modelOf(d.text), d.outputParams.length > 0)
      expect(reread(store), d.text).toEqual(opened)
      for (const n of store.getState().nodes.filter((m) => m.blockTypeId === 'Loop' && typeof m.id === 'string' && typeof m.subCanvases?.['Body'] === 'object')) {
        store.getState().enterSubCanvas(n.id, 'Body')
        store.getState().exitToLevel(0)
      }
      expect(reread(store), `${d.text}\nafter opening and leaving each Body`).toEqual(withBodiesOpened(opened))
    }
  })
})

describe('S1: a canvas\'s entry and exit are its nodes of those block types, whatever their ids', () => {
  const param = (name: string) => ({ name, displayName: name, type: 'String', isRequired: false })
  const reeve = (text: string, outputs: string[]) => {
    const { store } = open(text)
    store.getState().setWorkflowParams([param('Room')], outputs.map(param))
    return store
  }
  const blueprint = { nodes: [placed('start', { blockTypeId: 'WorkflowInput' }), placed('a')], edges: [{ id: 'e', sourceNodeId: 'start', targetNodeId: 'a', mappings: {} }] }

  it('opens a blueprint whose entry is `start` with no second entry and no finding, and saves it as loaded', () => {
    const store = reeve(JSON.stringify(blueprint), [])
    expect(store.getState().nodes.map((n) => n.id)).toEqual(['start', 'a'])
    expect(store.getState().validateStructureFindings()).toEqual([])
    expect(reread(store)).toEqual(modelOf(JSON.stringify(blueprint)))
  })

  it('keeps a seed\'s exit `wo`, declared or not, and adds none beside it', () => {
    const seed = { ...blueprint, nodes: [...blueprint.nodes, placed('wo', { blockTypeId: 'WorkflowOutput' })], edges: [...blueprint.edges, { id: 'o', sourceNodeId: 'a', targetNodeId: 'wo', mappings: {} }] }
    for (const outputs of [['Result'], []]) expect(reread(reeve(JSON.stringify(seed), outputs)), String(outputs)).toEqual(modelOf(JSON.stringify(seed)))
  })

  it('adds nothing to a branch whose exit has another id, and keeps it when nothing calls for it', () => {
    const exitIn = (declared: boolean) => JSON.stringify({ ...blueprint, nodes: [...blueprint.nodes, placed('l', {
      blockTypeId: 'Loop', outputPorts: declared ? [{ name: 'R', displayName: 'R', sources: { Body: 'Out' } }] : [],
      subCanvases: { Body: { nodes: [placed('b_in', { blockTypeId: 'WorkflowInput' }), placed('t_out', { blockTypeId: 'WorkflowOutput' })], edges: [] } },
    })] })
    for (const declared of [true, false]) {
      const store = reeve(exitIn(declared), [])
      store.getState().enterSubCanvas('l', 'Body')
      expect(store.getState().nodes.map((n) => n.id), String(declared)).toEqual(['b_in', 't_out'])
      store.getState().exitToLevel(0)
      expect(reread(store), String(declared)).toEqual(modelOf(exitIn(declared)))
    }
  })

  it('removes an exit node it added once the outputs are no longer declared, with its wires', () => {
    const store = reeve(JSON.stringify(blueprint), ['Result'])
    store.getState().addEdge('a', '__workflow_output__')
    store.getState().setWorkflowParams([param('Room')], [])
    expect(store.getState().nodes.map((n) => n.id)).toEqual(['start', 'a'])
    expect(store.getState().edges.map((e) => e.targetNodeId)).toEqual(['a'])
  })

  it('adds an entry under the next free id where a node of another block type holds the reserved one', () => {
    const store = reeve(JSON.stringify({ nodes: [placed(IN)] }), [])
    expect(store.getState().nodes.map((n) => [n.id, n.blockTypeId])).toEqual([[`${IN}2`, 'WorkflowInput'], [IN, 'Widget']])
  })

  it('forgets on each load the nodes it added: switching to a workflow whose own exit holds the reserved id keeps it', () => {
    const store = reeve(JSON.stringify(blueprint), ['Result'])
    const other = { ...blueprint, nodes: [...blueprint.nodes, placed('__workflow_output__', { blockTypeId: 'WorkflowOutput' })],
      edges: [...blueprint.edges, { id: 'o', sourceNodeId: 'a', targetNodeId: '__workflow_output__', mappings: {} }] }
    store.getState().loadStoredWorkflow(readStoredDefinitionText(JSON.stringify(other)))
    store.getState().setWorkflowParams([param('Room')], [])
    expect(JSON.stringify(store.getState().getWorkflowDefinition())).toBe(JSON.stringify(other))
  })

  it('records an added node under its container as well as its branch: another Body keeps its own exit', () => {
    const loop = (id: string, extra: Record<string, unknown>) => placed(id, { blockTypeId: 'Loop', ...extra })
    const store = reeve(JSON.stringify({ ...blueprint, nodes: [...blueprint.nodes,
      loop('l1', { outputPorts: [{ name: 'R', displayName: 'R', sources: { Body: 'Out' } }], subCanvases: { Body: { nodes: [placed('b1')], edges: [] } } }),
      loop('l2', { subCanvases: { Body: { nodes: [placed('c'), placed('__workflow_output__', { blockTypeId: 'WorkflowOutput' })],
        edges: [{ id: 'w', sourceNodeId: 'c', targetNodeId: '__workflow_output__', mappings: {} }] } } })] }), [])
    store.getState().enterSubCanvas('l1', 'Body')
    expect(store.getState().nodes.map((n) => n.id)).toContain('__workflow_output__')
    store.getState().exitToLevel(0)
    store.getState().enterSubCanvas('l2', 'Body')
    expect(store.getState().nodes.map((n) => n.id)).toEqual([IN, 'c', '__workflow_output__'])
    expect(store.getState().edges.map((e) => e.id)).toEqual(['w'])
  })

  it('keeps the last entry node, and the last exit node the outputs call for, from removeNode; lets any other go', () => {
    const store = reeve(JSON.stringify({ ...blueprint, nodes: [...blueprint.nodes, placed('wo', { blockTypeId: 'WorkflowOutput' })] }), ['Result'])
    store.getState().removeNode('start')
    store.getState().removeNode('wo')
    expect(store.getState().nodes.map((n) => n.id)).toEqual(['start', 'a', 'wo'])
    store.getState().setWorkflowParams([param('Room')], [])
    store.getState().removeNode('wo')
    const second = reeve(JSON.stringify({ ...blueprint, nodes: [placed(IN, { blockTypeId: 'WorkflowInput' }), ...blueprint.nodes] }), [])
    second.getState().removeNode('start')
    expect([store.getState().nodes.map((n) => n.id), second.getState().nodes.map((n) => n.id)]).toEqual([['start', 'a'], [IN, 'a']])
  })
})

describe('the refusal names the first undrawable member: a node, then its branches, then the canvas\'s edges', () => {
  const unplaced = (id: string, extra: Record<string, unknown> = {}) => ({ id, blockTypeId: 'Widget', configuration: {}, ...extra })
  const arrayEdge = { id: 'x', sourceNodeId: 'a', targetNodeId: 'b', mappings: [{ sourceOutput: 'Out', targetInput: 'In' }] }
  const body = (nodes: unknown[], edges: unknown[] = []) => ({ blockTypeId: 'Loop', position: { x: 0, y: 0 }, configuration: {}, subCanvases: { Body: { nodes, edges } } })
  it.each([
    ['a node before the edges', { nodes: [placed('a'), unplaced('b')], edges: [arrayEdge] }, '/nodes/1/position'],
    ['a node\'s branch before the next node', { nodes: [{ id: 'l', ...body([unplaced('deep')]) }, unplaced('b')] }, '/nodes/0/subCanvases/Body/nodes/0/position'],
    ['a branch\'s edge before the canvas\'s edges', { nodes: [{ id: 'l', ...body([], [arrayEdge]) }], edges: [arrayEdge] }, '/nodes/0/subCanvases/Body/edges/0/mappings'],
    ['a branch named with / and ~, escaped', { nodes: [{ id: 'l', blockTypeId: 'Loop', position: { x: 0, y: 0 }, configuration: {}, subCanvases: { 'a/b~c': { nodes: [unplaced('deep')] } } }] }, '/nodes/0/subCanvases/a~1b~0c/nodes/0/position'],
  ])('%s', (_name, document, location) => {
    expect(open(JSON.stringify(document)).store.getState().loadedDocumentFault?.location).toBe(location)
  })
})

describe('a root outputMappings key is not opened, so no save has one to carry', () => {
  it('opens as the refusal output-mappings-unread and holds no definition to save', () => {
    const { store } = open(JSON.stringify({
      nodes: [placed('source'), placed('exit', { blockTypeId: 'WorkflowOutput' })],
      outputMappings: [{ sourceNodeId: 'source', sourceOutput: 'Out', workflowOutput: 'Result' }],
    }))
    expect(store.getState().loadedDocumentFault).toMatchObject({ rule: 'output-mappings-unread', location: '/outputMappings' })
    expect(() => store.getState().getWorkflowDefinition()).toThrow(WorkflowDocumentUnreadableError)
  })
})

describe('SavedWorkflowDefinition says what a save writes', () => {
  it('types optional saved members without a cast', () => {
    const { store } = open(JSON.stringify({
      nodes: [placed(IN, { blockTypeId: 'WorkflowInput' })],
    }))
    const saved: SavedWorkflowDefinition = store.getState().getWorkflowDefinition()
    const node = saved.nodes?.[0]
    const written: unknown[] = [node?.subCanvases, node?.passthrough, node?.outputPorts, saved.edges]
    expect(written).toEqual([undefined, undefined, undefined, []])
  })
})
