import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { validationMessage } from './messageCatalogue.js'
import { validateNodes } from './validateNodes.js'

const NAMES = ['Toggle', 'Dim', 'Scene']
const withNames = (allowedValues: string[] | null | undefined, type = 'Enum'): BlockTypeDescriptor => ({
  blockTypeId: 'setter', displayName: 'Setter', inputs: [], outputs: [],
  configurationSchema: [{ name: 'Kind', label: 'Kind', type, required: false, ...(allowedValues === undefined ? {} : { allowedValues }) }],
})
const setter = (allowedValues: string[] | null = NAMES, type = 'Enum') => withNames(allowedValues, type)

function all(value: unknown, block = setter()) {
  const node: WorkflowNode = { id: 'n', blockTypeId: 'setter', position: { x: 0, y: 0 }, configuration: { Kind: value } as never }
  return validateNodes({
    nodes: [node], edges: [], blockTypes: [block], workflowOutputParams: [],
    portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES), resolveOutputType: () => 'String',
  })
}

const grade = (value: unknown, block = setter()) => all(value, block).filter((f) => f.ruleId === 'literal-port-value-invalid')

const required = (block: BlockTypeDescriptor): BlockTypeDescriptor => ({
  ...block, configurationSchema: block.configurationSchema?.map((f) => ({ ...f, required: true })),
})

function wire(value: unknown) {
  const block: BlockTypeDescriptor = { ...setter(), inputs: [{ name: 'Kind', displayName: 'Kind', type: 'String', isRequired: false }] }
  const nodes: WorkflowNode[] = [
    { id: 'a', blockTypeId: 'setter', position: { x: 0, y: 0 }, configuration: {} },
    { id: 'b', blockTypeId: 'setter', position: { x: 0, y: 0 }, configuration: {} },
  ]
  return validateNodes({
    nodes, edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'b', mappings: { Kind: { type: 'literal', literalValue: value as never } } }],
    blockTypes: [block], workflowOutputParams: [], canvasPorts: { entry: [], exit: [] },
    portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES), resolveOutputType: () => 'String',
  }).filter((f) => f.ruleId === 'literal-port-value-invalid')
}

describe('an Enum configuration field in the editor', () => {
  it.each([
    ['a listed name', 'Toggle', true],
    ['the last name', 'Scene', true],
    ['an expression', 'expr:"Dim"', true],
    ['the old ordinal', 1, false],
    ['zero', 0, false],
    ['a boolean', true, false],
    ['an object', { a: 1 }, false],
    ['a list holding a name', ['Toggle'], false],
    ['a name that is not listed', 'Other', false],
    ['a listed name in another case', 'toggle', false],
    ['a listed name with a space', ' Toggle', false],
    ['empty text, which is absent', '', true],
  ] as const)('%s', (_, value, accepted) => {
    expect(grade(value).length === 0).toBe(accepted)
  })

  it('names the field and the allowed names, in order, and says the rule the compiler says', () => {
    const [finding] = grade(1)
    expect(finding?.ruleId).toBe('literal-port-value-invalid')
    expect(finding?.portName).toBe('Kind')
    expect(finding?.message).toBe("Node 'Setter' must set 'Kind' to one of 'Toggle', 'Dim', 'Scene'.")
    expect(finding && validationMessage(finding)).toBe(finding?.message)
  })

  it('accepts no value at all from a field that lists no names, as the compiler does', () => {
    expect(grade('Toggle', withNames(undefined)).length).toBe(1)
    expect(grade('Toggle', setter(null)).length).toBe(1)
    expect(grade('Toggle', setter([])).length).toBe(1)
  })

  it('grades a field of another type by that type, whatever names it carries', () => {
    expect(grade('anything', setter(NAMES, 'String'))).toEqual([])
    expect(grade(1, setter(NAMES, 'String')).length).toBe(1)
  })

  it('reads empty text as absent: one finding when required, none when optional', () => {
    expect(all('', required(setter())).map((f) => f.ruleId)).toEqual(['required-config-missing'])
    expect(all('', setter())).toEqual([])
  })

  it.each([
    ['a listed name', 'Dim', 0],
    ['a name that is not listed', 'Bogus', 1],
    ['a number', 1, 1],
    ['empty text, which is absent', '', 0],
  ] as const)('grades a wire literal into the same-named port: %s', (_, value, findings) => {
    expect(wire(value)).toHaveLength(findings)
  })
})
