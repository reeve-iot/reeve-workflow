import type { ReactNode } from 'react'
import { useMemo, useState } from 'react'
import {
  Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControl, IconButton,
  MenuItem, Paper, Select, Stack, Tab, Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  Tabs, TextField, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteIcon from '@mui/icons-material/Delete'
import type { PortTypeName, WorkflowConcurrency, WorkflowParameter } from '@reeve-iot/workflow-core'
import { usePortTypes } from '@reeve-iot/workflow-core'
import { BLANK_NAME_REFUSED, draftInScope, nameFault } from '../nameDraft.js'
import type { NameDraft } from '../nameDraft.js'
import { CollectionComposer, typesWithCurrent } from '../ports/collectionComposer.js'
import { ConcurrencySection } from './ConcurrencySection.js'
import { WorkflowExecutionSection } from './WorkflowExecutionSection.js'
import type { WorkflowExecutionSectionProps } from './WorkflowExecutionSection.js'

export type ParameterSide = 'input' | 'output'

export interface ParameterDeclarationDialogProps {
  open: boolean
  onClose: () => void
  inputParameters: WorkflowParameter[]
  onInputParametersChange: (params: WorkflowParameter[]) => void
  outputParameters: WorkflowParameter[]
  onOutputParametersChange: (params: WorkflowParameter[]) => void
  name: string
  onNameChange: (name: string) => void
  description: string | null
  onDescriptionChange: (description: string | null) => void
  /** Omit both to drop the Concurrency tab, for a host whose engine does not offer the choice. */
  concurrency?: WorkflowConcurrency
  onConcurrencyChange?: (next: WorkflowConcurrency) => void
  /** The workflow row's own engine settings, in the same tab as ConcurrencySection. Each control is rendered
   * only where its own change callback is passed, and the tab itself only where one of the three is. */
  executionTimeoutSeconds?: WorkflowExecutionSectionProps['executionTimeoutSeconds']
  onExecutionTimeoutSecondsChange?: WorkflowExecutionSectionProps['onExecutionTimeoutSecondsChange']
  retryPolicyJson?: WorkflowExecutionSectionProps['retryPolicyJson']
  onRetryPolicyJsonChange?: WorkflowExecutionSectionProps['onRetryPolicyJsonChange']
  checkpointPolicyJson?: WorkflowExecutionSectionProps['checkpointPolicyJson']
  onCheckpointPolicyJsonChange?: WorkflowExecutionSectionProps['onCheckpointPolicyJsonChange']
  /** Narrows what the type picker offers. Defaults to every registered type except the bare collection,
   * which has no element type to carry. */
  parameterTypes?: PortTypeName[]
  /** Rendered at the foot of the General tab — where a host puts the fields only it knows about, such as
   * a folder picker or its own tags. */
  generalSlot?: ReactNode
  title?: string
}

function nextParameterName(existing: readonly WorkflowParameter[]): { name: string; index: number } {
  const taken = new Set(existing.map((p) => p.name))
  for (let i = 0; ; i++) {
    if (!taken.has(`param${String(i)}`)) return { name: `param${String(i)}`, index: i }
  }
}

export function ParameterDeclarationDialog({
  open, onClose,
  inputParameters, onInputParametersChange,
  outputParameters, onOutputParametersChange,
  name, onNameChange,
  description, onDescriptionChange,
  concurrency, onConcurrencyChange,
  executionTimeoutSeconds, onExecutionTimeoutSecondsChange,
  retryPolicyJson, onRetryPolicyJsonChange,
  checkpointPolicyJson, onCheckpointPolicyJsonChange,
  parameterTypes, generalSlot,
  title = 'Workflow Parameters',
}: ParameterDeclarationDialogProps) {
  const portTypes = usePortTypes()
  const [activeTab, setActiveTab] = useState(0)
  const [nameDrafts, setNameDrafts] = useState<Record<string, NameDraft>>({})

  const typeOptions = useMemo(
    () =>
      parameterTypes?.map((t) => ({ name: t, label: t })) ??
      portTypes.list().map((t) => ({ name: t.name, label: t.displayName ?? t.name })),
    [parameterTypes, portTypes],
  )

  const showConcurrency = concurrency !== undefined && onConcurrencyChange !== undefined
  const showExecution = onExecutionTimeoutSecondsChange !== undefined
    || onRetryPolicyJsonChange !== undefined
    || onCheckpointPolicyJsonChange !== undefined
  const rowTab = showConcurrency ? 'Concurrency' : 'Execution'
  const tabs = [
    'General', 'Inputs', 'Outputs',
    ...(showConcurrency || showExecution ? [rowTab] : []),
  ]
  const current = tabs[activeTab] ?? 'General'

  const listFor = (side: ParameterSide) => (side === 'input' ? inputParameters : outputParameters)
  const emit = (side: ParameterSide, params: WorkflowParameter[]) => {
    if (side === 'input') onInputParametersChange(params)
    else onOutputParametersChange(params)
  }

  const scope = JSON.stringify([inputParameters, outputParameters])
  const changeName = (side: ParameterSide, index: number, text: string) => {
    const key = `${side}:${String(index)}`
    if (nameFault(text, BLANK_NAME_REFUSED) !== null) {
      setNameDrafts((drafts) => ({ ...drafts, [key]: { scope, text } }))
      return
    }
    const next = listFor(side).map((p, i) => (i === index ? { ...p, name: text } : p))
    const produced = JSON.stringify(side === 'input' ? [next, outputParameters] : [inputParameters, next])
    setNameDrafts((drafts) => ({ ...drafts, [key]: { scope: produced, text } }))
    emit(side, next)
  }

  const addParam = (side: ParameterSide) => {
    const params = listFor(side)
    const { name: paramName, index } = nextParameterName(params)
    emit(side, [
      ...params,
      { name: paramName, displayName: `Parameter ${String(index)}`, type: 'String', isRequired: false },
    ])
  }

  const removeParam = (side: ParameterSide, index: number) => {
    emit(side, listFor(side).filter((_, i) => i !== index))
  }

  const changeParam = (side: ParameterSide, index: number, field: keyof WorkflowParameter, value: string | boolean) => {
    emit(side, listFor(side).map((p, i) => (i === index ? { ...p, [field]: value } : p)))
  }

  const renderTable = (side: ParameterSide) => {
    const params = listFor(side)
    const columns = 5
    return (
      <Box sx={{ mt: 2 }}>
        <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <Typography variant="subtitle1">{side === 'input' ? 'Input Parameters' : 'Output Parameters'}</Typography>
          <Button startIcon={<AddIcon />} variant="outlined" size="small" onClick={() => { addParam(side) }}>
            Add Parameter
          </Button>
        </Box>
        <TableContainer component={Paper} variant="outlined">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Name (ID)</TableCell>
                <TableCell>Display Name</TableCell>
                <TableCell>Type</TableCell>
                <TableCell>Required</TableCell>
                <TableCell width={50} />
              </TableRow>
            </TableHead>
            <TableBody>
              {params.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={columns} align="center" sx={{ py: 3 }}>No parameters defined</TableCell>
                </TableRow>
              ) : (
                params.map((param, index) => {
                  const storedName = param.name ?? ''
                  const nameText = draftInScope(nameDrafts[`${side}:${String(index)}`], scope)?.text ?? storedName
                  const nameFaultText = nameFault(nameText, BLANK_NAME_REFUSED)
                  const currentType = portTypes.normalize(param.type)
                  const options = typesWithCurrent(typeOptions.map((t) => t.name), currentType)
                    .map((listed) => typeOptions.find((t) => t.name === listed) ?? { name: listed, label: listed })
                  return (
                    <TableRow key={index}>
                      <TableCell>
                        <TextField
                          fullWidth size="small" value={nameText}
                          error={nameFaultText !== null}
                          helperText={nameFaultText ?? undefined}
                          slotProps={{ htmlInput: { 'aria-label': `${side} ${String(index)} name` } }}
                          onChange={(e) => { changeName(side, index, e.target.value) }}
                        />
                      </TableCell>
                      <TableCell>
                        <TextField
                          fullWidth size="small" value={param.displayName}
                          slotProps={{ htmlInput: { 'aria-label': `${side} ${String(index)} display name` } }}
                          onChange={(e) => { changeParam(side, index, 'displayName', e.target.value) }}
                        />
                      </TableCell>
                      <TableCell>
                        <Box sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
                          <FormControl fullWidth size="small">
                            <Select
                              value={currentType}
                              inputProps={{ 'aria-label': `${side} ${String(index)} type` }}
                              onChange={(e) => { changeParam(side, index, 'type', e.target.value) }}
                            >
                              {options.map((t) => <MenuItem key={t.name} value={t.name}>{t.label}</MenuItem>)}
                            </Select>
                          </FormControl>
                          <CollectionComposer
                            type={currentType} portTypes={portTypes}
                            ariaLabel={`${side} ${String(index)} type wrapped in a collection`}
                            onWrap={(wrapped) => { changeParam(side, index, 'type', wrapped) }}
                          />
                        </Box>
                      </TableCell>
                      <TableCell>
                        <Checkbox
                          checked={param.isRequired}
                          slotProps={{ input: { 'aria-label': `${side} ${String(index)} required` } }}
                          onChange={(e) => { changeParam(side, index, 'isRequired', e.target.checked) }}
                        />
                      </TableCell>
                      <TableCell>
                        <IconButton size="small" color="error" aria-label={`Remove ${side} ${String(index)}`} onClick={() => { removeParam(side, index) }}>
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </TableCell>
                    </TableRow>
                  )
                })
              )}
            </TableBody>
          </Table>
        </TableContainer>
      </Box>
    )
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle>{title}</DialogTitle>
      <DialogContent dividers>
        <Tabs value={activeTab} onChange={(_, val: number) => { setActiveTab(val) }}>
          {tabs.map((label) => <Tab key={label} label={label} />)}
        </Tabs>

        {current === 'General' && (
          <Stack spacing={3} sx={{ mt: 2 }}>
            <TextField
              label="Workflow Name" fullWidth value={name}
              slotProps={{ htmlInput: { 'aria-label': 'Workflow Name' } }}
              onChange={(e) => { onNameChange(e.target.value) }}
            />
            <TextField
              label="Description" fullWidth multiline rows={3} value={description ?? ''}
              slotProps={{ htmlInput: { 'aria-label': 'Description' } }}
              onChange={(e) => { onDescriptionChange(e.target.value || null) }}
            />
            {generalSlot}
          </Stack>
        )}

        {current === 'Inputs' && renderTable('input')}
        {current === 'Outputs' && renderTable('output')}
        {current === rowTab && showConcurrency && concurrency !== undefined && onConcurrencyChange !== undefined && (
          <ConcurrencySection value={concurrency} onChange={onConcurrencyChange} />
        )}
        {current === rowTab && showExecution && (
          <WorkflowExecutionSection
            executionTimeoutSeconds={executionTimeoutSeconds}
            onExecutionTimeoutSecondsChange={onExecutionTimeoutSecondsChange}
            retryPolicyJson={retryPolicyJson}
            onRetryPolicyJsonChange={onRetryPolicyJsonChange}
            checkpointPolicyJson={checkpointPolicyJson}
            onCheckpointPolicyJsonChange={onCheckpointPolicyJsonChange}
          />
        )}
      </DialogContent>
      <DialogActions>
        <Button variant="contained" color="primary" onClick={onClose}>Done</Button>
      </DialogActions>
    </Dialog>
  )
}
