import { beforeEach, describe, expect, it } from 'vitest'
import type { WorkflowDefinition, WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import type { WorkflowEditorStore } from './store.js'
import { createWorkflowEditorStore } from './store.js'
import { validatePortTypeNames } from './validatePortTypeNames.js'
import { readMappingProjection } from '../model/storedDocument.js'

const node = (id: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {}, ...extra })

const wired = (mappings: WorkflowDefinition['edges'][number]['mappings']): WorkflowDefinition => ({
  nodes: [node('a'), node('b')],
  edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings }],
})

const projected = { type: 'direct', sourceOutputName: 'Body', projection: { path: '$.items[*]', mode: 'jsonText', type: 'Collection<String>' } } as const

let store: WorkflowEditorStore
beforeEach(() => { store = createWorkflowEditorStore({ portTypes: createPortTypeRegistry([...BUILTIN_PORT_TYPES]) }) })

const theNode = () => store.getState().nodes.find((n) => n.id === 'n')
const mappings = () => store.getState().edges[0]?.mappings

describe('a mapping projection through the editor store', () => {
  it('survives a save and a rewrite of the wire\'s mappings', () => {
    store.getState().loadWorkflow(wired({ In: projected }))
    expect(store.getState().loadedDocumentFault).toBeNull()

    store.getState().updateEdgeMappings('e', { ...mappings(), Other: { type: 'direct', sourceOutputName: 'Count' } })

    expect(mappings()?.['In']).toEqual(projected)
    expect(store.getState().getWorkflowDefinition().edges?.[0]?.mappings).toEqual({ In: projected, Other: { type: 'direct', sourceOutputName: 'Count' } })
  })

  it('is written on a literal mapping that already holds one, and not dropped there', () => {
    store.getState().loadWorkflow(wired({ In: { type: 'literal', literalValue: 'x', projection: { path: '$.a' } } }))
    store.getState().updateEdgeMappings('e', { ...mappings() })
    expect(mappings()?.['In']).toEqual({ type: 'literal', literalValue: 'x', projection: { path: '$.a' } })
  })

  it('is refused by updateEdgeMappings when the shape is one the reader refuses', () => {
    store.getState().loadWorkflow(wired({}))
    const write = (projection: unknown) => () => { store.getState().updateEdgeMappings('e', { In: { type: 'direct', sourceOutputName: 'Body', projection } as never }) }
    expect(write({ path: '$.a', depth: 1 })).toThrow("unlisted key 'depth' on projection (rule stored-key-unknown, at '/depth')")
    expect(write({ mode: 'jsonText' })).toThrow("a projection with no 'path' (rule L7, at '/path')")
    expect(write({ path: '$.a', mode: 'text' })).toThrow("where one of jsonText is required (rule L7, at '/mode')")
    expect(write({ path: '$.a', type: null })).toThrow("null where a port type name is required (rule L7, at '/type')")
    expect(write(null)).toThrow("null where a projection is required (rule L7, at '')")
    expect(mappings()).toEqual({})
  })

  it('is set and cleared on a direct mapping, leaving the rest of the mapping', () => {
    store.getState().loadWorkflow(wired({ In: { type: 'direct', sourceOutputName: 'Body' } }))

    store.getState().setMappingProjection('e', 'In', { path: '$.id', type: 'Int' })
    expect(mappings()?.['In']).toEqual({ type: 'direct', sourceOutputName: 'Body', projection: { path: '$.id', type: 'Int' } })
    expect(store.getState().isDirty).toBe(true)

    store.getState().setMappingProjection('e', 'In', null)
    expect(mappings()?.['In']).toEqual({ type: 'direct', sourceOutputName: 'Body' })
  })

  it('is not writable on a literal mapping or on an input the wire does not map', () => {
    store.getState().loadWorkflow(wired({ Lit: { type: 'literal', literalValue: 'x' } }))
    expect(() => { store.getState().setMappingProjection('e', 'Lit', { path: '$.a' }) }).toThrow(/no direct mapping into 'Lit'/)
    expect(() => { store.getState().setMappingProjection('e', 'Missing', { path: '$.a' }) }).toThrow(/no direct mapping into 'Missing'/)
    expect(mappings()).toEqual({ Lit: { type: 'literal', literalValue: 'x' } })
  })

  it('clears nothing, without a throw, where there is nothing to clear', () => {
    store.getState().loadWorkflow(wired({ Lit: { type: 'literal', literalValue: 'x' } }))
    store.getState().setMappingProjection('e', 'Lit', null)
    store.getState().setMappingProjection('nope', 'In', null)
    expect(store.getState().isDirty).toBe(false)
  })

  it('refuses a malformed projection without writing', () => {
    store.getState().loadWorkflow(wired({ In: { type: 'direct', sourceOutputName: 'Body' } }))
    expect(() => { store.getState().setMappingProjection('e', 'In', { path: '$.a', mode: 'text' } as never) }).toThrow(/rule L7, at '\/mode'/)
    expect(mappings()?.['In']).toEqual({ type: 'direct', sourceOutputName: 'Body' })
  })
})

describe('readMappingProjection', () => {
  it('hands back a projection the reader accepts, and leaves its path and type ungraded', () => {
    expect(readMappingProjection({ path: 'not a path', mode: 'jsonText', type: 'Int<' })).toEqual({ ok: true, projection: { path: 'not a path', mode: 'jsonText', type: 'Int<' } })
  })

  it('refuses under the stored-document rules, as a document would', () => {
    expect(readMappingProjection({ Path: '$.a' })).toEqual({ ok: false, fault: expect.objectContaining({ rule: 'L5', location: '/Path' }) as unknown })
    expect(readMappingProjection(undefined)).toEqual({ ok: false, fault: expect.objectContaining({ rule: 'L7', location: '' }) as unknown })
  })
})

describe('node type arguments through the editor store', () => {
  it('are replaced, and removed by null or by an empty record', () => {
    store.getState().loadWorkflow({ nodes: [node('n', { configuration: { K: 'v' } })], edges: [] })

    store.getState().setNodeTypeArguments('n', { T: 'Int' })
    expect(theNode()).toMatchObject({ configuration: { K: 'v' }, typeArguments: { T: 'Int' } })

    store.getState().setNodeTypeArguments('n', { T: 'String', U: 'Collection<Int>' })
    expect(theNode()?.typeArguments).toEqual({ T: 'String', U: 'Collection<Int>' })

    store.getState().setNodeTypeArguments('n', {})
    expect(theNode()).not.toHaveProperty('typeArguments')

    store.getState().setNodeTypeArguments('n', { T: 'Int' })
    store.getState().setNodeTypeArguments('n', null)
    expect(theNode()).not.toHaveProperty('typeArguments')
  })

  it('refuse a value that is not a string, without writing', () => {
    store.getState().loadWorkflow({ nodes: [node('n', { typeArguments: { T: 'Int' } })], edges: [] })
    expect(() => { store.getState().setNodeTypeArguments('n', { T: 3 as unknown as string }) }).toThrow(/written as a string/)
    expect(theNode()?.typeArguments).toEqual({ T: 'Int' })
  })
})

describe('a projection type among the stored port type names', () => {
  it('is graded as malformed at the input it feeds, naming the node and the input', () => {
    const definition = wired({ In: { type: 'direct', sourceOutputName: 'Body', projection: { path: '$.a', type: 'Int<' } } })
    const findings = validatePortTypeNames({ definition, workflowInputParams: [], workflowOutputParams: [] })
    expect(findings).toEqual([expect.objectContaining({
      ruleId: 'port-type-malformed', nodeId: 'b', portName: 'In',
      params: expect.objectContaining({ origin: "the projection type of input 'In' of node 'b'" }) as unknown,
    })])
  })

  it('is not reported when it parses', () => {
    const definition = wired({ In: { type: 'direct', sourceOutputName: 'Body', projection: { path: '$.a', type: 'Collection<Int>' } } })
    expect(validatePortTypeNames({ definition, workflowInputParams: [], workflowOutputParams: [] })).toEqual([])
  })
})
