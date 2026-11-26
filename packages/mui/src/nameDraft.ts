/** Why a name may not hold a dot: the expression reader reads one as the separator between the segments of a `${…}` path, so
 * a port named `a.b` becomes a key and a field. Every name field says this same thing. */
export const NAME_WITH_A_DOT = 'A name may not hold a dot: the expression reader splits ${…} paths on dots.'

/** The blank a reader already refuses: the stored-document walk holds a parameter's and a port-list entry's name non-empty. */
export const BLANK_NAME_REFUSED = 'The stored-document reader refuses a blank name: it reads an empty string where a name is required.'

/** The blank the stored-document reader accepts but a name field never writes. */
export const BLANK_NAME_ACCEPTED = 'The stored-document reader accepts a blank name, but this field writes none.'

/** Why the text may not be written as a name, or `null` when it may be written. `blank` is the field's own
 * reason: the reader holds a parameter's and a port-list entry's name non-empty, a declared output's not. */
export function nameFault(text: string | null | undefined, blank: string): string | null {
  if (text === null || text === undefined || text === '') return blank
  return text.includes('.') ? NAME_WITH_A_DOT : null
}

/** A name being typed and the value it was typed over: the draft belongs to that one value, not to the row
 * position it was typed at, so any other value under it shows its own names. */
export interface NameDraft {
  /** The whole value the field is looking at, as text: a change to any part of it drops the draft. Two values
   * with the same text are one scope, so a host swapping whole equal documents must key the component. */
  scope: string
  text: string
}

/** The draft while the field still holds the value it was typed over, and undefined once that value has
 * changed for any reason but this field's own write, so what the new value holds shows instead. */
export function draftInScope<T extends { scope: string }>(draft: T | undefined, scope: string): T | undefined {
  return draft !== undefined && draft.scope === scope ? draft : undefined
}
