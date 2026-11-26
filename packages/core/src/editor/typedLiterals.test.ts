import { beforeEach, describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import type { WorkflowEditorStore } from './store.js'
import { createWorkflowEditorStore } from './store.js'
import { retypeLiteral } from './typedLiterals.js'

const registry = createPortTypeRegistry([...BUILTIN_PORT_TYPES])

const setter: BlockTypeDescriptor = {
  blockTypeId: 'Setter', displayName: 'Setter', outputs: [], typeParameters: [{ name: 'T' }],
  inputs: [
    { name: 'Value', displayName: 'Value', type: '$T', isRequired: true },
    { name: 'Default', displayName: 'Default', type: '$T', isRequired: false },
    { name: 'Note', displayName: 'Note', type: 'String', isRequired: false },
    { name: 'Many', displayName: 'Many', type: 'Collection<$T>', isRequired: false },
    { name: 'Keyed', displayName: 'Keyed', type: 'Map<String,$T>', isRequired: false },
    { name: 'Choice', displayName: 'Choice', type: '$T', isRequired: false },
  ],
  configurationSchema: [
    { name: 'Default', label: 'Default', type: 'String', required: false },
    { name: 'Choice', label: 'Choice', type: 'Enum', required: false, allowedValues: ['true', 'false'] },
  ],
}
const source: BlockTypeDescriptor = { blockTypeId: 'Source', displayName: 'Source', inputs: [], outputs: [] }

const node = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })

let store: WorkflowEditorStore
beforeEach(() => {
  store = createWorkflowEditorStore({ portTypes: registry })
  store.getState().setBlockTypes([setter, source])
})

const load = (value: unknown, configuration: WorkflowNode['configuration'] = {}) => {
  store.getState().loadWorkflow({
    nodes: [node('a', 'Source'), node('n', 'Setter', { configuration })],
    edges: [{
      id: 'e', sourceNodeId: 'a', targetNodeId: 'n',
      mappings: { Value: { type: 'literal', literalValue: value as string }, Note: { type: 'literal', literalValue: 'true' } },
    }],
  })
}
const literal = (input: string) => store.getState().edges[0]?.mappings[input]?.literalValue

describe('binding a type argument', () => {
  it('rewrites a wire literal typed while the parameter was open in the form the bound term takes', () => {
    load('true')
    store.getState().setNodeTypeArguments('n', { T: 'Boolean' })
    expect(literal('Value')).toBe(true)
    expect(literal('Note')).toBe('true')
    expect(store.getState().getWorkflowDefinition().edges?.[0]?.mappings?.['Value' as never]).toEqual({ type: 'literal', literalValue: true })
  })

  it('rewrites again when the argument changes, and a configuration literal of the input too', () => {
    load('12', { Default: '7' })
    store.getState().setNodeTypeArguments('n', { T: 'Int' })
    expect(literal('Value')).toBe(12)
    expect(store.getState().nodes.find((n) => n.id === 'n')?.configuration['Default']).toBe(7)

    store.getState().setNodeTypeArguments('n', { T: 'String' })
    expect(literal('Value')).toBe('12')
  })

  it('leaves a literal that does not parse, an expression, and an unbound parameter as stored', () => {
    load('yes')
    store.getState().setNodeTypeArguments('n', { T: 'Boolean' })
    expect(literal('Value')).toBe('yes')

    load('expr:${a.flag}')
    store.getState().setNodeTypeArguments('n', { T: 'Boolean' })
    expect(literal('Value')).toBe('expr:${a.flag}')

    load('true')
    store.getState().setNodeTypeArguments('n', { U: 'Boolean' })
    expect(literal('Value')).toBe('true')
  })
})

describe('binding a type argument, beside other mappings and forms', () => {
  it('leaves a list, a map, a direct mapping, an Enum field\'s value as they are', () => {
    store.getState().loadWorkflow({
      nodes: [node('a', 'Source'), node('n', 'Setter', { configuration: { Choice: 'true' } })],
      edges: [{
        id: 'e', sourceNodeId: 'a', targetNodeId: 'n',
        mappings: {
          Many: { type: 'literal', literalValue: ['1', '2'] },
          Keyed: { type: 'literal', literalValue: { a: '1' } },
          Value: { type: 'direct', sourceOutputName: 'Out' },
        },
      }],
    })
    expect(store.getState().loadedDocumentFault).toBeNull()
    const before = structuredClone(store.getState().edges)

    store.getState().setNodeTypeArguments('n', { T: 'Int' })

    expect(store.getState().edges).toEqual(before)
    expect(store.getState().nodes.find((n) => n.id === 'n')?.configuration).toEqual({ Choice: 'true' })
  })

  it('rewrites the literal of every wire feeding the input', () => {
    store.getState().loadWorkflow({
      nodes: [node('a', 'Source'), node('b', 'Source'), node('n', 'Setter')],
      edges: [
        { id: 'e1', sourceNodeId: 'a', targetNodeId: 'n', mappings: { Value: { type: 'literal', literalValue: '1' } } },
        { id: 'e2', sourceNodeId: 'b', targetNodeId: 'n', mappings: { Value: { type: 'literal', literalValue: '2' } } },
      ],
    })
    store.getState().setNodeTypeArguments('n', { T: 'Int' })
    expect(store.getState().edges.map((e) => e.mappings['Value']?.literalValue)).toEqual([1, 2])
  })

  it('reads back to the text it came from when the argument goes back', () => {
    for (const text of ['0', '-12', '1.5', '1e+21', '9007199254740991']) {
      load(text)
      store.getState().setNodeTypeArguments('n', { T: 'Number' })
      expect(typeof literal('Value')).toBe('number')
      store.getState().setNodeTypeArguments('n', { T: 'String' })
      expect(literal('Value')).toBe(text)
    }
  })
})

describe('retypeLiteral', () => {
  it('rewrites only the canonical text of a value of the term\'s form', () => {
    expect(retypeLiteral('false', 'Boolean', registry)).toEqual({ value: false })
    expect(retypeLiteral('1.5', 'Number', registry)).toEqual({ value: 1.5 })
    expect(retypeLiteral('-3', 'Int', registry)).toEqual({ value: -3 })
    expect(retypeLiteral(true, 'String', registry)).toEqual({ value: 'true' })
    expect(retypeLiteral(1.5, 'String', registry)).toEqual({ value: '1.5' })
  })

  it('leaves any other spelling as stored, so browsing the argument never changes what was typed', () => {
    for (const text of ['TRUE', 'True', ' true']) expect(retypeLiteral(text, 'Boolean', registry)).toBeUndefined()
    for (const text of ['0x1F', '0b11', '0o7', '1e3', '.5', '5.', '+5', '007', '1.50', '-0', ' 2', 'Infinity', 'NaN', '']) {
      expect(retypeLiteral(text, 'Number', registry)).toBeUndefined()
    }
    expect(retypeLiteral('1.5', 'Int', registry)).toBeUndefined()
    expect(retypeLiteral('9007199254740993', 'Int', registry)).toBeUndefined()
    expect(retypeLiteral('9007199254740992', 'Int', registry)).toBeUndefined()
  })

  it('leaves what already fits, a Json term, and a String with a format as stored', () => {
    expect(retypeLiteral(true, 'Boolean', registry)).toBeUndefined()
    expect(retypeLiteral('true', 'Json', registry)).toBeUndefined()
    expect(retypeLiteral(5, 'DateTime', registry)).toBeUndefined()
    expect(retypeLiteral(true, 'DateTime', registry)).toBeUndefined()
    expect(retypeLiteral(null, 'Boolean?', registry)).toBeUndefined()
    expect(retypeLiteral('true', 'Boolean?', registry)).toEqual({ value: true })
  })
})
