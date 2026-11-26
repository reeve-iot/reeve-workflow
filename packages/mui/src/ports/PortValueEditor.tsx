import type { ReactNode } from 'react'
import { useState } from 'react'
import { Box, Switch, TextField, ToggleButton, ToggleButtonGroup, Typography } from '@mui/material'
import { describeRepeatedMember, findRepeatedMember, isMapTerm, isStringMapTerm } from '@reeve-iot/workflow-core'
import type { PortLiteralForm, PortTypeName, WorkflowNode } from '@reeve-iot/workflow-core'
import { KeyValueRowsField, readKeyValueRows } from './KeyValueRowsField.js'
import { RefusedLiteral } from './refusedLiteral.js'

type JsonLiteral = WorkflowNode['configuration'][string]

export type PortEditorMode = 'value' | 'expression' | 'connection'

/** Turns typed text into what the port expects: a number for a `Number` or `Int`, the text as typed for anything else. */
export function coercePortValue(raw: string, fieldType: PortTypeName | undefined): string | number {
  if (fieldType === 'Number') {
    const parsed = Number(raw)
    return raw.trim() !== '' && Number.isFinite(parsed) ? parsed : raw
  }
  if (fieldType === 'Int') {
    const parsed = Number(raw)
    return raw.trim() !== '' && Number.isInteger(parsed) ? parsed : raw
  }
  return raw
}

/** The number a field's text is, or undefined when it is not one: an `Int` takes a whole number's value, a
 * `Number` any finite one, and neither is repaired from text that does not parse (`7.5` into an `Int`). */
export function readExactNumber(raw: string, fieldType: string | undefined): number | undefined {
  if (raw === '' || raw.trim() !== raw) return undefined
  const parsed = Number(raw)
  if (!Number.isFinite(parsed)) return undefined
  if (fieldType === 'Int' && !Number.isInteger(parsed)) return undefined
  return fieldType === 'Number' || fieldType === 'Int' ? parsed : undefined
}

/** What a host renderer is told about the port it is being asked to edit. */
export interface PortValueEditorContext {
  fieldName: string
  label: string
  rawValue: unknown
  fieldType: PortTypeName | undefined
  /** The form the field's declared type gives a literal, which decides the control and the JSON the field writes. Null
   * when the type is registered with no form, so it has no literal to write; absent when the type is not the editor's to
   * read (a collection, a type nobody registered), which keeps the control its name implies. */
  literalForm?: PortLiteralForm | null
  required: boolean
  description: string | undefined
  /** What the field offers while it holds nothing: a schema default the engine never applies, so a hint
   * for what to type rather than a value. Drawn only by the typed editors; a switch has no room for it. */
  placeholder?: string
  /** Never null: a stored null is refused by the reader, and the panel refuses the write at the field. */
  onChange: (value: string | number | boolean) => void
  /** The names an `Enum` field accepts, in the order to list them. The field is drawn as a select over these and
   * writes the name itself, never its position. Ignored for any other type. */
  allowedValues?: readonly string[] | null
}

export interface PortValueEditorProps extends Omit<PortValueEditorContext, 'required' | 'fieldType' | 'description'> {
  fieldType?: PortTypeName
  required?: boolean
  description?: string
  /** True when a wire feeds this port, which makes the textual editors irrelevant. */
  hasConnection: boolean
  /** Editor for a type the host owns. Return null to fall back to the plain text or number field. */
  renderTypeEditor?: (context: PortValueEditorContext) => ReactNode | null
  /** The host's expression editor. Without it the Expression mode is not offered at all, because an
   * expression language is something a host has and this library does not. */
  renderExpressionEditor?: (context: PortValueEditorContext & { expressionBody: string }) => ReactNode
  /** Writes the value a `Json` field or port holds: any JSON but null, an object, a list, text, a number or a boolean.
   * Without it no JSON field is offered, because an object is what `onChange` has no type for. */
  onObjectChange?: (value: JsonLiteral) => void
  /** Prefix marking a stored value as an expression rather than a literal. */
  expressionPrefix?: string
  /** Takes the value off the node, which an `Enum` select's blank entry asks for; without it the blank entry writes
   * an empty string. */
  onClear?: () => void
  /** True for an input that declares `secretValues`: its map keys are field names, and a row with another key is not written. */
  secretValues?: boolean
  /** True when the host has a secret codec, so a hide toggle may be offered on a `secretValues` input's rows. */
  canProtectSecrets?: boolean
  /** Set when the stored value is not a literal of the port's term, e.g. the text `true` on a `Boolean` port: names the
   * term it cannot be read as. Unless this editor wrote that value itself under the same `valueTerm`, the value mode then
   * shows the stored value as invalid, with a Replace action that writes `false` for a `Boolean` and clears the value
   * otherwise (through `onClear` where given), instead of drawing it in the typed control. */
  storedValueRefusal?: string
  /** The closed term the value is written in, where the caller knows it (for example `Collection<Int>` for an input
   * declared `Collection<$T>`). A value this editor wrote is exempt from `storedValueRefusal` only while this term is
   * unchanged; without it, while `fieldType` is unchanged. */
  valueTerm?: string
}

function enumStoredOutside(raw: unknown, names: readonly string[]): string | undefined {
  if (raw === undefined || raw === null || raw === '') return undefined
  if (typeof raw === 'string' && names.includes(raw)) return undefined
  return typeof raw === 'string' ? raw : JSON.stringify(raw)
}
const enumShown = (raw: unknown, names: readonly string[]): string =>
  enumStoredOutside(raw, names) ?? (typeof raw === 'string' ? raw : '')

const JSON_ERROR = 'Must be valid JSON'
const JSON_NULL_ERROR = 'null is not a literal value; use an expression instead'
const isTrueBooleanValue = (value: unknown): boolean => value === true

const kindOfName = (name: PortTypeName | undefined): PortLiteralForm['gradedAs'] | undefined =>
  name === 'Number' ? 'Number' : name === 'Int' ? 'Int' : name === 'Boolean' ? 'Boolean' : name === 'Json' ? 'Json' : undefined

function readJsonValue(text: string): { value: JsonLiteral } | { error: string } {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return { error: JSON_ERROR }
  }
  if (parsed === null) return { error: JSON_NULL_ERROR }
  const repeat = findRepeatedMember(text)
  return repeat === undefined ? { value: parsed as JsonLiteral } : { error: `Ambiguous JSON: ${describeRepeatedMember(repeat)}` }
}

interface JsonObjectFieldProps {
  fieldName: string
  label: string
  rawValue: unknown
  description: string | undefined
  placeholder: string | undefined
  onChange: PortValueEditorContext['onChange']
  onObjectChange: (value: JsonLiteral) => void
  onClear: (() => void) | undefined
}

function JsonObjectField({ fieldName, label, rawValue, description, placeholder, onChange, onObjectChange, onClear }: JsonObjectFieldProps) {
  const [text, setText] = useState<string | null>(null)
  const [error, setError] = useState('')
  const shown = text ?? (rawValue === undefined || rawValue === null || rawValue === '' ? '' : JSON.stringify(rawValue, null, 2))

  const change = (next: string) => {
    setText(next)
    if (next.trim() === '') {
      setError('')
      if (onClear) onClear()
      else onChange('')
      return
    }
    const read = readJsonValue(next)
    if ('error' in read) {
      setError(read.error)
      return
    }
    setError('')
    onObjectChange(read.value)
  }

  return (
    <TextField
      fullWidth size="small" multiline minRows={3} maxRows={8}
      value={shown}
      placeholder={placeholder ?? '{"key": "value"}'}
      error={error !== ''}
      helperText={error || description || undefined}
      slotProps={{
        htmlInput: {
          'data-testid': `port-value-input-${fieldName}`,
          'aria-label': label,
          style: { fontFamily: 'monospace', fontSize: 11 },
        },
      }}
      onChange={(event) => { change(event.target.value) }}
    />
  )
}

/** Three-mode port editor: a literal, an expression, or driven by a wire. The Connection mode cannot be
 * chosen here — leaving it means removing the wire on the canvas. */
export function PortValueEditor({
  fieldName,
  label,
  rawValue,
  fieldType,
  literalForm,
  description,
  required = false,
  placeholder,
  onChange,
  allowedValues,
  onClear,
  hasConnection,
  renderTypeEditor,
  renderExpressionEditor,
  onObjectChange,
  expressionPrefix = 'expr:',
  secretValues = false,
  canProtectSecrets = false,
  storedValueRefusal,
  valueTerm,
}: PortValueEditorProps) {
  const isExpression = typeof rawValue === 'string' && rawValue.startsWith(expressionPrefix)
  const blank = rawValue === undefined || rawValue === null || rawValue === ''
  const opensAsExpression = isExpression
    || (blank && renderExpressionEditor !== undefined && (placeholder ?? '').startsWith(expressionPrefix))
  const [mode, setMode] = useState<PortEditorMode>(hasConnection ? 'connection' : opensAsExpression ? 'expression' : 'value')
  const [numberDraft, setNumberDraft] = useState<string | null>(null)
  const writtenTerm = valueTerm ?? fieldType
  const [written, setWritten] = useState<{ term: string | undefined; json: string } | null>(null)
  const own = (value: unknown) => { setWritten({ term: writtenTerm, json: JSON.stringify(value) }) }
  const write = (value: string | number | boolean) => {
    own(value)
    onChange(value)
  }
  const writeObject = onObjectChange === undefined ? undefined : (value: JsonLiteral) => {
    own(value)
    onObjectChange(value)
  }

  const context: PortValueEditorContext = {
    fieldName, label, rawValue, fieldType, literalForm, required, description, placeholder, onChange: write, allowedValues,
  }
  const kind = literalForm === undefined ? kindOfName(fieldType) : (literalForm?.gradedAs ?? null)
  const isNumeric = kind === 'Number' || kind === 'Int'
  const isBoolean = kind === 'Boolean'
  const isMap = fieldType !== undefined && isMapTerm(fieldType)
  const isStringMap = fieldType !== undefined && isStringMapTerm(fieldType)
  const stringMapRows = isStringMap ? readKeyValueRows(rawValue) : undefined
  const expressionBody = isExpression ? (rawValue as string).slice(expressionPrefix.length) : ''

  const changeMode = (next: PortEditorMode | null) => {
    if (!next || next === mode || next === 'connection') return
    setMode(next)
    if (next === 'value') {
      if (isExpression) {
        const body = (rawValue as string).slice(expressionPrefix.length)
        if (isMap && body === '' && onClear) onClear()
        else write(isBoolean ? body.trim() === 'true' : body)
      }
      return
    }
    if (isBoolean || (rawValue !== null && typeof rawValue === 'object')) {
      write(expressionPrefix)
      return
    }
    if (typeof rawValue === 'string' && !isExpression) write(`${expressionPrefix}${rawValue}`)
    else if (rawValue === null || rawValue === undefined || rawValue === '') write(expressionPrefix)
    else write(`${expressionPrefix}${String(rawValue)}`)
  }

  const ownsStored = written !== null && written.term === writtenTerm && written.json === JSON.stringify(rawValue)
  const refused = mode === 'value' && storedValueRefusal !== undefined && !isExpression && !ownsStored
  const replace = () => {
    setNumberDraft(null)
    if (isBoolean) write(false)
    else if (onClear) onClear()
    else write('')
  }
  const hostEditor = mode === 'value' && !refused ? (renderTypeEditor?.(context) ?? null) : null
  const textValue = rawValue === undefined || rawValue === null ? '' : String(rawValue)
  const numberInvalid = isNumeric && numberDraft !== null && numberDraft !== '' && readExactNumber(numberDraft, kind ?? undefined) === undefined

  return (
    <Box data-testid={`port-value-editor-${fieldName}`} sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <Typography variant="caption">{label}{required ? ' *' : ''}</Typography>
        <ToggleButtonGroup
          size="small" exclusive value={mode}
          onChange={(_, next: PortEditorMode | null) => changeMode(next)}
          aria-label={`${fieldName} input mode`} data-testid={`port-value-editor-mode-${fieldName}`}
        >
          <ToggleButton value="value" data-testid={`mode-value-${fieldName}`}>Value</ToggleButton>
          {renderExpressionEditor && (
            <ToggleButton value="expression" data-testid={`mode-expression-${fieldName}`}>Expression</ToggleButton>
          )}
          <ToggleButton value="connection" disabled={!hasConnection} data-testid={`mode-connection-${fieldName}`}>Connection</ToggleButton>
        </ToggleButtonGroup>
      </Box>

      {mode === 'connection' ? (
        <Typography variant="caption" color="text.secondary">
          Driven by an incoming connection. Disconnect the wire to edit a value.
        </Typography>
      ) : mode === 'expression' && renderExpressionEditor ? (
        renderExpressionEditor({ ...context, expressionBody })
      ) : refused ? (
        <RefusedLiteral testId={`port-value-refused-${fieldName}`} value={rawValue} expected={storedValueRefusal ?? ''} onReplace={replace} />
      ) : hostEditor ? (
        hostEditor
      ) : isStringMap && stringMapRows !== undefined && writeObject !== undefined ? (
        <KeyValueRowsField
          fieldName={fieldName} label={label} rawValue={rawValue} description={description}
          onObjectChange={writeObject} onClear={onClear ?? (() => { write('') })} secretValues={secretValues} canProtect={canProtectSecrets}
        />
      ) : (kind === 'Json' || isMap) && writeObject !== undefined ? (
        <JsonObjectField
          fieldName={fieldName} label={label} rawValue={rawValue}
          description={description} placeholder={placeholder}
          onChange={write} onObjectChange={writeObject} onClear={isMap ? onClear : undefined}
        />
      ) : fieldType === 'Enum' && allowedValues ? (
        <TextField
          select fullWidth size="small" value={enumShown(rawValue, allowedValues)}
          helperText={description ?? undefined}
          slotProps={{ select: { native: true }, htmlInput: { 'data-testid': `port-value-input-${fieldName}`, 'aria-label': label } }}
          onChange={(event) => {
            if (event.target.value !== '') write(event.target.value)
            else if (onClear) onClear()
            else write('')
          }}
        >
          <option value="" />
          {enumStoredOutside(rawValue, allowedValues) !== undefined && (
            <option value={enumStoredOutside(rawValue, allowedValues)} disabled>{enumStoredOutside(rawValue, allowedValues)} (not allowed)</option>
          )}
          {allowedValues.map((name) => <option key={name} value={name}>{name}</option>)}
        </TextField>
      ) : isBoolean ? (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
          <Switch
            size="small"
            checked={isTrueBooleanValue(rawValue)}
            data-testid={`port-value-input-${fieldName}`}
            slotProps={{ input: { 'aria-label': label } }}
            onChange={(event) => { write(event.target.checked) }}
          />
          {description && <Typography variant="caption" color="text.secondary">{description}</Typography>}
        </Box>
      ) : isNumeric ? (
        <TextField
          fullWidth size="small" type="text" value={numberDraft ?? textValue}
          placeholder={placeholder}
          error={numberInvalid}
          helperText={numberInvalid ? (
            <span data-testid={`port-value-error-${fieldName}`} data-rule-id="literal-port-value-invalid">
              This value cannot be read as {kind}.
            </span>
          ) : (description ?? undefined)}
          slotProps={{ htmlInput: { inputMode: 'decimal', 'data-testid': `port-value-input-${fieldName}` } }}
          onChange={(event) => {
            setNumberDraft(event.target.value)
            const number = readExactNumber(event.target.value, kind ?? undefined)
            if (number !== undefined) write(number)
          }}
        />
      ) : kind === null ? (
        <Typography variant="caption" color="text.secondary" data-testid={`port-value-none-${fieldName}`}>
          This type takes no literal value. Connect a wire or write an expression.
        </Typography>
      ) : (
        <TextField
          fullWidth size="small" value={textValue} type="text"
          placeholder={placeholder}
          helperText={description ?? undefined}
          slotProps={{ htmlInput: { 'data-testid': `port-value-input-${fieldName}` } }}
          onChange={(event) => write(event.target.value)}
        />
      )}
    </Box>
  )
}
