import { Button } from '@mui/material'
import type { PortTypeRegistry } from '@reeve-iot/workflow-core'
import { parsePortTypeName } from '@reeve-iot/workflow-core'

/** One more `Collection<…>` level around the picker's type, in the registry's canonical spelling, so the
 * value a write stores is one the compiler reads and never a spelling only this control produced. */
export function wrapInCollection(type: string, portTypes: PortTypeRegistry): string {
  return `Collection<${portTypes.normalize(type)}>`
}

/** Whether the type names something a Select can show, whether or not the grammar parses it: a name it
 * refuses is still text that was written, while nothing at all is nothing to show. */
export function namesAnElement(type: string | null | undefined): boolean {
  return type !== null && type !== undefined && type !== ''
}

/** Whether one more collection level may be built on the picker's type: a name the grammar parses that the registry
 * holds under its canonical spelling. */
export function canWrapInCollection(type: string | null | undefined, portTypes: PortTypeRegistry): boolean {
  if (type === null || type === undefined || type === '') return false
  const parsed = parsePortTypeName(type)
  if (!parsed.ok) return false
  return portTypes.normalize(type) === type && portTypes.has(type)
}

/** The registry's names plus the current type where the registry does not list it, so a type composed by the
 * button or read from a document shows in the Select instead of blank. Nothing to show is never appended. */
export function typesWithCurrent(names: readonly string[], current: string | null | undefined): string[] {
  if (current === null || current === undefined || names.includes(current)) return [...names]
  return namesAnElement(current) ? [...names, current] : [...names]
}

export interface CollectionComposerProps {
  /** The picker's current type, exactly as the picker holds it. */
  type: string | null | undefined
  /** The vocabulary the type must be a known, canonically spelled name of. */
  portTypes: PortTypeRegistry
  /** The control's accessible name, which names the row it belongs to. */
  ariaLabel: string
  /** Receives the type as the next write should store it: this type in one more collection. */
  onWrap: (wrapped: string) => void
}

/** One more collection level around a picker's current type, beside the type Select. Rendered even when it
 * refuses, so the control is discoverable; a disabled control offers nothing. */
export function CollectionComposer({ type, portTypes, ariaLabel, onWrap }: CollectionComposerProps) {
  const wrapped = canWrapInCollection(type, portTypes) ? wrapInCollection(type ?? '', portTypes) : null
  return (
    <Button
      size="small" variant="outlined" disabled={wrapped === null} aria-label={ariaLabel}
      onClick={() => { if (wrapped !== null) onWrap(wrapped) }}
    >
      Collection
    </Button>
  )
}
