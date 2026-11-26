import { Fragment, useMemo, useState } from 'react'
import { Alert, Box, Divider, Paper, Typography } from '@mui/material'
import type { BlockConfigField, BlockPort, NodeErrorPolicy, PortTypeName, PortTypeRegistry, VariableMapping, WorkflowEdge, WorkflowNode } from '@reeve-iot/workflow-core'
import { nullConfigurationKeys } from '@reeve-iot/workflow-core'
import {
  WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID,
  buildPortTypeScopes, computeIncomingVariables, createPortTypeResolver, draftAnalysisFreshness, feedsInput, indexDraftAnalysis, isMapTerm, literalFormOfType, openTermLiteralType, readMapping, usePortTypes, useWorkflowEditorStore,
} from '@reeve-iot/workflow-core'
import { useAnalysedPortTerm } from '../canvas/analysedPortTerm.js'
import { NODE_CONFIG_PANEL_WIDTH } from '../canvas/geometry.js'
import { useNodePorts } from '../canvas/nodePorts.js'
import type { DescribeUnavailableBlock } from '../canvas/unavailableBlock.js'
import { unavailableBlockText } from '../canvas/unavailableBlock.js'
import { WiredJsonOutputs } from '../mapping/wireSourceOutputs.js'
import { DeclaredOutputPortTable, PortDeclarationTable } from '../ports/PortDeclarationTable.js'
import { PortValueEditor } from '../ports/PortValueEditor.js'
import { inputWriteTerm } from '../ports/writeTerm.js'
import type { PortValueEditorProps } from '../ports/PortValueEditor.js'
import { ErrorHandlingSection } from './ErrorHandlingSection.js'
import { PassthroughSection } from './PassthroughSection.js'
import { ResiliencySection } from './ResiliencySection.js'
import { useNodeConfigCustomization } from './nodeConfigCustomization.js'
import type { ConfigFieldContext, NodeConfigurationValue } from './nodeConfigCustomization.js'

const RESERVED_CONFIG_KEYS = new Set(['timeoutSeconds', 'retryCount', 'retryEnabled', 'retryBackoffSeconds', 'errorPolicy'])
const DESIGN_TIME_CONFIG_PREFIX = '__'

export interface NodeConfigPanelProps {
  /** Passed through to every generated port editor, which is where a host's expression editor and
   * type-specific editors are supplied. */
  portValueEditor?: Pick<PortValueEditorProps, 'renderTypeEditor' | 'renderExpressionEditor' | 'expressionPrefix'>
  rootCanvasLabel?: string
  /** The host's wording for a selected node whose block type is not in the catalogue. Such a node gets a read-only panel
   * showing what it stores; without this it says the block type is not available on this host. */
  describeUnavailableBlock?: DescribeUnavailableBlock
  /** Draws each input fed by a literal on an incoming wire with a value editor that writes the wire's mapping, in place
   * of a schema field of the same name. Off by default: a host that draws such an input in its own section turns this
   * on and returns null from `NodeConfigCustomization.renderWireLiteral` for it. */
  showWireLiterals?: boolean
}

function describeMapping(mapping: VariableMapping): string {
  const read = readMapping(mapping)
  return read.literal ? `= ${read.value === undefined ? 'nothing' : JSON.stringify(read.value)}` : `↳ ${read.source}`
}

function UnavailableBlockPanel({ node, edges, describe }: { node: WorkflowNode; edges: readonly WorkflowEdge[]; describe: DescribeUnavailableBlock | undefined }) {
  const text = unavailableBlockText(node, describe)
  const wires = edges.flatMap((edge) => {
    if (edge.targetNodeId === node.id) return Object.entries(edge.mappings).map(([input, mapping]) => `${input} ${describeMapping(mapping)} (from ${edge.sourceNodeId})`)
    if (edge.sourceNodeId === node.id) return Object.entries(edge.mappings).map(([input, mapping]) => `${edge.targetNodeId}.${input} ${describeMapping(mapping)}`)
    return []
  })
  return (
    <Paper
      elevation={2} data-testid="node-config-panel" data-unavailable="true"
      sx={{ width: NODE_CONFIG_PANEL_WIDTH, boxSizing: 'border-box', height: '100%', overflow: 'auto', borderLeft: '1px solid', borderColor: 'divider', flexShrink: 0 }}
    >
      <Box sx={{ p: 2, borderBottom: '1px solid', borderColor: 'divider' }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 'bold' }}>{text.title}</Typography>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Node: {node.id}</Typography>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Block type: {node.blockTypeId}</Typography>
      </Box>
      <Box sx={{ p: 2, display: 'flex', flexDirection: 'column', gap: 1 }}>
        <Alert severity="warning" data-testid="unavailable-block-reason">{text.reason}</Alert>
        <Typography variant="caption" color="text.secondary">Stored configuration</Typography>
        <Box
          component="pre" data-testid="unavailable-block-configuration"
          sx={{ m: 0, p: 1, fontFamily: 'monospace', fontSize: 11, whiteSpace: 'pre-wrap', wordBreak: 'break-all', bgcolor: 'action.hover', borderRadius: 1 }}
        >
          {JSON.stringify(node.configuration, null, 2)}
        </Box>
        {wires.length > 0 && (
          <>
            <Typography variant="caption" color="text.secondary">Stored wire mappings</Typography>
            <Box component="ul" data-testid="unavailable-block-wires" sx={{ m: 0, pl: 2, fontFamily: 'monospace', fontSize: 11 }}>
              {wires.map((line) => <li key={line}>{line}</li>)}
            </Box>
          </>
        )}
      </Box>
    </Paper>
  )
}

/** The control a literal of `declared` is edited with, given its closed term where one is known: the map term for a map, Json
 * for a term with no form of its own whose open literal is JSON, the closed term otherwise, or text while the term is open. */
function editorFieldType(declared: string, closed: string | undefined, portTypes: PortTypeRegistry): PortTypeName {
  const writeTerm = closed ?? openTermLiteralType(declared)
  const mapTerm = closed ?? declared
  if (isMapTerm(mapTerm)) return mapTerm as PortTypeName
  return literalFormOfType(writeTerm as PortTypeName, portTypes) === undefined && openTermLiteralType(writeTerm) === 'Json' ? 'Json' : writeTerm as PortTypeName
}

interface WireLiteral {
  input: BlockPort
  edge: WorkflowEdge
  declared: PortTypeName
  closed: string | undefined
  allowed: string[] | undefined
}

const UNBOUND_HINT = 'The type of this input is not chosen yet; the value is checked once it is.'

/** Configuration form for the selected node: the block type's own schema, then resiliency, error handling
 * and passthrough. Everything domain-specific arrives through NodeConfigCustomization — the panel itself
 * knows nothing beyond what a descriptor declares. */
export function NodeConfigPanel({
  portValueEditor, rootCanvasLabel = 'Main Workflow', describeUnavailableBlock, showWireLiterals = false,
}: NodeConfigPanelProps) {
  const custom = useNodeConfigCustomization()
  const portTypes = usePortTypes()
  const selectedNodeId = useWorkflowEditorStore((s) => s.selectedNodeId)
  const nodes = useWorkflowEditorStore((s) => s.nodes)
  const edges = useWorkflowEditorStore((s) => s.edges)
  const blockTypes = useWorkflowEditorStore((s) => s.blockTypes)
  const canvasStack = useWorkflowEditorStore((s) => s.canvasStack)
  const workflowInputParams = useWorkflowEditorStore((s) => s.workflowInputParams)
  const isPaletteDragActive = useWorkflowEditorStore((s) => s.isPaletteDragActive)
  const updateNodeConfig = useWorkflowEditorStore((s) => s.updateNodeConfig)
  const patchNodeConfig = useWorkflowEditorStore((s) => s.patchNodeConfig)
  const updateNodePassthrough = useWorkflowEditorStore((s) => s.updateNodePassthrough)
  const updateNodeOutputPorts = useWorkflowEditorStore((s) => s.updateNodeOutputPorts)
  const updateNodeErrorPolicy = useWorkflowEditorStore((s) => s.updateNodeErrorPolicy)
  const updateEdgeMappings = useWorkflowEditorStore((s) => s.updateEdgeMappings)
  const setNodeTypeArguments = useWorkflowEditorStore((s) => s.setNodeTypeArguments)
  const analysis = useWorkflowEditorStore((s) => s.analysis)
  const analysisIndex = useMemo(() => indexDraftAnalysis(analysis), [analysis])
  const revision = useWorkflowEditorStore((s) => s.revision)
  const analysisFreshness = draftAnalysisFreshness(analysis, revision)
  const analysedTerm = useAnalysedPortTerm()
  const canvasPath = useMemo(() => canvasStack.map((f) => ({ nodeId: f.parentNodeId, branchName: f.subCanvasName })), [canvasStack])

  const [refusedNull, setRefusedNull] = useState<{ nodeId: string; keys: readonly string[] } | null>(null)

  const node = selectedNodeId === null ? undefined : nodes.find((n) => n.id === selectedNodeId)
  const blockType = blockTypes.find((bt) => bt.blockTypeId === node?.blockTypeId)
  const nodeIds = useMemo(() => new Set(nodes.map((n) => n.id)), [nodes])
  const hostPorts = useNodePorts(node ?? null, blockType)

  const resolver = useMemo(
    () => createPortTypeResolver({
      scopes: buildPortTypeScopes({ nodes, edges, canvasStack }),
      blockTypes,
      workflowInputParams,
    }),
    [nodes, edges, canvasStack, blockTypes, workflowInputParams],
  )

  const incoming = useMemo(
    () => (node
      ? computeIncomingVariables({ node, nodes, edges, blockTypes, canvasStack, workflowInputParams, resolver, analysis: analysisIndex, freshness: analysisFreshness })
      : []),
    [node, nodes, edges, blockTypes, canvasStack, workflowInputParams, resolver, analysisIndex, analysisFreshness],
  )

  if (!node) return null
  if (node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID || node.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID) return null
  if (!blockType) return <UnavailableBlockPanel node={node} edges={edges} describe={describeUnavailableBlock} />

  const passthrough = node.passthrough ?? []
  const refusesNull = (written: Readonly<Record<string, unknown>>) => {
    const keys = nullConfigurationKeys(written)
    setRefusedNull(keys.length > 0 ? { nodeId: node.id, keys } : null)
    return keys.length > 0
  }
  const setConfiguration = (configuration: WorkflowNode['configuration']) => {
    if (!refusesNull(configuration)) updateNodeConfig(node.id, configuration)
  }
  const setField = (fieldName: string, value: NodeConfigurationValue) => {
    if (!refusesNull({ [fieldName]: value })) patchNodeConfig(node.id, { [fieldName]: value })
  }
  const nullFault = (keys: readonly string[]) => {
    const refused = refusedNull?.nodeId === node.id ? keys.filter((key) => refusedNull.keys.includes(key)) : []
    return refused.length === 0 ? null : (
      <Typography key={`null:${refused.join(',')}`} role="alert" variant="caption" color="error">
        Not saved: {refused.join(', ')} cannot be null. Clearing a value removes it.
      </Typography>
    )
  }
  const clearFields = (fieldNames: readonly string[]) => {
    const dropped = new Set(fieldNames)
    setConfiguration(Object.fromEntries(
      Object.entries(node.configuration).filter(([key]) => !dropped.has(key)),
    ))
  }

  const writeErrorPolicy = (policy: NodeErrorPolicy | null) => {
    updateNodeErrorPolicy(node.id, policy)
    if (Object.hasOwn(node.configuration, 'errorPolicy')) clearFields(['errorPolicy'])
  }

  const readEdgeBinding = (inputName: string): string | undefined => {
    for (const edge of edges) {
      if (edge.targetNodeId !== node.id || !feedsInput(edge, inputName, nodeIds)) continue
      const mapping = edge.mappings[inputName]
      const read = mapping && readMapping(mapping)
      if (read?.literal) return `= ${String(read.value)}`
      if (read?.source) return `↳ ${read.source}`
    }
    return undefined
  }

  const isFedByAnEdge = (inputName: string) =>
    edges.some((e) => e.targetNodeId === node.id && feedsInput(e, inputName, nodeIds))

  const literalFormOf = (type: string | undefined) => (type === undefined ? undefined : literalFormOfType(type, portTypes))
  const prefix = portValueEditor?.expressionPrefix ?? 'expr:'
  const refusalOf = (value: unknown, closed: string | undefined): string | undefined =>
    value === undefined || closed === undefined || (typeof value === 'string' && value.startsWith(prefix)) || portTypes.isLiteralOf(value, closed)
      ? undefined : closed
  const closedTermOf = (declared: string, name: string) => inputWriteTerm(declared, node, name, analysisIndex, canvasPath)

  const schemaFieldOf = (name: string) => (blockType.configurationSchema ?? []).find((f) => f.name === name)
  const wireLiterals = !showWireLiterals ? [] : blockType.inputs.flatMap((input) => {
    if (hostPorts.isInputHidden(input.name)) return []
    const edge = edges.find((e) => e.targetNodeId === node.id && feedsInput(e, input.name, nodeIds) && readMapping(e.mappings[input.name] ?? {}).literal)
    if (edge === undefined) return []
    const declared = hostPorts.inputType(input)
    const field = schemaFieldOf(input.name)
    const allowed = (input.allowedValues ?? []).length > 0 ? input.allowedValues : field?.type === 'Enum' ? field.allowedValues : undefined
    return [{ input, edge, declared, closed: closedTermOf(declared, input.name), allowed: allowed ?? undefined }]
  })
  const wireLiteralOf = (name: string) => wireLiterals.find((wired) => wired.input.name === name)
  const writeWireLiteral = (edge: WorkflowEdge, inputName: string, value: VariableMapping['literalValue'] | undefined) => {
    const written: VariableMapping = { ...(edge.mappings[inputName] ?? { type: 'literal' }) }
    delete written.literalValue
    if (value !== undefined) written.literalValue = value
    updateEdgeMappings(edge.id, { ...edge.mappings, [inputName]: written })
  }
  const renderWireLiteral = ({ input, edge, declared, closed, allowed }: WireLiteral) => {
    const read = readMapping(edge.mappings[input.name] ?? {})
    const value = read.literal ? read.value : undefined
    const fieldType = allowed === undefined ? editorFieldType(declared, closed, portTypes) : 'Enum'
    const hosted = custom.renderWireLiteral?.({
      node, blockType, input, edgeId: edge.id, value, portType: (closed ?? declared) as PortTypeName,
      onChange: (next) => { writeWireLiteral(edge, input.name, next) },
    })
    if (hosted !== undefined) return hosted === null ? null : <Box key={`${node.id}:wire:${input.name}`}>{hosted}</Box>
    return (
      <Box key={`${node.id}:wire:${edge.id}:${input.name}`} data-testid={`wire-literal-${input.name}`}>
        <PortValueEditor
          fieldName={input.name}
          label={input.displayName}
          required={input.isRequired}
          rawValue={value === undefined ? '' : value}
          fieldType={fieldType}
          literalForm={allowed === undefined ? literalFormOfType(fieldType, portTypes) : undefined}
          allowedValues={allowed}
          description={closed === undefined ? UNBOUND_HINT : 'Set on the incoming connection.'}
          hasConnection={false}
          storedValueRefusal={allowed === undefined ? refusalOf(value, closed) : undefined}
          valueTerm={closed}
          secretValues={input.secretValues}
          canProtectSecrets={portTypes.secretCodec}
          onClear={() => { writeWireLiteral(edge, input.name, undefined) }}
          onChange={(next) => { writeWireLiteral(edge, input.name, next) }}
          onObjectChange={(next) => { writeWireLiteral(edge, input.name, next as VariableMapping['literalValue']) }}
          {...portValueEditor}
        />
      </Box>
    )
  }

  const mapInputs = blockType.inputs.flatMap((input) => {
    if ((blockType.configurationSchema ?? []).some((f) => f.name === input.name)) return []
    if (wireLiteralOf(input.name) !== undefined) return []
    const term = closedTermOf(input.type, input.name) ?? input.type
    return isMapTerm(term) ? [{ port: input, term: term as PortTypeName }] : []
  })
  const isValueAuthorable = (name: string) =>
    !RESERVED_CONFIG_KEYS.has(name) &&
    !name.startsWith(DESIGN_TIME_CONFIG_PREFIX) &&
    !(blockType.configurationSchema ?? []).some((f) => f.name === name) &&
    !mapInputs.some(({ port }) => port.name === name)

  const togglePassthrough = (name: string) => {
    if (!passthrough.includes(name)) {
      updateNodePassthrough(node.id, [...passthrough, name])
      const offer = incoming.find((v) => v.name === name)
      const edge = offer?.edgeId === undefined ? undefined : edges.find((e) => e.id === offer.edgeId)
      if (edge && !edge.mappings[name]) {
        updateEdgeMappings(edge.id, { ...edge.mappings, [name]: { type: 'direct', sourceOutputName: name } })
      }
      return
    }
    updateNodePassthrough(node.id, passthrough.filter((p) => p !== name))
    if (blockType.inputs.some((i) => i.name === name)) return
    const owner = edges.find((e) =>
      e.targetNodeId === node.id &&
      e.mappings[name]?.type === 'direct' &&
      e.mappings[name]?.sourceOutputName === name)
    if (owner) {
      updateEdgeMappings(owner.id, Object.fromEntries(Object.entries(owner.mappings).filter(([key]) => key !== name)))
    }
  }

  const incomingEdges = edges.filter((e) => e.targetNodeId === node.id)

  const renderField = (field: BlockConfigField) => {
    const wired = wireLiteralOf(field.name)
    if (wired !== undefined) return renderWireLiteral(wired)
    const value = node.configuration[field.name]
    const context: ConfigFieldContext = {
      node, blockType, field, value,
      portType: blockType.inputs.find((input) => input.name === field.name)?.type,
      edgeBinding: readEdgeBinding(field.name),
      onChange: (next) => { setField(field.name, next) },
      onConfigurationChange: setConfiguration,
    }

    const hosted = custom.renderConfigField?.(context)
    if (hosted !== undefined) {
      return hosted === null ? null : <Box key={`${node.id}:${field.name}`}>{hosted}{nullFault([field.name])}</Box>
    }

    const portTerm = context.portType === undefined ? undefined
      : closedTermOf(context.portType, field.name)
    const writeTerm = context.portType === undefined ? undefined : portTerm ?? openTermLiteralType(context.portType)
    const mapTerm = portTerm ?? context.portType
    const fieldType = field.type === 'Enum' || writeTerm === undefined ? field.type
      : mapTerm !== undefined && isMapTerm(mapTerm) ? (mapTerm as PortTypeName)
      : literalFormOfType(writeTerm as PortTypeName, portTypes) === undefined && openTermLiteralType(writeTerm) === 'Json' ? 'Json'
        : (writeTerm as PortTypeName)
    const opensAsExpression = (value === undefined || value === null || value === '') && portValueEditor?.renderExpressionEditor !== undefined
      && (field.defaultValue ?? '').startsWith(prefix)
    const expressionBody = typeof value === 'string' && value.startsWith(prefix) ? value.slice(prefix.length) : opensAsExpression ? '' : null
    return (
      <Fragment key={`${node.id}:${field.name}`}>
        <PortValueEditor
          fieldName={field.name}
          label={field.label}
          rawValue={value ?? ''}
          placeholder={field.defaultValue ?? undefined}
          fieldType={fieldType}
          literalForm={field.type === 'Enum' ? undefined : literalFormOfType(fieldType, portTypes)}
          allowedValues={field.allowedValues}
          onClear={() => { clearFields([field.name]) }}
          description={field.description ?? undefined}
          required={field.required}
          hasConnection={isFedByAnEdge(field.name)}
          storedValueRefusal={field.type === 'Enum' ? undefined : refusalOf(value, portTerm)}
          valueTerm={portTerm}
          secretValues={blockType.inputs.find((input) => input.name === field.name)?.secretValues}
          canProtectSecrets={portTypes.secretCodec}
          onChange={context.onChange}
          onObjectChange={(value) => { setField(field.name, value) }}
          {...portValueEditor}
        />
        {expressionBody !== null && custom.renderProjectionControl && (
          <WiredJsonOutputs edges={incomingEdges}>
            {(jsonOutputs) => jsonOutputs.length > 0 && custom.renderProjectionControl?.({
              placement: 'configuration', node, blockType, fieldName: field.name, targetType: fieldType, jsonOutputs, expressionBody,
              onExpressionChange: (body) => { setField(field.name, `${prefix}${body}`) },
            })}
          </WiredJsonOutputs>
        )}
        {nullFault([field.name])}
      </Fragment>
    )
  }

  const schemaFields = (blockType.configurationSchema ?? []).filter(
    (field) => !(blockType.hasDynamicPorts && (field.name === 'InputPorts' || field.name === 'OutputPorts')),
  )

  return (
    <Paper
      elevation={2}
      data-testid="node-config-panel"
      sx={{
        width: NODE_CONFIG_PANEL_WIDTH, boxSizing: 'border-box', height: '100%', overflow: 'auto',
        borderLeft: '1px solid', borderColor: 'divider',
        display: 'flex', flexDirection: 'column', flexShrink: 0,
        pointerEvents: isPaletteDragActive ? 'none' : 'auto',
        opacity: isPaletteDragActive ? 0.6 : 1,
      }}
    >
      <Box sx={{ p: 2, borderBottom: '1px solid', borderColor: 'divider' }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 'bold' }}>{blockType.displayName}</Typography>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Node: {node.id}</Typography>
        {canvasStack.length > 0 && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
            Path: {rootCanvasLabel} / {canvasStack.map((frame) => frame.subCanvasName).join(' / ')}
          </Typography>
        )}
        <Typography variant="caption" color="text.secondary">Configuration</Typography>
      </Box>

      <Box sx={{ p: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
        {blockType.hasDynamicPorts && (
          <>
            <PortDeclarationTable
              key={`${node.id}-input-ports`}
              label="Input Ports" value={node.configuration['InputPorts'] as string}
              onChange={(json) => { setField('InputPorts', json) }}
            />
            <PortDeclarationTable
              key={`${node.id}-output-ports`}
              label="Output Ports" value={node.configuration['OutputPorts'] as string}
              onChange={(json) => { setField('OutputPorts', json) }}
            />
            <Divider />
          </>
        )}

        {blockType.supportsDeclaredOutputs && (
          <>
            <DeclaredOutputPortTable
              key={`${node.id}-declared-outputs`}
              label="Declared Outputs (mapped per branch)"
              subCanvasNames={blockType.subCanvasNames ?? []}
              value={node.outputPorts}
              onChange={(ports) => { updateNodeOutputPorts(node.id, ports) }}
            />
            <Divider />
          </>
        )}

        {schemaFields.map(renderField)}

        {wireLiterals.filter((wired) => schemaFieldOf(wired.input.name) === undefined).map(renderWireLiteral)}

        {mapInputs.map(({ port, term }) => (
          <Fragment key={`${node.id}:${port.name}`}>
            <PortValueEditor
              fieldName={port.name}
              label={port.displayName}
              required={port.isRequired}
              rawValue={node.configuration[port.name] ?? ''}
              fieldType={term}
              literalForm={literalFormOfType(term, portTypes)}
              onClear={() => { clearFields([port.name]) }}
              hasConnection={isFedByAnEdge(port.name)}
              storedValueRefusal={refusalOf(node.configuration[port.name], closedTermOf(port.type, port.name))}
              valueTerm={closedTermOf(port.type, port.name)}
              secretValues={port.secretValues}
              canProtectSecrets={portTypes.secretCodec}
              onChange={(value) => { setField(port.name, value) }}
              onObjectChange={(value) => { setField(port.name, value) }}
              {...portValueEditor}
            />
            {nullFault([port.name])}
          </Fragment>
        ))}

        {(blockType.typeParameters ?? []).length > 0 && custom.renderTypeArgumentPicker?.({
          node, blockType, typeParameters: blockType.typeParameters ?? [], typeArguments: node.typeArguments ?? {},
          onChange: (typeArguments) => { setNodeTypeArguments(node.id, typeArguments) },
        })}

        {custom.renderNodeSections?.({ node, blockType, onConfigurationChange: setConfiguration })}
        {nullFault((refusedNull?.keys ?? []).filter((key) => !schemaFields.some((field) => field.name === key)))}

        <Divider />
        <ResiliencySection
          key={`${node.id}-resiliency`}
          configuration={node.configuration} onChange={setField}
          isContainer={(blockType.subCanvasNames ?? []).length > 0} onClear={clearFields}
        />

        <Divider />
        <ErrorHandlingSection
          errorPolicy={node.errorPolicy}
          onChange={writeErrorPolicy}
          onReset={() => { writeErrorPolicy(null) }}
        />

        <Divider />
        <PassthroughSection
          key={`${node.id}-passthrough`}
          incoming={incoming} passthrough={passthrough}
          termOf={(name) => analysedTerm(node.id, 'inputs', name)}
          onToggle={togglePassthrough}
          subCanvasNames={blockType.subCanvasNames ?? []}
          renderValueEditor={(name) => (isValueAuthorable(name)
            ? (
              <PortValueEditor
                key={`${node.id}:passthrough:${name}`}
                fieldName={name}
                label={name}
                rawValue={node.configuration[name] ?? ''}
                fieldType={incoming.find((v) => v.name === name)?.type}
                literalForm={literalFormOf(incoming.find((v) => v.name === name)?.type)}
                onClear={() => { clearFields([name]) }}
                hasConnection={isFedByAnEdge(name)}
                onChange={(value) => { setField(name, value) }}
                onObjectChange={(value) => { setField(name, value) }}
                {...portValueEditor}
              />
            )
            : null)}
        />
      </Box>
    </Paper>
  )
}
