import { foldAsciiCase } from './ascii.js'

/** Why a port type name is not a sentence of the grammar in docs/reference/port-types.md. The same members as the backend's
 * `PortTypeSyntaxFault`, so a finding can be acted on by its fault rather than by its wording. */
export type PortTypeSyntaxFault =
  | 'Absent'
  | 'Empty'
  | 'IllegalCharacter'
  | 'ExpectedTypeName'
  | 'CollectionWithoutBracket'
  | 'ReservedCollectionPrefix'
  | 'MissingClose'
  | 'UnexpectedOpen'
  | 'UnexpectedClose'
  | 'DoubleNullable'
  | 'MapWithoutBracket'
  | 'MapKey'
  | 'MapWithoutValue'

/** Which grammar a term is read under. `document` is a stored document, an API value or a `typeArguments` value, and
 * a `$` there is an illegal character; `declaration` is a block or plugin declaration, which may also write `$T`. */
export type PortTermGrammar = 'document' | 'declaration'

/** A port type read into a tree: a `constructor` continues through its `argument`, and a map also holds its `key`. `nullable` is
 * the level's own `?`; the `null` kind is the literal's term. Only `parsePortTerm` validates names. */
export type PortTerm =
  | { readonly kind: 'null'; readonly nullable: true }
  | { readonly kind: 'atom' | 'parameter'; readonly name: string; readonly nullable: boolean }
  | {
      readonly kind: 'constructor'
      readonly name: string
      readonly key?: { readonly kind: 'atom'; readonly name: string; readonly nullable: false }
      readonly argument: PortTerm
      readonly nullable: boolean
    }

export interface ParsedPortTerm {
  ok: true
  term: PortTerm
}

/** A well-formed name as the editor's registry reads it: `depth` non-nullable `Collection` wraps around `element`, the
 * rest of the name as it was written, an atom unless that rest is nullable or a constructor. */
export interface PortTypeNameTerm {
  ok: true
  depth: number
  element: string
}

/** A refusal: the fault, the UTF-16 index it was found at, and, for some faults, a hint for the author. */
export interface PortTypeSyntaxError {
  ok: false
  fault: PortTypeSyntaxFault
  position: number
  hint?: string
}

export type ParsedPortTypeName = PortTypeNameTerm | PortTypeSyntaxError

const KEYWORD = 'Collection'
const BRACKET_PREFIX = `${KEYWORD}<`
const MAP_KEYWORD = 'Map'

type MapKey = { readonly kind: 'atom'; readonly name: string; readonly nullable: false }

const isIdentStart = (c: string): boolean => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c === '_'
const isIdentPart = (c: string): boolean => isIdentStart(c) || (c >= '0' && c <= '9')

const isWhiteSpace = (code: number): boolean =>
  (code >= 0x09 && code <= 0x0d) || code === 0x85 || /[\p{Zs}\p{Zl}\p{Zp}]/u.test(String.fromCharCode(code))
const isControl = (code: number): boolean => code <= 0x1f || (code >= 0x7f && code <= 0x9f)
const isSurrogate = (code: number): boolean => code >= 0xd800 && code <= 0xdfff

const refuse = (fault: PortTypeSyntaxFault, position: number, hint?: string): PortTypeSyntaxError =>
  hint === undefined ? { ok: false, fault, position } : { ok: false, fault, position, hint }

function illegalCharacter(text: string, position: number): PortTypeSyntaxError {
  const code = text.charCodeAt(position)
  const hint = isWhiteSpace(code)
    ? 'white space'
    : isControl(code)
      ? 'a control character'
      : isSurrogate(code) || code > 0x7e
        ? 'a non-ASCII character'
        : `'${text.charAt(position)}'`
  return refuse('IllegalCharacter', position, hint)
}

/** The term of the literal `null`: assignable to every nullable term and to nothing else. */
export const NULL_PORT_TERM: PortTerm = { kind: 'null', nullable: true }

/** Reads a port type as written, with no trimming and no repair, under `grammar` (`document` by default). Iterative,
 * so a name of any depth parses without growing the stack. `null` and `undefined` are `Absent`, `''` is `Empty`. */
export function parsePortTerm(
  text: string | null | undefined,
  grammar: PortTermGrammar = 'document',
): ParsedPortTerm | PortTypeSyntaxError {
  if (text === null || text === undefined) return refuse('Absent', 0)
  if (text.length === 0) return refuse('Empty', 0)

  const declaration = grammar === 'declaration'
  const heads: { name: string; key?: MapKey }[] = []
  let i = 0
  let leaf: string
  let parameter = false
  for (;;) {
    if (i >= text.length) return refuse('ExpectedTypeName', i)
    let c = text.charAt(i)
    if (c === '<' || c === '>' || c === '?') return refuse('ExpectedTypeName', i)
    if (c === '$') {
      if (!declaration) return illegalCharacter(text, i)
      parameter = true
      i++
      if (i >= text.length || text.charAt(i) === '<' || text.charAt(i) === '>' || text.charAt(i) === '?') {
        return refuse('ExpectedTypeName', i)
      }
      c = text.charAt(i)
    }
    if (!isIdentStart(c)) return illegalCharacter(text, i)
    let j = i + 1
    while (j < text.length && isIdentPart(text.charAt(j))) j++
    const after = text.charAt(j)
    if (j < text.length && after !== '<' && after !== '>' && after !== '?') return illegalCharacter(text, j)

    const ident = text.slice(i, j)
    if (parameter) {
      if (j < text.length && after === '<') return refuse('UnexpectedOpen', j)
      leaf = ident
      i = j
      break
    }
    if (ident === KEYWORD) {
      if (j >= text.length || after !== '<') {
        return refuse('CollectionWithoutBracket', i, 'write Collection<T> with the type of its elements')
      }
    } else if (foldAsciiCase(ident).startsWith('collection')) {
      return refuse('ReservedCollectionPrefix', i, 'write Collection<T> with the type of its elements')
    } else if (ident === MAP_KEYWORD) {
      if (j >= text.length || after !== '<') return refuse('MapWithoutBracket', i, 'write Map<String,V> with the type of its values')
      const key = readMapKey(text, j + 1, declaration)
      if ('fault' in key) return key.fault
      heads.push({ name: MAP_KEYWORD, key: key.term })
      i = key.next
      continue
    }
    if (j < text.length && after === '<') {
      heads.push({ name: ident })
      i = j + 1
      continue
    }
    leaf = ident
    i = j
    break
  }

  const nullable: boolean[] = []
  const readNullable = (): PortTypeSyntaxError | undefined => {
    const question = text.charAt(i) === '?'
    if (question) {
      i++
      if (text.charAt(i) === '?') return refuse('DoubleNullable', i)
    }
    nullable.push(question)
    return undefined
  }
  const first = readNullable()
  if (first !== undefined) return first
  for (let k = 0; k < heads.length; k++) {
    if (i >= text.length) return refuse('MissingClose', i)
    if (text.charAt(i) !== '>') return text.charAt(i) === '<' ? refuse('UnexpectedOpen', i) : illegalCharacter(text, i)
    i++
    const next = readNullable()
    if (next !== undefined) return next
  }
  if (i !== text.length) {
    const c = text.charAt(i)
    return c === '>' ? refuse('UnexpectedClose', i) : c === '<' ? refuse('UnexpectedOpen', i) : illegalCharacter(text, i)
  }

  let term: PortTerm = { kind: parameter ? 'parameter' : 'atom', name: leaf, nullable: nullable[0] === true }
  for (let k = 0; k < heads.length; k++) {
    const head = heads[heads.length - 1 - k]
    term = {
      kind: 'constructor', name: head?.name ?? '', ...(head?.key === undefined ? {} : { key: head.key }), argument: term, nullable: nullable[k + 1] === true,
    }
  }
  return { ok: true, term }
}

function readMapKey(text: string, at: number, declaration: boolean): { term: MapKey; next: number } | { fault: PortTypeSyntaxError } {
  const c = text.charAt(at)
  if (at >= text.length || c === '<' || c === '>' || c === '?') return { fault: refuse('ExpectedTypeName', at) }
  if (c === '$') return { fault: declaration ? refuse('MapKey', at) : illegalCharacter(text, at) }
  if (!isIdentStart(c)) return { fault: illegalCharacter(text, at) }
  let end = at + 1
  while (end < text.length && isIdentPart(text.charAt(end))) end++
  const after = text.charAt(end)
  if (end < text.length && after !== '<' && after !== '>' && after !== '?' && after !== ',') return { fault: illegalCharacter(text, end) }
  if (foldAsciiCase(text.slice(at, end)) !== 'string' || (end < text.length && (after === '<' || after === '?'))) return { fault: refuse('MapKey', at) }
  if (end >= text.length || after === '>') return { fault: refuse('MapWithoutValue', end) }
  return { term: { kind: 'atom', name: text.slice(at, end), nullable: false }, next: end + 1 }
}

/** Writes a term in the one notation: constructor heads around their argument in angle brackets, a `$` before a
 * parameter, a `?` after each nullable level. Reading the result gives an equal term. The null literal's term is
 * `null`, which no grammar reads. */
export function formatPortTerm(term: PortTerm): string {
  if (term.kind === 'null') return 'null'
  const closing: boolean[] = []
  let text = ''
  let node: PortTerm = term
  while (node.kind === 'constructor') {
    text += `${node.name}<${node.key === undefined ? '' : `${node.key.name},`}`
    closing.push(node.nullable)
    node = node.argument
  }
  if (node.kind === 'null') return 'null'
  text += (node.kind === 'parameter' ? '$' : '') + node.name + (node.nullable ? '?' : '')
  for (let k = closing.length - 1; k >= 0; k--) text += closing[k] === true ? '>?' : '>'
  return text
}

/** The type of a collecting block's declared output `X` whose stored type is `type`: `Collection<T?>`, or `Collection<T>` when
 * `T` is already nullable. A text that is not a term in the document grammar is answered as it is: the grading reports it. */
export function collectedPortType(type: string): string {
  const parsed = parsePortTerm(type, 'document')
  if (!parsed.ok) return type
  return formatPortTerm({ kind: 'constructor', name: KEYWORD, argument: { ...parsed.term, nullable: true } as PortTerm, nullable: false })
}

/** Whether two terms are the same type: names compare ASCII case-insensitively, a parameter's exactly. */
export function portTermsEqual(a: PortTerm, b: PortTerm): boolean {
  let x: PortTerm | undefined = a
  let y: PortTerm | undefined = b
  while (x !== undefined && y !== undefined) {
    if (x === y) return true
    if (x.kind !== y.kind || x.nullable !== y.nullable) return false
    if (x.kind === 'null' || y.kind === 'null') return true
    if (x.kind === 'parameter' ? x.name !== y.name : foldAsciiCase(x.name) !== foldAsciiCase(y.name)) return false
    if ((x.kind === 'constructor' && x.key !== undefined) !== (y.kind === 'constructor' && y.key !== undefined)) return false
    x = x.kind === 'constructor' ? x.argument : undefined
    y = y.kind === 'constructor' ? y.argument : undefined
  }
  return x === undefined && y === undefined
}

/** Parses a port type name as written, with no trimming and no repair, under the document grammar, and reads it the
 * way the registry does: the non-nullable `Collection` levels it opens with, and the rest. `null` and `undefined`
 * are `Absent`, `''` is `Empty`. */
export function parsePortTypeName(text: string | null | undefined): ParsedPortTypeName {
  const parsed = parsePortTerm(text)
  if (!parsed.ok) return parsed
  let term = parsed.term
  let depth = 0
  while (term.kind === 'constructor' && term.name === KEYWORD && !term.nullable) {
    depth++
    term = term.argument
  }
  if (term.kind === 'atom' && !term.nullable) {
    return { ok: true, depth, element: term.name }
  }
  return { ok: true, depth, element: formatPortTerm(term) }
}

/** Writes a term in the one notation, `Collection<…>` around the element. It does not check the element: a caller
 * that passes one `parsePortTypeName` would refuse gets text it refuses. */
export function formatPortTypeName(depth: number, element: string): string {
  if (!Number.isInteger(depth) || depth < 0) {
    throw new RangeError(`A port type's depth must be a non-negative integer; got ${String(depth)}.`)
  }
  return BRACKET_PREFIX.repeat(depth) + element + '>'.repeat(depth)
}

const FAULT_TEXT: Record<PortTypeSyntaxFault, string> = {
  Absent: 'the type is missing',
  Empty: 'the type is blank',
  IllegalCharacter: 'an illegal character',
  ExpectedTypeName: 'a type name was expected',
  CollectionWithoutBracket: 'Collection needs a bracketed element',
  ReservedCollectionPrefix: "names starting with 'collection' are reserved",
  MissingClose: "a closing '>' was expected",
  UnexpectedOpen: "an unexpected '<'",
  UnexpectedClose: "an unexpected '>'",
  DoubleNullable: "a second '?' after '?'",
  MapWithoutBracket: 'Map needs a bracketed key and value',
  MapKey: 'the key of a Map must be String',
  MapWithoutValue: "a ',' and the value type were expected after the key",
}

/** The message the backend's `PortTypeSyntaxError.Message` gives for the same refusal, word for word, so a
 * finding reads the same on both sides. An absent name is quoted as `''`, as the backend quotes it. */
export function describePortTypeSyntaxError(text: string | null | undefined, error: PortTypeSyntaxError): string {
  const head = `'${text ?? ''}' is not a port type name: ${FAULT_TEXT[error.fault]} at ${String(error.position)}.`
  return error.hint === undefined ? head : `${head} ${error.hint}.`
}
