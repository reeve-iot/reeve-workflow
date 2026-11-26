import type { ReactNode } from 'react'
import { useMemo, useState } from 'react'
import { Box, Button, Checkbox, FormControl, FormControlLabel, MenuItem, Select, TextField, Typography } from '@mui/material'
import { DISPATCH_PORT_TYPES } from '@reeve-iot/workflow-example-dispatch'
import type {
  ConfigFieldContext, ConfigProjectionControlContext, MappingCustomization, NodeConfigCustomization, PortTypeName,
  ProjectionControlContext, WireProjectionControlContext,
} from '@reeve-iot/workflow-mui'
import type { PortValueEditorContext } from '@reeve-iot/workflow-mui'
import { TypeArgumentPicker } from '@reeve-iot/workflow-mui'
import type { WorkflowOptionsSource } from '@reeve-iot/workflow-adapters'

export const EXPRESSION_PREFIX = 'expr:'

const HOST_TYPES = new Set<PortTypeName>(DISPATCH_PORT_TYPES.map((type) => type.name))

function HostExpressionEditor(context: PortValueEditorContext & { expressionBody: string }): ReactNode {
  return (
    <TextField
      fullWidth multiline minRows={2} size="small"
      label={`${context.label} expression`}
      value={context.expressionBody}
      slotProps={{
        htmlInput: {
          'aria-label': `${context.label} expression`,
          'data-testid': `host-expression-editor-${context.fieldName}`,
        },
      }}
      onChange={(event) => { context.onChange(`${EXPRESSION_PREFIX}${event.target.value}`) }}
    />
  )
}

function HostTypeEditor(context: PortValueEditorContext): ReactNode | null {
  const { fieldType } = context
  if (fieldType === undefined || !HOST_TYPES.has(fieldType)) return null
  return (
    <TextField
      fullWidth size="small"
      label={`${context.label} (${fieldType})`}
      value={context.rawValue === null || context.rawValue === undefined ? '' : String(context.rawValue)}
      slotProps={{
        htmlInput: {
          'aria-label': `${context.label} (${fieldType})`,
          'data-testid': `host-type-editor-${context.fieldName}`,
        },
      }}
      onChange={(event) => { context.onChange(event.target.value) }}
    />
  )
}

export function portValueEditorProps() {
  return {
    renderTypeEditor: HostTypeEditor,
    renderExpressionEditor: HostExpressionEditor,
    expressionPrefix: EXPRESSION_PREFIX,
  }
}

function SubWorkflowPicker({ context, source }: { context: ConfigFieldContext; source: WorkflowOptionsSource }) {
  const options = source.useWorkflowOptions()
  const value = typeof context.value === 'string' ? context.value : ''

  return (
    <FormControl fullWidth size="small">
      <Typography variant="caption">{context.field.label}</Typography>
      <Select
        value={value}
        displayEmpty
        inputProps={{ 'aria-label': 'Sub-workflow', 'data-testid': 'subworkflow-workflow-id' }}
        onChange={(event) => { context.onChange(event.target.value) }}
      >
        <MenuItem value=""><em>Not chosen</em></MenuItem>
        {options.map((workflow) => (
          <MenuItem key={workflow.id} value={workflow.id} data-testid={`subworkflow-option-${workflow.id}`}>
            {workflow.name}
          </MenuItem>
        ))}
      </Select>
      {context.edgeBinding !== undefined && (
        <Typography variant="caption" color="text.secondary">
          {`Driven by an incoming wire (${context.edgeBinding}); this field is not read.`}
        </Typography>
      )}
    </FormControl>
  )
}

export function useNodeConfigCustomization(source: WorkflowOptionsSource): NodeConfigCustomization {
  return useMemo<NodeConfigCustomization>(
    () => ({
      renderConfigField: (context: ConfigFieldContext): ReactNode | null | undefined => {
        if (context.blockType.runsWorkflowFrom !== context.field.name) return undefined
        return <SubWorkflowPicker context={context} source={source} />
      },
      renderProjectionControl: renderConfigProjectionControl,
      renderTypeArgumentPicker: (context) => <TypeArgumentPicker context={context} />,
    }),
    [source],
  )
}

function WireProjectionStub({ context }: { context: WireProjectionControlContext }): ReactNode {
  const { projection, inputName } = context
  const write = (next: { path?: string; jsonText?: boolean; type?: string }) => {
    const path = next.path ?? projection?.path ?? ''
    const jsonText = next.jsonText ?? projection?.mode === 'jsonText'
    const type = next.type ?? projection?.type ?? ''
    context.onChange({ path, ...(jsonText ? { mode: 'jsonText' } : {}), ...(type === '' ? {} : { type }) })
  }
  return (
    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', mt: 0.5 }} data-testid={`host-projection-${inputName}`}>
      <TextField
        size="small" label="Path" value={projection?.path ?? ''}
        slotProps={{ htmlInput: { 'aria-label': `${inputName} projection path` } }}
        onChange={(event) => { write({ path: event.target.value }) }}
      />
      <FormControlLabel
        label="jsonText"
        control={(
          <Checkbox
            size="small" checked={projection?.mode === 'jsonText'}
            slotProps={{ input: { 'aria-label': `${inputName} projection jsonText` } }}
            onChange={(event) => { write({ jsonText: event.target.checked }) }}
          />
        )}
      />
      <TextField
        size="small" label="Type" value={projection?.type ?? ''}
        slotProps={{ htmlInput: { 'aria-label': `${inputName} projection type` } }}
        onChange={(event) => { write({ type: event.target.value }) }}
      />
      <Button size="small" aria-label={`Clear ${inputName} projection`} onClick={() => { context.onChange(null) }}>Clear</Button>
    </Box>
  )
}

function stringLiteral(text: string): string {
  return `"${text.replaceAll('\\', '\\\\').replaceAll('"', '\\"')}"`
}

function ExpressionProjectionStub({ label, source, onExpressionChange }: {
  label: string
  source: { sourceNodeId: string; name: string } | undefined
  onExpressionChange: (body: string) => void
}): ReactNode {
  const [path, setPath] = useState('$')
  const [jsonText, setJsonText] = useState(false)
  const [type, setType] = useState('')
  if (source === undefined) return null
  const read = `\${previous.${source.sourceNodeId}.${source.name}}`
  const call = jsonText ? 'jsonPathText' : 'jsonPath'
  const body = `${call}(${read}, ${stringLiteral(path)}, ${stringLiteral(type === '' ? 'String' : type)})`
  return (
    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', mt: 0.5 }} data-testid={`host-expression-projection-${label}`}>
      <TextField size="small" label="Path" value={path} slotProps={{ htmlInput: { 'aria-label': `${label} expression projection path` } }} onChange={(event) => { setPath(event.target.value) }} />
      <FormControlLabel
        label="jsonText"
        control={<Checkbox size="small" checked={jsonText} slotProps={{ input: { 'aria-label': `${label} expression projection jsonText` } }} onChange={(event) => { setJsonText(event.target.checked) }} />}
      />
      <TextField size="small" label="Type" value={type} slotProps={{ htmlInput: { 'aria-label': `${label} expression projection type` } }} onChange={(event) => { setType(event.target.value) }} />
      <Button size="small" aria-label={`Insert ${label} projection`} onClick={() => { onExpressionChange(body) }}>Insert</Button>
    </Box>
  )
}

export const MAPPING_CUSTOMIZATION: MappingCustomization = {
  renderProjectionControl: (context: ProjectionControlContext): ReactNode => (context.placement === 'wire'
    ? <WireProjectionStub context={context} />
    : (
      <ExpressionProjectionStub
        label={context.inputName}
        source={context.jsonOutputs[0] === undefined ? undefined : { sourceNodeId: context.sourceNodeId, name: context.jsonOutputs[0].name }}
        onExpressionChange={context.onExpressionChange}
      />
    )),
}

export function renderConfigProjectionControl(context: ConfigProjectionControlContext): ReactNode {
  return <ExpressionProjectionStub label={context.fieldName} source={context.jsonOutputs[0]} onExpressionChange={context.onExpressionChange} />
}
