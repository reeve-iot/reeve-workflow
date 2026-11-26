import { existsSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { BUILTIN_PORT_TYPES } from './builtins.js'
import { PORT_CAPABILITIES } from './capabilities.js'
import type { PortTerm } from './grammar.js'
import { parsePortTerm } from './grammar.js'
import type { PortLiteralForm } from './literalForm.js'
import { createPortTypeRegistry } from './registry.js'
import type { PortTypeConstructorDefinition, PortTypeDefinition } from './types.js'

interface Row {
  name: string
  kind: 'alias' | 'assignable' | 'widening' | 'declare' | 'constructor' | 'term' | 'row' | 'claim' | 'literal' | 'reference'
  alias?: string
  target?: string
  from?: string
  widensTo?: string[]
  type?: string
  head?: string
  allowedArguments?: string[]
  declaredAtoms?: string[]
  after?: PortTypeConstructorDefinition[]
  constructors?: PortTypeConstructorDefinition[]
  declaredAliases?: { alias: string; target: string }[]
  declaredWidenings?: { from: string; widensTo: string[] }[]
  term?: string
  subject?: 'atom' | 'constructor'
  claims?: string[]
  literal?: PortLiteralForm
  records?: { owner: string; atom?: string; constructorName?: string; allowedArguments?: string[]; widensTo?: string[] }[]
  backendRefusal?: string
  backend: unknown
}

function findCorpus(): string {
  for (let dir = __dirname; ; dir = dirname(dir)) {
    const candidate = join(dir, 'contracts', 'port-type-registration.json')
    if (existsSync(candidate)) return candidate
    if (dirname(dir) === dir) throw new Error('contracts/port-type-registration.json not found above ' + __dirname)
  }
}

interface CapabilitiesSection {
  capabilities: { name: string; associated?: string[] }[]
  vocabulary: {
    atoms: Record<string, { implements: string[] }>
    constructors: Record<string, { allowedArguments: string[]; implements: string[] }>
  }
  vectors: { term: string; implements: string[]; item?: string }[]
  refused: string[]
}

const corpus = JSON.parse(readFileSync(findCorpus(), 'utf8')) as {
  termVocabulary: { atoms: string[]; constructors: PortTypeConstructorDefinition[] }
  capabilities: CapabilitiesSection
  cases: Row[]
}
const rows = corpus.cases

function withAlias(alias: string, target: string, base: readonly PortTypeDefinition[] = BUILTIN_PORT_TYPES): PortTypeDefinition[] {
  const known = base.some((d) => d.name === target)
  return known
    ? base.map((d) => (d.name === target ? { ...d, aliases: [...(d.aliases ?? []), alias] } : d))
    : [...base, { name: target, aliases: [alias] }]
}

function withWidening(from: string, widensTo: string[], base: readonly PortTypeDefinition[] = BUILTIN_PORT_TYPES): PortTypeDefinition[] {
  const known = base.some((d) => d.name === from)
  return known
    ? base.map((d) => (d.name === from ? { ...d, widensTo: [...(d.widensTo ?? []), ...widensTo] } : d))
    : [...base, { name: from, widensTo }]
}

function declaredBy(row: Row): PortTypeDefinition[] {
  let defs: PortTypeDefinition[] = [...BUILTIN_PORT_TYPES, ...(row.declaredAtoms ?? []).map((name) => ({ name }))]
  for (const { alias, target } of row.declaredAliases ?? []) defs = withAlias(alias, target, defs)
  for (const { from, widensTo } of row.declaredWidenings ?? []) defs = withWidening(from, widensTo, defs)
  return defs
}


function registryOf(row: Row): ReturnType<typeof createPortTypeRegistry> {
  switch (row.kind) {
    case 'alias':
      return createPortTypeRegistry(withAlias(row.alias ?? '', row.target ?? ''), row.constructors ?? [])
    case 'widening':
      return createPortTypeRegistry(withWidening(row.from ?? '', row.widensTo ?? []), row.constructors ?? [])
    case 'declare':
      return createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: row.type ?? '' }], row.constructors ?? [])
    case 'constructor':
      return createPortTypeRegistry(declaredBy(row), [...(row.after ?? []), { name: row.head ?? '', allowedArguments: row.allowedArguments ?? [] }])
    case 'row': {
      const atoms: PortTypeDefinition[] = (row.declaredAtoms ?? []).map((name) => ({
        name,
        aliases: (row.declaredAliases ?? []).filter((a) => a.target === name).map((a) => a.alias),
      }))
      return createPortTypeRegistry([...BUILTIN_PORT_TYPES, ...atoms, { name: row.from ?? '', widensTo: row.widensTo ?? [] }], row.constructors ?? [])
    }
    case 'claim': {
      const listed = (row.claims ?? []) as never
      return row.subject === 'constructor'
        ? createPortTypeRegistry(BUILTIN_PORT_TYPES, [{ name: row.head ?? '', allowedArguments: row.allowedArguments ?? [], implements: listed }])
        : createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: row.head ?? '', implements: listed }])
    }
    case 'literal':
      return row.subject === 'constructor'
        ? createPortTypeRegistry(BUILTIN_PORT_TYPES, [{ name: row.head ?? '', allowedArguments: row.allowedArguments ?? [], literal: row.literal }])
        : createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: row.head ?? '', literal: row.literal }])
    case 'reference': {
      const owned = row.records ?? []
      return createPortTypeRegistry(
        [...BUILTIN_PORT_TYPES, ...owned.filter((r) => r.atom !== undefined).map((r) => ({ name: r.atom ?? '', widensTo: r.widensTo ?? [] }))],
        owned.filter((r) => r.constructorName !== undefined).map((r) => ({ name: r.constructorName ?? '', allowedArguments: r.allowedArguments ?? [] })),
      )
    }
    default:
      throw new Error(`kind '${row.kind}' is not a record`)
  }
}

describe('the registration corpus', () => {
  const records = rows.filter((r) => r.backend === 'accepts' && r.kind !== 'term')
  const termVocabulary = createPortTypeRegistry(
    [...BUILTIN_PORT_TYPES, ...corpus.termVocabulary.atoms.map((name) => ({ name }))],
    corpus.termVocabulary.constructors,
  )

  it('carries accepted records of every kind the editor reads', () => {
    expect(new Set(records.map((r) => r.kind))).toEqual(new Set(['alias', 'widening', 'declare', 'constructor', 'row', 'claim', 'literal', 'reference']))
  })

  it.each(records.map((r) => [r.name, r] as const))('reads the accepted record: %s', (_, row) => {
    const registry = registryOf(row)
    if (row.kind === 'alias') expect(registry.normalize(row.alias ?? '')).toBe(row.target)
    if (row.kind === 'declare') expect(registry.has(row.type ?? '')).toBe(true)
    if (row.kind === 'literal') expect(registry.literalFormOf(row.head ?? '')).toEqual(row.literal)
  })

  it.each(rows.filter((r) => r.kind === 'term').map((r) => [r.name, r] as const))('answers the term as the server does: %s', (_, row) => {
    const parsed = parsePortTerm(row.term, 'document')
    if (!parsed.ok) throw new Error(`${row.term ?? ''} is not a term`)
    const refusal = termVocabulary.refusalOf(parsed.term)
    if (row.backend === 'accepts') expect(refusal).toBeUndefined()
    else expect(`${refusal?.code ?? ''}: ${refusal?.message ?? ''}`).toBe(row.backendRefusal)
  })
})

describe('the capabilities section of the registration corpus', () => {
  const section = corpus.capabilities
  const read = (text: string): PortTerm => {
    const parsed = parsePortTerm(text, 'document')
    if (!parsed.ok) throw new Error(`${text} is not a term: ${parsed.fault}`)
    return parsed.term
  }
  const registry = createPortTypeRegistry(
    [
      ...BUILTIN_PORT_TYPES,
      ...Object.entries(section.vocabulary.atoms).map(([name, entry]) => ({ name, implements: entry.implements as never })),
    ],
    Object.entries(section.vocabulary.constructors).map(([name, entry]) => ({
      name,
      allowedArguments: entry.allowedArguments,
      implements: entry.implements as never,
    })),
  )

  it('has the five capabilities, Iterable carrying an Item', () => {
    expect(PORT_CAPABILITIES.map((c) => ({ name: c.name, ...(c.associated.length > 0 ? { associated: [...c.associated] } : {}) }))).toEqual(
      section.capabilities,
    )
  })

  it('implements, for every term, exactly the capabilities the corpus states', () => {
    const wrong = section.vectors.flatMap((v) => {
      const mine = PORT_CAPABILITIES.map((c) => c.name).filter((name) => registry.implementsCapability(read(v.term), name))
      return mine.join() === v.implements.join() ? [] : [`${v.term}: expected [${v.implements.join()}], got [${mine.join()}]`]
    })
    expect(wrong).toEqual([])
    expect(section.vectors.length).toBeGreaterThan(40)
  })

  it('refuses membership for every term that names something unregistered', () => {
    expect(section.refused.length).toBeGreaterThan(0)
    for (const text of section.refused) {
      expect(() => registry.implementsCapability(read(text), 'Iterable'), text).toThrow(/^port-type-unregistered:/)
    }
  })
})
