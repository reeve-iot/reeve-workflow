import { describe, expect, it } from 'vitest'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowNode } from '../model/workflow.js'
import { BUILTIN_PORT_TYPES } from '../portTypes/builtins.js'
import { createPortTypeRegistry } from '../portTypes/registry.js'
import { validateNodes } from './validateNodes.js'

const DYNAMIC: BlockTypeDescriptor = { blockTypeId: 'dyn', displayName: 'Dyn', inputs: [], outputs: [], hasDynamicPorts: true }
const START: WorkflowNode = { id: 's', blockTypeId: 'dyn', position: { x: 0, y: 0 }, configuration: {} }
const ports = (type: string) => JSON.stringify([{ name: 'x', type }])

const HOST = createPortTypeRegistry(
  [...BUILTIN_PORT_TYPES, { name: 'Stamp', literal: { gradedAs: 'String' } }],
  [{ name: 'Gauge', allowedArguments: ['Boolean'], literal: { gradedAs: 'Number' } }],
)

function findings(type: string, via: 'wire' | 'configuration', value: unknown, registry = createPortTypeRegistry(BUILTIN_PORT_TYPES)) {
  const target: WorkflowNode = {
    id: 'n', blockTypeId: 'dyn', position: { x: 0, y: 0 },
    configuration: {
      InputPorts: ports(type), OutputPorts: ports(type),
      ...(via === 'configuration' ? { x: value } : {}),
    } as never,
  }
  const edges = via === 'wire'
    ? [{ id: 'e', sourceNodeId: 's', targetNodeId: 'n', mappings: { x: { type: 'literal' as const, literalValue: value as never } } }]
    : []
  return validateNodes({
    nodes: [START, target], edges, blockTypes: [DYNAMIC], workflowOutputParams: [], canvasPorts: { entry: [], exit: [] },
    portTypes: registry, resolveOutputType: () => undefined,
  }).filter((f) => f.ruleId === 'literal-port-value-invalid')
}

describe('a literal into a stored term the compiler refuses', () => {
  it.each(['Any', 'Collection<Any>', 'Float', 'Collection<'] as const)('gets no literal finding for %s, by any path', (type) => {
    for (const via of ['wire', 'configuration'] as const)
      expect(findings(type, via, 5), `${type} via ${via}`).toEqual([])
  })

  it.each(['wire', 'configuration'] as const)('is still refused for a term the compiler grades, by %s', (via) => {
    expect(findings('Boolean', via, 5)).toHaveLength(1)
    expect(findings('Boolean', via, true)).toEqual([])
  })
})

describe('a literal whose type the mount-time vocabulary does not know', () => {
  it.each(['Shipment', 'Collection<Shipment>'] as const)('gets no literal finding for %s, by any path', (type) => {
    for (const via of ['wire', 'configuration'] as const)
      expect(findings(type, via, 5), `${type} via ${via}`).toEqual([])
  })

  it.each(['Boolean?', 'Collection<Boolean?>'] as const)('is still refused for the known nullable term %s, by any path', (type) => {
    for (const via of ['wire', 'configuration'] as const)
      expect(findings(type, via, 'not-a-bool'), `${type} via ${via}`).toHaveLength(1)
  })

  it.each([['Stamp', 5], ['Stamp?', 5], ['Gauge<Boolean>', 'text'], ['Gauge<Boolean>?', 'text'], ['Collection<Gauge<Boolean>>', ['text']]] as const)(
    'is still refused for the registered host term %s, by any path', (type, value) => {
      for (const via of ['wire', 'configuration'] as const)
        expect(findings(type, via, value, HOST), `${type} via ${via}`).toHaveLength(1)
    })
})

describe('a literal into a port whose declared term names a type parameter', () => {
  it.each(['$T', '$T?', 'Collection<$T>', 'Collection<$T?>'] as const)('is not refused for %s, by any path', (type) => {
    for (const via of ['wire', 'configuration'] as const) {
      for (const value of [5, 'text', true, [1, 'two'], []]) {
        expect(findings(type, via, value), `${type} via ${via} holding ${JSON.stringify(value)}`).toEqual([])
      }
    }
  })
})

describe('a configuration value on a passthrough name', () => {
  const PLAIN: BlockTypeDescriptor = {
    blockTypeId: 'plain', displayName: 'Plain', inputs: [], outputs: [],
    configurationSchema: [{ name: 'p', label: 'P', type: 'String', required: false }],
  }
  const run = (sourceType: string | undefined, value: unknown, projection?: { path: string; type: string }) => {
    const target: WorkflowNode = { id: 'n', blockTypeId: 'plain', position: { x: 0, y: 0 }, configuration: { p: value as never }, passthrough: ['p'] }
    const edges = [{ id: 'e', sourceNodeId: 's', targetNodeId: 'n', mappings: { p: { type: 'direct' as const, sourceOutputName: 'o', ...(projection ? { projection } : {}) } } }]
    return validateNodes({
      nodes: [START, target], edges, blockTypes: [DYNAMIC, PLAIN], workflowOutputParams: [], canvasPorts: { entry: [], exit: [] },
      portTypes: createPortTypeRegistry(BUILTIN_PORT_TYPES), resolveOutputType: () => sourceType,
    }).filter((f) => f.ruleId === 'literal-port-value-invalid')
  }

  it.each([['Boolean', 5], ['Number', 5], ['Int', 'text'], [undefined, 5], ['$T', 5]])(
    'is not graded by the editor, whatever the wire\'s term %s and the field\'s own String type: %j', (sourceType, value) => {
      expect(run(sourceType, value)).toEqual([])
    })

  it('is not graded when the wire carries a projection', () => {
    expect(run('Boolean', 5, { path: '$.n', type: 'Int' })).toEqual([])
  })
})
