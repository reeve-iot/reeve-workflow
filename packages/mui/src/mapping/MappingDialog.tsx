import { useMemo, useState } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControl, MenuItem, Paper, Select,
  Switch, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, ToggleButton,
  ToggleButtonGroup, Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import type { MappingProjection, MappingType, PortLiteralForm, PortTermDisplay, PortTypeName, VariableMapping } from '@reeve-iot/workflow-core'
import {
  WORKFLOW_INPUT_BLOCK_TYPE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID,
  describeRepeatedMember,
  findRepeatedMember,
  indexDraftAnalysis,
  literalFormOfType,
  namesTypeParameter,
  openTermLiteralType,
  parseDynamicPorts,
  readMapping,
  usePortTypes,
  useWorkflowEditorStore,
} from '@reeve-iot/workflow-core'
import { termText, useAnalysedPortTerm } from '../canvas/analysedPortTerm.js'
import { useNodePorts } from '../canvas/nodePorts.js'
import { readExactNumber } from '../ports/PortValueEditor.js'
import { RefusedLiteral } from '../ports/refusedLiteral.js'
import { inputWriteTerm } from '../ports/writeTerm.js'
import { useMappingCustomization } from './mappingCustomization.js'
import { isJsonTerm, useWireSourceOutputs } from './wireSourceOutputs.js'

interface MappableInput {
  name: string
  displayName: string
  isRequired: boolean
  type?: PortTypeName
  shown?: PortTermDisplay
  allowedValues?: string[]
}

type LiteralCellKind = 'allowed' | 'number' | 'boolean' | 'json' | 'text' | 'none'

type LiteralCellMode = 'value' | 'expression'

const EXPRESSION_PREFIX = 'expr:'

function isExpressionLiteral(value: unknown): boolean {
  return typeof value === 'string' && value.startsWith(EXPRESSION_PREFIX)
}

function literalCellKind(input: MappableInput, form: PortLiteralForm | null | undefined): LiteralCellKind {
  if (input.allowedValues && input.allowedValues.length > 0) return 'allowed'
  if (form === null) return 'none'
  if (form !== undefined) {
    return form.gradedAs === 'Int' || form.gradedAs === 'Number' ? 'number'
      : form.gradedAs === 'Boolean' ? 'boolean' : form.gradedAs === 'Json' ? 'json' : 'text'
  }
  if (input.type === 'Number' || input.type === 'Int') return 'number'
  if (input.type === 'Boolean') return 'boolean'
  return input.type === 'Json' || (input.type !== undefined && openTermLiteralType(input.type) === 'Json') ? 'json' : 'text'
}

const LITERAL_JSON_ERROR = 'Must be valid JSON'
const LITERAL_NULL_ERROR = 'null is not a literal value; use an expression instead'

function readJsonLiteral(text: string): { value: unknown; error: string } {
  if (text.trim() === '') return { value: undefined, error: '' }
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch {
    return { value: undefined, error: LITERAL_JSON_ERROR }
  }
  if (parsed === null) return { value: undefined, error: LITERAL_NULL_ERROR }
  const repeat = findRepeatedMember(text)
  if (repeat !== undefined) return { value: undefined, error: `Ambiguous JSON: ${describeRepeatedMember(repeat)}` }
  return { value: parsed, error: '' }
}

function literalJsonText(value: unknown): string {
  return value === undefined || value === null ? '' : JSON.stringify(value, null, 2)
}

export interface MappingDialogProps {
  edgeId: string
  onClose: () => void
  entryNodeLabel?: string
  exitNodeLabel?: string
}

/** Edits what one wire carries: which source output feeds each input of the target, or what literal does.
 * The port lists come from the same customization seam the canvas uses, so the dialog cannot drift from
 * what the node draws. */
export function MappingDialog({ edgeId, onClose, entryNodeLabel = 'Workflow Input', exitNodeLabel = 'Workflow Output' }: MappingDialogProps) {
  const edges = useWorkflowEditorStore((s) => s.edges)
  const nodes = useWorkflowEditorStore((s) => s.nodes)
  const canvasStack = useWorkflowEditorStore((s) => s.canvasStack)
  const blockTypes = useWorkflowEditorStore((s) => s.blockTypes)
  const workflowOutputParams = useWorkflowEditorStore((s) => s.workflowOutputParams)
  const portTypes = usePortTypes()
  const analysis = useWorkflowEditorStore((s) => s.analysis)
  const custom = useMappingCustomization()
  const updateEdgeMappings = useWorkflowEditorStore((s) => s.updateEdgeMappings)
  const removeEdge = useWorkflowEditorStore((s) => s.removeEdge)

  const edge = edges.find((e) => e.id === edgeId)
  const sourceNode = nodes.find((n) => n.id === edge?.sourceNodeId)
  const targetNode = nodes.find((n) => n.id === edge?.targetNodeId)
  const sourceBlockType = blockTypes.find((bt) => bt.blockTypeId === sourceNode?.blockTypeId)
  const targetBlockType = blockTypes.find((bt) => bt.blockTypeId === targetNode?.blockTypeId)

  const hostPorts = useNodePorts(targetNode ?? null, targetBlockType)
  const frame = canvasStack[canvasStack.length - 1]
  const analysisIndex = useMemo(() => indexDraftAnalysis(analysis), [analysis])
  const analysedTerm = useAnalysedPortTerm()
  const canvasPath = useMemo(() => canvasStack.map((f) => ({ nodeId: f.parentNodeId, branchName: f.subCanvasName })), [canvasStack])

  const isSourceEntry = sourceNode?.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID
  const isTargetExit = targetNode?.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID
  const isSourceExit = sourceNode?.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID
  const isTargetEntry = targetNode?.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID

  const availableOutputs = useWireSourceOutputs(edge?.sourceNodeId)

  const jsonOutputs = useMemo(
    () => availableOutputs.flatMap((out) => (out.type !== undefined && isJsonTerm(out.type) ? [{ name: out.name, type: out.type }] : [])),
    [availableOutputs],
  )

  const targetInputs = useMemo<MappableInput[]>(() => {
    if (isTargetExit) {
      if (!frame) {
        return workflowOutputParams.map((p) => ({ name: p.name, displayName: p.displayName, isRequired: p.isRequired, type: p.type }))
      }
      const parentNode = frame.nodes.find((n) => n.id === frame.parentNodeId)
      const parentBlockType = blockTypes.find((bt) => bt.blockTypeId === parentNode?.blockTypeId)
      const declared = (parentBlockType?.subCanvasOutputs?.[frame.subCanvasName] ?? []).map((p) => ({
        name: p.name, displayName: p.displayName, isRequired: p.isRequired, type: p.type, allowedValues: p.allowedValues ?? undefined,
      }))
      const bySourceKey = new Map<string, MappableInput>()
      for (const p of parentNode?.outputPorts ?? []) {
        const sourceKey = p.sources?.[frame.subCanvasName]
        if (!sourceKey || declared.some((d) => d.name === sourceKey) || bySourceKey.has(sourceKey)) continue
        bySourceKey.set(sourceKey, { name: sourceKey, displayName: sourceKey, isRequired: false, type: p.type })
      }
      return [...declared, ...bySourceKey.values()]
    }
    if (targetBlockType?.hasDynamicPorts) {
      return parseDynamicPorts(targetNode?.configuration['InputPorts'] as string)
    }
    const base = (targetBlockType?.inputs ?? [])
      .filter((i) => !hostPorts.isInputHidden(i.name))
      .map((i) => {
        const port = { name: i.name, displayName: i.displayName, isRequired: i.isRequired, type: i.type }
        const declared = hostPorts.inputType(port)
        const bound = declared === undefined || targetNode === undefined ? undefined
          : inputWriteTerm(declared, targetNode, i.name, analysisIndex, canvasPath)
        return { ...port, type: (bound ?? declared) as PortTypeName | undefined, allowedValues: i.allowedValues ?? undefined }
      })
    const extra = hostPorts.hostInputs.filter((c) => !base.some((i) => i.name === c.name))
    const forwarded = (targetNode?.passthrough ?? [])
      .filter((name) => !base.some((i) => i.name === name) && !extra.some((c) => c.name === name))
      .map((name): MappableInput => ({ name, displayName: name, isRequired: false, shown: analysedTerm(targetNode?.id ?? '', 'inputs', name) }))
    return [...base, ...extra, ...forwarded]
  }, [isTargetExit, frame, workflowOutputParams, blockTypes, targetBlockType, targetNode, hostPorts, analysisIndex, canvasPath, analysedTerm])

  const [mappings, setMappings] = useState<Record<string, VariableMapping>>(edge?.mappings ?? {})
  const [literalText, setLiteralText] = useState<Record<string, string>>({})
  const [literalMode, setLiteralMode] = useState<Record<string, LiteralCellMode>>({})
  const [writtenAs, setWrittenAs] = useState<Record<string, string | undefined>>({})

  if (!edge || !sourceNode || !targetNode) return null
  if (isSourceExit || isTargetEntry) return null
  if ((!isSourceEntry && !sourceBlockType) || (!isTargetExit && !targetBlockType)) {
    return (
      <Dialog open onClose={onClose} maxWidth="sm" fullWidth>
        <DialogTitle>Stored mappings</DialogTitle>
        <DialogContent dividers data-testid="mapping-dialog-unavailable">
          <Typography variant="body2" color="textSecondary" gutterBottom>
            A block on this connection is not available on this host, so its mappings are shown as stored and cannot be edited.
          </Typography>
          <Box component="ul" sx={{ m: 0, pl: 2, fontFamily: 'monospace', fontSize: 12 }}>
            {Object.entries(edge.mappings).map(([input, mapping]) => {
              const read = readMapping(mapping)
              return (
                <li key={input}>
                  {input} {read.literal ? `= ${read.value === undefined ? 'nothing' : JSON.stringify(read.value)}` : `↳ ${read.source}`}
                </li>
              )
            })}
          </Box>
        </DialogContent>
        <DialogActions sx={{ justifyContent: 'space-between', px: 3 }}>
          <Button color="error" startIcon={<DeleteIcon />} onClick={() => { removeEdge(edgeId); onClose() }}>
            Remove Connection
          </Button>
          <Button onClick={onClose}>Close</Button>
        </DialogActions>
      </Dialog>
    )
  }

  const sourceDisplayName = isSourceEntry ? entryNodeLabel : (sourceBlockType?.displayName ?? '')
  const targetDisplayName = isTargetExit ? exitNodeLabel : (targetBlockType?.displayName ?? '')

  const written = (inputName: string) => {
    setWrittenAs((prev) => ({ ...prev, [inputName]: targetInputs.find((input) => input.name === inputName)?.type }))
  }

  const change = (inputName: string, field: keyof VariableMapping, value: string) => {
    if (field === 'literalValue') written(inputName)
    setMappings((prev) => {
      const next = { ...(prev[inputName] ?? { type: 'direct' as MappingType }), [field]: value }
      if (field === 'type' && value === 'literal') delete next.projection
      if (field === 'sourceOutputName' && !readMapping(next).literal && value === '') {
        const rest = { ...prev }
        delete rest[inputName]
        return rest
      }
      return { ...prev, [inputName]: next }
    })
  }

  const changeProjection = (inputName: string, projection: MappingProjection | null) => {
    setMappings((prev) => {
      const entry = prev[inputName]
      if (!entry || readMapping(entry).literal) return prev
      const next: VariableMapping = { ...entry }
      delete next.projection
      if (projection !== null) next.projection = projection
      return { ...prev, [inputName]: next }
    })
  }

  const changeLiteral = (inputName: string, value: unknown) => {
    written(inputName)
    const literalValue = value as VariableMapping['literalValue']
    setMappings((prev) => {
      const entry = prev[inputName] ?? { type: 'literal' as MappingType }
      return { ...prev, [inputName]: { ...entry, literalValue } }
    })
  }

  const changeNumber = (inputName: string, shape: string | undefined, raw: string) => {
    setLiteralText((prev) => ({ ...prev, [inputName]: raw }))
    const number = readExactNumber(raw, shape)
    if (number !== undefined) changeLiteral(inputName, number)
  }

  const changeJson = (input: MappableInput, text: string) => {
    const inputName = input.name
    setLiteralText((prev) => ({ ...prev, [inputName]: text }))
    const parsed = readJsonLiteral(text)
    if (parsed.error === '' && parsed.value !== undefined) changeLiteral(inputName, parsed.value)
  }

  const literalStored = (input: MappableInput): unknown => {
    const read = readMapping(mappings[input.name] ?? { type: 'direct' })
    return read.literal ? read.value : undefined
  }

  const valueStored = (input: MappableInput): unknown => {
    const stored = literalStored(input)
    return isExpressionLiteral(stored) ? undefined : stored
  }

  const numberInvalid = (input: MappableInput): boolean => {
    const typed = literalText[input.name]
    return typed !== undefined && typed !== '' && readExactNumber(typed, formOf(input)?.gradedAs ?? input.type) === undefined
  }

  const formOf = (input: MappableInput) => (input.type === undefined ? undefined : literalFormOfType(input.type, portTypes))
  const kindOf = (input: MappableInput) => literalCellKind(input, formOf(input))

  const modeOf = (input: MappableInput): LiteralCellMode =>
    literalMode[input.name] ?? (isExpressionLiteral(literalStored(input)) ? 'expression' : 'value')

  const valueTextOf = (input: MappableInput): string => {
    const stored = valueStored(input)
    return literalText[input.name] ?? (stored === undefined || stored === null ? '' : String(stored))
  }

  const jsonTextOf = (input: MappableInput) =>
    literalText[input.name] ?? literalJsonText(valueStored(input))

  const expressionTextOf = (input: MappableInput): string => {
    const stored = literalStored(input)
    if (isExpressionLiteral(stored)) return (stored as string).slice(EXPRESSION_PREFIX.length)
    return kindOf(input) === 'number' ? valueTextOf(input) : ''
  }

  const refusalOf = (input: MappableInput) => {
    if (kindOf(input) !== 'json' || !readMapping(mappings[input.name] ?? { type: 'direct' }).literal) return ''
    return readJsonLiteral(modeOf(input) === 'value' ? jsonTextOf(input) : literalJsonText(valueStored(input))).error
  }

  const switchMode = (input: MappableInput, next: LiteralCellMode | null) => {
    if (!next || next === modeOf(input)) return
    setLiteralMode((prev) => ({ ...prev, [input.name]: next }))
    const body = expressionTextOf(input)
    if (next === 'expression' && body !== '') changeLiteral(input.name, `${EXPRESSION_PREFIX}${body}`)
  }

  const storedRefusalOf = (input: MappableInput): string | undefined => {
    const kind = kindOf(input)
    if (input.type === undefined || kind === 'allowed' || (formOf(input) === undefined && kind !== 'json')) return undefined
    if (Object.hasOwn(writtenAs, input.name) && writtenAs[input.name] === input.type) return undefined
    const stored = valueStored(input)
    return stored === undefined || portTypes.isLiteralOf(stored, input.type) ? undefined : input.type
  }

  const replaceStored = (input: MappableInput) => {
    setLiteralText((prev) => {
      const rest = { ...prev }
      delete rest[input.name]
      return rest
    })
    if (kindOf(input) === 'boolean') {
      changeLiteral(input.name, false)
      return
    }
    setMappings((prev) => {
      const entry = prev[input.name]
      if (entry === undefined) return prev
      const next: VariableMapping = { ...entry }
      delete next.literalValue
      return { ...prev, [input.name]: next }
    })
  }

  const refused = targetInputs.some((input) => refusalOf(input) !== '')

  const save = () => {
    updateEdgeMappings(edgeId, Object.fromEntries(Object.entries(mappings).filter(([, m]) => { const read = readMapping(m); return read.literal || read.source !== '' })))
    onClose()
  }

  return (
    <Dialog open onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle>Map Variables: {sourceDisplayName} &rarr; {targetDisplayName}</DialogTitle>
      <DialogContent dividers>
        <Typography variant="body2" color="textSecondary" gutterBottom>
          Map the outputs from &apos;{sourceDisplayName}&apos; to the inputs of &apos;{targetDisplayName}&apos;. All required inputs (*) must be mapped.
        </Typography>

        {targetInputs.length === 0 ? (
          <Box sx={{ py: 4, textAlign: 'center' }}>
            <Typography variant="body1" color="textSecondary">
              This connection is for <strong>execution ordering only</strong>.
            </Typography>
            <Typography variant="caption" color="textSecondary">Target block has no input ports to map.</Typography>
          </Box>
        ) : (
          <TableContainer component={Paper} variant="outlined">
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>Target Input</TableCell>
                  <TableCell>Mapping Type</TableCell>
                  <TableCell>Source Output / Value</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {targetInputs.map((input) => {
                  const read = readMapping(mappings[input.name] ?? { type: 'direct' })
                  const kind = kindOf(input)
                  return (
                    <TableRow key={input.name}>
                      <TableCell>
                        <Typography variant="body2" sx={{ fontWeight: input.isRequired ? 'bold' : 'normal' }}>
                          {input.displayName} {input.isRequired && '*'}
                        </Typography>
                        {input.type !== undefined && <Typography variant="caption" color="textSecondary">({input.type})</Typography>}
                        {input.type === undefined && input.shown !== undefined && (
                          <Typography
                            variant="caption" color={input.shown.knowledge === 'refused' ? 'error' : 'textSecondary'}
                            data-testid={`mapping-term-${input.name}`} data-term-knowledge={input.shown.knowledge}
                            data-analysis={input.shown.stale ? 'stale' : 'current'}
                            sx={input.shown.stale ? { fontStyle: 'italic' } : undefined}
                          >({input.shown.text})</Typography>
                        )}
                      </TableCell>
                      <TableCell>
                        <FormControl fullWidth size="small">
                          <Select
                            value={read.literal ? 'literal' : 'direct'}
                            inputProps={{ 'aria-label': `${input.displayName} mapping type` }}
                            onChange={(e) => { change(input.name, 'type', e.target.value) }}
                          >
                            <MenuItem value="direct">Direct</MenuItem>
                            <MenuItem value="literal">Literal</MenuItem>
                          </Select>
                        </FormControl>
                      </TableCell>
                      <TableCell>
                        {!read.literal && (
                          <FormControl fullWidth size="small">
                            <Select
                              value={read.source}
                              inputProps={{ 'aria-label': `${input.displayName} source` }}
                              onChange={(e) => { change(input.name, 'sourceOutputName', e.target.value) }}
                            >
                              <MenuItem value=""><em>None</em></MenuItem>
                              {availableOutputs.map((out) => (
                                <MenuItem key={out.name} value={out.name}>{out.displayName}{out.type === undefined && out.shown === undefined ? '' : ` (${out.type ?? (out.shown === undefined ? '' : termText(out.shown))})`}</MenuItem>
                              ))}
                            </Select>
                          </FormControl>
                        )}
                        {!read.literal && read.source !== '' && (() => {
                          const sourceType = availableOutputs.find((out) => out.name === read.source)?.type
                          const stored = mappings[input.name]?.projection
                          if (!custom.renderProjectionControl || (stored === undefined && !(sourceType && isJsonTerm(sourceType)))) return null
                          return custom.renderProjectionControl({
                            placement: 'wire', edgeId, targetNodeId: edge.targetNodeId, inputName: input.name,
                            sourceNodeId: edge.sourceNodeId, sourceOutputName: read.source, sourceType,
                            targetType: input.type, projection: stored,
                            onChange: (projection) => { changeProjection(input.name, projection) },
                          })
                        })()}
                        {read.literal && kind === 'allowed' && (
                          <FormControl fullWidth size="small">
                            <Select
                              value={String(read.value ?? '')}
                              inputProps={{ 'aria-label': `${input.displayName} literal` }}
                              onChange={(e) => { change(input.name, 'literalValue', e.target.value) }}
                            >
                              <MenuItem value=""><em>None</em></MenuItem>
                              {(input.allowedValues ?? []).map((val) => <MenuItem key={val} value={val}>{val}</MenuItem>)}
                            </Select>
                          </FormControl>
                        )}
                        {read.literal && kind === 'text' && formOf(input) === undefined && (
                          <TextField
                            fullWidth size="small" multiline rows={4} placeholder="Literal value"
                            value={String(read.value ?? '')}
                            helperText={input.type !== undefined && namesTypeParameter(input.type)
                              ? 'The type of this input is not chosen yet; the value is checked once it is.' : undefined}
                            slotProps={{ htmlInput: { 'aria-label': `${input.displayName} literal` } }}
                            onChange={(e) => { change(input.name, 'literalValue', e.target.value) }}
                          />
                        )}
                        {read.literal && (kind === 'number' || kind === 'boolean' || kind === 'json' || (kind === 'text' && formOf(input) !== undefined) || kind === 'none') && (
                          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.5, minWidth: 220 }}>
                            <ToggleButtonGroup
                              size="small" exclusive value={modeOf(input)}
                              aria-label={`${input.displayName} literal mode`}
                              onChange={(_, next: LiteralCellMode | null) => { switchMode(input, next) }}
                            >
                              <ToggleButton value="value" data-testid={`literal-value-${input.name}`}>Value</ToggleButton>
                              <ToggleButton value="expression" data-testid={`literal-expression-${input.name}`}>Expression</ToggleButton>
                            </ToggleButtonGroup>
                            {modeOf(input) === 'expression' ? (
                              <>
                                <TextField
                                  fullWidth size="small" placeholder="Expression"
                                  value={expressionTextOf(input)}
                                  error={refusalOf(input) !== ''}
                                  helperText={refusalOf(input) || undefined}
                                  slotProps={{ htmlInput: { 'aria-label': `${input.displayName} expression`, style: { fontFamily: 'monospace', fontSize: 11 } } }}
                                  onChange={(e) => { changeLiteral(input.name, `${EXPRESSION_PREFIX}${e.target.value}`) }}
                                />
                                {jsonOutputs.length > 0 && custom.renderProjectionControl?.({
                                  placement: 'expression', edgeId, targetNodeId: edge.targetNodeId, inputName: input.name,
                                  sourceNodeId: edge.sourceNodeId, jsonOutputs, targetType: input.type,
                                  expressionBody: expressionTextOf(input),
                                  onExpressionChange: (body) => { changeLiteral(input.name, `${EXPRESSION_PREFIX}${body}`) },
                                })}
                              </>
                            ) : storedRefusalOf(input) !== undefined ? (
                              <RefusedLiteral
                                testId={`literal-refused-${input.name}`} value={valueStored(input)} expected={storedRefusalOf(input) ?? ''}
                                onReplace={() => { replaceStored(input) }}
                              />
                            ) : kind === 'number' ? (
                              <TextField
                                fullWidth size="small" type="text" placeholder="Literal value"
                                value={valueTextOf(input)}
                                error={numberInvalid(input)}
                                helperText={numberInvalid(input) ? (
                                  <span data-testid={`literal-error-${input.name}`} data-rule-id="literal-port-value-invalid">
                                    This value cannot be read as {formOf(input)?.gradedAs ?? input.type}.
                                  </span>
                                ) : undefined}
                                slotProps={{ htmlInput: { inputMode: 'decimal', 'aria-label': `${input.displayName} literal` } }}
                                onChange={(e) => { changeNumber(input.name, formOf(input)?.gradedAs ?? input.type, e.target.value) }}
                              />
                            ) : kind === 'text' ? (
                              <TextField
                                fullWidth size="small" multiline rows={4} placeholder="Literal value"
                                value={String(valueStored(input) ?? '')}
                                slotProps={{ htmlInput: { 'aria-label': `${input.displayName} literal` } }}
                                onChange={(e) => { change(input.name, 'literalValue', e.target.value) }}
                              />
                            ) : kind === 'none' ? (
                              <Typography variant="caption" color="textSecondary" data-testid={`literal-none-${input.name}`}>
                                This type takes no literal value. Connect a wire or write an expression.
                              </Typography>
                            ) : kind === 'boolean' ? (
                              <Switch
                                size="small"
                                checked={valueStored(input) === true}
                                slotProps={{ input: { 'aria-label': `${input.displayName} literal` } }}
                                onChange={(e) => { changeLiteral(input.name, e.target.checked) }}
                              />
                            ) : (
                              <TextField
                                fullWidth size="small" multiline rows={4} placeholder="[1, 2, 3]"
                                value={jsonTextOf(input)}
                                error={refusalOf(input) !== ''}
                                helperText={refusalOf(input) || undefined}
                                slotProps={{ htmlInput: { 'aria-label': `${input.displayName} literal`, style: { fontFamily: 'monospace', fontSize: 11 } } }}
                                onChange={(e) => { changeJson(input, e.target.value) }}
                              />
                            )}
                          </Box>
                        )}
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          </TableContainer>
        )}
      </DialogContent>
      <DialogActions sx={{ justifyContent: 'space-between', px: 3 }}>
        <Button color="error" startIcon={<DeleteIcon />} onClick={() => { removeEdge(edgeId); onClose() }}>
          Remove Connection
        </Button>
        <Box>
          <Button onClick={onClose} sx={{ mr: 1 }}>Cancel</Button>
          <Button variant="contained" color="primary" disabled={refused} onClick={save}>Save Mappings</Button>
        </Box>
      </DialogActions>
    </Dialog>
  )
}
