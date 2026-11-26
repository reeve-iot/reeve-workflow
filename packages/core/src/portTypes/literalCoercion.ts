import type { PortTerm } from './grammar.js'
import { parsePortTerm } from './grammar.js'
import type { PortLiteralForm } from './literalForm.js'
import type { PortTypeName } from './name.js'
import type { PortTypeRegistry } from './types.js'

/** What the shared decision asks of a vocabulary: the form a registered atom or constructor head declares. */
export interface PortLiteralForms {
  /** The form that grades a literal of the atom or constructor named `head`, or undefined when it declares none or is
   * not registered. Aliases are not resolved: ask with the canonical name. */
  literalFormOf(head: string): PortLiteralForm | undefined
}

const TWO_TO_THE_63 = 9223372036854775808

const isIntegral = (value: number): boolean => Number.isInteger(value) && value >= -TWO_TO_THE_63 && value < TWO_TO_THE_63

function fits(value: unknown, form: PortLiteralForm): boolean {
  switch (form.gradedAs) {
    case 'String':
      return typeof value === 'string'
    case 'Boolean':
      return typeof value === 'boolean'
    case 'Int':
      return typeof value === 'number' && isIntegral(value)
    case 'Number':
      return typeof value === 'number' && Number.isFinite(value)
    case 'Json':
      return typeof value === 'string' || typeof value === 'boolean' || typeof value === 'number' || typeof value === 'object'
    default:
      return false
  }
}

const isNull = (value: unknown): boolean => value === null || value === undefined

/** Whether `value`, a JSON value as the stored-document reader makes it, is a literal of the closed `term`: `null` of a
 * nullable term and of no other, a `Collection` an array and a `Map` an object of literals of its argument, any other head by
 * its form. A repeated member name never reaches here: the reader refuses it. The backend's twin is `PortLiteralCoercion.IsLiteralOf`. */
export function isLiteralOfTerm(value: unknown, term: PortTerm, forms: PortLiteralForms): boolean {
  const work: { value: unknown; term: PortTerm }[] = [{ value, term }]
  for (let item = work.pop(); item !== undefined; item = work.pop()) {
    const node = item.term
    if (node.kind === 'parameter') return false
    if (isNull(item.value)) {
      if (!node.nullable) return false
      continue
    }
    if (node.kind === 'null') return false
    if (node.kind === 'constructor' && node.name === 'Collection') {
      if (!Array.isArray(item.value)) return false
      for (const element of item.value as unknown[]) work.push({ value: element, term: node.argument })
      continue
    }
    if (node.kind === 'constructor' && node.name === 'Map') {
      if (typeof item.value !== 'object' || Array.isArray(item.value)) return false
      for (const member of Object.values(item.value as Record<string, unknown>)) work.push({ value: member, term: node.argument })
      continue
    }
    const form = forms.literalFormOf(node.name)
    if (form === undefined || !fits(item.value, form)) return false
  }
  return true
}

/** Whether `value` is a literal of the declared port type `type`, in the vocabulary `forms` answers for: the one decision
 * the editor and the compiler share, graded by `contracts/literal-coercion.json`, a format row as a JSON string only. An
 * absent literal is a null one. A type that is not a well-formed closed name has no literal. */
export function isLiteralCoercible(value: unknown, type: PortTypeName, forms: PortLiteralForms): boolean {
  const parsed = parsePortTerm(String(type), 'document')
  return parsed.ok && isLiteralOfTerm(value, parsed.term, forms)
}

/** The form an editor writes a literal of the declared `type` in: that of its atom, or of its constructor head, with `?`
 * set aside. Null when the type is registered and declares no form, so no literal can be written for it; undefined when
 * the form is not the type's to give: a collection (a list of literals), a map (an object of them), a term naming a type
 * parameter, a name nobody registered, or a name that is not well formed. */
export function literalFormOfType(type: PortTypeName, registry: PortTypeRegistry): PortLiteralForm | null | undefined {
  const parsed = parsePortTerm(String(type), 'declaration')
  if (!parsed.ok) return undefined
  const term = parsed.term
  if (term.kind === 'null' || term.kind === 'parameter') return undefined
  if (term.kind === 'constructor' && (term.name === 'Collection' || term.name === 'Map')) return undefined
  for (let node: PortTerm | undefined = term; node !== undefined; node = node.kind === 'constructor' ? node.argument : undefined) {
    if (node.kind === 'parameter') return undefined
  }
  if (registry.refusalOf(term) !== undefined) return undefined
  const head = term.kind === 'atom' ? registry.normalize(term.name) : term.name
  return registry.literalFormOf(head) ?? null
}
