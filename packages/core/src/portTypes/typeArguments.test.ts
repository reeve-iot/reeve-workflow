import { describe, expect, it } from 'vitest'
import { BUILTIN_PORT_TYPES } from './builtins.js'
import { createPortTypeRegistry } from './registry.js'
import { isTypeArgumentFor, typeArgumentBases, wrapTypeArgument } from './typeArguments.js'

const registry = createPortTypeRegistry(
  [...BUILTIN_PORT_TYPES, { name: 'Depot', implements: ['Equatable'] }, { name: 'Ticket', implements: ['Comparable', 'Equatable'] }],
  [{ name: 'Gauge', allowedArguments: ['Boolean', 'Int?'], implements: ['Equatable'] }],
)

describe('typeArgumentBases', () => {
  it('offers only the numeric types for a Numeric parameter', () => {
    expect([...typeArgumentBases(registry, ['Numeric'])].sort()).toEqual(['Int', 'Number'])
  })

  it('offers every type that implements all the constraints, and never Json', () => {
    const offered = typeArgumentBases(registry, ['Equatable', 'JsonSerializable'])
    expect(offered).toEqual(expect.arrayContaining(['String', 'Int', 'Collection<String>']))
    expect(offered).not.toContain('Json')
    expect(offered).not.toContain('Depot')
  })

  it('offers the host types that claim the capability, and the constructor terms over their admitted arguments', () => {
    const offered = typeArgumentBases(registry, ['Equatable'])
    expect(offered).toEqual(expect.arrayContaining(['Depot', 'Ticket', 'Gauge<Boolean>', 'Gauge<Int?>']))
  })

  it('offers only Collection terms for an Iterable parameter', () => {
    const offered = typeArgumentBases(registry, ['Iterable'])
    expect(offered.length).toBeGreaterThan(0)
    expect(offered.every((term) => term.startsWith('Collection<'))).toBe(true)
    expect(offered).not.toContain('Collection<Json>')
  })

  it('offers every type but Json for a parameter with no constraint', () => {
    const offered = typeArgumentBases(registry)
    expect(offered).toEqual(expect.arrayContaining(['Int', 'Boolean', 'String', 'Depot', 'Collection<Int>']))
    expect(offered).not.toContain('Json')
  })

  it('offers nothing for a constraint outside the capability vocabulary', () => {
    expect(typeArgumentBases(registry, ['Frobnicable'])).toEqual([])
  })
})

describe('isTypeArgumentFor', () => {
  it.each([
    ['Json', []], ['Json?', []], ['Collection<Json>', []], ['Gauge<Json>', []],
    ['Int?', ['Numeric']], ['String', ['Numeric']], ['Mystery', []], ['Gauge<String>', []], ['Int<', []], ['$T', []],
  ])('refuses %s for %j', (term, constraints) => {
    expect(isTypeArgumentFor(registry, term, constraints)).toBe(false)
  })

  it.each([['Int?', []], ['Collection<Int?>', []], ['Collection<Collection<Int>>', ['Iterable']], ['Int?', ['JsonSerializable']]])('accepts %s for %j', (term, constraints) => {
    expect(isTypeArgumentFor(registry, term, constraints)).toBe(true)
  })
})

describe('wrapTypeArgument', () => {
  it('wraps in a collection and makes the outer level nullable, each only where the result still fits', () => {
    expect(wrapTypeArgument(registry, 'Int', 'collection')).toBe('Collection<Int>')
    expect(wrapTypeArgument(registry, 'Int', 'nullable')).toBe('Int?')
    expect(wrapTypeArgument(registry, 'Int?', 'collection')).toBe('Collection<Int?>')
    expect(wrapTypeArgument(registry, 'Collection<Int>', 'nullable')).toBe('Collection<Int>?')
  })

  it('refuses a wrap the constraints do not admit, a second nullable level and text that is no term', () => {
    expect(wrapTypeArgument(registry, 'Int', 'collection', ['Numeric'])).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Int', 'nullable', ['Numeric'])).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Int?', 'nullable')).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Int<', 'collection')).toBeUndefined()
    expect(wrapTypeArgument(registry, 'int', 'collection')).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Collection<int>', 'nullable')).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Map<string,Int>', 'nullable')).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Map<String,Int>', 'nullable')).toBe('Map<String,Int>?')
  })
})

describe('a map as a type argument', () => {
  it('fits a parameter by its value, and never when it holds Json', () => {
    expect(isTypeArgumentFor(registry, 'Map<String,Int>', ['Equatable', 'JsonSerializable'])).toBe(true)
    expect(isTypeArgumentFor(registry, 'Map<String,Int>', ['Iterable'])).toBe(false)
    expect(isTypeArgumentFor(registry, 'Map<String,Int>', ['Comparable'])).toBe(false)
    expect(isTypeArgumentFor(registry, 'Map<String,Int>?', ['JsonSerializable'])).toBe(true)
    expect(isTypeArgumentFor(registry, 'Map<String,Depot>', ['JsonSerializable'])).toBe(false)
    expect(isTypeArgumentFor(registry, 'Map<String,Json>', [])).toBe(false)
    expect(isTypeArgumentFor(registry, 'Collection<Map<String,Json?>>', [])).toBe(false)
  })

  it('is wrapped, and made nullable, where the result still fits', () => {
    expect(wrapTypeArgument(registry, 'Map<String,Int>', 'collection')).toBe('Collection<Map<String,Int>>')
    expect(wrapTypeArgument(registry, 'Map<String,Int>', 'nullable')).toBe('Map<String,Int>?')
    expect(wrapTypeArgument(registry, 'Map<String,Int>?', 'nullable')).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Map<String,int>', 'nullable')).toBeUndefined()
    expect(wrapTypeArgument(registry, 'Map<String,Int>', 'collection', ['Iterable'])).toBe('Collection<Map<String,Int>>')
    expect(wrapTypeArgument(registry, 'Map<String,Int>', 'nullable', ['Equatable'])).toBeUndefined()
  })
})
