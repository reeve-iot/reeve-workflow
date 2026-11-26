import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { isLiteralCoercible, literalFormOfType } from './literalCoercion.js'
import type { PortLiteralForm } from './literalForm.js'
import type { PortTypeName } from './name.js'
import { BUILTIN_PORT_TYPES } from './builtins.js'
import { createPortTypeRegistry } from './registry.js'
import { validateNodes } from '../editor/validateNodes.js'
import { isGradableTerm } from './closedTerm.js'
import { parsePortTerm } from './grammar.js'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID, type WorkflowEdge, type WorkflowNode } from '../model/workflow.js'

interface Vector {
  value?: unknown
  text?: string
  duplicateMember?: boolean
  type: string
  accepted: boolean
  note?: string
}

const file = JSON.parse(readFileSync('contracts/literal-coercion.json', 'utf8')) as {
  hostAtoms: Record<string, PortLiteralForm | null>
  hostConstructors: Record<string, { allowedArguments: string[]; literal: PortLiteralForm | null }>
  rows: Vector[]
}

const rows = file.rows.filter((row) => row.duplicateMember !== true)
  .map((row) => (row.text === undefined ? row : { ...row, value: JSON.parse(row.text) as unknown }))

const registry = createPortTypeRegistry(
  [...BUILTIN_PORT_TYPES, ...Object.entries(file.hostAtoms).map(([name, literal]) => ({ name, literal }))],
  Object.entries(file.hostConstructors).map(([name, entry]) => ({ name, allowedArguments: entry.allowedArguments, literal: entry.literal })),
)

function formatted(type: string): boolean {
  const parsed = parsePortTerm(type, 'document')
  for (let term = parsed.ok ? parsed.term : undefined; term !== undefined; term = term.kind === 'constructor' ? term.argument : undefined) {
    if (term.kind === 'atom' && registry.literalFormOf(registry.normalize(term.name))?.format) return true
  }
  return false
}
const isJsonString = (row: Vector): boolean => typeof row.value === 'string'
  || (row.type.startsWith('Collection<') && Array.isArray(row.value) && row.value.every((element) => typeof element === 'string'))
  || (row.type.startsWith('Map<') && typeof row.value === 'object' && row.value !== null && !Array.isArray(row.value)
    && Object.values(row.value).every((member) => typeof member === 'string'))
const editorAccepts = (row: Vector): boolean => row.accepted || (formatted(row.type) && isJsonString(row))

describe('literal coercion agreement', () => {
  it.each(rows.map((row) => [JSON.stringify(row.value ?? null), row.type, editorAccepts(row), row] as const))(
    'grades %s against %s as %s', (_, type, accepted, row) => {
      expect(registry.isLiteralOf(row.value, type as PortTypeName)).toBe(accepted)
      expect(isLiteralCoercible(row.value, type as PortTypeName, registry)).toBe(accepted)
    })

  it('accepts a DateTime or Uuid string whatever its format, and still refuses what is no string', () => {
    for (const [type, value] of [['DateTime', '2024-02-30'], ['DateTime', 'Tuesday'], ['Ref', 'not-a-uuid'], ['Collection<Ref>', ['x']]] as const) {
      expect(registry.isLiteralOf(value, type as PortTypeName), `${type} ${JSON.stringify(value)}`).toBe(true)
    }
    for (const [type, value] of [['DateTime', 5], ['Ref', true], ['Ref', null], ['Collection<DateTime>', 'x']] as const) {
      expect(registry.isLiteralOf(value, type as PortTypeName), `${type} ${JSON.stringify(value)}`).toBe(false)
    }
  })

  it('states enough vectors, and one that no text is read into a scalar', () => {
    expect(rows.length).toBeGreaterThan(150)
    expect(registry.isLiteralOf('5', 'Int')).toBe(false)
    expect(registry.isLiteralOf('true', 'Boolean')).toBe(false)
    expect(registry.isLiteralOf('1.5', 'Number')).toBe(false)
  })

  it('answers the library form of each of its atoms, none for Collection, and the form a host record declares', () => {
    expect(registry.literalFormOf('Boolean')).toEqual({ gradedAs: 'Boolean' })
    expect(registry.literalFormOf('int')).toEqual({ gradedAs: 'Int' })
    expect(registry.literalFormOf('Number')).toEqual({ gradedAs: 'Number' })
    expect(registry.literalFormOf('String')).toEqual({ gradedAs: 'String' })
    expect(registry.literalFormOf('DateTime')).toEqual({ gradedAs: 'String', format: 'DateTime' })
    expect(registry.literalFormOf('Json')).toEqual({ gradedAs: 'Json' })
    expect(registry.literalFormOf('Collection')).toBeUndefined()
    expect(registry.literalFormOf('Ref')).toEqual({ gradedAs: 'String', format: 'Uuid' })
    expect(registry.literalFormOf('Gauge')).toEqual({ gradedAs: 'Json' })
    expect(registry.literalFormOf('Scene')).toBeUndefined()
    expect(registry.literalFormOf('Probe')).toBeUndefined()
  })

  it.each(['$T', 'Collection<$T>', '$T?'])('grades %s once the parameter is bound', (type) => {
    expect(registry.isLiteralOf(5, type as PortTypeName)).toBe(true)
    expect(isLiteralCoercible(5, type as PortTypeName, registry)).toBe(false)
  })

  it.each(['', 'Collection<', 'Int??', 'not a type'])('has no literal for the malformed type %j', (type) => {
    expect(registry.isLiteralOf(5, type as PortTypeName)).toBe(false)
  })

  it('the editor emits the compiler rule code for a rejected literal', () => {
    const nodes: WorkflowNode[] = [
      { id: 'entry', blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} },
      { id: 'target', blockTypeId: 'Receiver', position: { x: 0, y: 0 }, configuration: {} },
    ]
    const edges: WorkflowEdge[] = [{ id: 'e', sourceNodeId: 'entry', targetNodeId: 'target',
      mappings: { Value: { type: 'literal', literalValue: 'not-a-number' } } }]
    const findings = validateNodes({
      nodes, edges, blockTypes: [
        { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] },
        { blockTypeId: 'Receiver', displayName: 'Receiver', inputs: [{ name: 'Value', displayName: 'Value', type: 'Int', isRequired: true}], outputs: [] },
      ], workflowOutputParams: [], portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES),
      resolveOutputType: () => 'String', canvasPorts: { entry: [], exit: [] },
    })
    expect(findings).toContainEqual(expect.objectContaining({ code: 'literal-port-value-invalid', ruleId: 'literal-port-value-invalid', nodeId: 'target', portName: 'Value' }))
  })

  const grade = (nodes: WorkflowNode[], edges: WorkflowEdge[], exits: { name: string; displayName: string; isRequired: boolean; type: string }[] = []) =>
    validateNodes({
      nodes, edges, blockTypes: [
        { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] },
        { blockTypeId: WORKFLOW_OUTPUT_BLOCK_TYPE_ID, displayName: 'Output', inputs: [], outputs: [] },
        { blockTypeId: 'Gate', displayName: 'Gate', inputs: [], outputs: [],
          configurationSchema: [{ name: 'Condition', label: 'Condition', type: 'Boolean', required: false }] },
      ], workflowOutputParams: exits, portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES),
      resolveOutputType: () => 'String', canvasPorts: { entry: [], exit: exits.map((p) => p.name) },
    }).filter((f) => f.code === 'literal-port-value-invalid')
  const entry: WorkflowNode = { id: 'entry', blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} }

  it.each([['expr:true', 0], ['expr: ${previous.read.Value} > 3', 0], ['maybe', 1]])(
    'grades a Boolean configuration field holding %s: %i findings (an expression is graded by what it yields)', (text, count) => {
      const gate: WorkflowNode = { id: 'gate', blockTypeId: 'Gate', position: { x: 0, y: 0 }, configuration: { Condition: text } }
      expect(grade([entry, gate], [])).toHaveLength(count)
    })

  it('grades a configuration field that names an input by the input\'s term, whatever type the field has', () => {
    const graded = rows.filter((row) => row.value !== undefined && row.value !== null && isGradableTerm(row.type as PortTypeName, registry)
      && !(typeof row.value === 'number' && !Number.isFinite(row.value)) && !(typeof row.value === 'string' && row.value.startsWith('expr:')))
    for (const row of graded) {
      const receiver: WorkflowNode = { id: 'target', blockTypeId: 'Receiver', position: { x: 0, y: 0 }, configuration: { Value: row.value as WorkflowNode['configuration'][string] } }
      const findings = validateNodes({
        nodes: [entry, receiver], edges: [], blockTypes: [
          { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] },
          { blockTypeId: 'Receiver', displayName: 'Receiver', outputs: [],
            inputs: [{ name: 'Value', displayName: 'Value', type: row.type as PortTypeName, isRequired: false }],
            configurationSchema: [{ name: 'Value', label: 'Value', type: 'String', required: false }] },
        ], workflowOutputParams: [], portTypes: registry,
        resolveOutputType: () => 'String', canvasPorts: { entry: [], exit: [] },
      }).filter((f) => f.code === 'literal-port-value-invalid' && f.nodeId === 'target')
      expect(findings.length > 0, JSON.stringify(row)).toBe(!editorAccepts(row))
    }
    expect(graded.length).toBeGreaterThan(100)
  })

  it.each(['$T', '$T?', 'Collection<$T>', 'Collection<$T?>', 'Collection<Collection<$T>>'])(
    'does not grade a literal into a port whose declared term is %s', (type) => {
      for (const value of [5, 'text', true, [1, 'two'], [], { a: 1 }, 'abc']) {
        const receiver: WorkflowNode = { id: 'target', blockTypeId: 'Receiver', position: { x: 0, y: 0 }, configuration: { Value: value as WorkflowNode['configuration'][string] } }
        const wired: WorkflowEdge = { id: 'e', sourceNodeId: 'entry', targetNodeId: 'target', mappings: { Other: { type: 'literal', literalValue: value as never } } }
        const findings = validateNodes({
          nodes: [entry, receiver], edges: [wired], blockTypes: [
            { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] },
            { blockTypeId: 'Receiver', displayName: 'Receiver', outputs: [],
              inputs: [{ name: 'Value', displayName: 'Value', type, isRequired: false }, { name: 'Other', displayName: 'Other', type, isRequired: false }],
              configurationSchema: [{ name: 'Value', label: 'Value', type: 'String', required: false }] },
          ], workflowOutputParams: [], portTypes: registry,
          resolveOutputType: () => undefined, canvasPorts: { entry: [], exit: [] },
        }).filter((f) => f.code === 'literal-port-value-invalid')
        expect(findings, JSON.stringify(value)).toEqual([])
      }
    })

  it('grades a literal into a typed exit as the compiler does', () => {
    const exit: WorkflowNode = { id: 'exit', blockTypeId: WORKFLOW_OUTPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} }
    const into = (literalValue: string | number): WorkflowEdge[] => [{ id: 'e', sourceNodeId: 'entry', targetNodeId: 'exit',
      mappings: { Count: { type: 'literal', literalValue } } }]
    const exits = [{ name: 'Count', displayName: 'Count', isRequired: false, type: 'Int' }]
    expect(grade([entry, exit], into('abc'), exits)).toContainEqual(expect.objectContaining({ nodeId: 'exit', portName: 'Count' }))
    expect(grade([entry, exit], into(5), exits)).toHaveLength(0)
  })

  it('does not grade an expression mapping by the target port literal shape', () => {
    const receiver: WorkflowNode = { id: 'target', blockTypeId: 'Receiver', position: { x: 0, y: 0 }, configuration: {} }
    const edge: WorkflowEdge = { id: 'e', sourceNodeId: 'entry', targetNodeId: 'target',
      mappings: { Value: { type: 'literal', literalValue: 'expr:1 + 1' } } }
    const findings = validateNodes({
      nodes: [entry, receiver], edges: [edge], blockTypes: [
        { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] },
        { blockTypeId: 'Receiver', displayName: 'Receiver', inputs: [{ name: 'Value', displayName: 'Value', type: 'Int', isRequired: true }], outputs: [] },
      ], workflowOutputParams: [], portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES),
      resolveOutputType: () => 'String', canvasPorts: { entry: [], exit: [] },
    })
    expect(findings.filter((finding) => finding.code === 'literal-port-value-invalid')).toHaveLength(0)
  })

  it.each([['Int', '5'], ['Boolean', 'true'], ['Number', '1.5'], ['Scene', 'scene-1']])(
    'refuses %s text %j on a port of the host vocabulary, as the compiler does', (type, literalValue) => {
      const receiver: WorkflowNode = { id: 'target', blockTypeId: 'Receiver', position: { x: 0, y: 0 }, configuration: {} }
      const edge: WorkflowEdge = { id: 'e', sourceNodeId: 'entry', targetNodeId: 'target', mappings: { Value: { type: 'literal', literalValue } } }
      const findings = validateNodes({
        nodes: [entry, receiver], edges: [edge], blockTypes: [
          { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] },
          { blockTypeId: 'Receiver', displayName: 'Receiver', inputs: [{ name: 'Value', displayName: 'Value', type, isRequired: true }], outputs: [] },
        ], workflowOutputParams: [], portTypes: registry,
        resolveOutputType: () => 'String', canvasPorts: { entry: [], exit: [] },
      })
      expect(findings.filter((finding) => finding.code === 'literal-port-value-invalid')).toHaveLength(1)
    })

  it.each([['Ref', 'not-a-uuid'], ['DateTime', '2024-02-30']])(
    'leaves the format of %s text %j to the compiler, and refuses a value that is no string', (type, literalValue) => {
      const receiver: WorkflowNode = { id: 'target', blockTypeId: 'Receiver', position: { x: 0, y: 0 }, configuration: {} }
      const into = (value: string | number): ReturnType<typeof grade> => validateNodes({
        nodes: [entry, receiver], edges: [{ id: 'e', sourceNodeId: 'entry', targetNodeId: 'target', mappings: { Value: { type: 'literal', literalValue: value } } }],
        blockTypes: [
          { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] },
          { blockTypeId: 'Receiver', displayName: 'Receiver', inputs: [{ name: 'Value', displayName: 'Value', type, isRequired: true }], outputs: [] },
        ], workflowOutputParams: [], portTypes: registry,
        resolveOutputType: () => 'String', canvasPorts: { entry: [], exit: [] },
      }).filter((finding) => finding.code === 'literal-port-value-invalid')
      expect(into(literalValue)).toEqual([])
      expect(into(5)).toHaveLength(1)
    })
})

describe('literalFormOfType', () => {
  it.each(['Collection<Int>', 'Map<String,Int>', 'Map<string,Collection<Int>>', 'Map<String,Int>?', 'Map<String,Map<String,Json?>>?', 'Collection<Map<String,Int>>'])(
    'leaves %s to the JSON editor, no form being the type\'s to give', (type) => {
      expect(literalFormOfType(type as PortTypeName, registry)).toBeUndefined()
    })

  it.each(['Map<Int,Int>', 'Map', 'Map<String,$T>', 'Map<String,Shipment>'])('gives no form for %s, which is no closed term of this vocabulary', (type) => {
    expect(literalFormOfType(type as PortTypeName, registry)).toBeUndefined()
  })

  it('still gives the form of an atom and of a registered constructor', () => {
    expect(literalFormOfType('Int' as PortTypeName, registry)).toEqual({ gradedAs: 'Int' })
    expect(literalFormOfType('Int?' as PortTypeName, registry)).toEqual({ gradedAs: 'Int' })
    expect(literalFormOfType('Gauge<Boolean>' as PortTypeName, registry)).toEqual({ gradedAs: 'Json' })
  })
})
