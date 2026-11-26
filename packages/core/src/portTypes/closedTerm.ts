import { foldAsciiCase } from './ascii.js'
import type { PortTerm } from './grammar.js'
import { parsePortTerm } from './grammar.js'
import type { PortTypeName } from './name.js'
import type { PortTypeRegistry } from './types.js'

const namesParameter = (term: PortTerm): boolean => {
  for (let node: PortTerm | undefined = term; node !== undefined; node = node.kind === 'constructor' ? node.argument : undefined) {
    if (node.kind === 'parameter') return true
  }
  return false
}

/** Whether `type` reads under the declaration grammar and names no type parameter anywhere in it. Only a closed term is a
 * type the editor can grade a literal or a wire against; `$T` and `Collection<$T?>` are the compiler's to bind. */
export function isClosedPortTerm(type: PortTypeName): boolean {
  const parsed = parsePortTerm(String(type), 'declaration')
  return parsed.ok && !namesParameter(parsed.term)
}

const registrable = (term: PortTerm): PortTerm =>
  term.kind === 'constructor' && foldAsciiCase(term.name) === 'expression' ? term.argument : term

/** Whether the editor grades against `type`: the registry knows every name in it and it is closed. A term with a name
 * outside the vocabulary read at mount, or an open one, is never refused here; the server's analysis answers for it. */
export function isGradableTerm(type: PortTypeName | undefined, registry: PortTypeRegistry): type is PortTypeName {
  if (type === undefined) return false
  const parsed = parsePortTerm(String(type), 'declaration')
  return parsed.ok && !namesParameter(parsed.term) && registry.refusalOf(registrable(parsed.term)) === undefined
}
