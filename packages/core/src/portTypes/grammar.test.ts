import { existsSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { BUILTIN_PORT_TYPES } from './builtins.js'
import type { PortTerm, PortTypeSyntaxFault } from './grammar.js'
import {
  collectedPortType,
  describePortTypeSyntaxError,
  formatPortTerm,
  formatPortTypeName,
  parsePortTerm,
  parsePortTypeName,
  portTermsEqual,
} from './grammar.js'
import { createPortTypeRegistry } from './registry.js'

function findCorpus(): string {
  for (let dir = __dirname; ; dir = dirname(dir)) {
    const candidate = join(dir, 'contracts', 'port-type-names.tsv')
    if (existsSync(candidate)) return candidate
    if (dirname(dir) === dir) throw new Error('contracts/port-type-names.tsv not found above ' + __dirname)
  }
}

const unescape = (field: string): string =>
  field.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))

interface TermRow { line: number; name: string; depth: number; element: string; canonical: string }
interface SyntaxRow { line: number; name: string; fault: string; position: number }
interface ShapeRow { line: number; name: string; kind: 'doc' | 'decl'; shape: string; docFault?: string; docPosition?: number }
interface RefusedRow { line: number; name: string; fault: string; position: number; docFault: string; docPosition: number }

function readCorpus(): { terms: TermRow[]; syntax: SyntaxRow[]; shapes: ShapeRow[]; refused: RefusedRow[] } {
  const terms: TermRow[] = []
  const syntax: SyntaxRow[] = []
  const shapes: ShapeRow[] = []
  const refused: RefusedRow[] = []
  const lines = readFileSync(findCorpus(), 'utf8').split('\n')
  lines.forEach((text, index) => {
    if (text === '' || text.startsWith('#')) return
    const fields = text.split('\t')
    const [name = '', rowKind, a = '', b = '', c = ''] = fields
    const kind = rowKind?.startsWith('map:') ? rowKind.slice('map:'.length) : rowKind
    if (kind === 'term') {
      terms.push({ line: index + 1, name: unescape(name), depth: Number(a), element: unescape(b), canonical: unescape(c) })
    } else if (kind === 'syntax') {
      syntax.push({ line: index + 1, name: unescape(name), fault: a, position: Number(b) })
    } else if (kind === 'doc') {
      shapes.push({ line: index + 1, name: unescape(name), kind, shape: a })
    } else if (kind === 'decl') {
      shapes.push({ line: index + 1, name: unescape(name), kind, shape: a, docFault: b, docPosition: Number(c) })
    } else if (kind === 'reserved') {
      return
    } else if (kind === 'refused') {
      refused.push({ line: index + 1, name: unescape(name), fault: a, position: Number(b), docFault: a, docPosition: Number(b) })
    } else if (kind === 'declrefused') {
      refused.push({ line: index + 1, name: unescape(name), fault: a, position: Number(b), docFault: c, docPosition: Number(fields[5] ?? '') })
    } else {
      throw new Error(`line ${String(index + 1)} is neither a term nor a syntax row: ${text}`)
    }
  })
  return { terms, syntax, shapes, refused }
}

const shapeOf = (term: PortTerm): string => {
  if (term.kind === 'null') return 'null'
  const inside = term.kind === 'constructor' ? `(${term.key === undefined ? '' : `${term.key.name},`}${shapeOf(term.argument)})` : ''
  return (term.nullable ? '?' : '') + (term.kind === 'parameter' ? '$' : '') + term.name + inside
}

describe('parsePortTypeName against the golden corpus', () => {
  const corpus = readCorpus()

  it('reads the whole file, not a truncated one', () => {
    expect(corpus.terms.length + corpus.syntax.length).toBeGreaterThan(9000)
  })

  it('parses every term row to its pinned depth and element', () => {
    const wrong = corpus.terms.flatMap((row) => {
      const parsed = parsePortTypeName(row.name)
      return parsed.ok && parsed.depth === row.depth && parsed.element === row.element
        ? []
        : [{ line: row.line, name: row.name, expected: [row.depth, row.element], actual: parsed }]
    })
    expect(wrong).toEqual([])
  })

  it('normalizes every term row to its pinned canonical spelling under the built-ins', () => {
    const builtins = createPortTypeRegistry(BUILTIN_PORT_TYPES)
    const wrong = corpus.terms.flatMap((row) => {
      const actual = builtins.normalize(row.name)
      return actual === row.canonical ? [] : [{ line: row.line, name: row.name, expected: row.canonical, actual }]
    })
    expect(wrong).toEqual([])
  })

  it('refuses every syntax row with its pinned fault and position', () => {
    const wrong = corpus.syntax.flatMap((row) => {
      const parsed = parsePortTypeName(row.name)
      return !parsed.ok && parsed.fault === row.fault && parsed.position === row.position
        ? []
        : [{ line: row.line, name: row.name, expected: [row.fault, row.position], actual: parsed }]
    })
    expect(wrong).toEqual([])
  })
})

describe('parsePortTerm against the golden corpus', () => {
  const corpus = readCorpus()
  const answer = (name: string, grammar: 'document' | 'declaration'): string => {
    const parsed = parsePortTerm(name, grammar)
    return parsed.ok ? shapeOf(parsed.term) : `${parsed.fault}@${String(parsed.position)}`
  }

  it('reads the new rows, so the section is not silently empty', () => {
    expect(corpus.shapes.length).toBeGreaterThan(40)
    expect(corpus.refused.length).toBeGreaterThan(1000)
  })

  it('reads the Map<String,V> rows too, none skipped', () => {
    const isMap = (name: string): boolean => /(^|[<>])Map</.test(name)
    expect(corpus.shapes.filter((row) => isMap(row.name)).length).toBeGreaterThan(50)
    expect(corpus.refused.filter((row) => isMap(row.name)).length).toBeGreaterThan(150)
    expect(corpus.syntax.filter((row) => isMap(row.name)).length).toBeGreaterThan(10)
  })

  it('reads every well-formed row to its shape under both grammars and writes it back as it was read', () => {
    const wrong = corpus.shapes.flatMap((row) => {
      const document = parsePortTerm(row.name, 'document')
      const declaration = parsePortTerm(row.name, 'declaration')
      const written = declaration.ok ? formatPortTerm(declaration.term) : undefined
      const expectedDocument = row.kind === 'doc' ? row.shape : `${row.docFault ?? ''}@${String(row.docPosition)}`
      const actualDocument = document.ok ? shapeOf(document.term) : `${document.fault}@${String(document.position)}`
      return actualDocument === expectedDocument && answer(row.name, 'declaration') === row.shape && written === row.name
        ? []
        : [{ line: row.line, name: row.name, expected: row.shape, actualDocument, declaration: answer(row.name, 'declaration') }]
    })
    expect(wrong).toEqual([])
  })

  it('refuses every refused row with its pinned fault and position under each grammar', () => {
    const wrong = corpus.refused.flatMap((row) => {
      const declaration = answer(row.name, 'declaration')
      const document = answer(row.name, 'document')
      return declaration === `${row.fault}@${String(row.position)}` && document === `${row.docFault}@${String(row.docPosition)}`
        ? []
        : [{ line: row.line, name: row.name, expected: [row.fault, row.position, row.docFault, row.docPosition], declaration, document }]
    })
    expect(wrong).toEqual([])
  })

  it('reads every older row under the declaration grammar as under the document grammar, a $ aside', () => {
    const rows = [
      ...corpus.terms.map((r) => r.name),
      ...corpus.syntax.map((r) => r.name),
    ].filter((name) => !name.includes('$'))
    const wrong = rows.filter((name) => answer(name, 'declaration') !== answer(name, 'document'))
    expect(wrong).toEqual([])
  })
})

describe('parsePortTerm', () => {
  const shape = (text: string, grammar: 'document' | 'declaration' = 'document'): string => {
    const parsed = parsePortTerm(text, grammar)
    if (!parsed.ok) throw new Error(`${text} was refused: ${parsed.fault}`)
    return shapeOf(parsed.term)
  }

  it.each([
    ['Int?', '?Int'],
    ['Collection<Int?>', 'Collection(?Int)'],
    ['Collection<Int>?', '?Collection(Int)'],
    ['Collection<Int?>?', '?Collection(?Int)'],
    ['Gauge<Boolean>', 'Gauge(Boolean)'],
    ['Gauge<Collection<Json?>>?', '?Gauge(Collection(?Json))'],
    ['Expression<Boolean>', 'Expression(Boolean)'],
  ])('reads %j as %s in a stored document', (text, expected) => {
    expect(shape(text)).toBe(expected)
  })

  it.each([
    ['$T', '$T'],
    ['$T?', '?$T'],
    ['Collection<$T?>?', '?Collection(?$T)'],
    ['Expression<$T>', 'Expression($T)'],
    ['$Collection', '$Collection'],
    ['Collection<$collectionX?>', 'Collection(?$collectionX)'],
  ])('reads %j as %s in a declaration', (text, expected) => {
    expect(shape(text, 'declaration')).toBe(expected)
  })

  it.each([
    ['$T', 'IllegalCharacter', 0],
    ['Collection<$T>', 'IllegalCharacter', 11],
    ['Int??', 'DoubleNullable', 4],
    ['Collection<Int??>', 'DoubleNullable', 15],
    ['Collection<Int>??', 'DoubleNullable', 16],
    ['?', 'ExpectedTypeName', 0],
    ['Int?Foo', 'IllegalCharacter', 4],
    ['Gauge<>', 'ExpectedTypeName', 6],
    ['Gauge<Int', 'MissingClose', 9],
  ] as const)('refuses %j in a stored document with %s at %i', (text, fault, position) => {
    expect(parsePortTerm(text, 'document')).toMatchObject({ ok: false, fault, position })
  })

  it.each([
    ['$', 'ExpectedTypeName', 1],
    ['$$T', 'IllegalCharacter', 1],
    ['$1', 'IllegalCharacter', 1],
    ['$T<X>', 'UnexpectedOpen', 2],
    ['$T??', 'DoubleNullable', 3],
  ] as const)('refuses %j in a declaration with %s at %i', (text, fault, position) => {
    expect(parsePortTerm(text, 'declaration')).toMatchObject({ ok: false, fault, position })
  })

  it('keeps Collection a case-sensitive keyword and its prefix reserved before a nullable suffix', () => {
    expect(parsePortTerm('Collection?')).toMatchObject({ fault: 'CollectionWithoutBracket', position: 0 })
    expect(parsePortTerm('collection<Int>?')).toMatchObject({ fault: 'ReservedCollectionPrefix', position: 0 })
  })

  it('names a second question mark as a fault of its own, in the backend words', () => {
    const parsed = parsePortTerm('Int??')
    if (parsed.ok) throw new Error('expected a refusal')
    expect(describePortTypeSyntaxError('Int??', parsed)).toBe("'Int??' is not a port type name: a second '?' after '?' at 4.")
  })

  it('parses and writes a 100,000-deep nullable chain without recursing', () => {
    const deep = 'Gauge<'.repeat(100_000) + 'Int?' + '>?'.repeat(100_000)
    const parsed = parsePortTerm(deep)
    if (!parsed.ok) throw new Error('expected a term')
    expect(formatPortTerm(parsed.term)).toBe(deep)
    expect(portTermsEqual(parsed.term, parsed.term)).toBe(true)
  })

  it('compares names ASCII case-insensitively and a parameter exactly', () => {
    const read = (text: string, grammar: 'document' | 'declaration' = 'document'): PortTerm => {
      const parsed = parsePortTerm(text, grammar)
      if (!parsed.ok) throw new Error(text)
      return parsed.term
    }
    expect(portTermsEqual(read('Collection<int?>'), read('Collection<INT?>'))).toBe(true)
    expect(portTermsEqual(read('Int'), read('Int?'))).toBe(false)
    expect(portTermsEqual(read('$T', 'declaration'), read('$t', 'declaration'))).toBe(false)
    expect(portTermsEqual(read('Map<String,Int>'), read('MAP<Int>'))).toBe(false)
    expect(portTermsEqual(read('Map<string,Int>'), read('Map<STRING,INT>'))).toBe(true)
  })
})

describe('parsePortTypeName over the nullable and constructor grammar', () => {
  it.each([
    ['Int?', 0, 'Int?'],
    ['Collection<Int?>', 1, 'Int?'],
    ['Collection<Int>?', 0, 'Collection<Int>?'],
    ['Collection<Collection<Int>?>', 1, 'Collection<Int>?'],
    ['Gauge<Boolean>', 0, 'Gauge<Boolean>'],
    ['Collection<Gauge<Boolean>>', 1, 'Gauge<Boolean>'],
  ] as const)('reads %j as depth %i of %j', (text, depth, element) => {
    expect(parsePortTypeName(text)).toEqual({ ok: true, depth, element })
  })
})

describe('parsePortTypeName', () => {
  it.each([
    ['Int', 0, 'Int'],
    ['Collection<Int>', 1, 'Int'],
    ['Collection<Collection<Any>>', 2, 'Any'],
    ['Collection<any>', 1, 'any'],
    ['_x9', 0, '_x9'],
    ['Collectible', 0, 'Collectible'],
  ] as const)('reads %j as depth %i of %j', (text, depth, element) => {
    expect(parsePortTypeName(text)).toEqual({ ok: true, depth, element })
  })

  it.each([
    [null, 'Absent', 0],
    [undefined, 'Absent', 0],
    ['', 'Empty', 0],
    ['Pt ', 'IllegalCharacter', 2],
    ['Foo Bar', 'IllegalCharacter', 3],
    ['Reeve.Device', 'IllegalCharacter', 5],
    ['Collection< Int >', 'IllegalCharacter', 11],
    ['\u0000', 'IllegalCharacter', 0],
    ['\ud800', 'IllegalCharacter', 0],
    ['<X>', 'ExpectedTypeName', 0],
    ['Collection<>', 'ExpectedTypeName', 11],
    ['Collection<', 'ExpectedTypeName', 11],
    ['Collection', 'CollectionWithoutBracket', 0],
    ['Collection<Collection>', 'CollectionWithoutBracket', 11],
    ['CollectionInt', 'ReservedCollectionPrefix', 0],
    ['collection<Int>', 'ReservedCollectionPrefix', 0],
    ['Collections', 'ReservedCollectionPrefix', 0],
    ['Collection<Int', 'MissingClose', 14],
    ['Int>', 'UnexpectedClose', 3],
    ['Collection<Int>>', 'UnexpectedClose', 15],
  ] as const)('refuses %j with %s at %i', (text, fault: PortTypeSyntaxFault, position) => {
    expect(parsePortTypeName(text)).toMatchObject({ ok: false, fault, position })
  })

  const GENERIC_HINT = 'write Collection<T> with the type of its elements'

  it.each(['CollectionInt', 'CollectionCollection', 'CollectionX<Y>', 'CollectionAny', 'CollectionFloat', 'Collectionany'])(
    'hints to write Collection<T> for the reserved prefix in %s',
    (text) => {
      expect(parsePortTypeName(text)).toMatchObject({ ok: false, fault: 'ReservedCollectionPrefix', position: 0, hint: GENERIC_HINT })
    },
  )

  it.each(['Collection', 'Collection<Collection>', 'Collection>'])('hints to write Collection<T> for the missing bracket in %s', (text) => {
    expect(parsePortTypeName(text)).toMatchObject({ ok: false, fault: 'CollectionWithoutBracket', hint: GENERIC_HINT })
  })

  it.each([
    ['Foo Bar', 'white space'],
    ['Foo\u0085', 'white space'],
    ['Foo\u0001', 'a control character'],
    ['Größe', 'a non-ASCII character'],
    ['\ud800', 'a non-ASCII character'],
    ['Foo\u007f', 'a control character'],
    ['Reeve.Device', "'.'"],
  ])('names what was illegal in %j as %s', (text, hint) => {
    expect(parsePortTypeName(text)).toMatchObject({ fault: 'IllegalCharacter', hint })
  })

  it('carries no hint for a fault that has none', () => {
    expect(parsePortTypeName('Collection<Int')).not.toHaveProperty('hint')
  })

  it('parses a 100,000-deep collection without recursing', () => {
    const deep = 'Collection<'.repeat(100_000) + 'Int' + '>'.repeat(100_000)
    expect(parsePortTypeName(deep)).toEqual({ ok: true, depth: 100_000, element: 'Int' })
  })
})

describe('formatPortTypeName', () => {
  it('writes a term in the one notation, the element as given', () => {
    expect([formatPortTypeName(0, 'Device'), formatPortTypeName(2, 'Any')]).toEqual(['Device', 'Collection<Collection<Any>>'])
  })

  it('writes what parsePortTypeName reads back as the same term', () => {
    const parsed = parsePortTypeName(formatPortTypeName(3, 'Room'))
    expect(parsed).toEqual({ ok: true, depth: 3, element: 'Room' })
  })

  it('does not check the element, so a malformed element makes a name the grammar refuses', () => {
    const written = formatPortTypeName(1, 'Foo Bar')
    expect([written, parsePortTypeName(written).ok]).toEqual(['Collection<Foo Bar>', false])
  })

  it('refuses a depth that is not a count', () => {
    expect(() => formatPortTypeName(-1, 'Int')).toThrow(/depth must be a non-negative integer/)
  })
})

describe('describePortTypeSyntaxError', () => {
  it('reads like the backend message, hint included', () => {
    const parsed = parsePortTypeName('CollectionInt')
    if (parsed.ok) throw new Error('expected a refusal')
    expect(describePortTypeSyntaxError('CollectionInt', parsed)).toBe(
      "'CollectionInt' is not a port type name: names starting with 'collection' are reserved at 0. " +
        'write Collection<T> with the type of its elements.',
    )
  })

  it('reads like the backend message without a hint', () => {
    const parsed = parsePortTypeName(undefined)
    if (parsed.ok) throw new Error('expected a refusal')
    expect(describePortTypeSyntaxError(undefined, parsed)).toBe("'' is not a port type name: the type is missing at 0.")
  })
})

describe('collectedPortType', () => {
  it.each([
    ['Int', 'Collection<Int?>'],
    ['Int?', 'Collection<Int?>'],
    ['Collection<String>', 'Collection<Collection<String>?>'],
    ['Collection<String>?', 'Collection<Collection<String>?>'],
  ])('writes %s as %s', (stored, collected) => {
    expect(collectedPortType(stored)).toBe(collected)
  })

  it('answers a text that is not a term as it is', () => {
    expect(collectedPortType('Collection<')).toBe('Collection<')
  })
})

describe('the Map<String,V> term', () => {
  const parse = (text: string, grammar: 'document' | 'declaration' = 'document'): PortTerm => {
    const parsed = parsePortTerm(text, grammar)
    if (!parsed.ok) throw new Error(`${text} was refused: ${parsed.fault} at ${String(parsed.position)}`)
    return parsed.term
  }

  it.each([
    'Map<String,Int>', 'Map<string,Int>', 'Map<STRING,Json>', 'Map<String,Int>?', 'Map<String,Int?>', 'Map<String,Collection<Int>>',
    'Collection<Map<String,Int>>?', 'Map<String,Map<String,Gauge<Int>?>>?',
  ])('writes %s back as it was written and reads that back equal', (text) => {
    const term = parse(text)
    expect(formatPortTerm(term)).toBe(text)
    expect(portTermsEqual(parse(formatPortTerm(term)), term)).toBe(true)
  })

  it('holds the key as written and continues through the value', () => {
    const term = parse('Map<string,Collection<Int>>?')
    expect(term).toMatchObject({ kind: 'constructor', name: 'Map', nullable: true, key: { name: 'string' }, argument: { name: 'Collection' } })
  })

  it('equals a map only by its value and its nullability, the key compared in any case', () => {
    const a = parse('Map<String,Int>')
    expect(portTermsEqual(a, parse('Map<string,int>'))).toBe(true)
    expect(portTermsEqual(a, parse('Map<String,Number>'))).toBe(false)
    expect(portTermsEqual(a, parse('Map<String,Int>?'))).toBe(false)
    expect(portTermsEqual(a, parse('Collection<Int>'))).toBe(false)
    expect(portTermsEqual(a, parse('Gauge<Int>'))).toBe(false)
  })

  it('reads a map as a name whose element is the whole map', () => {
    expect(parsePortTypeName('Collection<Map<String,Int>>')).toEqual({ ok: true, depth: 1, element: 'Map<String,Int>' })
    expect(parsePortTypeName('Map<String,Int>')).toEqual({ ok: true, depth: 0, element: 'Map<String,Int>' })
  })

  it.each([
    ['Map<Int,String>', 'MapKey', 4],
    ['Map<String?,Int>', 'MapKey', 4],
    ['Map<String<Int>,Int>', 'MapKey', 4],
    ['Map<Collection<String>,Int>', 'MapKey', 4],
    ['Map<Foo,Int>', 'MapKey', 4],
    ['Map<K,V>', 'MapKey', 4],
    ['Collection<Map<Int,Int>>', 'MapKey', 15],
    ['Map', 'MapWithoutBracket', 0],
    ['Map?', 'MapWithoutBracket', 0],
    ['Collection<Map>', 'MapWithoutBracket', 11],
    ['Map<String>', 'MapWithoutValue', 10],
    ['Map<String', 'MapWithoutValue', 10],
    ['Map<String,', 'ExpectedTypeName', 11],
    ['Map<String,Int,Int>', 'IllegalCharacter', 14],
    ['Map<String, Int>', 'IllegalCharacter', 11],
    ['Gauge<Int,Int>', 'IllegalCharacter', 9],
    ['Collection<Int,Int>', 'IllegalCharacter', 14],
    ['Map<String,Int', 'MissingClose', 14],
  ] as const)('refuses %j with %s at %i', (text, fault, position) => {
    expect(parsePortTerm(text, 'document')).toMatchObject({ ok: false, fault, position })
  })

  it('never takes a type parameter for a key, and takes one for a value under the declaration grammar only', () => {
    expect(parsePortTerm('Map<$T,Int>', 'declaration')).toMatchObject({ ok: false, fault: 'MapKey', position: 4 })
    expect(parsePortTerm('Map<$T,Int>', 'document')).toMatchObject({ ok: false, fault: 'IllegalCharacter', position: 4 })
    expect(formatPortTerm(parse('Map<String,$T?>', 'declaration'))).toBe('Map<String,$T?>')
    expect(parsePortTerm('Map<String,$T?>', 'document').ok).toBe(false)
  })

  it('leaves map and Mapx as plain names', () => {
    expect(parse('map')).toMatchObject({ kind: 'atom', name: 'map' })
    expect(parse('Mapx')).toMatchObject({ kind: 'atom', name: 'Mapx' })
  })

  it('reads, writes and compares a map ten thousand levels deep without recursing', () => {
    const text = 'Map<String,'.repeat(10_000) + 'Int' + '>'.repeat(10_000)
    const term = parse(text)
    expect(formatPortTerm(term)).toBe(text)
    expect(portTermsEqual(term, parse(text))).toBe(true)
  })

  it('describes a map fault as the backend does', () => {
    const error = parsePortTerm('Map<Int,String>', 'document')
    if (error.ok) throw new Error('expected a refusal')
    expect(describePortTypeSyntaxError('Map<Int,String>', error)).toBe("'Map<Int,String>' is not a port type name: the key of a Map must be String at 4.")
  })
})
