import { useState } from 'react'
import { Box, TextField, Typography } from '@mui/material'

export const DEFAULT_TIMEOUT_SECONDS = 30
export const DEFAULT_RETRY_COUNT = 3
export const DEFAULT_RETRY_BACKOFF_SECONDS = 1.0

const WIDEST_ENGINE_INTEGER = 2147483647

const LONGEST_ENGINE_WAIT_SECONDS = 86400

const NUMBER_INPUT_TEXT = /^-?(\d+(\.\d+)?|\.\d+)(e[-+]?\d+)?$/i

export interface ResiliencySectionProps {
  configuration: Record<string, unknown>
  onChange: (fieldName: string, value: number | boolean) => void
  /** Whether this node holds a sub-canvas. The engine applies its block default to a leaf and nothing at all
   * to a container, so the timeout box must not offer a container a default it will never get. */
  isContainer?: boolean
  /** Removes fields rather than giving them values; a container with no timeout has no key, not a number.
   * A set rather than one name: going back to the retry default takes three keys off in one write, and a
   * host removing them one at a time would write each from the configuration it had before the first. */
  onClear?: (fieldNames: readonly string[]) => void
}

interface EngineBounds {
  min: number
  whole: boolean
  wait?: string
}

interface EngineVerdict {
  kind: 'clamped' | 'refused'
  reason: string
  typed?: string
}

function refuse(value: number, bounds: EngineBounds): EngineVerdict | null {
  if (Number.isNaN(value)) return { kind: 'refused', reason: 'Enter a number' }
  if (!Number.isFinite(value) || (bounds.whole && value > WIDEST_ENGINE_INTEGER)) {
    return { kind: 'refused', reason: 'Too large for the engine to read — the run fails at this block' }
  }
  if (bounds.whole && value < -WIDEST_ENGINE_INTEGER - 1) {
    return { kind: 'refused', reason: 'Further below zero than the engine reads — the run fails at this block' }
  }
  if (bounds.whole && !Number.isInteger(value)) {
    return { kind: 'refused', reason: 'Whole numbers only — with a fraction here the run fails at this block' }
  }
  if (bounds.wait !== undefined && value > LONGEST_ENGINE_WAIT_SECONDS) {
    return {
      kind: 'refused',
      reason: `At most ${LONGEST_ENGINE_WAIT_SECONDS} seconds — one day, the longest single wait the engine `
        + `can apply. ${bounds.wait}`,
    }
  }
  if (value < bounds.min) {
    return {
      kind: 'clamped',
      reason: `Must be at least ${bounds.min} — the engine runs anything lower as ${bounds.min}`,
      typed: `Must be at least ${bounds.min} — the engine would run anything lower as ${bounds.min}`,
    }
  }
  return null
}

function typeName(value: unknown): string {
  if (typeof value === 'string') return 'text'
  if (typeof value === 'boolean') return 'true/false'
  if (Array.isArray(value)) return 'a list'
  if (typeof value === 'object') return 'an object'
  return `a ${typeof value}`
}

function storedVerdict(value: unknown, bounds: EngineBounds): EngineVerdict | null {
  if (value === undefined || value === null) return null
  if (typeof value !== 'number') {
    return {
      kind: 'refused',
      reason: `Stored as ${typeName(value)}, not a number — the engine refuses the definition and the run `
        + 'fails at this block',
    }
  }
  return refuse(value, bounds)
}

function displayStored(value: unknown, fallback: string): string {
  if (value === undefined || value === null) return fallback
  return typeof value === 'object' ? JSON.stringify(value) : String(value)
}

interface CommittedNumberFieldProps {
  label: string
  display: string
  stored?: EngineVerdict | null
  bounds: EngineBounds
  helperText: string
  placeholder?: string
  onCommit: (value: number) => void
  onCommitEmpty: () => void
}

function CommittedNumberField(
  { label, display, stored = null, bounds, helperText, placeholder, onCommit, onCommitEmpty }: CommittedNumberFieldProps,
) {
  const [draft, setDraft] = useState<string | null>(null)
  const [refusal, setRefusal] = useState<string | null>(null)

  const commit = () => {
    if (draft === null) return
    const text = draft.trim()
    if (text === '') {
      setDraft(null)
      setRefusal(null)
      onCommitEmpty()
      return
    }
    const value = Number(text)
    const verdict = refuse(value, bounds)
    if (verdict !== null) {
      setRefusal(verdict.typed ?? verdict.reason)
      return
    }
    setDraft(null)
    setRefusal(null)
    onCommit(value)
  }

  const showing = draft ?? display
  const parsable = display === '' || NUMBER_INPUT_TEXT.test(display)

  return (
    <TextField
      fullWidth size="small" label={label} type={parsable ? 'number' : 'text'}
      value={showing}
      placeholder={placeholder}
      error={refusal !== null || (draft === null && stored?.kind === 'refused')}
      slotProps={{ htmlInput: { 'aria-label': label } }}
      onChange={(e) => { setDraft(e.target.value); setRefusal(null) }}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') { e.preventDefault(); commit() }
        else if (e.key === 'Escape') { setDraft(null); setRefusal(null) }
      }}
      helperText={refusal ?? (draft === null ? stored?.reason : null) ?? helperText}
    />
  )
}

const RETRY_KEYS = ['retryEnabled', 'retryCount', 'retryBackoffSeconds'] as const

type RetryChoice = 'default' | 'on' | 'off' | 'unreadable'

function readRetryChoice(configuration: Record<string, unknown>): RetryChoice {
  const flag = configuration['retryEnabled']
  if (flag === true) return 'on'
  if (flag === false) return 'off'
  return flag === undefined || flag === null ? 'default' : 'unreadable'
}

const RETRY_HELP: Record<RetryChoice, string> = {
  default: 'The block’s own declaration decides, together with the host’s retry setting',
  on: 'Retried on failure, a timeout included, as many times as the count below',
  off: 'Never retried',
  unreadable: 'This node stores a retry flag the engine cannot read; the run fails at this block',
}

const isSet = (value: unknown) => value !== undefined && value !== null

const TIMEOUT_BOUNDS: EngineBounds = {
  min: 1,
  whole: true,
  wait: 'A longer one fails the run at this block, and a block that must run longer than a day carries no '
    + 'timeout of its own.',
}
const COUNT_BOUNDS: EngineBounds = { min: 0, whole: true }

function backoffBounds(retryChoice: RetryChoice, configuration: Record<string, unknown>): EngineBounds {
  const count = configuration['retryCount']
  const attempts = typeof count === 'number' ? count : isSet(count) ? 0 : DEFAULT_RETRY_COUNT
  return retryChoice === 'on' && attempts > 0
    ? { min: 0, whole: false, wait: 'A longer delay fails the run at this block.' }
    : { min: 0, whole: false }
}

/** Per-node timeout and retry. What the engine does with an unset retry is not a fixed answer — it is the
 * block's own IsIdempotent — so the box offers that as a state rather than guessing one of the two. */
export function ResiliencySection({ configuration, onChange, isContainer = false, onClear }: ResiliencySectionProps) {
  const retryChoice = readRetryChoice(configuration)
  const stored = configuration['timeoutSeconds']
  const backoff = backoffBounds(retryChoice, configuration)

  return (
    <Box>
      <Typography variant="subtitle2" sx={{ mb: 1 }}>Resiliency</Typography>
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <CommittedNumberField
          label="Timeout (seconds)"
          display={displayStored(stored, '')}
          stored={storedVerdict(stored, TIMEOUT_BOUNDS)}
          bounds={TIMEOUT_BOUNDS}
          onCommit={(value) => { onChange('timeoutSeconds', value) }}
          onCommitEmpty={() => {
            if (onClear) onClear(['timeoutSeconds'])
            else onChange('timeoutSeconds', DEFAULT_TIMEOUT_SECONDS)
          }}
          placeholder={isContainer ? undefined : String(DEFAULT_TIMEOUT_SECONDS)}
          helperText={isContainer
            ? 'Empty means this block sets no deadline of its own'
            : 'Empty leaves the engine’s own 30-second default in place'}
        />
        <TextField
          select fullWidth size="small" label="Retry"
          value={retryChoice}
          slotProps={{
            select: { native: true }, inputLabel: { shrink: true }, htmlInput: { 'aria-label': 'Retry' },
          }}
          onChange={(e) => {
            const next = e.target.value as RetryChoice
            if (next === 'on') onChange('retryEnabled', true)
            else if (next === 'off') onChange('retryEnabled', false)
            else onClear?.(RETRY_KEYS)
          }}
          helperText={RETRY_HELP[retryChoice]}
        >
          <option value="default" disabled={onClear === undefined}>Default — the block decides</option>
          <option value="on">On</option>
          <option value="off">Off</option>
          {retryChoice === 'unreadable' && (
            <option value="unreadable" disabled>Unreadable — the engine refuses this node</option>
          )}
        </TextField>
        {(retryChoice === 'on' || isSet(configuration['retryCount'])) && (
          <CommittedNumberField
            label="Retry Count"
            display={displayStored(configuration['retryCount'], String(DEFAULT_RETRY_COUNT))}
            stored={storedVerdict(configuration['retryCount'], COUNT_BOUNDS)}
            bounds={COUNT_BOUNDS}
            onCommit={(value) => { onChange('retryCount', value) }}
            onCommitEmpty={() => { onChange('retryCount', DEFAULT_RETRY_COUNT) }}
            helperText="Retry attempts after the first, where 0 means none. Max time is roughly count x timeout."
          />
        )}
        {(retryChoice === 'on' || isSet(configuration['retryBackoffSeconds'])) && (
          <CommittedNumberField
            label="Backoff (seconds)"
            display={displayStored(configuration['retryBackoffSeconds'], String(DEFAULT_RETRY_BACKOFF_SECONDS))}
            stored={storedVerdict(configuration['retryBackoffSeconds'], backoff)}
            bounds={backoff}
            onCommit={(value) => { onChange('retryBackoffSeconds', value) }}
            onCommitEmpty={() => { onChange('retryBackoffSeconds', DEFAULT_RETRY_BACKOFF_SECONDS) }}
            helperText="Initial delay for exponential backoff"
          />
        )}
      </Box>
    </Box>
  )
}
