import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { VariableMapping, WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { readStoredDefinitionText } from '../model/storedDocument.js'
import { readMapping } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { engineReadsMapping, mappingForms } from './documentGrammar.testkit.js'
import { feedsInput, isActiveMapping } from './mappingFeeds.js'
import { tracedPassthrough } from './passthroughTrace.js'
import { createWorkflowEditorStore } from './store.js'

const WIDGET: BlockTypeDescriptor = {
  blockTypeId: 'Widget', displayName: 'Widget', inputs: [],
  outputs: [{ name: 'Out', displayName: 'Out', isRequired: false, type: 'String' }],
}
const place = (id: string, blockTypeId = 'Widget', extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })
const wire = (m: Record<string, unknown>, input = 'In'): WorkflowEdge =>
  ({ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: { [input]: m as VariableMapping } })

describe('each reader of a mapping reads it as ReadMappings does', () => {
  it.each(mappingForms().map((m) => [JSON.stringify(m), m] as const))('%s', (_, m) => {
    const expected = engineReadsMapping(m)
    const edge = wire(m)
    const read = readMapping(m as VariableMapping)

    if (expected === null) expect(!read.literal && read.source.trim() === '').toBe(true)
    else expect(read).toEqual(expected)
    expect(isActiveMapping(edge, 'In')).toBe(expected !== null)
    expect(feedsInput(edge, 'In', new Set(['a', 'b']))).toBe(expected !== null)
    expect(feedsInput(edge, 'In', new Set(['b']))).toBe(expected?.literal === true)

    const carrier = place('b', 'Widget', { passthrough: ['Carried'] })
    const carried = wire(m, 'Carried')
    const traced = tracedPassthrough({ nodes: [place('a'), carrier], edges: [carried], bound: new Map([['a', WIDGET], ['b', WIDGET]]), entryPorts: [] }, carrier)
    expect(traced.has('Carried')).toBe(expected !== null && (expected.literal || expected.source === 'Out'))

    const stored = readStoredDefinitionText(JSON.stringify({ nodes: [place('__workflow_input__'), place('a'), place('b')], edges: [edge] }))
    if ('sourceOutput' in m || (m['type'] !== 'direct' && m['type'] !== 'literal')) {
      expect(stored.ok).toBe(false)
      return
    }
    if (!stored.ok) return
    const store = createWorkflowEditorStore({ portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES) })
    store.getState().setBlockTypes([{ ...WIDGET, inputs: [{ name: 'In', displayName: 'In', isRequired: false, type: 'String' }] }])
    store.getState().loadWorkflow({ nodes: [place('__workflow_input__'), place('a'), place('b')], edges: [edge] })
    const unknownPorts = store.getState().validateWorkflowFindings().filter((f) => f.ruleId === 'wire-port-unknown').map((f) => f.portName)
    expect(unknownPorts).toEqual(expected !== null && !expected.literal && expected.source !== 'Out' ? [expected.source] : [])
  })
})
