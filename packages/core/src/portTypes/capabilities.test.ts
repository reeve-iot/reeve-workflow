import { describe, expect, it } from 'vitest'
import { BUILTIN_PORT_TYPES } from './builtins.js'
import { PORT_CAPABILITIES, isPortCapabilityName, termImplements } from './capabilities.js'
import type { PortTerm } from './grammar.js'
import { parsePortTerm } from './grammar.js'
import { createPortTypeRegistry } from './registry.js'

const read = (text: string): PortTerm => {
  const parsed = parsePortTerm(text, 'document')
  if (!parsed.ok) throw new Error(`${text} is not a term: ${parsed.fault}`)
  return parsed.term
}

const implementedBy = (registry: ReturnType<typeof createPortTypeRegistry>, text: string): string[] =>
  PORT_CAPABILITIES.map((c) => c.name).filter((name) => registry.implementsCapability(read(text), name))

describe('capabilities', () => {
  it('knows a capability by its exact name only', () => {
    expect(['Iterable', 'Numeric', 'Comparable', 'Equatable', 'JsonSerializable'].every(isPortCapabilityName)).toBe(true)
    expect(['numeric', 'Iterable<Item>', 'Json', ''].some(isPortCapabilityName)).toBe(false)
  })

  it('holds the library fixed: a definition that lists other claims for a library type does not change them', () => {
    const lying = createPortTypeRegistry([{ name: 'String', implements: ['Comparable'] }, { name: 'Int', implements: [] }])

    expect(implementedBy(lying, 'String')).toEqual(['Comparable', 'Equatable', 'JsonSerializable'])
    expect(implementedBy(lying, 'Int')).toEqual(['Numeric', 'Comparable', 'Equatable', 'JsonSerializable'])
  })

  it('implements nothing for a type that lists nothing, whatever it is called', () => {
    const plain = createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Depot' }])

    expect(implementedBy(plain, 'Depot')).toEqual([])
  })

  it('refuses to ask a term holding a type parameter', () => {
    const parameter = parsePortTerm('Collection<$T>', 'declaration')
    if (!parameter.ok) throw new Error('expected a term')

    expect(() => createPortTypeRegistry(BUILTIN_PORT_TYPES).implementsCapability(parameter.term, 'Equatable')).toThrow(/closed terms/)
  })

  it('answers a 100,000-deep collection without recursing', () => {
    const deep = read('Collection<'.repeat(100_000) + 'Int' + '>'.repeat(100_000))
    const claims = { claims: (head: PortTerm) => head.kind === 'atom' && head.name === 'Int' }

    expect(termImplements(deep, 'Equatable', claims)).toBe(true)
    expect(termImplements(deep, 'Comparable', claims)).toBe(false)
    expect(termImplements(deep, 'Iterable', claims)).toBe(true)
  })

  it('answers a map by its value: Equatable and JsonSerializable follow it, Iterable and Comparable are never its own', () => {
    const registry = createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Handle' }])

    expect(implementedBy(registry, 'Map<String,Int>')).toEqual(['Equatable', 'JsonSerializable'])
    expect(implementedBy(registry, 'Map<string,Collection<String>>')).toEqual(['Equatable', 'JsonSerializable'])
    expect(implementedBy(registry, 'Map<String,Map<String,Boolean>>')).toEqual(['Equatable', 'JsonSerializable'])
    expect(implementedBy(registry, 'Collection<Map<String,Int>>')).toEqual(['Iterable', 'Equatable', 'JsonSerializable'])
    expect(implementedBy(registry, 'Map<String,Json>')).toEqual(['JsonSerializable'])
    expect(implementedBy(registry, 'Map<String,Handle>')).toEqual([])
    expect(implementedBy(registry, 'Map<String,Collection<Handle>>')).toEqual([])
  })

  it('lets a nullable map implement JsonSerializable alone, when its value does', () => {
    const registry = createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Handle' }])

    expect(implementedBy(registry, 'Map<String,Int>?')).toEqual(['JsonSerializable'])
    expect(implementedBy(registry, 'Map<String,Handle>?')).toEqual([])
  })

  it('answers a 100,000-deep map without recursing', () => {
    const deep = read('Map<String,'.repeat(100_000) + 'Int' + '>'.repeat(100_000))
    const claims = { claims: (head: PortTerm) => head.kind === 'atom' && head.name === 'Int' }

    expect(termImplements(deep, 'Equatable', claims)).toBe(true)
    expect(termImplements(deep, 'JsonSerializable', claims)).toBe(true)
    expect(termImplements(deep, 'Iterable', claims)).toBe(false)
  })
})
