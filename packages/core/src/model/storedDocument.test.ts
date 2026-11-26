import { existsSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { StoredDocumentFault, StoredPortTypeSite } from './storedDocument.js'
import { readParameterList, readPortList, readStoredDefinition, readStoredDefinitionText as rawReadStoredDefinitionText, readParameterListText } from './storedDocument.js'
import { STORED_DOCUMENT_RULES } from './storedDocumentGrammar.js'

interface CorpusCase {
  name: string
  reads: 'definition' | 'parameters'
  text: string
  fault?: Omit<StoredDocumentFault, 'detail'> & { detail?: string }
  sites?: StoredPortTypeSite[]
}

function findCorpus(): string {
  for (let dir = __dirname; ; dir = dirname(dir)) {
    const candidate = join(dir, 'contracts', 'workflow-documents.json')
    if (existsSync(candidate)) return candidate
    if (dirname(dir) === dir) throw new Error('contracts/workflow-documents.json not found above ' + __dirname)
  }
}

const cases = (JSON.parse(readFileSync(findCorpus(), 'utf8')) as { cases: CorpusCase[] }).cases
const units = (text: string): string => text.replace(/⟨([0-9A-F]{4})⟩/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
function readStoredDefinitionText(text: string) {
  const completeCanvas = (canvas: unknown): void => {
    if (!canvas || typeof canvas !== 'object' || Array.isArray(canvas)) return
    const value = canvas as Record<string, unknown>
    if (!Object.hasOwn(value, 'edges')) value['edges'] = []
    if (Array.isArray(value['nodes'])) for (const node of value['nodes']) {
      if (!node || typeof node !== 'object' || Array.isArray(node)) continue
      const branches = (node as Record<string, unknown>)['subCanvases']
      if (branches && typeof branches === 'object' && !Array.isArray(branches)) Object.values(branches).forEach(completeCanvas)
    }
  }
  const value: unknown = JSON.parse(text)
  completeCanvas(value)
  return rawReadStoredDefinitionText(JSON.stringify(value))
}
const read = (row: CorpusCase) =>
  row.reads === 'definition' ? rawReadStoredDefinitionText(units(row.text)) : readParameterListText(units(row.text), 'input')

describe('the stored-document corpus', () => {
  it.each(cases.map((c) => [c.name, c] as const))('%s', (_, row) => {
    const result = read(row)
    if (row.fault) {
      expect(result.ok).toBe(false)
      if (result.ok) return
      const { detail, ...where } = row.fault
      expect({
        rule: result.fault.rule, location: result.fault.location, nodeId: result.fault.nodeId,
        ...(result.fault.canvasPath ? { canvasPath: result.fault.canvasPath } : {}),
      }).toEqual(where)
      if (detail !== undefined) expect(result.fault.detail).toBe(detail)
    } else {
      expect(result.ok).toBe(true)
      if (result.ok) expect(result.sites).toEqual(row.sites)
    }
  })

  it('accepts node ids that differ only in case', () => {
    const result = readStoredDefinitionText('{"nodes":[{"id":"Light","position":{"x":0,"y":0},"configuration":{}},{"id":"light","position":{"x":0,"y":0},"configuration":{}}]}')

    expect(result.ok).toBe(true)
    if (result.ok) expect(result.definition.nodes.map((node) => node.id)).toEqual(['Light', 'light'])
  })

  it('refuses an empty name in a port list', () => {
    expect(readStoredDefinitionText('{"nodes":[{"id":"a","configuration":{"InputPorts":"[{\\"name\\":\\"\\",\\"type\\":\\"Int\\"}]"}}]}')).toMatchObject({
      ok: false, fault: { rule: 'L7' },
    })
  })

  it.each([
    ['canvas.nodes absent', '{"edges":[]}', '/nodes'],
    ['canvas.nodes null', '{"nodes":null}', '/nodes'],
    ['node.id absent', '{"nodes":[{"blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}]}', '/nodes/0/id'],
    ['node.id empty', '{"nodes":[{"id":"","position":{"x":0,"y":0},"configuration":{}}]}', '/nodes/0/id'],
    ['node.position absent', '{"nodes":[{"id":"a","configuration":{}}]}', '/nodes/0/position'],
    ['position.x absent', '{"nodes":[{"id":"a","position":{"y":0},"configuration":{}}]}', '/nodes/0/position/x'],
    ['position.y absent', '{"nodes":[{"id":"a","position":{"x":0},"configuration":{}}]}', '/nodes/0/position/y'],
    ['configuration absent', '{"nodes":[{"id":"a","position":{"x":0,"y":0}}]}', '/nodes/0/configuration'],
    ['subCanvases null', '{"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{},"subCanvases":null}]}', '/nodes/0/subCanvases'],
  ])('refuses required or canonical stored form: %s', (_name, source, location) => {
    expect(readStoredDefinitionText(source)).toMatchObject({ ok: false, fault: { rule: 'L7', location } })
  })

  it('refuses null optional collections, blank passthrough names, absent declared output names and null policy JSON', () => {
    const invalid = [
      ['subCanvases', '{"subCanvases":null}'],
      ['passthrough', '{"passthrough":[""]}'],
      ['outputPorts', '{"outputPorts":[{"type":"Int"}]}'],
      ['errorPolicy', '{"errorPolicy":{"policy":"Skip","defaultOutputJson":null}}'],
    ] as const
    for (const [field, rest] of invalid) {
      const source = `{"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{},${rest.slice(1, -1)}}]}`
      expect(readStoredDefinitionText(source), field).toMatchObject({ ok: false, fault: { rule: 'L7' } })
    }
  })

  it('grades every rule, L3 included', () => {
    const rules = new Set(cases.flatMap((c) => (c.fault ? [c.fault.rule] : [])))
    expect([...rules].sort()).toEqual(Object.values(STORED_DOCUMENT_RULES).sort())
  })

  it('refuses dotted node ids, port names and parameters with the dotted-name rule', () => {
    expect(readStoredDefinitionText('{"nodes":[{"id":"node.one"}]}')).toMatchObject({ ok: false, fault: { rule: 'dotted-name', location: '/nodes/0/id', nodeId: 'node.one' } })
    expect(readStoredDefinitionText('{"nodes":[{"id":"n","configuration":{"InputPorts":"[{\\"name\\":\\"in.one\\",\\"type\\":\\"Int\\"}]"}}]}')).toMatchObject({ ok: false, fault: { rule: 'dotted-name', location: '/nodes/0/configuration/InputPorts#/0/name', nodeId: 'n' } })
    expect(readParameterListText('[{"name":"p.one","type":"Int"}]', 'input')).toMatchObject({ ok: false, fault: { rule: 'dotted-name', location: '/0/name' } })
  })
})

describe('Open file type arguments', () => {
  it('Open file accepts type argument terms', () => {
    const result = readStoredDefinitionText('{"nodes":[{"id":"n","position":{"x":0,"y":0},"configuration":{},"typeArguments":{"T":"Collection<Int>"}}],"edges":[]}')

    expect(result.ok).toBe(true)
    if (result.ok) expect(result.sites).toContainEqual(expect.objectContaining({ site: 'TypeArgument', portName: 'T', rawType: 'Collection<Int>' }))
  })

  it('Open file accepts a node without type arguments', () => {
    expect(readStoredDefinitionText('{"nodes":[{"id":"n","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}'))
      .toMatchObject({ ok: true })
  })
})

describe('projection type sites', () => {
  const edge = (id: string, target: string, input: string, type: string): string =>
    `{"id":"${id}","sourceNodeId":"a","targetNodeId":"${target}","mappings":{"${input}":{"type":"direct","sourceOutputName":"V","projection":{"path":"$.x","type":"${type}"}}}}`
  const nodes = (inner = ''): string => `"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{}},{"id":"b","position":{"x":0,"y":0},"configuration":{}},{"id":"c","position":{"x":0,"y":0},"configuration":{}${inner}}]`

  it('are counted per canvas in document order, each naming the node its connection enters', () => {
    const inner = ',"subCanvases":{"Body":{"nodes":[{"id":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[' + edge('i', 'x', 'Q', 'String') + ']}}'
    const result = readStoredDefinitionText(`{${nodes(inner)},"edges":[${edge('e1', 'b', 'In', 'Int')},${edge('e2', 'c', 'Out', 'Number')}]}`)

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.sites.filter((site) => site.site === 'ProjectionType').map(({ location, nodeId, portName, index, rawType }) => ({ location, nodeId, portName, index, rawType }))).toEqual([
      { location: '/nodes/2/subCanvases/Body/edges/0/mappings/Q/projection/type', nodeId: 'x', portName: 'Q', index: 0, rawType: 'String' },
      { location: '/edges/0/mappings/In/projection/type', nodeId: 'b', portName: 'In', index: 0, rawType: 'Int' },
      { location: '/edges/1/mappings/Out/projection/type', nodeId: 'c', portName: 'Out', index: 1, rawType: 'Number' },
    ])
  })

  it('are not recorded for a projection with no type, and the document still reads', () => {
    const result = readStoredDefinitionText(`{${nodes()},"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":{"In":{"type":"direct","sourceOutputName":"V","projection":{"path":"$.x"}}}}]}`)
    expect(result).toMatchObject({ ok: true })
    if (result.ok) expect(result.sites).toEqual([])
  })

  it('are refused by their type, never by silence, when the type is not a string', () => {
    const result = readStoredDefinitionText(`{${nodes()},"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":{"In":{"type":"direct","sourceOutputName":"V","projection":{"path":"$.x","type":4}}}}]}`)
    expect(result).toMatchObject({ ok: false, fault: { rule: 'L7', location: '/edges/0/mappings/In/projection/type', detail: 'a number where a port type name is required' } })
  })
})

describe('edge and mapping rows', () => {
  const valid = (edge: string): string => `{"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{}},{"id":"b","position":{"x":0,"y":0},"configuration":{}}],"edges":[${edge}]}`

  it.each([
    ['edges absent', '{"nodes":[]}', '/edges'],
    ['edge id absent', valid('{"sourceNodeId":"a","targetNodeId":"b","mappings":{}}'), '/edges/0/id'],
    ['edge id empty', valid('{"id":"","sourceNodeId":"a","targetNodeId":"b","mappings":{}}'), '/edges/0/id'],
    ['source endpoint absent', valid('{"id":"e","targetNodeId":"b","mappings":{}}'), '/edges/0/sourceNodeId'],
    ['target endpoint empty', valid('{"id":"e","sourceNodeId":"a","targetNodeId":"","mappings":{}}'), '/edges/0/targetNodeId'],
    ['mappings absent', valid('{"id":"e","sourceNodeId":"a","targetNodeId":"b"}'), '/edges/0/mappings'],
    ['array mapping refused', valid('{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":[]}'), '/edges/0/mappings'],
    ['mapping type absent', valid('{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":{"In":{}}}'), '/edges/0/mappings/In/type'],
    ['literal value null refused', valid('{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":{"In":{"type":"literal","literalValue":null}}}'), '/edges/0/mappings/In/literalValue'],
  ])('refuses %s at its own location', (_name, text, location) => {
    expect(rawReadStoredDefinitionText(text)).toMatchObject({ ok: false, fault: { location } })
  })

  it('refuses duplicate edge ids and the outputMappings key under named rules', () => {
    const duplicate = readStoredDefinitionText(valid('{"id":"e","sourceNodeId":"a","targetNodeId":"b","mappings":{}},{"id":"e","sourceNodeId":"b","targetNodeId":"a","mappings":{}}'))
    expect(duplicate).toMatchObject({ ok: false, fault: { rule: 'duplicate-edge-id', location: '/edges/1/id' } })
    expect(rawReadStoredDefinitionText('{"nodes":[],"edges":[],"outputMappings":[]}')).toMatchObject({
      ok: false, fault: { rule: 'output-mappings-unread', location: '/outputMappings' },
    })
  })
})

describe('reading a value the editor holds', () => {
  it('reads a property holding undefined as absent, as JSON.stringify does', () => {
    expect(readStoredDefinition({ nodes: [{ id: 'a', position: { x: 0, y: 0 }, configuration: {}, config: undefined }], edges: [] })).toMatchObject({ ok: true, sites: [] })
  })

  it('reads an array element holding undefined as null, as JSON.stringify writes it', () => {
    expect(readStoredDefinition({ nodes: [undefined] })).toEqual({
      ok: false,
      fault: { rule: 'L7', location: '/nodes/0', detail: 'null where a node is required', nodeId: null },
    })
  })

  it('refuses no document at all', () => {
    expect(readStoredDefinition(undefined)).toMatchObject({ ok: false, fault: { rule: 'L1', location: '' } })
  })

  it('reads absent and null parameter lists as none, and names the output side', () => {
    expect(readParameterList(undefined, 'output')).toEqual({ ok: true, sites: [], entries: [] })
    expect(readParameterList([{ name: 'r', type: 'Int' }], 'output')).toMatchObject({ ok: true, sites: [{ site: 'OutputParameter', location: '/0/type' }] })
  })

  it('reads an absent parameter list text as none and refuses a blank one and the JSON null', () => {
    expect(readParameterListText(null, 'input')).toEqual({ ok: true, sites: [], entries: [] })
    expect(readParameterListText(undefined, 'output')).toEqual({ ok: true, sites: [], entries: [] })
    expect(readParameterListText('[]', 'input')).toEqual({ ok: true, sites: [], entries: [] })
    for (const blank of ['', ' \n\t']) {
      expect(readParameterListText(blank, 'input')).toMatchObject({ ok: false, fault: { rule: 'L7', location: '', detail: expect.stringContaining('is blank') } })
    }
    expect(readParameterListText('null', 'output')).toMatchObject({ ok: false, fault: { rule: 'L7', detail: expect.stringContaining('JSON null') } })
  })

  it('reads a port list on its own, with no node and locations after #', () => {
    expect(readPortList('[{"name":"o","type":"Int"}]', 'output')).toEqual({
      ok: true,
      sites: [{ site: 'DynamicOutputPort', location: '#/0/type', nodeId: null, portName: 'o', index: 0, rawType: 'Int', isRequired: false }],
      entries: [{ name: 'o', type: 'Int' }],
    })
    expect(readPortList('', 'input')).toMatchObject({ ok: false, fault: { rule: 'L1' } })
    expect(readPortList('[5]', 'input')).toMatchObject({ ok: false, fault: { rule: 'L7', location: '#/0' } })
  })

  it('keeps a __proto__ key as an own property of the value it builds', () => {
    const read = readStoredDefinitionText('{"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{"__proto__":{"x":1}}}]}')
    const configuration = read.ok ? read.definition.nodes[0]?.configuration : undefined
    expect(Object.keys(configuration ?? {})).toEqual(['__proto__'])
    expect(Object.getPrototypeOf(configuration)).toBe(Object.prototype)
  })

  it('refuses a value nested deeper than a text may be, at the depth the text reader refuses', () => {
    const nested = (levels: number): unknown => ({ nodes: [{ id: 'a', position: { x: 0, y: 0 }, configuration: { k: JSON.parse('['.repeat(levels) + ']'.repeat(levels)) as unknown } }], edges: [] })
    const verdicts = [59, 60, 61, 62].map((levels) => {
      const value = readStoredDefinition(nested(levels))
      expect(value).toEqual(readStoredDefinitionText(JSON.stringify(nested(levels))))
      return value.ok ? 'ok' : value.fault.rule
    })
    expect(verdicts).toEqual(['ok', 'ok', 'L2', 'L2'])
  })

  it('refuses an unpaired surrogate in a string or a property name the editor holds', () => {
    expect(readStoredDefinition({ nodes: [{ id: 'a\ud800', blockTypeId: 'x' }] })).toMatchObject({
      ok: false,
      fault: { rule: 'L4', location: '/nodes/0/id', detail: 'a string with an unpaired surrogate' },
    })
    expect(readStoredDefinition({ nodes: [{ id: 'a', blockTypeId: 'x', configuration: { '\udc00k': 1 } }] })).toMatchObject({
      ok: false,
      fault: { rule: 'L4', location: '/nodes/0/configuration', detail: 'a property name with an unpaired surrogate' },
    })
  })

  it('refuses a value in memory that is not JSON, wherever it sits', () => {
    class Point { x = 1 }
    const notJson: [string, unknown][] = [
      ['a bigint', 5n], ['a symbol', Symbol('s')], ['a function', () => 0], ['a Date', new Date(0)],
      ['a Map', new Map([['k', 1]])], ['a class instance', new Point()],
      ['an array with its own toJSON', Object.assign([1, 2], { toJSON: () => [9] })],
      ['a plain object with its own toJSON', { k: 1, toJSON: () => ({ k: 2 }) }],
      ['a plain object with a toJSON it does not enumerate', Object.defineProperty({ k: 1 }, 'toJSON', { value: () => 2, enumerable: false })],
      ['a prototype-free object with a toJSON', Object.assign(Object.create(null) as Record<string, unknown>, { toJSON: () => 3 })],
      ['an array that inherits a toJSON', Object.setPrototypeOf([1], Object.assign(Object.create(Array.prototype) as object, { toJSON: () => [] }))],
    ]
    for (const [what, inner] of notJson) {
      for (const [where, document] of [
        ['a configuration value', { nodes: [{ id: 'a', blockTypeId: 'x', configuration: { k: inner } }], edges: [] }],
        ['a literal value', { nodes: [], edges: [{ id: 'e', sourceNodeId: 'a', targetNodeId: 'a', mappings: { In: { type: 'literal', literalValue: [inner] } } }] }],
        ['an unlisted root key', { nodes: [], edges: [], notes: inner }],
      ] as const) {
        const read = readStoredDefinition(document)
        expect(read.ok, `${what} in ${where}`).toBe(false)
        if (!read.ok) expect(read.fault, `${what} in ${where}`).toMatchObject({ rule: where === 'an unlisted root key' ? 'stored-key-unknown' : 'L7' })
      }
    }
    expect(readParameterList([{ name: 'p', type: 'Int', isRequired: 1n }], 'input')).toMatchObject({
      ok: false, fault: { rule: 'L7', location: '/0/isRequired', detail: 'a value that is not JSON where true or false is required' },
    })
    expect(readStoredDefinition({ nodes: [], edges: [], notes: { n: Number.NaN } })).toMatchObject({
      ok: false, fault: { rule: 'stored-key-unknown', location: '/notes' },
    })
  })

  it('reads plain objects, prototype-free ones, and a hole in an array as null', () => {
    const bare = Object.assign(Object.create(null) as Record<string, unknown>, { k: 1 })
    const holed: unknown[] = [1]
    holed[2] = 3
    const read = readStoredDefinition({ nodes: [{ id: 'a', blockTypeId: 'x', position: { x: 0, y: 0 }, configuration: { bare, holed } }], edges: [] })
    expect(read.ok).toBe(true)
    if (read.ok) expect(read.definition.nodes[0]?.configuration).toEqual({ bare: { k: 1 }, holed: [1, null, 3] })
  })

  it('never throws on a value that is not JSON', () => {
    expect(readStoredDefinition({ nodes: [{ id: 'a', position: { x: () => 0, y: 0 } }] })).toMatchObject({
      ok: false,
      fault: { rule: 'L7', location: '/nodes/0/position/x', detail: 'a value that is not JSON where a number is required' },
    })
  })
})

describe('no input throws', () => {
  let seed = 12_345
  const next = (n: number): number => {
    seed = (seed * 1_103_515_245 + 12_345) % 2_147_483_648
    return seed % n
  }
  const keys = ['nodes', 'Nodes', 'id', 'blockTypeId', 'config', 'configuration', 'InputPorts', 'OutputPorts', 'outputPorts',
    'subCanvases', 'edges', 'connections', 'mappings', 'type', 'name', 'isRequired', 'position', 'x', 'errorPolicy', 'policy', '\ud800']
  const value = (depth: number): unknown => {
    switch (depth > 6 ? next(5) : next(9)) {
      case 0: return null
      case 1: return next(2) === 0
      case 2: return next(1000) - 500
      case 3: return keys[next(keys.length)]
      case 4: return JSON.stringify(value(depth + 1))
      case 5: case 6: return Array.from({ length: next(4) }, () => value(depth + 1))
      default: return Object.fromEntries(Array.from({ length: next(5) }, () => [keys[next(keys.length)], value(depth + 1)]))
    }
  }

  it('reads 5000 seeded documents, values and texts, parameter lists and port lists, without an exception', () => {
    for (let i = 0; i < 5000; i++) {
      const document = value(0)
      const documentText = JSON.stringify(document)
      const cut = documentText.slice(0, next(documentText.length + 1))
      expect(() => {
        readStoredDefinition(document)
        rawReadStoredDefinitionText(documentText)
        rawReadStoredDefinitionText(cut)
        readParameterList(document, 'input')
        readParameterListText(cut, 'output')
        readPortList(documentText, 'input')
      }, documentText).not.toThrow()
    }
  })
})
