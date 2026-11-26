/** The JSON shape a literal of a registered type takes: the library's grading, applied before a host's own check. */
export type PortLiteralKind = 'String' | 'Boolean' | 'Int' | 'Number' | 'Json'

/** A further reading of a string literal, applied by the compiler alone: the editor checks only that it is a JSON string. */
export type PortLiteralFormat = 'Uuid' | 'DateTime'

/** What a type declares about its literals. A type with no form has no literal: every one written for it is refused. */
export interface PortLiteralForm {
  /** The JSON shape a literal must have. */
  readonly gradedAs: PortLiteralKind
  /** The reading of a string literal; only with `String`. Null and absent are the same. */
  readonly format?: PortLiteralFormat | null
}

export const PORT_LITERAL_KINDS: readonly PortLiteralKind[] = Object.freeze(['String', 'Boolean', 'Int', 'Number', 'Json'])
export const PORT_LITERAL_FORMATS: readonly PortLiteralFormat[] = Object.freeze(['Uuid', 'DateTime'])

/** Why `form` cannot be read, or undefined when it can: a member outside the closed set. */
export function literalFormRefusal(form: PortLiteralForm): string | undefined {
  if (!PORT_LITERAL_KINDS.includes(form.gradedAs)) {
    return `its literal form is graded as '${String(form.gradedAs)}', which is outside String, Boolean, Int, Number and Json`
  }
  const format = form.format ?? undefined
  if (format !== undefined && !PORT_LITERAL_FORMATS.includes(format)) {
    return `its literal format '${String(format)}' is outside Uuid and DateTime`
  }
  return undefined
}

/** The library's own atoms and the form each declares, keyed by the ASCII-folded name: fixed, not registered. */
export const LIBRARY_LITERAL_FORMS: Readonly<Record<string, PortLiteralForm>> = Object.freeze({
  string: Object.freeze({ gradedAs: 'String' }),
  boolean: Object.freeze({ gradedAs: 'Boolean' }),
  int: Object.freeze({ gradedAs: 'Int' }),
  number: Object.freeze({ gradedAs: 'Number' }),
  datetime: Object.freeze({ gradedAs: 'String', format: 'DateTime' }),
  json: Object.freeze({ gradedAs: 'Json' }),
})
