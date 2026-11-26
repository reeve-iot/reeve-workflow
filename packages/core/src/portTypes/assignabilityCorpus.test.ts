import { existsSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { termImplements } from './capabilities.js'
import type { PortTermClaims } from './capabilities.js'
import type { PortTerm } from './grammar.js'
import { formatPortTerm, parsePortTerm } from './grammar.js'

interface Vector { rule: string; from: string | null; to: string; assignable: boolean }

function findCorpus(): string {
  for (let dir = __dirname; ; dir = dirname(dir)) {
    const candidate = join(dir, 'contracts', 'port-type-assignability.json')
    if (existsSync(candidate)) return candidate
    if (dirname(dir) === dir) throw new Error('contracts/port-type-assignability.json not found above ' + __dirname)
  }
}

const corpus = JSON.parse(readFileSync(findCorpus(), 'utf8')) as {
  terms: {
    vocabulary: { atoms: Record<string, { json: boolean }>; constructors: Record<string, { json: boolean }> }
    vectors: Vector[]
  }
}
const { vocabulary, vectors } = corpus.terms

const read = (text: string): PortTerm => {
  const parsed = parsePortTerm(text, 'document')
  if (!parsed.ok) throw new Error(`${text} is not a term: ${parsed.fault} at ${String(parsed.position)}`)
  return parsed.term
}

const claims: PortTermClaims = {
  claims: (head, capability) => {
    if (capability !== 'JsonSerializable' || head.kind === 'null' || head.kind === 'parameter') return false
    const entry = head.kind === 'atom' ? vocabulary.atoms[head.name] : vocabulary.constructors[head.name]
    return entry?.json === true
  },
}

describe('contracts/port-type-assignability.json, the editor half', () => {
  it('reads every name of every vector with the one grammar and writes it back as it stands', () => {
    const names = vectors.flatMap((vector) => [vector.from, vector.to]).filter((name): name is string => name !== null)
    const wrong = names.filter((name) => formatPortTerm(read(name)) !== name)

    expect(names.length).toBeGreaterThan(300)
    expect(wrong).toEqual([])
  })

  it('answers an A4 or A2 row to Json as JsonSerializable membership of its source, a map included', () => {
    const rows = vectors.filter((vector): vector is Vector & { from: string } =>
      vector.from !== null && (vector.to === 'Json?' || (vector.to === 'Json' && !vector.from.endsWith('?'))))
    const wrong = rows.filter((row) => termImplements(read(row.from), 'JsonSerializable', claims) !== row.assignable)

    expect(rows.filter((row) => row.from.includes('Map<')).length).toBeGreaterThan(8)
    expect(wrong).toEqual([])
  })

  it('is Iterable for a collection and for no map, whatever its value is', () => {
    const maps = vectors.flatMap((vector) => [vector.from, vector.to]).filter((name): name is string => name?.includes('Map<') === true)

    expect(maps.length).toBeGreaterThan(40)
    for (const name of new Set(maps)) {
      const term = read(name)
      expect(termImplements(term, 'Iterable', claims), name).toBe(term.kind === 'constructor' && term.name === 'Collection' && !term.nullable)
    }
  })
})
