import { isPortCapabilityName } from './capabilities.js'
import { formatPortTerm, parsePortTerm } from './grammar.js'
import type { PortTerm } from './grammar.js'
import type { PortTypeRegistry } from './types.js'

function holdsJson(term: PortTerm): boolean {
  for (let node: PortTerm | undefined = term; node !== undefined; node = node.kind === 'constructor' ? node.argument : undefined) {
    if (node.kind === 'atom' && node.name.toLowerCase() === 'json') return true
  }
  return false
}

/** Whether `text` is a closed term of the registry's types with no `Json` that implements every constraint; a constraint
 * that is no capability of this vocabulary cannot be decided, so nothing fits it. */
export function isTypeArgumentFor(registry: PortTypeRegistry, text: string, constraints: readonly string[] = []): boolean {
  const parsed = parsePortTerm(text, 'document')
  if (!parsed.ok || holdsJson(parsed.term) || registry.refusalOf(parsed.term) !== undefined) return false
  return constraints.every((constraint) => isPortCapabilityName(constraint) && registry.implementsCapability(parsed.term, constraint))
}

/** The listed types, then each host constructor over each argument it admits, that `isTypeArgumentFor` accepts for
 * `constraints`, in the registry's order. */
export function typeArgumentBases(registry: PortTypeRegistry, constraints: readonly string[] = []): string[] {
  const names = registry.list().map((definition) => definition.name)
  for (const constructor of registry.constructors()) {
    for (const argument of constructor.allowedArguments) names.push(`${constructor.name}<${argument}>`)
  }
  return [...new Set(names)].filter((name) => isTypeArgumentFor(registry, name, constraints))
}

function spelledAsRegistered(registry: PortTypeRegistry, term: PortTerm): boolean {
  const atoms = new Set(registry.list().map((definition) => definition.name))
  const heads = new Set(registry.constructors().map((constructor) => constructor.name))
  for (let node: PortTerm | undefined = term; node !== undefined; node = node.kind === 'constructor' ? node.argument : undefined) {
    if (node.kind === 'atom' && !atoms.has(node.name)) return false
    if (node.kind === 'constructor' && node.name !== 'Collection' && node.name !== 'Map' && !heads.has(node.name)) return false
    if (node.kind === 'constructor' && node.key !== undefined && node.key.name !== 'String') return false
  }
  return true
}

/** `text` in a `Collection` or with its outer level nullable; undefined when `text` is no term, is already nullable there,
 * or the result fails `isTypeArgumentFor`. */
export function wrapTypeArgument(
  registry: PortTypeRegistry, text: string, level: 'collection' | 'nullable', constraints: readonly string[] = [],
): string | undefined {
  const parsed = parsePortTerm(text, 'document')
  if (!parsed.ok || parsed.term.kind === 'null' || (level === 'nullable' && parsed.term.nullable) || !spelledAsRegistered(registry, parsed.term)) return undefined
  const wrapped: PortTerm = level === 'nullable'
    ? { ...parsed.term, nullable: true }
    : { kind: 'constructor', name: 'Collection', argument: parsed.term, nullable: false }
  const written = formatPortTerm(wrapped)
  return isTypeArgumentFor(registry, written, constraints) ? written : undefined
}
