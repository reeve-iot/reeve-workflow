import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowParameter } from '../model/workflow.js'

export interface GeneratedDocument {
  readonly text: string
  /** Pointers of the members the editor refuses to open: the load must fault at one of them. */
  readonly undrawable: readonly string[]
  readonly inputParams: WorkflowParameter[]
  readonly outputParams: WorkflowParameter[]
}

export const GRAMMAR_BLOCK_TYPES: BlockTypeDescriptor[] = [
  {
    blockTypeId: 'Widget', displayName: 'Widget', category: 'Test',
    inputs: [{ name: 'In', displayName: 'In', isRequired: false, type: 'String' }],
    outputs: [{ name: 'Out', displayName: 'Out', isRequired: false, type: 'String' }],
  },
  {
    blockTypeId: 'Loop', displayName: 'Loop', category: 'Test', inputs: [], outputs: [],
    subCanvasNames: ['Body'], subCanvasInputs: { Body: [] }, subCanvasOutputs: { Body: [] },
  },
]

const ENTRY_IDS = ['__workflow_input__', 'start', 'in-3'] as const
const EXIT_IDS = ['__workflow_output__', 'wo', 'out-3'] as const

function prng(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/** `count` documents from `seed`; every returned readable document uses the current stored grammar. */
export function generateDocuments(seed: number, count: number): GeneratedDocument[] {
  const random = prng(seed)
  const pick = <T>(items: readonly T[]): T => items[Math.floor(random() * items.length)] as T
  const chance = (p: number): boolean => random() < p
  let serial = 0

  const member = (target: Record<string, unknown>, key: string, present: () => unknown, nullable: boolean): void => {
    const roll = random()
    if (roll < 0.2) return
    if (nullable && roll < 0.35) target[key] = null
    else target[key] = present()
  }

  function one(): GeneratedDocument {
    const undrawable: string[] = []
    const canvas = (at: string, depth: number, root: boolean): Record<string, unknown> => {
      const result: Record<string, unknown> = {}
      const ids: string[] = []
      if (chance(0.02)) result['zCanvas'] = { kept: [1, 'two', null] }
      result['nodes'] = (() => {
        const nodes: unknown[] = []
        if (chance(root ? 0.6 : 0.5)) nodes.push({ id: pick(ENTRY_IDS), blockTypeId: 'WorkflowInput', position: { x: 50, y: 100 }, configuration: {} })
        if (chance(0.05)) nodes.push({ id: '__workflow_input__', blockTypeId: 'Widget', position: { x: 5, y: 5 }, configuration: {} })
        const count = Math.floor(random() * 4)
        for (let i = 0; i < count; i++) nodes.push(node(`${at}/nodes/${String(nodes.length)}`, depth))
        return nodes
      })()
      for (const n of (Array.isArray(result['nodes']) ? result['nodes'] : []) as Record<string, unknown>[]) {
        if (typeof n['id'] === 'string') ids.push(n['id'])
      }
      result['edges'] = (() => {
        const count = Math.floor(random() * 4)
        return Array.from({ length: count }, (_, i) => edge(`${at}/edges/${String(i)}`, ids))
      })()
      return result
    }

    const node = (at: string, depth: number): Record<string, unknown> => {
      const result: Record<string, unknown> = {}
      const id = `n${String(serial++)}`
      result['id'] = id
      result['blockTypeId'] = pick(depth < 2 ? ['Widget', 'Loop', 'Unknown'] : ['Widget', 'Unknown'])
      result['position'] = { x: Math.floor(random() * 500), y: -Math.floor(random() * 50) }
      result['configuration'] = pick([{}, { Mode: 'fast', Count: 2, Nested: { deep: [true, null] } }])
      if (result['blockTypeId'] === 'Loop' || chance(0.1)) {
        member(result, 'subCanvases', () => ({ Body: canvas(`${at}/subCanvases/Body`, depth + 1, false) }), false)
      }
      if (chance(0.3)) member(result, 'passthrough', () => ['Carried'], false)
      if (chance(0.3)) {
        member(result, 'outputPorts', () => [{
          name: 'Result', displayName: 'Result', type: pick(['String', null]), sources: pick([{ Body: 'Out' }, null]),
        }], false)
      }
      if (chance(0.3)) member(result, 'errorPolicy', () => ({ policy: pick(['Skip', 'EmitError']) }), false)
      if (chance(0.02)) result['zNode'] = { kept: 1 }
      return result
    }

    const edge = (_at: string, ids: string[]): Record<string, unknown> => {
      const result: Record<string, unknown> = { id: `e${String(serial++)}` }
      result['sourceNodeId'] = ids.length > 0 && chance(0.9) ? pick(ids) : 'ghost'
      result['targetNodeId'] = ids.length > 0 ? pick(ids) : 'ghost'
      const mappings: Record<string, unknown> = {}
      for (const input of ['In', 'Other'].slice(0, 1 + Math.floor(random() * 2))) mappings[input] = mapping()
      return { ...result, mappings }
    }

    const mapping = (): Record<string, unknown> => {
      const result: Record<string, unknown> = {}
      const type = pick(['direct', 'literal'])
      result['type'] = type
      if (type === 'literal') {
        member(result, 'literalValue', () => pick([5, 'text', [1, 2], { a: 1 }, false]), false)
      } else {
        result['sourceOutputName'] = pick(['Out', 'Missing'])
      }
      if (chance(0.02)) result['zMapping'] = 'extra'
      return result
    }

    const root = canvas('', 0, true)
    const param = (name: string): WorkflowParameter => ({ name, displayName: name, type: 'String', isRequired: false })
    const outputParams = chance(0.5) ? [param('Result')] : []
    if (Array.isArray(root['nodes']) && chance(0.5)) {
      root['nodes'].push({ id: pick(EXIT_IDS), blockTypeId: 'WorkflowOutput', position: { x: 800, y: 100 }, configuration: {} })
    }
    return { text: JSON.stringify(root), undrawable, inputParams: chance(0.5) ? [param('Room')] : [], outputParams }
  }

  return Array.from({ length: count }, one)
}

export type EngineRead = { literal: true; value: unknown } | { literal: false; source: string } | null

/** ReadMappings' object branch (WorkflowGraphHelpers.ReadMappings) written out on its own: null where a run drops the
 * mapping, a wire whose source is blank. */
export function engineReadsMapping(m: Record<string, unknown>): EngineRead {
  if (typeof m['type'] === 'string' && m['type'].toLowerCase() === 'literal') return { literal: true, value: m['literalValue'] }
  const raw = m['sourceOutputName']
  const source = typeof raw === 'string' ? raw : ''
  return source.trim() === '' ? null : { literal: false, source }
}

/** Every mapping form a run reads: each type × each source spelling × each literal value, with `out` the output
 * the source has and `Gone` one it lacks. `literals: false` keeps one literal value per form. */
export function mappingForms(out = 'Out', literals = true): Record<string, unknown>[] {
  const types: unknown[] = ['direct', 'literal', 'Literal', 'LITERAL', 'Direct', 'DIRECT', 'expression', '', null, undefined]
  const sources: Record<string, unknown>[] = [
    {}, { sourceOutputName: out }, { sourceOutputName: 'Gone' }, { sourceOutputName: '' }, { sourceOutputName: ' ' },
    { sourceOutputName: null }, { sourceOutput: out }, { sourceOutput: null },
  ]
  const values: Record<string, unknown>[] = literals ? [{}, { literalValue: 5 }, { literalValue: 'text' }, { literalValue: null }] : [{ literalValue: 'text' }]
  return types.flatMap((type) => sources.flatMap((source) => values.map((literal) => {
    const m: Record<string, unknown> = { ...source, ...literal }
    if (type !== undefined) m['type'] = type
    return m
  })))
}
