import type { PortTerm } from './grammar.js'

/** A capability a port term can implement. The vocabulary is the library's and is closed. */
export type PortCapabilityName = 'Iterable' | 'Numeric' | 'Comparable' | 'Equatable' | 'JsonSerializable'

/** The capability vocabulary in the order the server publishes it, with the associated types a capability names
 * (`Iterable<Item>`). */
export const PORT_CAPABILITIES: readonly { readonly name: PortCapabilityName; readonly associated: readonly string[] }[] = Object.freeze([
  Object.freeze({ name: 'Iterable', associated: Object.freeze(['Item']) }),
  Object.freeze({ name: 'Numeric', associated: Object.freeze([]) }),
  Object.freeze({ name: 'Comparable', associated: Object.freeze([]) }),
  Object.freeze({ name: 'Equatable', associated: Object.freeze([]) }),
  Object.freeze({ name: 'JsonSerializable', associated: Object.freeze([]) }),
])

/** The capabilities, compared exactly: `numeric` is not one. */
export function isPortCapabilityName(text: string): text is PortCapabilityName {
  return PORT_CAPABILITIES.some((capability) => capability.name === text)
}

/** The library's atoms and what each implements (the term table): not verified, fixed. Keyed by the ASCII-folded name. */
export const LIBRARY_CLAIMS: Readonly<Record<string, readonly PortCapabilityName[]>> = Object.freeze({
  boolean: Object.freeze<PortCapabilityName[]>(['Equatable', 'JsonSerializable']),
  int: Object.freeze<PortCapabilityName[]>(['Numeric', 'Comparable', 'Equatable', 'JsonSerializable']),
  number: Object.freeze<PortCapabilityName[]>(['Numeric', 'Comparable', 'Equatable', 'JsonSerializable']),
  string: Object.freeze<PortCapabilityName[]>(['Comparable', 'Equatable', 'JsonSerializable']),
  datetime: Object.freeze<PortCapabilityName[]>(['Comparable', 'Equatable', 'JsonSerializable']),
  json: Object.freeze<PortCapabilityName[]>(['JsonSerializable']),
})

/** What membership asks a vocabulary: the claims a registered head makes, which a term's shape cannot answer. */
export interface PortTermClaims {
  /** Whether `head`, an atom or a constructor other than `Collection` and `Map` and not nullable, claims `capability`; a head
   * nobody registered claims nothing. */
  claims(head: PortTerm, capability: PortCapabilityName): boolean
}

function containsParameter(term: PortTerm): boolean {
  for (let node: PortTerm | undefined = term; node !== undefined; node = node.kind === 'constructor' ? node.argument : undefined) {
    if (node.kind === 'parameter') return true
  }
  return false
}

/** Whether the closed `term` implements `capability` (see `implementsCapability`). Iterative, so a term of any depth is
 * answered without growing the stack. Throws when the term holds a type parameter. */
export function termImplements(term: PortTerm, capability: PortCapabilityName, vocabulary: PortTermClaims): boolean {
  if (containsParameter(term)) throw new Error('Capability membership is defined on closed terms; a type parameter must be bound first.')
  let node: PortTerm = term
  for (;;) {
    if (node.kind === 'null') return false
    if (node.nullable) {
      if (capability !== 'JsonSerializable') return false
      node = { ...node, nullable: false }
    }
    const isCollection = node.kind === 'constructor' && node.name === 'Collection'
    if (node.kind !== 'constructor' || (!isCollection && node.name !== 'Map')) return vocabulary.claims(node, capability)
    if (capability === 'Iterable') return isCollection
    if (capability !== 'Equatable' && capability !== 'JsonSerializable') return false
    node = node.argument
  }
}
