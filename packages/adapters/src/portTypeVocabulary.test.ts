import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import type { PortTypeVocabularyResponse } from '@reeve-iot/workflow-client'
import type { PortTerm } from '@reeve-iot/workflow-core'
import { PORT_CAPABILITIES, createPortTypeRegistry, parsePortTerm } from '@reeve-iot/workflow-core'
import { readPortTypeConstructors, readPortTypeVocabulary } from './portTypeVocabulary.js'

interface Wire {
  response: PortTypeVocabularyResponse
  names: string[]
  canonical: string[]
  implements: (string[] | null)[]
}

const wire = JSON.parse(readFileSync(resolve(__dirname, 'port-type-vocabulary.wire.json'), 'utf8')) as Wire
const seeded = () => createPortTypeRegistry(readPortTypeVocabulary(wire.response), readPortTypeConstructors(wire.response))
const capabilitiesOf = (registry: ReturnType<typeof seeded>, term: PortTerm): string[] =>
  PORT_CAPABILITIES.map((c) => c.name).filter((name) => registry.implementsCapability(term, name))

describe('a registry seeded from the published vocabulary', () => {
  it('reads the built-in Map the server lists, with its arity and fixed key, into a registry that holds map terms', () => {
    expect(wire.response.constructors.find((c) => c.name === 'Map')).toMatchObject({ owner: 'library', arity: 2, key: 'String' })
    const registry = seeded()
    const parsed = parsePortTerm('Map<String,Int>', 'document')
    if (!parsed.ok) throw new Error('expected a term')

    expect(registry.refusalOf(parsed.term)).toBeUndefined()
    expect(capabilitiesOf(registry, parsed.term)).toEqual(['Equatable', 'JsonSerializable'])
    expect(registry.constructors().map((c) => c.name)).not.toContain('Map')
  })

  it('reads a sample the size the server wrote', () => {
    expect([wire.names.length, wire.canonical.length, wire.implements.length]).toEqual([73, 73, 73])
  })

  it('publishes each name as the spelling the server and the seeded registry both answer for it', () => {
    const registry = seeded()
    const published = wire.response.types.map((t) => t.name)
    expect(published).toContain('Boolean')
    expect(published.filter((name) => wire.canonical[wire.names.indexOf(name)] !== name || registry.normalize(name) !== name)).toEqual([])
  })

  it('canonicalises every name the server does, spelling included', () => {
    const registry = seeded()
    const differ = wire.names.flatMap((name, i) =>
      registry.normalize(name) === wire.canonical[i] ? [] : [`${name}: ${registry.normalize(name)} vs ${wire.canonical[i]}`])
    expect(differ).toEqual([])
  })

  it('implements, for every name, exactly the capabilities the server says it does', () => {
    const registry = seeded()
    const refuses = (term: PortTerm): boolean => {
      try {
        capabilitiesOf(registry, term)
        return false
      } catch (error) {
        return error instanceof Error && error.message.startsWith('port-type-unregistered:')
      }
    }
    const differ = wire.names.flatMap((name, i) => {
      const parsed = parsePortTerm(name, 'document')
      if (!parsed.ok) return [`${name}: does not parse`]
      const server = wire.implements[i]
      if (server === null || server === undefined) return refuses(parsed.term) ? [] : [`${name}: the server refuses it, the registry answers`]
      if (refuses(parsed.term)) return [`${name}: the registry refuses it, the server answers ${server.join()}`]
      const mine = capabilitiesOf(registry, parsed.term)
      return mine.join() === server.join() ? [] : [`${name}: ${mine.join()} vs ${server.join()}`]
    })
    expect(differ).toEqual([])
    expect(wire.implements.filter((entry) => entry === null).length).toBeGreaterThan(0)
  })

  it('carries a host constructor that claims Equatable through the wire', () => {
    expect(wire.response.constructors.find((c) => c.name === 'Gauge')?.implements).toEqual(['Equatable'])
    expect(wire.implements[wire.names.indexOf('Gauge<Boolean>')]).toEqual(['Equatable'])
    expect(wire.implements[wire.names.indexOf('Collection<Gauge<Boolean>>')]).toEqual(['Iterable', 'Equatable'])
  })

  it('publishes the vocabulary of five capabilities and lists the claims of a host type', () => {
    expect(wire.response.capabilities.map((c) => c.name)).toEqual(['Iterable', 'Numeric', 'Comparable', 'Equatable', 'JsonSerializable'])
    expect(wire.response.types.find((t) => t.name === 'Depot')?.implements).toEqual(['Comparable', 'Equatable', 'JsonSerializable'])
    expect(wire.response.constructors.find((c) => c.name === 'Collection')?.implements).toEqual(['Iterable'])
  })

  it('knows every plain name and alias the server publishes', () => {
    const registry = seeded()
    const published = wire.response.types.filter((t) => !t.name.includes('<')).flatMap((t) => [t.name, ...t.aliases])
    expect(published.filter((name) => !registry.has(name))).toEqual([])
  })

  it('lists every plain name the server publishes, each with its derived collection', () => {
    const names = seeded().list().map((t) => t.name)
    expect(names.filter((n) => !n.includes('<'))).toEqual(wire.response.types.map((t) => t.name).filter((n) => !n.includes('<')))
    expect([names.includes('Collection<Depot>'), names.includes('Collection<Any>'), names.includes('Any'), names.includes('Float')]).toEqual([true, false, false, false])
  })
})

describe('the literal forms the server publishes', () => {
  it('seeds each atom and constructor with the form the server wrote for it, the library atoms with their own', () => {
    const registry = seeded()
    const wrong = [
      ...wire.response.types.filter((t) => !t.name.includes('<')).map((t) => [t.name, t.literal] as const),
      ...wire.response.constructors.filter((c) => c.name !== 'Collection').map((c) => [c.name, c.literal] as const),
    ].filter(([name, literal]) => {
      const held = registry.literalFormOf(name)
      return JSON.stringify(held === undefined ? null : { gradedAs: held.gradedAs, format: held.format ?? null }) !== JSON.stringify(literal)
    })
    expect(wrong).toEqual([])
    expect(wire.response.types.some((t) => t.literal?.format === 'Uuid')).toBe(true)
    expect(wire.response.types.some((t) => t.literal === null)).toBe(true)
  })

  it('grades a literal as the server does: a type with a form by that form, a type with none not at all', () => {
    const registry = seeded()
    expect(registry.isLiteralOf('123e4567-e89b-12d3-a456-426614174000', 'Depot')).toBe(true)
    expect(registry.isLiteralOf(5, 'Depot')).toBe(false)
    expect(registry.isLiteralOf('not-a-uuid', 'Depot')).toBe(true)
    expect(registry.isLiteralOf('x', 'Carrier')).toBe(false)
    expect(registry.isLiteralOf(null, 'Carrier?')).toBe(true)
  })

  it('refuses a form outside the closed set, naming the type', () => {
    const response: PortTypeVocabularyResponse = {
      generation: 0, secretCodec: false, capabilities: [], constructors: [],
      types: [{ name: 'Depot', owner: 'host', aliases: [], widensTo: [], implements: [], literal: { gradedAs: 'Date', format: null } }],
    }
    expect(() => readPortTypeVocabulary(response)).toThrow(/^The server published 'Depot' with a literal form this editor cannot grade: its literal form is graded as 'Date'/)
    const constructors: PortTypeVocabularyResponse = {
      generation: 0, secretCodec: false, capabilities: [], types: [],
      constructors: [{ name: 'Gauge', owner: 'host', variance: 'invariant', allowedArguments: ['Int'], implements: [], literal: { gradedAs: 'String', format: 'Guid' }, arity: 1, key: null }],
    }
    expect(() => readPortTypeConstructors(constructors)).toThrow(/^The server published 'Gauge' with a literal form this editor cannot grade: its literal format 'Guid' is outside Uuid and DateTime/)
  })
})

describe('what the reader does with the rows of the server table', () => {
  const vocabulary = (name: string, aliases: string[], widensTo: string[]): PortTypeVocabularyResponse =>
    ({
      generation: 0,
      secretCodec: false,
      capabilities: [],
      constructors: [],
      types: [
        { name: 'Point', owner: 'host', aliases: [], widensTo: [], implements: [], literal: null },
        { name, owner: 'host', aliases, widensTo, implements: [], literal: null },
      ],
    })

  it('seeds a depth-1 collection name the server gives an alias, so the alias reads as the collection', () => {
    const registry = createPortTypeRegistry(readPortTypeVocabulary(vocabulary('Collection<Item>', ['Batch'], [])))
    expect([registry.normalize('Batch'), registry.has('Collection<Item>')]).toEqual(['Collection<Item>', true])
  })

  it('seeds a depth-2 collection name the server gives an alias, so the alias reads as the collection', () => {
    const registry = createPortTypeRegistry(readPortTypeVocabulary(vocabulary('Collection<Collection<Item>>', ['Strings'], [])))
    expect([
      registry.normalize('Strings'),
      registry.has('Collection<Collection<Item>>'),
      registry.get('Strings')?.name,
    ]).toEqual(['Collection<Collection<Item>>', true, 'Collection<Collection<Item>>'])
  })

  it('seeds a depth-3 collection name the server gives an alias, and no shallower wrapping of its element', () => {
    const registry = createPortTypeRegistry(readPortTypeVocabulary(
      vocabulary('Collection<Collection<Collection<Item>>>', ['Pages'], []),
    ))
    expect([
      registry.normalize('Pages'),
      registry.has('Collection<Collection<Item>>'),
      registry.has('Item'),
    ]).toEqual(['Collection<Collection<Collection<Item>>>', false, false])
  })

  it('spells a case variant of an atom declared through a collection alias target as the server does', () => {
    const registry = createPortTypeRegistry(readPortTypeVocabulary(vocabulary('Collection<Foo>', ['Bag'], [])))
    expect([registry.normalize('Collection<foo>'), registry.normalize('foo'), registry.normalize('Bag')])
      .toEqual(['Collection<Foo>', 'Foo', 'Collection<Foo>'])
  })

  it('leaves out a collection name with nothing of its own', () => {
    expect(readPortTypeVocabulary(vocabulary('Collection<Stop>', [], [])).map((d) => d.name)).toEqual(['Point'])
  })

  it('refuses a name that does not parse, naming it', () => {
    expect(() => readPortTypeVocabulary(vocabulary('CollectionPoint', [], []))).toThrow(
      /^The server published port type 'CollectionPoint', which does not parse/,
    )
  })
})

const CAPABILITIES = [{ name: 'Iterable', associated: ['Item'] }, { name: 'Numeric', associated: null }, { name: 'Comparable', associated: null },
  { name: 'Equatable', associated: null }, { name: 'JsonSerializable', associated: null }]

describe('what the reader does with the constructors of the server', () => {
  const response = (constructors: PortTypeVocabularyResponse['constructors']): PortTypeVocabularyResponse =>
    ({ generation: 3, secretCodec: false, capabilities: CAPABILITIES, types: [], constructors })

  it('leaves Collection and Map out and reads a host constructor with its arguments', () => {
    expect(readPortTypeConstructors(response([
      { name: 'Collection', owner: 'library', variance: 'covariant', allowedArguments: null, implements: ['Iterable'], literal: null, arity: 1, key: null },
      { name: 'Map', owner: 'library', variance: 'covariant', allowedArguments: null, implements: [], literal: null, arity: 2, key: 'String' },
      { name: 'Gauge', owner: 'host', variance: 'invariant', allowedArguments: ['Boolean', 'Int?'], implements: ['Equatable'], literal: null, arity: 1, key: null },
    ]))).toEqual([{ name: 'Gauge', allowedArguments: ['Boolean', 'Int?'], implements: ['Equatable'], literal: null }])
  })

  it('refuses a Map published with an arity or a key this editor does not read', () => {
    const published = (name: string, arity: number, key: string | null): PortTypeVocabularyResponse =>
      response([{ name, owner: 'library', variance: 'covariant', allowedArguments: null, implements: [], literal: null, arity, key }])

    expect(() => readPortTypeConstructors(published('Map', 1, null))).toThrow(
      /^The server published built-in constructor 'Map' with arity 1 and key null, which this editor cannot read\.$/,
    )
    expect(() => readPortTypeConstructors(published('Map', 2, 'Int'))).toThrow(/built-in constructor 'Map' with arity 2 and key Int/)
    expect(readPortTypeConstructors(published('Map', 2, 'String'))).toEqual([])
  })

  it('loads a vocabulary whose Collection carries no arity or key and which lists no Map', () => {
    const collection = { name: 'Collection', owner: 'library', variance: 'covariant', allowedArguments: null, implements: ['Iterable'], literal: null }

    expect(readPortTypeConstructors(response([collection as unknown as PortTypeVocabularyResponse['constructors'][number]]))).toEqual([])
  })

  it('refuses a constructor that lists no argument, naming it', () => {
    expect(() => readPortTypeConstructors(response([{ name: 'Gauge', owner: 'host', variance: 'invariant', allowedArguments: null, implements: [], literal: null, arity: 1, key: null }]))).toThrow(
      /^The server published port type constructor 'Gauge' with no allowed argument\.$/,
    )
  })

  it('refuses a capability the response does not list, and one this editor does not know, naming the type', () => {
    const unlisted = { ...response([]), capabilities: [], types: [{ name: 'Depot', owner: 'host', aliases: [], widensTo: [], implements: ['Equatable'], literal: null }] }
    const unknown = { ...response([]), capabilities: [{ name: 'Frobnicable', associated: null }], types: [{ name: 'Depot', owner: 'host', aliases: [], widensTo: [], implements: ['Frobnicable'], literal: null }] }

    expect(() => readPortTypeVocabulary(unlisted)).toThrow(/^The server published 'Depot' as implementing 'Equatable'/)
    expect(() => readPortTypeVocabulary(unknown)).toThrow(/^The server published 'Depot' as implementing 'Frobnicable'/)
  })

  it('reads the claims of a host type into its definition', () => {
    const depot = { name: 'Depot', owner: 'host', aliases: [], widensTo: [], implements: ['Equatable', 'JsonSerializable'], literal: null }
    const registry = createPortTypeRegistry(readPortTypeVocabulary({ ...response([]), types: [depot] }))

    const term = parsePortTerm('Collection<Depot>', 'document')
    if (!term.ok) throw new Error('expected a term')
    expect(capabilitiesOf(registry, term.term)).toEqual(['Iterable', 'Equatable', 'JsonSerializable'])
  })
})
