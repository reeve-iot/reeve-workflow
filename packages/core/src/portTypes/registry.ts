import { foldAsciiCase } from './ascii.js'
import type { PortCapabilityName, PortTermClaims } from './capabilities.js'
import { LIBRARY_CLAIMS, termImplements } from './capabilities.js'
import type { PortTerm, PortTypeNameTerm } from './grammar.js'
import { describePortTypeSyntaxError, formatPortTerm, formatPortTypeName, parsePortTerm, parsePortTypeName, portTermsEqual } from './grammar.js'
import type { PortLiteralForm } from './literalForm.js'
import { LIBRARY_LITERAL_FORMS } from './literalForm.js'
import { isLiteralOfTerm } from './literalCoercion.js'
import type { PortTermRefusal, PortTypeConstructorDefinition, PortTypeDefinition, PortTypeRegistry, PortTypeRegistryOptions } from './types.js'

const PORT_TYPE_REGISTRATION_INVALID = 'port-type-registration-invalid'
const PORT_TYPE_NAME_CLASH = 'port-type-name-clash'

const isBuiltInMap = (name: string): boolean => foldAsciiCase(name) === 'map'
const BUILT_IN_MAP = "Map is the library's built-in constructor"

const isLibraryAtom = (name: string): boolean => Object.hasOwn(LIBRARY_CLAIMS, foldAsciiCase(name))

function chainOf(term: PortTerm): PortTerm[] {
  const chain: PortTerm[] = []
  for (let node: PortTerm | undefined = term; node !== undefined; node = node.kind === 'constructor' ? node.argument : undefined) chain.push(node)
  return chain
}

const frozen = (definition: PortTypeConstructorDefinition): PortTypeConstructorDefinition =>
  Object.freeze({
    name: definition.name,
    allowedArguments: Object.freeze([...definition.allowedArguments]),
    ...(definition.implements === undefined ? {} : { implements: Object.freeze([...definition.implements]) }),
    ...(definition.literal == null ? {} : { literal: Object.freeze({ ...definition.literal }) }),
  })

interface Term {
  depth: number
  element: string
}

/** Reads the server's port-type vocabulary: call once at initialisation. The result is immutable. The server is the
 * registrar, so the registry grades no record; it throws only for a name that does not parse, a key two entries claim
 * or a constructor argument that is not a term. */
export function createPortTypeRegistry(
  definitions: readonly PortTypeDefinition[],
  hostConstructors: readonly PortTypeConstructorDefinition[] = [],
  options: PortTypeRegistryOptions = {},
): PortTypeRegistry {
  const byCanonical = new Map<string, PortTypeDefinition>()
  const byAlias = new Map<string, string>()
  const companions = new Map<string, PortTypeDefinition>()
  const collectionElements = new Map<string, { name: string; depth: number }>()
  const ordered: PortTypeDefinition[] = []
  const atomClaims = new Map<string, ReadonlySet<PortCapabilityName>>()
  const constructorClaims = new Map<string, ReadonlySet<PortCapabilityName>>()
  const atomForms = new Map<string, PortLiteralForm>()
  const constructorForms = new Map<string, PortLiteralForm>()

  const clash = (message: string): Error => new Error(`${PORT_TYPE_NAME_CLASH}: ${message}`)

  const register = (def: PortTypeDefinition): void => {
    if (isBuiltInMap(def.name)) throw new Error(`${PORT_TYPE_REGISTRATION_INVALID}: Port type '${def.name}' cannot be registered: ${BUILT_IN_MAP}.`)
    const mapAlias = (def.aliases ?? []).find(isBuiltInMap)
    if (mapAlias !== undefined) {
      throw new Error(`${PORT_TYPE_REGISTRATION_INVALID}: Port type '${def.name}' cannot be registered: alias '${mapAlias}': ${BUILT_IN_MAP}.`)
    }
    const parsed = parsePortTypeName(def.name)
    if (!parsed.ok) {
      throw new Error(`${PORT_TYPE_REGISTRATION_INVALID}: Port type '${def.name}' cannot be registered: ${describePortTypeSyntaxError(def.name, parsed)}`)
    }
    if (byCanonical.has(def.name)) throw clash(`Duplicate port type '${def.name}'.`)
    const taken = (key: string): boolean => byAlias.has(key) || collectionElements.has(key)
    if (parsed.depth >= 1) {
      const key = foldAsciiCase(parsed.element)
      if (taken(key)) throw clash(`Port type '${def.name}' cannot be registered: '${parsed.element}' is already declared.`)
      collectionElements.set(key, { name: def.name, depth: parsed.depth })
    }
    for (const alias of parsed.depth === 0 ? [def.name, ...(def.aliases ?? [])] : (def.aliases ?? [])) {
      const key = foldAsciiCase(alias)
      const owner = byAlias.get(key)
      if (owner !== undefined && owner !== def.name) throw clash(`Port type alias '${alias}' is claimed by both '${owner}' and '${def.name}'.`)
      if (owner === undefined && collectionElements.has(key)) {
        throw clash(`Port type alias '${alias}' of '${def.name}' cannot be registered: '${alias}' is already declared.`)
      }
      byAlias.set(key, def.name)
    }
    if (parsed.depth === 0 && !isLibraryAtom(def.name)) {
      atomClaims.set(foldAsciiCase(def.name), new Set(def.implements))
      if (def.literal != null) atomForms.set(foldAsciiCase(def.name), Object.freeze({ ...def.literal }))
    }
    byCanonical.set(def.name, def)
    ordered.push(def)
  }

  for (const def of definitions) register(def)
  for (const def of ordered) {
    if (def.collection === false) continue
    const parsed = parsePortTypeName(def.name)
    if (!parsed.ok || parsed.depth !== 0) continue
    companions.set(foldAsciiCase(def.name), {
      name: formatPortTypeName(1, def.name),
      displayName: def.displayName === undefined ? undefined : `Collection of ${def.displayName}`,
    })
  }
  const listed = [...ordered, ...companions.values()]

  const resolve = (term: PortTypeNameTerm): Term => {
    const canonical = byAlias.get(foldAsciiCase(term.element))
    if (canonical === undefined) {
      const owner = collectionElements.get(foldAsciiCase(term.element))
      const declared = owner === undefined ? undefined : parsePortTypeName(owner.name)
      return { depth: term.depth, element: declared?.ok === true ? declared.element : term.element }
    }
    const target = parsePortTypeName(canonical)
    return target.ok ? { depth: term.depth + target.depth, element: target.element } : { depth: term.depth, element: term.element }
  }

  const atomKey = (element: string): string | undefined => byAlias.get(foldAsciiCase(element))

  const constructorsByName = new Map<string, { definition: PortTypeConstructorDefinition; allowed: PortTerm[] }>()
  const atomIsKnown = (name: string): boolean => {
    const key = foldAsciiCase(name)
    return byAlias.has(key) || collectionElements.has(key)
  }
  for (const definition of hostConstructors) {
    const refuse = (why: string): Error => clash(`Port type constructor '${definition.name}' cannot be registered: ${why}.`)
    const key = foldAsciiCase(definition.name)
    if (isBuiltInMap(definition.name)) throw new Error(`${PORT_TYPE_REGISTRATION_INVALID}: Port type constructor '${definition.name}' cannot be registered: ${BUILT_IN_MAP}.`)
    if (constructorsByName.has(key)) throw refuse('a constructor of that name is already registered')
    if (atomIsKnown(definition.name)) throw refuse('a port type of that name is already registered')
    const allowed = definition.allowedArguments.map((argument) => {
      const parsed = parsePortTerm(argument, 'document')
      if (!parsed.ok) throw new Error(`${PORT_TYPE_REGISTRATION_INVALID}: Port type constructor '${definition.name}' cannot be registered: allowed argument '${argument}' is not a closed term.`)
      return parsed.term
    })
    constructorClaims.set(key, new Set(definition.implements))
    if (definition.literal != null) constructorForms.set(key, Object.freeze({ ...definition.literal }))
    constructorsByName.set(key, { definition: frozen(definition), allowed })
  }

  const upper = (text: string): string => text.replace(/[a-z]/g, (c) => c.toUpperCase())
  const sortedConstructors = Object.freeze(
    [...constructorsByName.values()]
      .map((entry) => entry.definition)
      .sort((a, b) => (upper(a.name) < upper(b.name) ? -1 : upper(a.name) > upper(b.name) ? 1 : 0)),
  )

  const refusalOf = (term: PortTerm): PortTermRefusal | undefined => {
    const unregistered = (message: string): PortTermRefusal => ({ code: 'port-type-unregistered', message })
    for (let node: PortTerm | undefined = term; node !== undefined; node = node.kind === 'constructor' ? node.argument : undefined) {
      if (node.kind === 'atom' && !atomIsKnown(node.name)) return unregistered(`'${node.name}' is not a registered type`)
      if (node.kind !== 'constructor' || node.name === 'Collection' || node.name === 'Map') continue
      const head = constructorsByName.get(foldAsciiCase(node.name))
      if (head === undefined) return unregistered(`'${node.name}' is not a registered type constructor`)
      const argument = node.argument
      if (argument.kind !== 'parameter' && !head.allowed.some((allowed) => portTermsEqual(allowed, argument))) {
        return unregistered(`'${node.name}' does not admit the argument '${formatPortTerm(argument)}'`)
      }
    }
    return undefined
  }

  const canonicalTerm = (term: PortTerm): PortTerm => {
    const chain = chainOf(term)
    let inner: PortTerm | undefined
    for (let i = chain.length - 1; i >= 0; i--) {
      const node = chain[i] as PortTerm
      let rebuilt: PortTerm
      if (node.kind === 'atom') {
        const resolved = resolve({ ok: true, depth: 0, element: node.name })
        rebuilt = { kind: 'atom', name: resolved.element, nullable: false }
        for (let level = 0; level < resolved.depth; level++) rebuilt = { kind: 'constructor', name: 'Collection', argument: rebuilt, nullable: false }
      } else if (node.kind === 'constructor') {
        const head = node.name === 'Collection' || node.name === 'Map' ? undefined : constructorsByName.get(foldAsciiCase(node.name))?.definition.name
        rebuilt = {
          kind: 'constructor', name: head ?? node.name, ...(node.key === undefined ? {} : { key: { kind: 'atom', name: 'String', nullable: false } }),
          argument: inner as PortTerm, nullable: false,
        }
      } else {
        rebuilt = node.kind === 'null' ? node : { ...node, nullable: false }
      }
      inner = node.kind !== 'null' && node.nullable ? { ...rebuilt, nullable: true } : rebuilt
    }
    return inner as PortTerm
  }

  const registeredTerm = (term: PortTerm): PortTerm => {
    const canonical = canonicalTerm(term)
    const refusal = refusalOf(canonical)
    if (refusal !== undefined) throw new Error(`${refusal.code}: ${refusal.message}; capability membership is answered only for registered terms.`)
    return canonical
  }

  const claimVocabulary: PortTermClaims = {
    claims: (head, capability) => {
      if (head.kind === 'atom') {
        const key = foldAsciiCase(head.name)
        if (isLibraryAtom(head.name)) return (LIBRARY_CLAIMS[key] ?? []).includes(capability)
        return atomClaims.get(key)?.has(capability) ?? false
      }
      return head.kind === 'constructor' && (constructorClaims.get(foldAsciiCase(head.name))?.has(capability) ?? false)
    },
  }

  const literalFormOf = (head: string): PortLiteralForm | undefined => {
    const key = foldAsciiCase(head)
    return Object.hasOwn(LIBRARY_LITERAL_FORMS, key) ? LIBRARY_LITERAL_FORMS[key] : (atomForms.get(key) ?? constructorForms.get(key))
  }

  return {
    secretCodec: options.secretCodec === true,
    has: (type) => {
      const parsed = parsePortTypeName(type)
      if (!parsed.ok) return false
      const key = foldAsciiCase(parsed.element)
      const declared = collectionElements.get(key)
      return byAlias.has(key) || (declared !== undefined && parsed.depth >= declared.depth)
    },
    get: (type) => {
      const parsed = parsePortTypeName(type)
      if (!parsed.ok) return undefined
      const declared = collectionElements.get(foldAsciiCase(parsed.element))
      if (declared !== undefined && declared.depth === parsed.depth) return byCanonical.get(declared.name)
      const canonical = atomKey(parsed.element)
      if (canonical === undefined) return undefined
      if (parsed.depth === 0) return byCanonical.get(canonical)
      return parsed.depth === 1 ? companions.get(foldAsciiCase(canonical)) : undefined
    },
    list: () => listed,
    constructors: () => sortedConstructors,
    literalFormOf,
    isLiteralOf: (value, type) => {
      const parsed = parsePortTerm(String(type), 'declaration')
      if (!parsed.ok) return false
      if (chainOf(parsed.term).some((node) => node.kind === 'parameter')) return true
      return isLiteralOfTerm(value, canonicalTerm(parsed.term), { literalFormOf })
    },
    refusalOf,
    normalize: (type) => {
      const parsed = parsePortTypeName(type)
      if (!parsed.ok) return type
      const resolved = resolve(parsed)
      return formatPortTypeName(resolved.depth, resolved.element)
    },
    implementsCapability: (term, capability) => termImplements(registeredTerm(term), capability, claimVocabulary),
  }
}
