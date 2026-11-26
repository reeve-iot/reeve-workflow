import { useRef, useState } from 'react'
import { Box, Button, FormControlLabel, IconButton, Switch, TextField, Typography } from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteIcon from '@mui/icons-material/Delete'

/** One entry of a map being edited. */
export interface KeyValueRow {
  readonly key: string
  readonly value: string
}

export const KEY_REQUIRED = 'A key is required'
export const KEY_REPEATED = 'Another row has this key'
export const VALUE_REPLACEMENT_REQUIRED = 'Type the new value, or cancel to keep the stored one'
export const KEY_FIXED_WHILE_REPLACING = 'The key stays fixed while replacing'
export const VALUE_HIDDEN_REQUIRED = 'A hidden value needs text; show it to leave it empty'
export const KEY_NOT_FIELD_NAME = 'A name is letters, digits and the marks ! # $ % & \' * + - . ^ _ ` | ~ only; a value goes in the value box'

const FIELD_NAME = /^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$/

/** The rows a stored value is, or undefined when it is not a plain object of text: any other value is not this editor's
 * to show, because rows could not hold it as it is. An empty value is no rows. */
export function readKeyValueRows(raw: unknown): readonly KeyValueRow[] | undefined {
  if (raw === undefined || raw === null || raw === '') return []
  if (typeof raw !== 'object' || Array.isArray(raw)) return undefined
  const entries = Object.entries(raw)
  return entries.every(([, value]) => typeof value === 'string')
    ? entries.map(([key, value]) => ({ key, value: value as string }))
    : undefined
}

/** Why each row cannot be written, by position; '' where it can. Nothing is trimmed, merged or dropped to make a row fit.
 * With `keysAreFieldNames`, a key that is no field name is a fault, so a header pasted whole as `Name: value` is never written. */
export function keyValueRowFaults(rows: readonly KeyValueRow[], keysAreFieldNames = false): string[] {
  return rows.map((row) => {
    if (row.key === '') return KEY_REQUIRED
    if (keysAreFieldNames && !FIELD_NAME.test(row.key)) return KEY_NOT_FIELD_NAME
    return rows.some((other) => other !== row && other.key === row.key) ? KEY_REPEATED : ''
  })
}

const MASKED_VALUE = '***'
const PROTECT_PREFIX = 'protect:'

type EntryForm = 'plain' | 'hidden' | 'masked'

interface EntryRow extends KeyValueRow {
  readonly form: EntryForm
  readonly replaces?: boolean
}

function valueFaults(rows: readonly EntryRow[]): string[] {
  return rows.map((row) => {
    if (row.value !== '') return ''
    if (row.replaces === true) return VALUE_REPLACEMENT_REQUIRED
    return row.form === 'hidden' ? VALUE_HIDDEN_REQUIRED : ''
  })
}

function entryRowsOf(raw: unknown, secretValues: boolean, canProtect: boolean): readonly EntryRow[] {
  return (readKeyValueRows(raw) ?? []).map(({ key, value }): EntryRow => {
    if (!secretValues) return { key, value, form: 'plain' }
    if (value === MASKED_VALUE) return { key, value, form: 'masked' }
    if (canProtect && value.startsWith(PROTECT_PREFIX)) return { key, value: value.slice(PROTECT_PREFIX.length), form: 'hidden' }
    return { key, value, form: 'plain' }
  })
}

function writtenValue(row: EntryRow, canProtect: boolean): string {
  if (row.form === 'masked') return MASKED_VALUE
  return row.form === 'hidden' && canProtect ? `${PROTECT_PREFIX}${row.value}` : row.value
}

interface KeyValueDraft {
  readonly rows: readonly EntryRow[]
  readonly scope: string
}

const scopeOf = (raw: unknown): string => JSON.stringify(raw ?? '')

export interface KeyValueRowsFieldProps {
  fieldName: string
  label: string
  rawValue: unknown
  description: string | undefined
  /** Writes the object the rows are. */
  onObjectChange: (value: Record<string, string>) => void
  /** Takes the value off the node, which the last row's removal asks for. */
  onClear: () => void
  /** True for a secret-capable map: its keys are field names and the host may keep an entry hidden, reading it back as stars. */
  secretValues?: boolean
  /** True when the host can protect an entry: each row of a secret-capable map then offers a hide toggle. Without it none does. */
  canProtect?: boolean
}

interface EntryValueFieldProps {
  label: string
  entryLabel: string
  row: EntryRow
  testId: string
  canProtect: boolean
  fault: string
  onChange: (change: Partial<EntryRow>) => void
}

function EntryValueField({ label, entryLabel, row, testId, canProtect, fault, onChange }: EntryValueFieldProps) {
  if (row.form === 'masked') {
    return (
      <Box sx={{ display: 'flex', gap: 1, alignItems: 'flex-start' }}>
        <TextField
          size="small" fullWidth type="password" value={row.value} helperText="Hidden: kept as stored until replaced"
          slotProps={{ htmlInput: { 'aria-label': label, 'data-testid': testId, readOnly: true, autoComplete: 'off' } }}
        />
        <Button
          size="small" variant="outlined" aria-label={`Replace value of ${entryLabel}`} data-testid={`${testId}-replace`}
          onClick={() => { onChange({ value: '', form: 'hidden', replaces: true }) }}
        >
          Replace
        </Button>
      </Box>
    )
  }
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
      <TextField
        size="small" fullWidth placeholder="Value" error={fault !== ''} helperText={fault === '' ? undefined : fault}
        type={row.form === 'hidden' ? 'password' : 'text'}
        value={row.value}
        slotProps={{ htmlInput: { 'aria-label': label, 'data-testid': testId, autoComplete: 'off' } }}
        onChange={(event) => { onChange({ value: event.target.value }) }}
      />
      {(canProtect || row.replaces === true) && (
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
          {canProtect && (
            <FormControlLabel
              sx={{ mr: 0, whiteSpace: 'nowrap' }} label="Hide"
              control={
                <Switch
                  size="small" checked={row.form === 'hidden'}
                  slotProps={{ input: { 'aria-label': `Hide value of ${entryLabel}` } }}
                  onChange={(event) => { onChange({ form: event.target.checked ? 'hidden' : 'plain' }) }}
                />
              }
            />
          )}
          {row.replaces === true && (
            <Button
              size="small" variant="outlined" aria-label={`Cancel replacing value of ${entryLabel}`} data-testid={`${testId}-cancel`}
              onClick={() => { onChange({ value: MASKED_VALUE, form: 'masked', replaces: false }) }}
            >
              Cancel
            </Button>
          )}
        </Box>
      )}
    </Box>
  )
}

/** The rows a `Map<String,String>` literal is edited in. Rows that cannot be written (no key, a repeated key, a key that is no field name) are marked
 * where they stand and written nowhere, so the document keeps the last map that was whole. */
export function KeyValueRowsField({ fieldName, label, rawValue, description, onObjectChange, onClear, secretValues = false, canProtect = false }: KeyValueRowsFieldProps) {
  const [draft, setDraft] = useState<KeyValueDraft | null>(null)
  const addButton = useRef<HTMLButtonElement>(null)
  const [focusedRow, setFocusedRow] = useState<number | null>(null)
  const stored = entryRowsOf(rawValue, secretValues, canProtect)
  const rows = draft !== null && draft.scope === scopeOf(rawValue) ? draft.rows : stored
  const faults = keyValueRowFaults(rows, secretValues)
  const valueFault = valueFaults(rows)

  const commit = (next: readonly EntryRow[]) => {
    if ([...keyValueRowFaults(next, secretValues), ...valueFaults(next)].some((fault) => fault !== '')) {
      setDraft({ rows: next, scope: scopeOf(rawValue) })
      return
    }
    if (next.length === 0) {
      setDraft({ rows: next, scope: scopeOf('') })
      onClear()
      return
    }
    const written = Object.fromEntries(next.map((row) => [row.key, writtenValue(row, canProtect)]))
    setDraft({ rows: next, scope: scopeOf(written) })
    onObjectChange(written)
  }
  const edit = (index: number, change: Partial<EntryRow>) => {
    const target = rows[index]
    if (target === undefined || (change.key !== undefined && (target.form === 'masked' || target.replaces === true))) return
    commit(rows.map((row, at) => (at === index ? { ...row, ...change } : row)))
  }

  return (
    <Box role="group" aria-label={label} data-testid={`port-value-input-${fieldName}`} sx={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
      {rows.map((row, index) => {
        const position = String(index + 1)
        return (
          <Box
            key={index} role="group" aria-label={`${label} entry ${position}`}
            sx={{ display: 'flex', gap: 1, alignItems: 'flex-start', border: '1px solid', borderColor: 'divider', borderRadius: 1, p: 1 }}
          >
            <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
              <TextField
                size="small" fullWidth placeholder="Key"
                value={row.key} autoFocus={focusedRow === index}
                error={faults[index] !== ''} helperText={faults[index] !== '' ? faults[index] : row.replaces === true ? KEY_FIXED_WHILE_REPLACING : undefined}
                slotProps={{ htmlInput: { 'aria-label': `${label} key ${position}`, 'data-testid': `kv-key-${fieldName}-${position}`, readOnly: row.form === 'masked' || row.replaces === true } }}
                onChange={(event) => { edit(index, { key: event.target.value }) }}
              />
              <EntryValueField
                label={`${label} value ${position}`} entryLabel={`${label} entry ${position}`} row={row} testId={`kv-value-${fieldName}-${position}`} canProtect={secretValues && canProtect} fault={valueFault[index] ?? ''}
                onChange={(change) => { edit(index, change) }}
              />
            </Box>
            <IconButton
              size="small" color="error" aria-label={`Remove ${label} entry ${position}`}
              onClick={() => {
                commit(rows.filter((_, at) => at !== index))
                setFocusedRow(null)
                addButton.current?.focus()
              }}
            >
              <DeleteIcon fontSize="small" />
            </IconButton>
          </Box>
        )
      })}
      {rows.length === 0 && <Typography variant="caption" color="text.secondary">No entries</Typography>}
      {description && <Typography variant="caption" color="text.secondary">{description}</Typography>}
      <Button
        ref={addButton} startIcon={<AddIcon />} size="small" variant="outlined"
        data-testid={`kv-add-${fieldName}`}
        onClick={() => {
          setFocusedRow(rows.length)
          commit([...rows, { key: '', value: '', form: 'plain' }])
        }}
      >
        Add {label} entry
      </Button>
    </Box>
  )
}
