import { describe, expect, it } from 'vitest'
import { BUILTIN_PORT_TYPES } from './builtins.js'
import { createPortTypeRegistry } from './registry.js'
import { isClosedPortTerm, isGradableTerm } from './closedTerm.js'

describe('isClosedPortTerm', () => {
  it.each(['Int', 'Int?', 'Json', 'Room', 'Collection<Int>', 'Collection<Int?>', 'Collection<Int>?', 'Collection<Collection<Room>>'])(
    'is true for %s, which names no parameter', (term) => {
      expect(isClosedPortTerm(term)).toBe(true)
    })

  it.each(['$T', '$T?', 'Collection<$T>', 'Collection<$T?>', 'Collection<Collection<$R>>', 'Collection<$T?>?'])(
    'is false for %s, which names a type parameter at some depth', (term) => {
      expect(isClosedPortTerm(term)).toBe(false)
    })

  it.each(['', 'Collection', 'Int??', 'Collection<', 'Int Int', '$', 'Collection<$>'])(
    'is false for %j, which is not a term', (term) => {
      expect(isClosedPortTerm(term)).toBe(false)
    })
})

describe('isGradableTerm', () => {
  const registry = createPortTypeRegistry(BUILTIN_PORT_TYPES)

  it('holds only for a known, closed term', () => {
    expect(isGradableTerm('Collection<Int>', registry)).toBe(true)
    expect(isGradableTerm('Int?', registry)).toBe(true)
    expect(isGradableTerm('Collection<Boolean?>', registry)).toBe(true)
    expect(isGradableTerm('Expression<Boolean>', registry)).toBe(true)
    expect(isGradableTerm(undefined, registry)).toBe(false)
    expect(isGradableTerm('Collection<$T?>', registry)).toBe(false)
    expect(isGradableTerm('Any', registry)).toBe(false)
    expect(isGradableTerm('Float', registry)).toBe(false)
    expect(isGradableTerm('Int??', registry)).toBe(false)
  })

  it('does not hold for a term naming a type the registry does not know', () => {
    expect(isGradableTerm('Shipment', registry)).toBe(false)
    expect(isGradableTerm('Collection<Shipment>', registry)).toBe(false)
    expect(isGradableTerm('Expression<Shipment>', registry)).toBe(false)
  })
})

describe('a map term', () => {
  const registry = createPortTypeRegistry(BUILTIN_PORT_TYPES)

  it.each(['Map<String,Int>', 'Map<string,Collection<Int?>>?', 'Collection<Map<String,Int>>'])('is closed and gradable: %s', (term) => {
    expect(isClosedPortTerm(term)).toBe(true)
    expect(isGradableTerm(term, registry)).toBe(true)
  })

  it.each(['Map<String,$T>', 'Map<String,$T?>?', 'Collection<Map<String,$T>>'])('is open while its value names a parameter: %s', (term) => {
    expect(isClosedPortTerm(term)).toBe(false)
    expect(isGradableTerm(term, registry)).toBe(false)
  })

  it.each(['Map<Int,Int>', 'Map', 'Map<String>', 'Map<$T,Int>'])('is no term when its key or brackets are wrong: %s', (term) => {
    expect(isClosedPortTerm(term)).toBe(false)
    expect(isGradableTerm(term, registry)).toBe(false)
  })

  it('is not gradable over a value the registry does not know', () => {
    expect(isGradableTerm('Map<String,Shipment>', registry)).toBe(false)
  })
})
