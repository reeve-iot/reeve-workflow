import { useState } from 'react'
import { Box, FormControl, InputLabel, MenuItem, Select, Stack, TextField, Typography } from '@mui/material'
import type { SelectChangeEvent } from '@mui/material'
import { findRepeatedMember } from '@reeve-iot/workflow-core'

const WIDEST_INT = 2147483647

const NARROWEST_INT = -WIDEST_INT - 1

const LONGEST_TIMER_SECONDS = 4294967

const WHOLE_DIGITS = /^-?\d+$/

function readObject(text: string): Record<string, unknown> | null {
  if (text.trim() === '') return null
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return null
  }
  if (findRepeatedMember(text) !== undefined) return null
  return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)
    ? (parsed as Record<string, unknown>)
    : null
}

type Keyed = { kind: 'absent' } | { kind: 'found'; value: unknown } | { kind: 'twice' }

function readKey(object: Record<string, unknown>, name: string): Keyed {
  const keys = Object.keys(object).filter((key) => key.toLowerCase() === name.toLowerCase())
  const first = keys[0]
  if (first === undefined) return { kind: 'absent' }
  return keys.length > 1 ? { kind: 'twice' } : { kind: 'found', value: object[first] }
}

function keyedValue(key: Keyed): unknown {
  return key.kind === 'found' ? key.value : undefined
}

type Read = { kind: 'empty' } | { kind: 'ok'; value: number } | { kind: 'refused'; message: string }

function readWhole(text: string, min: number, below: string, max: number, above: string): Read {
  const trimmed = text.trim()
  if (trimmed === '') return { kind: 'empty' }
  if (!WHOLE_DIGITS.test(trimmed)) return { kind: 'refused', message: 'A whole number: digits and nothing else' }
  const value = Number(trimmed)
  if (value > max) return { kind: 'refused', message: above }
  if (value < min) return { kind: 'refused', message: below }
  return { kind: 'ok', value }
}

function storedText(value: unknown): string {
  if (value === undefined || value === null) return ''
  return typeof value === 'object' ? JSON.stringify(value) : String(value)
}

function storedNote(value: unknown, min: number, fallback: string, max = WIDEST_INT): string | null {
  if (value === undefined || value === null) return null
  if (typeof value !== 'number' || !Number.isInteger(value) || value > WIDEST_INT || value < NARROWEST_INT) {
    return 'Stored as a value the server cannot read into a whole number; this editor writes no such key'
  }
  if (value > max) return 'Stored beyond what a run can be given; this editor writes no such value'
  return value < min ? `${fallback}; this editor writes no such key` : null
}

function writableCount(value: unknown): number | null {
  return typeof value === 'number' && Number.isInteger(value) && value >= 1 && value <= WIDEST_INT ? value : null
}

interface NumberFieldProps {
  label: string
  ariaLabel: string
  testId: string
  stored: string
  note: string | null
  min: number
  below: string
  max?: number
  above?: string
  hint: string
  emptyFault?: string
  onCommit: (value: number) => void
  onCommitEmpty?: () => void
}

function NumberField(
  { label, ariaLabel, testId, stored, note, min, below, max, above, hint, emptyFault, onCommit, onCommitEmpty }: NumberFieldProps,
) {
  const [draft, setDraft] = useState<string | null>(null)
  const [seen, setSeen] = useState(stored)
  const [refusal, setRefusal] = useState<string | null>(null)
  if (seen !== stored) {
    setSeen(stored)
    setDraft(null)
    setRefusal(null)
  }

  const ceiling = max ?? WIDEST_INT
  const over = above ?? `At most ${String(WIDEST_INT)} — a bigger number fails the request`
  const read = readWhole(draft ?? stored, min, below, ceiling, over)
  const empty = read.kind === 'empty' ? emptyFault ?? null : null

  const commit = () => {
    if (draft === null) return
    const written = readWhole(draft, min, below, ceiling, over)
    if (written.kind === 'refused' || (written.kind === 'empty' && emptyFault !== undefined)) {
      setRefusal(written.kind === 'refused' ? written.message : emptyFault ?? null)
      return
    }
    setDraft(null)
    setRefusal(null)
    if (written.kind === 'ok') onCommit(written.value)
    else onCommitEmpty?.()
  }

  return (
    <TextField
      fullWidth size="small" label={label} value={draft ?? stored}
      error={refusal !== null || note !== null || empty !== null}
      slotProps={{ htmlInput: { 'aria-label': ariaLabel, 'data-testid': testId } }}
      onChange={(event) => { setDraft(event.target.value); setRefusal(null) }}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') { event.preventDefault(); commit() }
        else if (event.key === 'Escape') { setDraft(null); setRefusal(null) }
      }}
      helperText={refusal ?? (draft === null ? note : null) ?? empty ?? hint}
    />
  )
}

function ExecutionTimeoutField(
  { value, onChange }: { value: number | null | undefined; onChange: (next: number | null) => void },
) {
  return (
    <NumberField
      label="Execution timeout (seconds)"
      ariaLabel="Execution timeout seconds"
      testId="execution-timeout-seconds"
      stored={storedText(value)}
      note={storedNote(value, 1, 'The run reads it as no timeout at all', LONGEST_TIMER_SECONDS)}
      min={1}
      below="Must be 1 or more — the run reads a smaller number as no timeout at all"
      max={LONGEST_TIMER_SECONDS}
      above={`At most ${String(LONGEST_TIMER_SECONDS)} seconds — .NET refuses to start a run with a longer deadline`}
      hint="Empty means the workflow sets no deadline of its own"
      onCommit={(seconds) => { onChange(seconds) }}
      onCommitEmpty={() => { onChange(null) }}
    />
  )
}

type BackoffChoice = 'none' | 'fixed' | 'exponential'

const BACKOFF_LABELS: Record<BackoffChoice, string> = {
  none: 'None — the workflow names no retry policy',
  fixed: 'Fixed delay',
  exponential: 'Exponential backoff',
}

function isReadableInt(value: unknown): boolean {
  if (value === undefined) return true
  return typeof value === 'number' && Number.isInteger(value) && value <= WIDEST_INT && value >= NARROWEST_INT
}

interface RetryRead {
  choice: BackoffChoice | 'unreadable'
  attempts: string
  delay: string
  attemptsValue: unknown
  delayValue: unknown
}

function readRetryPolicy(text: string): RetryRead {
  const object = readObject(text)
  if (object === null) {
    return { choice: text.trim() === '' ? 'none' : 'unreadable', attempts: '', delay: '', attemptsValue: undefined, delayValue: undefined }
  }
  const strategy = readKey(object, 'BackoffStrategy')
  const attempts = readKey(object, 'MaxAttempts')
  const delay = readKey(object, 'InitialDelayMs')
  const strategyValue = keyedValue(strategy)
  const twice = strategy.kind === 'twice' || attempts.kind === 'twice' || delay.kind === 'twice'
  const readable = !twice
    && (strategyValue === undefined || strategyValue === null || typeof strategyValue === 'string')
    && isReadableInt(keyedValue(attempts)) && isReadableInt(keyedValue(delay))
  return {
    choice: readable
      ? (strategyValue === 'exponential' || strategyValue === 'Exponential' ? 'exponential' : 'fixed')
      : 'unreadable',
    attempts: storedText(keyedValue(attempts)),
    delay: storedText(keyedValue(delay)),
    attemptsValue: keyedValue(attempts),
    delayValue: keyedValue(delay),
  }
}

function retryPolicyJson(choice: BackoffChoice, attempts: number | null, delay: number | null): string | null {
  if (choice === 'none' && attempts === null && delay === null) return null
  const policy: Record<string, unknown> = { BackoffStrategy: choice === 'none' ? 'fixed' : choice }
  if (attempts !== null) policy['MaxAttempts'] = attempts
  if (delay !== null) policy['InitialDelayMs'] = delay
  return JSON.stringify(policy)
}

function RetryPolicyField({ value, onChange }: { value: string | null | undefined; onChange: (next: string | null) => void }) {
  const stored = value ?? ''
  const read = readRetryPolicy(stored)
  const choice: BackoffChoice = read.choice === 'unreadable' ? 'fixed' : read.choice
  const attempts = writableCount(read.attemptsValue)
  const delay = writableCount(read.delayValue)

  return (
    <Box>
      <Typography variant="body2" sx={{ mb: 1 }}>Retry policy</Typography>
      <Stack spacing={2}>
        <FormControl fullWidth size="small" error={read.choice === 'unreadable'} data-testid="retry-policy-strategy">
          <InputLabel>Backoff</InputLabel>
          <Select<BackoffChoice | 'unreadable'>
            label="Backoff" value={read.choice}
            slotProps={{ input: { 'aria-label': 'Retry backoff strategy' } }}
            onChange={(event) => {
              const next = event.target.value
              if (next === 'unreadable') return
              onChange(next === 'none' ? null : retryPolicyJson(next, attempts, delay))
            }}
          >
            {(Object.keys(BACKOFF_LABELS) as BackoffChoice[]).map((option) => (
              <MenuItem key={option} value={option}>{BACKOFF_LABELS[option]}</MenuItem>
            ))}
            {read.choice === 'unreadable' && (
              <MenuItem value="unreadable" disabled>Unreadable — the run reads no policy here</MenuItem>
            )}
          </Select>
        </FormControl>
        <NumberField
          label="Max attempts"
          ariaLabel="Retry max attempts"
          testId="retry-policy-max-attempts"
          stored={read.attempts}
          note={storedNote(read.attemptsValue, 1, 'The run reads it as one attempt')}
          min={1}
          below="Must be 1 or more — the run makes one attempt whatever a lower number says"
          hint="Attempts in total, the first one included. Empty names no count, and the run makes one."
          onCommit={(count) => { onChange(retryPolicyJson(choice, count, delay)) }}
          onCommitEmpty={() => { onChange(retryPolicyJson(choice, null, delay)) }}
        />
        <NumberField
          label="Initial delay (ms)"
          ariaLabel="Retry initial delay ms"
          testId="retry-policy-initial-delay-ms"
          stored={read.delay}
          note={storedNote(read.delayValue, 1, 'The run reads it as no delay at all')}
          min={1}
          below="Must be 1 or more — the run reads a delay of 0 or less as no delay at all"
          hint="Milliseconds before the second attempt. Empty names no delay, and the run waits none."
          onCommit={(ms) => { onChange(retryPolicyJson(choice, attempts, ms)) }}
          onCommitEmpty={() => { onChange(retryPolicyJson(choice, attempts, null)) }}
        />
      </Stack>
    </Box>
  )
}

type CheckpointChoice = 'none' | 'EveryBlock' | 'BeforeNonIdempotentOnly' | 'Heartbeat' | 'unreadable'

const MODE_LABELS: Record<CheckpointChoice, string> = {
  none: 'None — the engine decides',
  EveryBlock: 'Every block',
  BeforeNonIdempotentOnly: 'Before non-idempotent blocks only',
  Heartbeat: 'Heartbeat',
  unreadable: 'Unreadable — the server refuses this policy at the next save',
}

interface CheckpointRead {
  choice: CheckpointChoice
  heartbeat: string
}

function readCheckpoint(text: string): CheckpointRead {
  const object = readObject(text)
  if (object === null) return { choice: text.trim() === '' ? 'none' : 'unreadable', heartbeat: '' }
  const mode = readKey(object, 'Mode')
  const seconds = readKey(object, 'HeartbeatSeconds')
  if (mode.kind !== 'found') return { choice: 'unreadable', heartbeat: '' }
  const secondsValue = keyedValue(seconds)
  const hasSeconds = secondsValue !== undefined && secondsValue !== null
  if (mode.value === 'Heartbeat') {
    return typeof secondsValue === 'number' && Number.isInteger(secondsValue) && secondsValue > 0
      ? { choice: 'Heartbeat', heartbeat: String(secondsValue) }
      : { choice: 'unreadable', heartbeat: '' }
  }
  if (mode.value === 'EveryBlock' || mode.value === 'BeforeNonIdempotentOnly') {
    return { choice: hasSeconds ? 'unreadable' : mode.value, heartbeat: '' }
  }
  return { choice: 'unreadable', heartbeat: '' }
}

function CheckpointPolicyField({ value, onChange }: { value: string | null | undefined; onChange: (next: string | null) => void }) {
  const stored = value ?? ''
  const read = readCheckpoint(stored)
  const [chosen, setChosen] = useState<CheckpointChoice | null>(null)
  const [seen, setSeen] = useState(stored)
  if (seen !== stored) {
    setSeen(stored)
    setChosen(null)
  }
  const choice = chosen ?? read.choice

  const choose = (next: CheckpointChoice) => {
    if (next === 'Heartbeat') {
      setChosen('Heartbeat')
      return
    }
    setChosen(null)
    onChange(next === 'none' ? null : JSON.stringify({ Mode: next }))
  }

  return (
    <Box>
      <Typography variant="body2" sx={{ mb: 1 }}>Checkpoint policy</Typography>
      <Stack spacing={2}>
        <FormControl fullWidth size="small" error={choice === 'unreadable'} data-testid="checkpoint-policy-mode">
          <InputLabel>Checkpoint policy</InputLabel>
          <Select<CheckpointChoice>
            label="Checkpoint policy" value={choice}
            slotProps={{ input: { 'aria-label': 'Checkpoint policy mode' } }}
            onChange={(event: SelectChangeEvent<CheckpointChoice>) => { choose(event.target.value) }}
          >
            {(Object.keys(MODE_LABELS) as CheckpointChoice[])
              .filter((option) => option !== 'unreadable')
              .map((option) => <MenuItem key={option} value={option}>{MODE_LABELS[option]}</MenuItem>)}
            {choice === 'unreadable' && (
              <MenuItem value="unreadable" disabled>{MODE_LABELS.unreadable}</MenuItem>
            )}
          </Select>
        </FormControl>
        {choice === 'Heartbeat' && (
          <NumberField
            label="Heartbeat (seconds)"
            ariaLabel="Checkpoint heartbeat seconds"
            testId="checkpoint-policy-heartbeat-seconds"
            stored={read.heartbeat}
            note={null}
            min={1}
            below="Must be 1 or more — an interval of zero or less is not one the reader takes"
            hint="How often a run writes its row while it sleeps between blocks"
            emptyFault="A heartbeat policy needs a positive whole number of seconds"
            onCommit={(seconds) => { onChange(JSON.stringify({ Mode: 'Heartbeat', HeartbeatSeconds: seconds })) }}
          />
        )}
      </Stack>
    </Box>
  )
}

export interface WorkflowExecutionSectionProps {
  /** The workflow row's own deadline in seconds, as a create and an update take it; `null` is none. */
  executionTimeoutSeconds?: number | null
  onExecutionTimeoutSecondsChange?: (next: number | null) => void
  /** The row's retry policy, the JSON text of the column; `null` is no policy. */
  retryPolicyJson?: string | null
  onRetryPolicyJsonChange?: (next: string | null) => void
  /** The row's checkpoint policy, the JSON text of the column; `null` is no policy. */
  checkpointPolicyJson?: string | null
  onCheckpointPolicyJsonChange?: (next: string | null) => void
}

/** The workflow row's own three engine settings: the deadline a whole run gets, and the retry and checkpoint
 * policies it names. Each control is rendered only where the host supplies that control's own callback. */
export function WorkflowExecutionSection({
  executionTimeoutSeconds, onExecutionTimeoutSecondsChange,
  retryPolicyJson, onRetryPolicyJsonChange,
  checkpointPolicyJson, onCheckpointPolicyJsonChange,
}: WorkflowExecutionSectionProps) {
  const any = onExecutionTimeoutSecondsChange !== undefined
    || onRetryPolicyJsonChange !== undefined
    || onCheckpointPolicyJsonChange !== undefined
  if (!any) return null

  return (
    <Box data-testid="workflow-execution-section" sx={{ mt: 3 }}>
      <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>Workflow execution</Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
        What the row itself names: the deadline a run gets, and the retry and checkpoint policies it obeys.
      </Typography>
      <Stack spacing={2}>
        {onExecutionTimeoutSecondsChange !== undefined && (
          <ExecutionTimeoutField value={executionTimeoutSeconds} onChange={onExecutionTimeoutSecondsChange} />
        )}
        {onRetryPolicyJsonChange !== undefined && (
          <RetryPolicyField value={retryPolicyJson} onChange={onRetryPolicyJsonChange} />
        )}
        {onCheckpointPolicyJsonChange !== undefined && (
          <CheckpointPolicyField value={checkpointPolicyJson} onChange={onCheckpointPolicyJsonChange} />
        )}
      </Stack>
    </Box>
  )
}
