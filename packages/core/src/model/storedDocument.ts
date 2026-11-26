import { foldAsciiCase } from '../portTypes/ascii.js'
import { DEFINITION, PARAMETER_LIST, PORT_LIST, PROJECTION, STORED_DOCUMENT_RULES } from './storedDocumentGrammar.js'
import type { ArrayKind, Kind, MapKind, ObjectKind, StoredDocumentRule } from './storedDocumentGrammar.js'
import type { CanvasPathSegment, MappingProjection, WorkflowDefinition } from './workflow.js'

export type { StoredDocumentRule } from './storedDocumentGrammar.js'

/** Where a document breaks a rule: an RFC 6901 pointer, with `#` where it steps into a port list held as a
 * string, and the node it sits in, when it sits in one the reader had begun to read. */
export interface StoredDocumentFault {
  rule: StoredDocumentRule
  location: string
  detail: string
  nodeId: string | null
  /** The nested canvas path, when the fault is a duplicate node id. */
  canvasPath?: readonly CanvasPathSegment[]
  /** Container block type ids aligned with canvasPath; use catalogue labels when describing a refused canvas. */
  canvasBlockTypeIds?: readonly string[]
}

export type StoredPortTypeSiteKind = 'DeclaredOutput' | 'DynamicInputPort' | 'DynamicOutputPort' | 'InputParameter' | 'OutputParameter' | 'TypeArgument' | 'ProjectionType'

/** One port type field of a document the reader accepted, with its text as written: `null` when it is
 * absent or `null`. The grammar, not the reader, decides whether the text is a name. */
export interface StoredPortTypeSite {
  site: StoredPortTypeSiteKind
  location: string
  nodeId: string | null
  portName: string
  index: number
  rawType: string | null
  isRequired: boolean
}

export type StoredDocumentRead = { ok: true; sites: StoredPortTypeSite[] } | { ok: false; fault: StoredDocumentFault }

/** A definition the reader accepted, with its sites, or the first rule it breaks. `definition` is the value
 * read, never repaired: each number in it is the double whose shortest round-trip form has the value its text
 * wrote (L7), so a save writes that value, though perhaps spelled anew (`1e2` as `100`). */
export type StoredDefinitionRead =
  | { ok: true; sites: StoredPortTypeSite[]; definition: WorkflowDefinition }
  | { ok: false; fault: StoredDocumentFault }

/** A parameter list or a port list the reader accepted, with its sites and its entries as read, or the first
 * rule it breaks. */
export type StoredListRead =
  | { ok: true; sites: StoredPortTypeSite[]; entries: readonly Readonly<Record<string, unknown>>[] }
  | { ok: false; fault: StoredDocumentFault }

/** The deepest nesting of arrays and objects a document may have, the outermost counting as 1. */
export const MAX_DOCUMENT_NESTING = 64

/** The configuration keys whose values are the library's port lists. */
export const INPUT_PORTS_KEY = 'InputPorts'
export const OUTPUT_PORTS_KEY = 'OutputPorts'

type Json =
  | { is: 'null' }
  | { is: 'boolean'; value: boolean }
  | { is: 'number'; value: number; text: string }
  | { is: 'string'; value: string }
  | { is: 'array'; items: Json[] }
  | { is: 'object'; entries: [string, Json][] }
  | { is: 'other' }

const escapeKey = (key: string): string => key.replaceAll('~', '~0').replaceAll('/', '~1')
const append = (pointer: string, key: string | number): string => `${pointer}/${typeof key === 'number' ? String(key) : escapeKey(key)}`

const isHigh = (unit: number): boolean => unit >= 0xd800 && unit <= 0xdbff
const isLow = (unit: number): boolean => unit >= 0xdc00 && unit <= 0xdfff

function isWellFormed(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i)
    if (isHigh(unit) && isLow(value.charCodeAt(i + 1))) i++
    else if (isHigh(unit) || isLow(unit)) return false
  }
  return true
}

const MAX_EXPONENT_DIGITS = 15

function decimal(text: string): { negative: boolean; digits: string; exponent: number; beyond: number } {
  const negative = text.startsWith('-')
  const body = negative ? text.slice(1) : text
  let e = body.indexOf('e')
  if (e < 0) e = body.indexOf('E')
  let exponent = 0
  let beyond = 0
  if (e >= 0) {
    let start = e + 1
    const negativePower = body.charAt(start) === '-'
    if (body.charAt(start) === '-' || body.charAt(start) === '+') start++
    while (start < body.length && body.charAt(start) === '0') start++
    const power = body.slice(start)
    if (power.length > MAX_EXPONENT_DIGITS) beyond = negativePower ? -1 : 1
    else if (power.length > 0) exponent = (negativePower ? -1 : 1) * Number(power)
  }
  const mantissa = e < 0 ? body : body.slice(0, e)
  const dot = mantissa.indexOf('.')
  const digits = dot < 0 ? mantissa : mantissa.slice(0, dot) + mantissa.slice(dot + 1)
  if (dot >= 0) exponent -= mantissa.length - dot - 1
  let first = 0
  while (first < digits.length && digits.charAt(first) === '0') first++
  let end = digits.length
  while (end > first && digits.charAt(end - 1) === '0') end--
  exponent += digits.length - end
  const significant = digits.slice(first, end)
  return significant === ''
    ? { negative: false, digits: '', exponent: 0, beyond: 0 }
    : { negative, digits: significant, exponent: beyond === 0 ? exponent : 0, beyond }
}

function numberRefusal(text: string): string | null {
  const written = decimal(text)
  if (written.digits === '') return null
  const notFinite = `the number ${text}, which is not a finite double`
  const notShortest = `the number ${text}, whose value differs from its double's shortest round-trip form`
  if (written.beyond !== 0) return written.beyond > 0 ? notFinite : notShortest
  const value = Number(text)
  if (!Number.isFinite(value)) return notFinite
  const held = decimal(String(value))
  return held.negative === written.negative && held.digits === written.digits && held.exponent === written.exponent
    ? null
    : notShortest
}

class Fault extends Error {
  constructor(readonly fault: StoredDocumentFault) {
    super(fault.detail)
  }
}

const fault = (rule: StoredDocumentRule, location: string, detail: string): Fault =>
  new Fault({ rule, location, detail, nodeId: null })

const tooDeep = (at: string): Fault => fault(STORED_DOCUMENT_RULES.L2, at, `more than ${String(MAX_DOCUMENT_NESTING)} nested arrays and objects`)

class Lexer {
  private i = 0

  constructor(private readonly source: string) {}

  document(): Json {
    this.space()
    const value = this.value('', 1)
    this.space()
    if (this.i < this.source.length) throw this.syntax('text after the value')
    return value
  }

  private syntax(what: string): Fault {
    return fault(STORED_DOCUMENT_RULES.L1, '', `not one JSON value (${what} at character ${String(this.i)})`)
  }

  private space(): void {
    while (this.i < this.source.length && ' \t\n\r'.includes(this.source.charAt(this.i))) this.i++
  }

  private value(at: string, depth: number): Json {
    const c = this.source.charAt(this.i)
    if (c === '{') return this.object(at, depth)
    if (c === '[') return this.array(at, depth)
    if (c === '"') {
      const value = this.string()
      if (!isWellFormed(value.text) || value.mixed) throw fault(STORED_DOCUMENT_RULES.L4, at, 'a string with an unpaired surrogate')
      return { is: 'string', value: value.text }
    }
    if (c === '-' || (c >= '0' && c <= '9')) return this.number()
    for (const [word, json] of [['true', { is: 'boolean', value: true }], ['false', { is: 'boolean', value: false }], ['null', { is: 'null' }]] as const) {
      if (this.source.startsWith(word, this.i)) {
        this.i += word.length
        return json
      }
    }
    throw this.syntax('no value')
  }

  private object(at: string, depth: number): Json {
    if (depth > MAX_DOCUMENT_NESTING) throw tooDeep(at)
    this.i++
    this.space()
    const entries: [string, Json][] = []
    const keys = new Set<string>()
    if (this.source.charAt(this.i) === '}') {
      this.i++
      return { is: 'object', entries }
    }
    for (;;) {
      if (this.source.charAt(this.i) !== '"') throw this.syntax('no property name')
      const key = this.string()
      this.space()
      if (this.source.charAt(this.i) !== ':') throw this.syntax('no colon')
      this.i++
      if (!isWellFormed(key.text) || key.mixed) throw fault(STORED_DOCUMENT_RULES.L4, at, 'a property name with an unpaired surrogate')
      if (keys.has(key.text)) throw fault(STORED_DOCUMENT_RULES.L3, append(at, key.text), `a second property named '${key.text}'`)
      keys.add(key.text)
      this.space()
      entries.push([key.text, this.value(append(at, key.text), depth + 1)])
      this.space()
      const next = this.source.charAt(this.i++)
      if (next === '}') return { is: 'object', entries }
      if (next !== ',') throw this.syntax('no comma')
      this.space()
    }
  }

  private array(at: string, depth: number): Json {
    if (depth > MAX_DOCUMENT_NESTING) throw tooDeep(at)
    this.i++
    this.space()
    const items: Json[] = []
    if (this.source.charAt(this.i) === ']') {
      this.i++
      return { is: 'array', items }
    }
    for (;;) {
      items.push(this.value(append(at, items.length), depth + 1))
      this.space()
      const next = this.source.charAt(this.i++)
      if (next === ']') return { is: 'array', items }
      if (next !== ',') throw this.syntax('no comma')
      this.space()
    }
  }

  private string(): { text: string; mixed: boolean } {
    this.i++
    let out = ''
    let mixed = false
    let previous: 'raw' | 'escaped' | null = null
    for (;;) {
      if (this.i >= this.source.length) throw this.syntax('an unterminated string')
      const c = this.source.charAt(this.i)
      const unit = c.charCodeAt(0)
      if (c === '"') {
        this.i++
        return { text: out, mixed }
      }
      if (unit < 0x20) throw this.syntax('a control character in a string')
      let decoded: number
      let how: 'raw' | 'escaped'
      if (c === '\\') {
        const e = this.source.charAt(this.i + 1)
        const simple: Record<string, string> = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' }
        if (e === 'u') {
          const hex = this.source.slice(this.i + 2, this.i + 6)
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) throw this.syntax('a malformed \\u escape')
          decoded = parseInt(hex, 16)
          this.i += 6
        } else if (Object.hasOwn(simple, e)) {
          decoded = (simple[e] ?? '').charCodeAt(0)
          this.i += 2
        } else {
          throw this.syntax('an escape that is not JSON')
        }
        how = 'escaped'
      } else {
        decoded = unit
        this.i++
        how = 'raw'
      }
      if (isLow(decoded) && previous !== null && previous !== how && isHigh(out.charCodeAt(out.length - 1))) mixed = true
      previous = isHigh(decoded) ? how : null
      out += String.fromCharCode(decoded)
    }
  }

  private number(): Json {
    const match = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(this.source.slice(this.i))
    if (match === null) throw this.syntax('a malformed number')
    this.i += match[0].length
    return { is: 'number', value: Number(match[0]), text: match[0] }
  }
}

function lex(source: string): Json {
  return new Lexer(source).document()
}

function hold(value: unknown, at: string, depth: number): Json {
  if (value === null) return { is: 'null' }
  switch (typeof value) {
    case 'boolean': return { is: 'boolean', value }
    case 'number': return { is: 'number', value, text: String(value) }
    case 'string':
      if (!isWellFormed(value)) throw fault(STORED_DOCUMENT_RULES.L4, at, 'a string with an unpaired surrogate')
      return { is: 'string', value }
    case 'object': {
      if (depth > MAX_DOCUMENT_NESTING) throw tooDeep(at)
      if (typeof (value as { toJSON?: unknown }).toJSON === 'function') return { is: 'other' }
      if (Array.isArray(value)) {
        return { is: 'array', items: Array.from(value as unknown[], (item, i) => hold(item === undefined ? null : item, append(at, i), depth + 1)) }
      }
      const prototype: unknown = Object.getPrototypeOf(value)
      if (prototype !== Object.prototype && prototype !== null) return { is: 'other' }
      const entries: [string, Json][] = []
      for (const [key, inner] of Object.entries(value as Record<string, unknown>)) {
        if (inner === undefined) continue
        if (!isWellFormed(key)) throw fault(STORED_DOCUMENT_RULES.L4, at, 'a property name with an unpaired surrogate')
        entries.push([key, hold(inner, append(at, key), depth + 1)])
      }
      return { is: 'object', entries }
    }
    default: return { is: 'other' }
  }
}

function plain(json: Json): unknown {
  switch (json.is) {
    case 'null': case 'other': return null
    case 'boolean': case 'number': case 'string': return json.value
    case 'array': return json.items.map(plain)
    case 'object': {
      const result: Record<string, unknown> = {}
      for (const [key, inner] of json.entries) {
        Object.defineProperty(result, key, { value: plain(inner), enumerable: true, writable: true, configurable: true })
      }
      return result
    }
  }
}

function describeValue(value: Json): string {
  switch (value.is) {
    case 'null': return 'null'
    case 'array': return 'an array'
    case 'string': return 'a string'
    case 'number': return 'a number'
    case 'boolean': return 'a boolean'
    case 'object': return 'an object'
    case 'other': return 'a value that is not JSON'
  }
}

function describeKind(kind: Kind): string {
  switch (kind.of) {
    case 'object': return `a ${kind.name}`
    case 'portType': return 'a port type name'
    case 'string': return 'a string'
    case 'number': return 'a number'
    case 'boolean': return 'true or false'
    case 'enum': return kind.noun ?? 'a policy name'
    case 'array': return 'an array'
    case 'any': return 'a literal value'
    default: return 'an object'
  }
}

const member = (value: Json & { is: 'object' }, key: string): Json | undefined => value.entries.find(([k]) => k === key)?.[1]

class Walk {
  readonly sites: StoredPortTypeSite[] = []
  private prefix = ''
  private nodeId: string | null = null
  private index = 0
  private readonly canvasNodeIds: Set<string>[] = []
  private readonly canvasEdgeIds: Set<string>[] = []
  private readonly canvasPath: CanvasPathSegment[] = []
  private readonly canvasBlockTypeIds: string[] = []
  private nodeBlockTypeId = ''
  private connectionTarget: string | null = null
  private mapKey = ''
  private readonly canvasProjectionCounts: number[] = []

  constructor(private listSite: StoredPortTypeSiteKind) {}

  value(value: Json, kind: Kind, nullable: boolean, at: string): void {
    if (kind.of === 'refused') return this.fail(kind.rule, at, kind.detail)
    if (value.is === 'null') {
      if (!nullable) this.fail(STORED_DOCUMENT_RULES.L7, at, `null where ${describeKind(kind)} is required`)
      return
    }
    switch (kind.of) {
      case 'string':
        if (value.is !== 'string') return this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where a string is required`)
        if (kind.nonEmpty && value.value === '') this.fail(STORED_DOCUMENT_RULES.L7, at, 'an empty string where a name is required')
        return
      case 'number':
        if (value.is !== 'number') return this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where a number is required`)
        this.numbers(value, at)
        return
      case 'boolean':
        if (value.is !== 'boolean') this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where true or false is required`)
        return
      case 'any':
        this.numbers(value, at)
        return
      case 'enum':
        if (value.is !== 'string' || !kind.names.includes(value.value)) {
          this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where one of ${kind.names.join(', ')} is required`)
        }
        return
      case 'portType':
        if (value.is !== 'string') this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where a port type name is required`)
        return
      case 'array':
        this.array(value, kind, at)
        return
      case 'map':
        this.map(value, kind, at)
        return
      case 'config':
        this.config(value, at)
        return
      case 'object':
        this.object(value, kind, at)
    }
  }

  embedded(listText: string, site: StoredPortTypeSiteKind, at: string): void {
    const [outerPrefix, outerSite, outerIndex] = [this.prefix, this.listSite, this.index]
    this.prefix = `${this.prefix}${at}#`
    this.listSite = site
    try {
      let list: Json
      try {
        list = lex(listText)
      } catch (error) {
        if (!(error instanceof Fault)) throw error
        return this.fail(error.fault.rule, error.fault.location, error.fault.detail)
      }
      this.value(list, PORT_LIST, false, '')
    } finally {
      [this.prefix, this.listSite, this.index] = [outerPrefix, outerSite, outerIndex]
    }
  }

  private array(value: Json, kind: ArrayKind, at: string): void {
    if (value.is !== 'array') return this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where an array is required`)
    value.items.forEach((element, i) => {
      this.index = i
      this.value(element, kind.element, false, append(at, i))
    })
  }

  private map(value: Json, kind: MapKind, at: string): void {
    if (value.is !== 'object') return this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where an object is required`)
    let typeArgumentIndex = 0
    for (const [key, inner] of value.entries) {
      if (kind.role === 'typeArguments') {
        this.value(inner, kind.value, false, append(at, key))
        if (inner.is === 'string') this.sites.push({ site: 'TypeArgument', location: append(at, key), nodeId: this.nodeId, portName: key, index: typeArgumentIndex++, rawType: inner.value, isRequired: false })
        continue
      }
      if (kind.role === 'subCanvases') {
        this.canvasPath.push({ nodeId: this.nodeId ?? '', branchName: key })
        this.canvasBlockTypeIds.push(this.nodeBlockTypeId)
      }
      const outerKey = this.mapKey
      this.mapKey = key
      try {
        this.value(inner, kind.value, false, append(at, key))
      } finally {
        this.mapKey = outerKey
        if (kind.role === 'subCanvases') {
          this.canvasPath.pop()
          this.canvasBlockTypeIds.pop()
        }
      }
    }
  }

  private config(value: Json, at: string): void {
    if (value.is !== 'object') return this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where an object is required`)
    for (const [key, inner] of value.entries) {
      const site = key === INPUT_PORTS_KEY ? 'DynamicInputPort' : key === OUTPUT_PORTS_KEY ? 'DynamicOutputPort' : undefined
      if (site === undefined) {
        if (inner.is === 'null') this.fail(STORED_DOCUMENT_RULES.L7, append(at, key), 'null where removing the configuration key is required')
        this.numbers(inner, append(at, key))
        continue
      }
      const listAt = append(at, key)
      if (inner.is !== 'string') return this.fail(STORED_DOCUMENT_RULES.L7, listAt, `${describeValue(inner)} where a port list written as a string is required`)
      if (inner.value.length === 0) this.fail(STORED_DOCUMENT_RULES.L7, listAt, 'an empty port list where the empty array string is required')
      this.embedded(inner.value, site, listAt)
    }
  }

  private object(value: Json, kind: ObjectKind, at: string): void {
    if (value.is !== 'object') return this.fail(STORED_DOCUMENT_RULES.L7, at, `${describeValue(value)} where ${describeKind(kind)} is required`)
    const entryIndex = this.index
    const outerNodeId = this.nodeId
    const outerBlockTypeId = this.nodeBlockTypeId
    const outerTarget = this.connectionTarget
    if (kind.role === 'canvas') {
      this.canvasNodeIds.push(new Set())
      this.canvasEdgeIds.push(new Set())
      this.canvasProjectionCounts.push(0)
    }
    if (kind.role === 'connection') {
      const target = member(value, 'targetNodeId')
      this.connectionTarget = target?.is === 'string' ? target.value : null
    }
    if (kind.role === 'node') {
      const id = member(value, 'id')
      this.nodeId = id?.is === 'string' ? id.value : null
      const blockType = member(value, 'blockTypeId')
      this.nodeBlockTypeId = blockType?.is === 'string' ? blockType.value : ''
    }
    try {
      const present = new Set<string>()
      for (const [key, inner] of value.entries) {
        const exact = kind.fields.find((f) => f.key === key)
        if (exact) {
          present.add(exact.key)
          if (exact.refusedRule) this.fail(exact.refusedRule, append(at, key), exact.refusedDetail ?? 'this member is refused')
          this.value(inner, exact.kind, exact.nullable, append(at, key))
          const isAddressableName = kind.role === 'node'
            ? exact.key === 'id'
            : (kind.role === 'declaredOutput' || kind.role === 'portEntry' || kind.role === 'parameter') && exact.key === 'name'
          if (isAddressableName) {
            if (inner.is === 'string' && inner.value.includes('.')) {
              this.fail(STORED_DOCUMENT_RULES.DottedName, append(at, key), `name '${inner.value}' contains a dot`)
            }
          }
          if (kind.role === 'node' && exact.key === 'passthrough' && inner.is === 'array') {
            inner.items.forEach((name, i) => {
              if (name.is === 'string' && name.value.includes('.')) this.fail(STORED_DOCUMENT_RULES.DottedName, append(append(at, key), i), `name '${name.value}' contains a dot`)
            })
          }
          if (kind.role === 'node' && exact.key === 'id' && inner.is === 'string') {
            const ids = this.canvasNodeIds[this.canvasNodeIds.length - 1]
            if (ids?.has(inner.value)) this.fail(STORED_DOCUMENT_RULES.DuplicateNodeId, append(at, key), `a second node with id '${inner.value}' on this canvas`)
            ids?.add(inner.value)
          }
          continue
        }
        const variant = kind.fields.find((f) => foldAsciiCase(f.key) === foldAsciiCase(key))
        if (variant) this.fail(STORED_DOCUMENT_RULES.L5, append(at, key), `a key '${key}' where '${variant.key}' is meant; keys match exactly`)
        this.fail(STORED_DOCUMENT_RULES.UnknownKey, append(at, key), `unlisted key '${key}' on ${kind.name}`)
      }
      for (const f of kind.fields) {
        if (f.required && !present.has(f.key)) this.fail(STORED_DOCUMENT_RULES.L7, append(at, f.key), `${describeKind(kind)} with no '${f.key}'`)
      }
      if (kind.role === 'connection') {
        const id = member(value, 'id')
        if (id?.is === 'string' && this.canvasEdgeIds[this.canvasEdgeIds.length - 1]?.has(id.value)) {
          this.fail(STORED_DOCUMENT_RULES.DuplicateEdgeId, append(at, 'id'), `a second connection with id '${id.value}' on this canvas`)
        }
        if (id?.is === 'string') this.canvasEdgeIds[this.canvasEdgeIds.length - 1]?.add(id.value)
      }
      if (kind.role === 'declaredOutput' || kind.role === 'portEntry' || kind.role === 'parameter') {
        const name = member(value, 'name')
        const type = member(value, 'type')
        const required = member(value, 'isRequired')
        this.sites.push({
          site: kind.role === 'declaredOutput' ? 'DeclaredOutput' : this.listSite,
          location: this.prefix + append(at, 'type'),
          nodeId: this.nodeId,
          portName: name?.is === 'string' ? name.value : '',
          index: entryIndex,
          rawType: type?.is === 'string' ? type.value : null,
          isRequired: required?.is === 'boolean' && required.value,
        })
      }
      const projectionType = kind.role === 'projection' && this.canvasProjectionCounts.length > 0 ? member(value, 'type') : undefined
      if (projectionType?.is === 'string') {
        const count = this.canvasProjectionCounts.length - 1
        this.sites.push({
          site: 'ProjectionType', location: this.prefix + append(at, 'type'), nodeId: this.connectionTarget, portName: this.mapKey,
          index: this.canvasProjectionCounts[count] ?? 0, rawType: projectionType.value, isRequired: false,
        })
        this.canvasProjectionCounts[count] = (this.canvasProjectionCounts[count] ?? 0) + 1
      }
    } finally {
      this.nodeId = outerNodeId
      this.nodeBlockTypeId = outerBlockTypeId
      this.connectionTarget = outerTarget
      if (kind.role === 'canvas') {
        this.canvasNodeIds.pop()
        this.canvasEdgeIds.pop()
        this.canvasProjectionCounts.pop()
      }
    }
  }

  private numbers(value: Json, at: string): void {
    if (value.is === 'other') {
      this.fail(STORED_DOCUMENT_RULES.L7, at, 'a value that is not JSON')
    } else if (value.is === 'number') {
      const refusal = numberRefusal(value.text)
      if (refusal !== null) this.fail(STORED_DOCUMENT_RULES.L7, at, refusal)
    } else if (value.is === 'array') {
      value.items.forEach((item, i) => { this.numbers(item, append(at, i)) })
    } else if (value.is === 'object') {
      for (const [key, inner] of value.entries) this.numbers(inner, append(at, key))
    }
  }

  private fail(rule: StoredDocumentRule, at: string, detail: string): never {
    throw new Fault({
      rule, location: this.prefix + at, detail, nodeId: this.nodeId,
      ...(rule === STORED_DOCUMENT_RULES.DuplicateNodeId || rule === STORED_DOCUMENT_RULES.DuplicateEdgeId || rule === STORED_DOCUMENT_RULES.ErrorPolicyRouteUnread
        ? { canvasPath: [...this.canvasPath], ...(this.canvasBlockTypeIds.length > 0 ? { canvasBlockTypeIds: [...this.canvasBlockTypeIds] } : {}) }
        : {}),
    })
  }
}

function attempt<T>(read: () => T): { ok: true; value: T } | { ok: false; fault: StoredDocumentFault } {
  try {
    return { ok: true, value: read() }
  } catch (error) {
    if (error instanceof Fault) return { ok: false, fault: error.fault }
    throw error
  }
}

function walk(json: Json, kind: Kind, site: StoredPortTypeSiteKind): StoredPortTypeSite[] {
  const reader = new Walk(site)
  reader.value(json, kind, false, '')
  return reader.sites
}

const BLANK_LIST = 'a parameter list is blank; no parameters is an absent list, or []'
const NULL_LIST = 'a parameter list is the JSON null; no parameters is an absent list, or []'
const NO_DOCUMENT: StoredDocumentFault = { rule: STORED_DOCUMENT_RULES.L1, location: '', detail: 'no document', nodeId: null }
const isBlank = (value: string | null | undefined): value is null | undefined | '' =>
  value === null || value === undefined || /^[ \t\n\r]*$/.test(value)

function readDefinition(json: () => Json): StoredDefinitionRead {
  const read = attempt(() => {
    const tree = json()
    return { sites: walk(tree, DEFINITION, 'DeclaredOutput'), tree }
  })
  return read.ok ? { ok: true, sites: read.value.sites, definition: plain(read.value.tree) as WorkflowDefinition } : read
}

/** Reads a workflow definition the editor holds, or was handed as a value, under the stored-document rules.
 * A value cannot hold two keys of one name, so L3 is seen only by `readStoredDefinitionText`. */
export function readStoredDefinition(value: unknown): StoredDefinitionRead {
  if (value === undefined) return { ok: false, fault: NO_DOCUMENT }
  return readDefinition(() => hold(value, '', 1))
}

/** Reads a stored definition's text under every reader rule, the first fault in document order. */
export function readStoredDefinitionText(documentText: string | null | undefined): StoredDefinitionRead {
  if (isBlank(documentText)) return { ok: false, fault: NO_DOCUMENT }
  return readDefinition(() => lex(documentText))
}

function readParameters(json: () => Json | null, direction: 'input' | 'output'): StoredListRead {
  const site = direction === 'input' ? 'InputParameter' : 'OutputParameter'
  const read = attempt(() => {
    const tree = json()
    if (tree === null) return { sites: [], entries: [] }
    if (tree.is === 'null') throw fault(STORED_DOCUMENT_RULES.L7, '', NULL_LIST)
    return { sites: walk(tree, PARAMETER_LIST, site), entries: plain(tree) as Record<string, unknown>[] }
  })
  return read.ok ? { ok: true, ...read.value } : read
}

/** Reads a workflow's declared input or output parameters the editor holds. Absent and `null` are none. */
export function readParameterList(value: unknown, direction: 'input' | 'output'): StoredListRead {
  return readParameters(() => (value === undefined || value === null ? null : hold(value, '', 1)), direction)
}

/** `readParameterList` over stored text: absent is no parameters; a blank text and the JSON `null` are refused (L7). */
export function readParameterListText(listText: string | null | undefined, direction: 'input' | 'output'): StoredListRead {
  return readParameters(() => {
    if (listText === null || listText === undefined) return null
    if (isBlank(listText)) throw fault(STORED_DOCUMENT_RULES.L7, '', BLANK_LIST)
    return lex(listText)
  }, direction)
}

/** Reads the port list a node's `InputPorts` or `OutputPorts` configuration value holds. An empty text is refused
 * (`"[]"` is no ports); the node is named on nothing, since the list is read on its own. */
export function readPortList(listText: string, direction: 'input' | 'output'): StoredListRead {
  const read = attempt(() => {
    const reader = new Walk('DeclaredOutput')
    reader.embedded(listText, direction === 'input' ? 'DynamicInputPort' : 'DynamicOutputPort', '')
    return { sites: reader.sites, entries: plain(lex(listText)) as Record<string, unknown>[] }
  })
  return read.ok ? { ok: true, ...read.value } : read
}

/** Reads a mapping's projection the editor holds under the stored-document rules; the server grades its path and type. */
export function readMappingProjection(value: unknown): { ok: true; projection: MappingProjection } | { ok: false; fault: StoredDocumentFault } {
  const read = attempt(() => {
    const tree = hold(value, '', 1)
    walk(tree, PROJECTION, 'ProjectionType')
    return plain(tree) as MappingProjection
  })
  return read.ok ? { ok: true, projection: read.value } : read
}

/** A fault as the backend words it: `detail (rule Lx, at 'location')`; the editor's refusal reads the same way. */
export function describeStoredDocumentFault(fault: Pick<StoredDocumentFault, 'location' | 'detail'> & { readonly rule: string }): string {
  return `${fault.detail} (rule ${fault.rule}, at '${fault.location}')`
}
