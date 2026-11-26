import { describe, expect, it } from 'vitest'
import { BUILTIN_PORT_TYPES } from './builtins.js'
import type { PortTerm } from './grammar.js'
import { parsePortTerm } from './grammar.js'
import { createPortTypeRegistry } from './registry.js'
import type { PortTypeConstructorDefinition, PortTypeDefinition } from './types.js'

const HOST_TYPES: PortTypeDefinition[] = [
  { name: 'Device', aliases: ['device'], displayName: 'Device' },
  { name: 'Room', aliases: ['room'] },
]

const registry = () => createPortTypeRegistry([...BUILTIN_PORT_TYPES, ...HOST_TYPES])

describe('createPortTypeRegistry', () => {
  it('says a host has no secret codec unless it was told so', () => {
    expect(registry().secretCodec).toBe(false)
    expect(createPortTypeRegistry(BUILTIN_PORT_TYPES, [], { secretCodec: true }).secretCodec).toBe(true)
  })

  it('accepts a host type the package never declared', () => {
    expect(registry().has('Device')).toBe(true)
  })

  it('canonicalises an alias to the host wire name', () => {
    expect(registry().normalize('device')).toBe('Device')
  })

  it('derives the collection companion of a host type', () => {
    expect(registry().has('Collection<Device>')).toBe(true)
  })

  it('canonicalises the bracketed collection form', () => {
    expect(registry().normalize('Collection<Device>')).toBe('Collection<Device>')
  })

  it('returns a blank type unchanged rather than inventing the wildcard', () => {
    expect(registry().normalize('')).toBe('')
  })

  it('returns an unregistered name unchanged so a newer graph still round-trips', () => {
    expect(registry().normalize('Thermostat')).toBe('Thermostat')
  })

  it('reports an unregistered name as absent even though normalize passes it through', () => {
    expect(registry().has('Thermostat')).toBe(false)
  })

  it('rejects a type registered twice', () => {
    expect(() => createPortTypeRegistry([{ name: 'Device' }, { name: 'Device' }])).toThrow(/^port-type-name-clash: Duplicate port type 'Device'\.$/)
  })

  it('rejects an alias claimed by two different types', () => {
    expect(() => createPortTypeRegistry([
      { name: 'Device', aliases: ['thing'] },
      { name: 'Room', aliases: ['thing'] },
    ])).toThrow(/^port-type-name-clash: Port type alias 'thing' is claimed by both 'Device' and 'Room'\.$/)
  })

  it('suppresses the collection companion when the definition opts out', () => {
    const optedOut = createPortTypeRegistry([{ name: 'Opaque', collection: false }])
    expect([optedOut.get('Collection<Opaque>'), optedOut.list().map((t) => t.name)]).toEqual([undefined, ['Opaque']])
  })
})

describe('one grammar, no repair', () => {
  it('returns a concatenated collection spelling unchanged rather than repairing it', () => {
    expect(registry().normalize('CollectionDevice')).toBe('CollectionDevice')
  })

  it('resolves the element of a collection through the alias table', () => {
    expect(registry().normalize('Collection<device>')).toBe('Collection<Device>')
  })

  it('resolves an alias at any depth and keeps any as written', () => {
    expect([registry().normalize('Collection<Collection<room>>'), registry().normalize('Collection<any>')])
      .toEqual(['Collection<Collection<Room>>', 'Collection<any>'])
  })

  it('keeps an unregistered element as written inside a well-formed collection', () => {
    expect(registry().normalize('Collection<Thermostat>')).toBe('Collection<Thermostat>')
  })

  it('does not trim a name', () => {
    expect(registry().normalize(' Device')).toBe(' Device')
  })

  it('knows a collection of a registered type at any depth, and none of Any', () => {
    expect([
      registry().has('Collection<Collection<Device>>'),
      registry().has('Collection<Any>'),
      registry().has('Any'),
      registry().has('Collection<Thermostat>'),
    ]).toEqual([true, false, false, false])
  })

  it('does not know a malformed name even when its letters spell a registered type', () => {
    expect([registry().has('CollectionDevice'), registry().has('Collection<Device')]).toEqual([false, false])
  })

  it('finds a companion by its alias spelling, and no definition deeper than one level', () => {
    expect([registry().get('Collection<device>')?.name, registry().get('Collection<Collection<Device>>')])
      .toEqual(['Collection<Device>', undefined])
  })

  it('lists the registered types first, then their companions, each in registration order', () => {
    const two = createPortTypeRegistry([{ name: 'Room' }, { name: 'Device' }])
    expect(two.list().map((t) => t.name)).toEqual(['Room', 'Device', 'Collection<Room>', 'Collection<Device>'])
  })

  it('finds no definition for a malformed name, even one whose letters spell a registered type', () => {
    expect([registry().get('CollectionDevice'), registry().get('Device ')]).toEqual([undefined, undefined])
  })

  it('lists each companion under its bracketed name, with no alias', () => {
    const companion = registry().list().find((t) => t.name === 'Collection<Device>')
    expect(companion).toEqual({ name: 'Collection<Device>', displayName: 'Collection of Device' })
  })

  it('refuses to register a name that is not a sentence of the grammar', () => {
    expect(() => createPortTypeRegistry([{ name: 'Foo Bar' }])).toThrow(
      /^port-type-registration-invalid: Port type 'Foo Bar' cannot be registered: 'Foo Bar' is not a port type name: an illegal character at 3\. white space\.$/,
    )
  })

  it('refuses a concatenated collection spelling as a definition name, naming the bracketed form', () => {
    expect(() => createPortTypeRegistry([{ name: 'CollectionDevice' }])).toThrow(/write Collection<T> with the type of its elements/)
  })
})

describe('a host definition that is a collection of another name', () => {
  const batch = () => createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Collection<Item>', aliases: ['Batch'] }])

  it('resolves the alias to the collection name it stands for', () => {
    expect(batch().normalize('Batch')).toBe('Collection<Item>')
  })

  it('composes the depth when the alias is read inside a collection', () => {
    expect(batch().normalize('Collection<batch>')).toBe('Collection<Collection<Item>>')
  })

  it('leaves the collection name where it already is, and spells its case variant as the host wrote it', () => {
    expect([batch().normalize('Collection<Item>'), batch().normalize('Collection<item>')])
      .toEqual(['Collection<Item>', 'Collection<Item>'])
  })

  it('knows the alias, the collection name and any collection of it, in either case', () => {
    const registry = batch()
    expect([
      registry.has('batch'),
      registry.has('Collection<Item>'),
      registry.has('Collection<item>'),
      registry.has('Collection<Collection<Item>>'),
      registry.has('Item'),
    ]).toEqual([true, true, true, true, false])
  })

  it('reads the alias, and the collection name itself, as the definition they name', () => {
    expect([batch().get('Batch')?.name, batch().get('Collection<Item>')?.name, batch().get('Collection<item>')?.name])
      .toEqual(['Collection<Item>', 'Collection<Item>', 'Collection<Item>'])
  })

  it('answers for every name list() returns', () => {
    const unanswered = (r: ReturnType<typeof registry>) =>
      r.list().filter((t) => r.get(t.name) === undefined).map((t) => t.name)
    const deep = createPortTypeRegistry([{ name: 'Device' }, { name: 'Collection<Collection<Leaf>>', aliases: ['Strings'] }])
    expect([unanswered(registry()), unanswered(batch()), unanswered(deep)]).toEqual([[], [], []])
  })

  it('answers nothing for a collection of the alias, which names a deeper collection', () => {
    expect([batch().get('Collection<Batch>'), batch().get('Collection<Collection<Item>>')]).toEqual([undefined, undefined])
  })

  it('lists the definition in registration order, then the companions, deriving none of its own', () => {
    const registry = createPortTypeRegistry([{ name: 'Device' }, { name: 'Collection<Item>', aliases: ['Batch'] }])
    expect(registry.list().map((t) => t.name)).toEqual(['Device', 'Collection<Item>', 'Collection<Device>'])
  })

  const strings = () => createPortTypeRegistry([{ name: 'Collection<Collection<Leaf>>', aliases: ['Strings'] }])
  const pages = () => createPortTypeRegistry([
    { name: 'Collection<Collection<Collection<Leaf>>>', aliases: ['Pages'] },
  ])

  it('holds a two-deep collection name beside its alias, and the definition answers for both spellings', () => {
    expect([
      strings().normalize('Strings'),
      strings().has('Collection<Collection<Leaf>>'),
      strings().get('Strings')?.name,
      strings().get('Collection<Collection<Leaf>>')?.name,
      strings().list().map((t) => t.name),
    ]).toEqual([
      'Collection<Collection<Leaf>>',
      true,
      'Collection<Collection<Leaf>>',
      'Collection<Collection<Leaf>>',
      ['Collection<Collection<Leaf>>'],
    ])
  })

  it('holds a three-deep collection name too, and composes its depth when the alias is read inside one', () => {
    const deep = (depth: number, element: string) => 'Collection<'.repeat(depth) + element + '>'.repeat(depth)
    expect([
      pages().normalize('Pages'),
      pages().get('Pages')?.name,
      pages().has(deep(3, 'Leaf')),
      pages().normalize('Collection<Pages>'),
    ]).toEqual([deep(3, 'Leaf'), deep(3, 'Leaf'), true, deep(4, 'Leaf')])
  })

  it('knows the target and every collection of it, and nothing wrapping its element less deep', () => {
    expect([
      strings().has('Collection<Collection<Collection<Leaf>>>'),
      strings().has('Collection<Leaf>'),
      strings().has('Leaf'),
      strings().get('Collection<Leaf>'),
      strings().get('Collection<Collection<Collection<Leaf>>>'),
    ]).toEqual([true, false, false, undefined, undefined])
  })
})

function term(text: string, grammar: 'document' | 'declaration' = 'document'): PortTerm {
  const parsed = parsePortTerm(text, grammar)
  if (!parsed.ok) throw new Error(`${text} is not a term: ${parsed.fault}`)
  return parsed.term
}

describe('a vocabulary the server published', () => {
  it('is read as given: the registry grades no rule of a record', () => {
    const read = createPortTypeRegistry(
      [...BUILTIN_PORT_TYPES, { name: 'Odd', aliases: ['odd'], implements: ['Iterable'], widensTo: ['Missing'], literal: { gradedAs: 'Json' } }],
    )

    expect(read.has('Odd')).toBe(true)
    expect(read.implementsCapability(term('Odd'), 'Iterable')).toBe(true)
    expect(read.literalFormOf('Odd')).toEqual({ gradedAs: 'Json' })
  })

  it.each([
    [[{ name: 'Collection<Item>', aliases: ['Batch'] }, { name: 'Item' }]],
    [[{ name: 'Item' }, { name: 'Collection<Item>', aliases: ['Batch'] }]],
    [[{ name: 'Collection<Leaf>', aliases: ['Strings'] }, { name: 'Collection<Collection<Leaf>>', aliases: ['Sheets'] }]],
    [[{ name: 'Collection<Item>', aliases: ['Item'] }]],
  ] as [PortTypeDefinition[]][])('refuses an atom that two entries declare, in either order', (definitions) => {
    expect(() => createPortTypeRegistry(definitions)).toThrow(/^port-type-name-clash: /)
  })
})

describe('a host type constructor', () => {
  const gauge: PortTypeConstructorDefinition = { name: 'Gauge', allowedArguments: ['Boolean', 'Number', 'String'] }
  const registry = createPortTypeRegistry(BUILTIN_PORT_TYPES, [gauge])

  it('is listed sorted by name, as the backend lists it, and leaves the builtin vocabulary alone', () => {
    const probe = { name: 'Probe', allowedArguments: ['Int'] }
    const underscore = { name: '_Tail', allowedArguments: ['Int'] }
    expect(createPortTypeRegistry(BUILTIN_PORT_TYPES, [probe, underscore, gauge]).constructors().map((c) => c.name)).toEqual([
      'Gauge', 'Probe', '_Tail',
    ])
    expect(createPortTypeRegistry(BUILTIN_PORT_TYPES).constructors()).toEqual([])
  })

  it('hands out frozen copies, so the caller keeps no handle on the registry', () => {
    const mine = { name: 'Gauge', allowedArguments: ['Boolean'] }
    const listed = createPortTypeRegistry(BUILTIN_PORT_TYPES, [mine]).constructors()
    mine.allowedArguments.push('Int')

    expect(listed[0]?.allowedArguments).toEqual(['Boolean'])
    expect(Object.isFrozen(listed)).toBe(true)
    expect(Object.isFrozen(listed[0])).toBe(true)
    expect(Object.isFrozen(listed[0]?.allowedArguments)).toBe(true)
  })

  it.each(['Json', 'bool', 'Boolean', 'Shipment'])('refuses a constructor named like the registered type %s', (name) => {
    expect(() => createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Shipment' }], [{ name, allowedArguments: ['Int'] }])).toThrow(
      `port-type-name-clash: Port type constructor '${name}' cannot be registered: a port type of that name is already registered.`,
    )
  })

  it('refuses a second constructor of the same name in any case', () => {
    expect(() => createPortTypeRegistry(BUILTIN_PORT_TYPES, [gauge, { name: 'GAUGE', allowedArguments: ['Int'] }])).toThrow(
      "port-type-name-clash: Port type constructor 'GAUGE' cannot be registered: a constructor of that name is already registered.",
    )
  })

  it.each(['$T', 'Int??', 'Collection<'])('refuses the allowed argument %j, which is not a term', (argument) => {
    expect(() => createPortTypeRegistry(BUILTIN_PORT_TYPES, [{ name: 'Gauge', allowedArguments: ['Boolean', argument] }])).toThrow(
      `port-type-registration-invalid: Port type constructor 'Gauge' cannot be registered: allowed argument '${argument}' is not a closed term.`,
    )
  })

  it.each(['Gauge<Boolean>', 'Gauge<Number>', 'Gauge<String>', 'Gauge<Boolean>?', 'gauge<String>', 'Collection<Gauge<Boolean>?>'])(
    'registers the term %s',
    (text) => {
      expect(registry.refusalOf(term(text))).toBeUndefined()
    },
  )

  it.each([
    ['Widget<Boolean>', "'Widget' is not a registered type constructor"],
    ['Collection<Widget<Boolean>>', "'Widget' is not a registered type constructor"],
    ['Gauge<Int>', "'Gauge' does not admit the argument 'Int'"],
    ['Gauge<Boolean?>', "'Gauge' does not admit the argument 'Boolean?'"],
    ['Collection<Gauge<Int>>', "'Gauge' does not admit the argument 'Int'"],
    ['Gauge', "'Gauge' is not a registered type"],
  ])('refuses the term %s as port-type-unregistered', (text, message) => {
    expect(registry.refusalOf(term(text))).toEqual({ code: 'port-type-unregistered', message })
  })

  it('admits a bare type parameter argument for a registered head and not for an unregistered one', () => {
    expect(registry.refusalOf(term('Gauge<$T>', 'declaration'))).toBeUndefined()
    expect(registry.refusalOf(term('Widget<$T>', 'declaration'))?.code).toBe('port-type-unregistered')
  })

  it('does not admit a parameter nested inside an argument', () => {
    const nested = createPortTypeRegistry(BUILTIN_PORT_TYPES, [{ name: 'Gauge', allowedArguments: ['Collection<Int>'] }])
    expect(nested.refusalOf(term('Gauge<Collection<$T>>', 'declaration'))?.message).toBe(
      "'Gauge' does not admit the argument 'Collection<$T>'",
    )
  })

  it('judges a term a hundred thousand levels deep without recursing', () => {
    const wrap = (inner: string): string => 'Collection<'.repeat(100_000) + inner + '>'.repeat(100_000)
    expect(registry.refusalOf(term(wrap('Gauge<Boolean>')))).toBeUndefined()
    expect(registry.refusalOf(term(wrap('Gauge<Int>')))?.message).toBe("'Gauge' does not admit the argument 'Int'")
  })

  it('stays unknown to the name readers: has is false for a name made of a constructor', () => {
    expect(registry.has('Gauge<Boolean>')).toBe(false)
    expect(registry.has('Gauge')).toBe(false)
    expect(registry.list().map((d) => d.name)).toEqual(createPortTypeRegistry(BUILTIN_PORT_TYPES).list().map((d) => d.name))
  })
})

describe('the built-in Map constructor', () => {
  const builtins = createPortTypeRegistry(BUILTIN_PORT_TYPES)
  const term = (text: string): PortTerm => {
    const parsed = parsePortTerm(text, 'document')
    if (!parsed.ok) throw new Error(`${text} is not a term`)
    return parsed.term
  }

  it.each(['Map', 'map', 'MAP'])('refuses a record named %s, as a name or as an alias', (name) => {
    const refusal = new RegExp(`^port-type-registration-invalid: Port type '${name}' cannot be registered`)
    expect(() => createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name }])).toThrow(refusal)
    expect(() => createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Shipment', aliases: [name] }])).toThrow(
      new RegExp(`^port-type-registration-invalid: Port type 'Shipment' cannot be registered: alias '${name}'`),
    )
  })

  it.each(['Map', 'map', 'MAP'])("refuses a host constructor named %s, the library's own", (name) => {
    expect(() => createPortTypeRegistry(BUILTIN_PORT_TYPES, [{ name, allowedArguments: ['Int'] }])).toThrow(
      `port-type-registration-invalid: Port type constructor '${name}' cannot be registered: Map is the library's built-in constructor.`,
    )
  })

  it('registers Map<String,V> over any registered value, as it does Collection', () => {
    for (const text of ['Map<String,Int>', 'Map<string,Collection<Boolean?>>?', 'Map<String,Map<String,Json>>', 'Collection<Map<String,String>>']) {
      expect(builtins.refusalOf(term(text)), text).toBeUndefined()
    }
    expect(builtins.refusalOf(term('Map<String,Shipment>'))?.code).toBe('port-type-unregistered')
    expect(builtins.refusalOf(term('map<Int>'))?.code).toBe('port-type-unregistered')
  })

  it('judges a map argument of a host constructor as the exact term the constructor admits', () => {
    const gauge = createPortTypeRegistry(BUILTIN_PORT_TYPES, [{ name: 'Gauge', allowedArguments: ['Map<String,Int>'] }])
    expect(gauge.refusalOf(term('Gauge<Map<string,Int>>'))).toBeUndefined()
    expect(gauge.refusalOf(term('Gauge<Map<String,Number>>'))?.code).toBe('port-type-unregistered')
  })

  it('knows a map name only as a term, not as a type name', () => {
    expect(builtins.has('Map<String,Int>')).toBe(false)
    expect(builtins.get('Map<String,Int>')).toBeUndefined()
    expect(builtins.normalize('Collection<Map<String,Int>>')).toBe('Collection<Map<String,Int>>')
  })
})
