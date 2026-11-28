import type { MouseEvent as ReactMouseEvent } from 'react'
import { memo, useId, useMemo } from 'react'
import type { PortInfo, PortTermDisplay, PortTypeName, WorkflowNode } from '@reeve-iot/workflow-core'
import {
  ERROR_OUTPUT_NAME,
  ERROR_OUTPUT_TYPE,
  WORKFLOW_INPUT_BLOCK_TYPE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID,
  WORKFLOW_START_ANCHOR_PORT_NAME,
  declaredOutputTerm,
  addsErrorOutput,
  displayPortTerm,
  draftAnalysisFreshness,
  indexDraftAnalysis,
  parseDynamicPorts,
  readMapping,
  useWorkflowEditorStore,
} from '@reeve-iot/workflow-core'
import { useEditorColors } from '../theme/editorColors.js'
import { NODE_HEADER_HEIGHT, NODE_ITEM_HEIGHT, NODE_WIDTH } from './geometry.js'
import { PORT_RADIUS, PORT_STROKE_WIDTH } from './portGeometry.js'
import { PORT_LABEL_FONT_SIZE, PORT_LABEL_INSET, layoutFittedPortLabelRow } from './portLabelLayout.js'
import type { NodeDiffKind, NodeHighlight } from './highlights.js'
import { useNodePorts } from './nodePorts.js'
import { useGraphTopology } from './graphTopology.js'
import type { DescribeUnavailableBlock } from './unavailableBlock.js'
import { unavailableBlockText } from './unavailableBlock.js'

const BUTTON_HEIGHT = 28
const BUTTON_GAP = 4
const BUTTONS_PADDING_TOP = 8

interface DrawnPort {
  name: string
  displayName: string
  isRequired: boolean
  type?: PortTypeName
}

const typeSuffix = (display: PortTermDisplay | undefined): string => (display === undefined ? '' : ` (${display.text})`)

/** The virtual output a node grows under the EmitError policy, core's ERROR_OUTPUT_NAME. */
export const ERROR_PORT_NAME = ERROR_OUTPUT_NAME

export interface BlockNodeProps {
  node: WorkflowNode
  isSelected: boolean
  isConnecting: boolean
  /** Every callback names the node, so a canvas can pass one stable handler to all of its nodes. */
  onDragStart: (event: ReactMouseEvent, nodeId: string) => void
  onConnectionStart: (nodeId: string, x: number, y: number) => void
  onConnectionEnd: (nodeId: string) => void
  onSelect: (nodeId: string) => void
  highlight?: NodeHighlight
  diffHighlight?: NodeDiffKind
  readOnly?: boolean
  isBreakpoint?: boolean
  isPausedHere?: boolean
  /** Draws a transient ring, for locating the block a validation finding points at. */
  isFlashing?: boolean
  /** Bumped per request so the pulse replays when the same node is asked for twice. */
  flashKey?: number
  entryNodeLabel?: string
  exitNodeLabel?: string
  /** The host's wording for a node whose block type is not in the catalogue, drawn as a placeholder in its place. */
  describeUnavailableBlock?: DescribeUnavailableBlock
}

export const BlockNode = memo(function BlockNode({
  node,
  isSelected,
  isConnecting,
  onDragStart,
  onConnectionStart,
  onConnectionEnd,
  onSelect,
  highlight,
  diffHighlight,
  readOnly,
  isBreakpoint,
  isPausedHere,
  isFlashing,
  flashKey,
  entryNodeLabel = 'Workflow Input',
  exitNodeLabel = 'Workflow Output',
  describeUnavailableBlock,
}: BlockNodeProps) {
  const c = useEditorColors()
  const clipId = useId().replace(/[^A-Za-z0-9_-]/g, '')
  const { canvasStack, edges } = useGraphTopology()
  const blockTypes = useWorkflowEditorStore((s) => s.blockTypes)
  const workflowInputParams = useWorkflowEditorStore((s) => s.workflowInputParams)
  const workflowOutputParams = useWorkflowEditorStore((s) => s.workflowOutputParams)
  const enterSubCanvas = useWorkflowEditorStore((s) => s.enterSubCanvas)
  const analysis = useWorkflowEditorStore((s) => s.analysis)
  const revision = useWorkflowEditorStore((s) => s.revision)

  const isEntry = node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID
  const isExit = node.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID
  const isSynthetic = isEntry || isExit

  const blockType = useMemo(
    () => (isSynthetic ? undefined : blockTypes.find((bt) => bt.blockTypeId === node.blockTypeId)),
    [blockTypes, node.blockTypeId, isSynthetic],
  )

  const hostPorts = useNodePorts(node, blockType)

  const analysisIndex = useMemo(() => indexDraftAnalysis(analysis), [analysis])
  const freshness = draftAnalysisFreshness(analysis, revision)
  const canvasPath = useMemo(() => canvasStack.map((f) => ({ nodeId: f.parentNodeId, branchName: f.subCanvasName })), [canvasStack])
  const termOf = (side: 'inputs' | 'outputs', port: DrawnPort): PortTermDisplay | undefined => {
    const analysed = analysisIndex.port(canvasPath, node.id, side, port.name)
    return displayPortTerm(port.type ?? analysed?.declared, analysed, freshness, analysisIndex.findings(canvasPath, node.id, port.name))
  }
  const termAttributes = (display: PortTermDisplay | undefined) => display === undefined ? {} : {
    'data-term-knowledge': display.knowledge,
    'data-analysis': display.knowledge === 'declared' ? 'none' : display.stale ? 'stale' : 'current',
  }

  let inputs: DrawnPort[] = []
  let outputs: DrawnPort[] = []
  const unavailable = !isSynthetic && blockType === undefined ? unavailableBlockText(node, describeUnavailableBlock) : undefined

  if (isSynthetic) {
    const frame = canvasStack[canvasStack.length - 1]
    if (!frame) {
      const params = isEntry ? workflowInputParams : workflowOutputParams
      const ports = params.map((p) => ({ name: p.name, displayName: p.displayName, isRequired: isEntry ? false : p.isRequired, type: p.type }))
      if (isEntry) outputs = ports
      else inputs = ports
    } else {
      const parentNode = frame.nodes.find((n) => n.id === frame.parentNodeId)
      const parentBlockType = blockTypes.find((bt) => bt.blockTypeId === parentNode?.blockTypeId)
      if (parentBlockType) {
        if (isEntry) {
          const declared = parentBlockType.subCanvasInputs?.[frame.subCanvasName] ?? []
          const ownerInputs = parentBlockType.hasDynamicPorts && parentNode
            ? parseDynamicPorts(parentNode.configuration['InputPorts'] as string)
            : (parentBlockType.inputs ?? [])
          const forwarded = (parentNode?.passthrough ?? [])
            .filter((name) => !declared.some((p) => p.name === name))
            .map((name): DrawnPort => ({ name, displayName: name, isRequired: false, type: ownerInputs.find((i) => i.name === name)?.type }))
          outputs = [...declared, ...forwarded].map((p) => ({ name: p.name, displayName: p.displayName, isRequired: p.isRequired, type: p.type }))
        } else {
          const declared = (parentBlockType.subCanvasOutputs?.[frame.subCanvasName] ?? []).map((p) => ({
            name: p.name, displayName: p.displayName, isRequired: p.isRequired, type: p.type,
          }))
          const fromParent = new Map<string, PortInfo>()
          for (const port of parentNode?.outputPorts ?? []) {
            const sourceKey = port.sources?.[frame.subCanvasName]
            if (!sourceKey) continue
            if (declared.some((d) => d.name === sourceKey)) continue
            if (!fromParent.has(sourceKey)) {
              fromParent.set(sourceKey, { name: sourceKey, displayName: sourceKey, isRequired: false, type: port.type })
            }
          }
          inputs = [...declared, ...fromParent.values()]
        }
      }
    }

    if (isEntry && outputs.length === 0) {
      outputs = [{ name: WORKFLOW_START_ANCHOR_PORT_NAME, displayName: 'Start', isRequired: false, type: 'Flow' }]
    }
  } else if (blockType) {
    const declaredInputs = (
      blockType.hasDynamicPorts
        ? parseDynamicPorts(node.configuration['InputPorts'] as string)
        : (blockType.inputs ?? []).map((i) => ({ name: i.name, displayName: i.displayName, isRequired: i.isRequired, type: i.type }))
    )
      .filter((i) => !hostPorts.isInputHidden(i.name))
      .map((i) => ({ ...i, type: hostPorts.inputType(i) }))

    const staticOutputs = (blockType.outputs ?? []).map((o) => ({ name: o.name, displayName: o.displayName, isRequired: false, type: o.type }))
    const declaredOutputs = (blockType.supportsDeclaredOutputs ? (node.outputPorts ?? []) : [])
      .filter((p) => !staticOutputs.some((s) => s.name === p.name))
      .map((p) => ({ name: p.name, displayName: p.displayName || p.name, isRequired: false, type: declaredOutputTerm(blockType, p) }))
    const baseOutputs = blockType.hasDynamicPorts
      ? parseDynamicPorts(node.configuration['OutputPorts'] as string)
      : [...staticOutputs, ...declaredOutputs]

    const extraInputs = hostPorts.hostInputs.filter((d) => !declaredInputs.some((i) => i.name === d.name))
    const passthroughNames = node.passthrough ?? []

    const virtualInputs = passthroughNames
      .filter((name) => !declaredInputs.some((i) => i.name === name) && !extraInputs.some((c) => c.name === name))
      .map((name) => ({ name, displayName: name, isRequired: false }))

    inputs = [...declaredInputs, ...extraInputs, ...virtualInputs]

    const extraOutputs = hostPorts.hostOutputs.filter((d) => !baseOutputs.some((o) => o.name === d.name))
    const showErrorPort = addsErrorOutput(node, [...baseOutputs, ...extraOutputs])
    const virtualOutputs = passthroughNames
      .filter((name) => !baseOutputs.some((o) => o.name === name) && !extraOutputs.some((c) => c.name === name) && !(showErrorPort && name === ERROR_PORT_NAME))
      .map((name) => {
        const matching = inputs.find((i) => i.name === name)
        return { name, displayName: matching?.displayName ?? name, isRequired: false, type: matching?.type }
      })

    const errorPort = showErrorPort ? [{ name: ERROR_PORT_NAME, displayName: 'Error', isRequired: false, type: ERROR_OUTPUT_TYPE }] : []

    outputs = [...baseOutputs, ...extraOutputs, ...virtualOutputs, ...errorPort]
  } else {
    const fed = new Set<string>()
    const offered = new Set<string>()
    for (const edge of edges) {
      if (edge.targetNodeId === node.id) for (const name of Object.keys(edge.mappings)) fed.add(name)
      if (edge.sourceNodeId !== node.id) continue
      for (const mapping of Object.values(edge.mappings)) {
        const read = readMapping(mapping)
        if (!read.literal && read.source !== '') offered.add(read.source)
      }
    }
    inputs = [...fed].map((name) => ({ name, displayName: name, isRequired: false }))
    outputs = [...offered].map((name) => ({ name, displayName: name, isRequired: false }))
  }
  const connectable = !readOnly && unavailable === undefined

  const inputTerms = inputs.map((port) => termOf('inputs', port))
  const outputTerms = outputs.map((port) => termOf('outputs', port))
  const inputNames = inputs.map((port) => `${port.displayName}${port.isRequired ? '*' : ''}`)
  const outputNames = outputs.map((port) => port.displayName)
  const inputLabels = inputNames.map((name, index) => `${name}${typeSuffix(inputTerms[index])}`)
  const outputLabels = outputNames.map((name, index) => `${name}${typeSuffix(outputTerms[index])}`)
  const labelText = (labels: string[], names: string[], index: number) => {
    const full = labels[index]
    const name = names[index]
    return full === undefined || name === undefined ? undefined : { full, name }
  }
  const labelRows = Array.from({ length: Math.max(inputs.length, outputs.length) }, (_, index) =>
    layoutFittedPortLabelRow(labelText(inputLabels, inputNames, index), labelText(outputLabels, outputNames, index), NODE_WIDTH))

  const displayName = isEntry ? entryNodeLabel : isExit ? exitNodeLabel : (unavailable?.title ?? blockType?.displayName ?? '')
  const subCanvasNames = isSynthetic ? [] : (blockType?.subCanvasNames ?? [])
  const hasSubCanvases = subCanvasNames.length > 0

  const portsHeight = Math.max(1, Math.max(inputs.length, outputs.length)) * NODE_ITEM_HEIGHT + 8
  const buttonsHeight = hasSubCanvases
    ? BUTTONS_PADDING_TOP + subCanvasNames.length * BUTTON_HEIGHT + (subCanvasNames.length - 1) * BUTTON_GAP + 8
    : 0
  const reasonHeight = unavailable === undefined ? 0 : NODE_ITEM_HEIGHT
  const nodeHeight = NODE_HEADER_HEIGHT + portsHeight + buttonsHeight + reasonHeight
  const portsEndY = NODE_HEADER_HEIGHT + portsHeight

  const headerColor = isSelected ? c.selected : isEntry ? c.headerInput : isExit ? c.headerOutput : c.nodeHeader
  const headerTextColor = isSelected || isSynthetic ? c.nodeFill : c.text

  const diffStroke = diffHighlight === 'added' ? c.ok : diffHighlight === 'removed' ? c.danger : diffHighlight === 'modified' ? c.warn : null
  const runStroke = highlight?.status === 'running' ? c.warn : highlight?.status === 'success' ? c.ok : highlight?.status === 'error' ? c.danger : null
  const outlineStroke =
    (isSelected ? c.selected : null) ?? diffStroke ?? runStroke ?? (unavailable ? c.danger : isEntry ? c.headerInput : isExit ? c.headerOutput : c.nodeStroke)
  const outlineStrokeWidth = isSelected || highlight?.status || diffHighlight ? 2 : isSynthetic || unavailable ? 2 : 1

  const tooltip = [
    unavailable?.reason ?? null,
    highlight?.inputsJson ? `Inputs:\n${highlight.inputsJson}` : null,
    highlight?.outputsJson ? `Outputs:\n${highlight.outputsJson}` : null,
  ]
    .filter(Boolean)
    .join('\n\n')
  const labelHint = (label: string | undefined, display: PortTermDisplay | undefined) =>
    [[label, ...(display?.findings ?? [])].join('\n'), tooltip].filter(Boolean).join('\n\n')

  const portY = (index: number) => NODE_HEADER_HEIGHT + (index + 0.5) * NODE_ITEM_HEIGHT + 4

  const bodyPointer = {
    onClick: (event: ReactMouseEvent) => {
      event.stopPropagation()
      onSelect(node.id)
    },
    onMouseDown: (event: ReactMouseEvent) => {
      if (event.button !== 0) return
      event.preventDefault()
      onDragStart(event, node.id)
    },
    onMouseUp: (event: ReactMouseEvent) => {
      if (isConnecting && unavailable === undefined) {
        event.stopPropagation()
        onConnectionEnd(node.id)
      }
    },
  }

  return (
    <g
      transform={`translate(${node.position.x}, ${node.position.y})`} data-testid={`workflow-node-${node.blockTypeId}`} data-node-id={node.id}
      data-unavailable={unavailable ? 'true' : undefined}
    >
      {tooltip && <title>{tooltip}</title>}
      <defs>
        {labelRows.slice(0, inputs.length).map((row, index) => (
          <clipPath key={`in-${index}`} id={`port-in-clip-${clipId}-${index}`}>
            <rect x={0} y={0} width={row.inputEnd} height={nodeHeight} />
          </clipPath>
        ))}
        <clipPath id={`node-clip-${clipId}`}>
          <rect x={0} y={0} width={NODE_WIDTH - 12} height={nodeHeight} />
        </clipPath>
        {labelRows.slice(0, outputs.length).map((row, index) => (
          <clipPath key={`out-${index}`} id={`port-out-clip-${clipId}-${index}`}>
            <rect x={row.outputStart} y={0} width={NODE_WIDTH - row.outputStart} height={nodeHeight} />
          </clipPath>
        ))}
      </defs>

      <rect
        data-testid={`workflow-node-body-${node.id}`}
        width={NODE_WIDTH}
        height={nodeHeight}
        rx={8}
        ry={8}
        fill={c.nodeFill}
        stroke={outlineStroke}
        strokeWidth={outlineStrokeWidth}
        strokeDasharray={unavailable ? '6 4' : undefined}
        {...bodyPointer}
        style={{ cursor: 'grab' }}
      />

      {isFlashing && (
        <rect
          key={flashKey}
          data-testid="node-flash-ring"
          x={-5} y={-5} width={NODE_WIDTH + 10} height={nodeHeight + 10} rx={12} ry={12}
          fill="none" stroke={c.info} strokeWidth={3} pointerEvents="none"
        >
          <animate attributeName="opacity" values="0;1;0;1;0;1;0" dur="1.1s" repeatCount="1" fill="freeze" />
        </rect>
      )}

      <rect width={NODE_WIDTH} height={NODE_HEADER_HEIGHT} rx={8} ry={8} fill={headerColor} style={{ pointerEvents: 'none' }} />
      <rect y={NODE_HEADER_HEIGHT - 8} width={NODE_WIDTH} height={8} fill={headerColor} style={{ pointerEvents: 'none' }} />
      <text x={12} y={26} fill={headerTextColor} style={{ fontSize: 13, fontWeight: 'bold', pointerEvents: 'none' }}>
        {unavailable ? `⚠ ${displayName}` : displayName}
      </text>

      {unavailable && (
        <text
          x={12} y={portsEndY + buttonsHeight + NODE_ITEM_HEIGHT / 2 + 4}
          clipPath={`url(#node-clip-${clipId})`}
          data-testid={`workflow-node-unavailable-${node.id}`}
          fill={c.danger} style={{ fontSize: 11, pointerEvents: 'none' }}
        >
          {unavailable.reason}
        </text>
      )}

      {hasSubCanvases && (
        <g style={{ pointerEvents: 'none' }}>
          <circle cx={NODE_WIDTH - 14} cy={14} r={10} fill={c.warn} />
          <text x={NODE_WIDTH - 14} y={18} textAnchor="middle" fill={c.nodeFill} style={{ fontSize: 10, fontWeight: 'bold' }}>⊡</text>
        </g>
      )}

      {(isBreakpoint || isPausedHere) && (
        <circle
          cx={-6} cy={14} r={6}
          fill={isPausedHere ? c.warn : c.danger} stroke={c.nodeFill} strokeWidth={1.5}
          style={{ pointerEvents: 'none' }}
          aria-label={isPausedHere ? 'Paused here' : 'Breakpoint'}
        />
      )}

      {inputs.map((input, index) => (
        <g key={input.name}>
          <circle
            cx={0} cy={portY(index)} r={PORT_RADIUS}
            data-testid={`workflow-port-${node.id}-input-${input.name}`}
            aria-label={`${displayName} input ${input.displayName}`}
            fill={c.nodeFill} stroke={isExit ? c.headerOutput : c.nodeStroke} strokeWidth={PORT_STROKE_WIDTH}
            onMouseUp={(event) => {
              if (!connectable || !isConnecting || event.button !== 0) return
              event.stopPropagation()
              onConnectionEnd(node.id)
            }}
            style={{ cursor: 'crosshair' }}
          />
          <g {...bodyPointer} style={{ cursor: 'grab' }}>
            <title>{labelHint(inputLabels[index], inputTerms[index])}</title>
            <text
              x={PORT_LABEL_INSET} y={portY(index) + 5}
              clipPath={`url(#port-in-clip-${clipId}-${index})`}
              data-testid={`workflow-port-label-${node.id}-input-${input.name}`}
              {...termAttributes(inputTerms[index])}
              fill={inputTerms[index]?.knowledge === 'refused' ? c.danger : c.textDim}
              style={{ fontSize: PORT_LABEL_FONT_SIZE, ...(inputTerms[index]?.stale === true ? { fontStyle: 'italic', opacity: 0.7 } : {}) }}
            >
              {labelRows[index]?.input}
            </text>
          </g>
        </g>
      ))}

      {outputs.map((output, index) => {
        const isErrorPort = output.name === ERROR_PORT_NAME
        return (
          <g key={output.name}>
            <circle
              cx={NODE_WIDTH} cy={portY(index)} r={PORT_RADIUS}
              data-testid={`workflow-port-${node.id}-output-${output.name}`}
              aria-label={`${displayName} output ${output.displayName}`}
              fill={c.nodeFill} stroke={isErrorPort ? c.danger : isEntry ? c.headerInput : c.nodeStroke} strokeWidth={PORT_STROKE_WIDTH}
              onMouseDown={(event) => {
                if (!connectable || event.button !== 0) return
                event.stopPropagation()
                event.preventDefault()
                onConnectionStart(node.id, node.position.x + NODE_WIDTH, node.position.y + portY(index))
              }}
              style={{ cursor: 'crosshair' }}
            />
            <g {...bodyPointer} style={{ cursor: 'grab' }}>
              <title>{labelHint(outputLabels[index], outputTerms[index])}</title>
              <text
                x={NODE_WIDTH - PORT_LABEL_INSET} y={portY(index) + 5} textAnchor="end"
                clipPath={`url(#port-out-clip-${clipId}-${index})`}
                data-testid={`workflow-port-label-${node.id}-output-${output.name}`}
                {...termAttributes(outputTerms[index])}
                fill={isErrorPort || outputTerms[index]?.knowledge === 'refused' ? c.danger : c.textDim}
                style={{ fontSize: PORT_LABEL_FONT_SIZE, ...(outputTerms[index]?.stale === true ? { fontStyle: 'italic', opacity: 0.7 } : {}) }}
              >
                {labelRows[index]?.output}
              </text>
            </g>
          </g>
        )
      })}

      {hasSubCanvases && (
        <>
          <line x1={8} y1={portsEndY} x2={NODE_WIDTH - 8} y2={portsEndY} stroke={c.nodeStroke} strokeWidth={1} style={{ pointerEvents: 'none' }} />
          {subCanvasNames.map((canvasName, index) => {
            const buttonY = portsEndY + BUTTONS_PADDING_TOP + index * (BUTTON_HEIGHT + BUTTON_GAP)
            return (
              <g key={canvasName} style={{ cursor: 'pointer' }}>
                <rect x={8} y={buttonY} width={NODE_WIDTH - 16} height={BUTTON_HEIGHT} rx={4} ry={4} fill={c.infoBg} stroke={c.infoBorder} strokeWidth={1} style={{ pointerEvents: 'none' }} />
                <text x={NODE_WIDTH / 2} y={buttonY + BUTTON_HEIGHT / 2 + 5} textAnchor="middle" fill={c.info} style={{ fontSize: 11, fontWeight: 500, pointerEvents: 'none' }}>
                  ▶ Enter: {canvasName}
                </text>
                <rect
                  x={8} y={buttonY} width={NODE_WIDTH - 16} height={BUTTON_HEIGHT} rx={4} ry={4} fill="transparent"
                  data-subcanvas-entry={`${node.id}:${canvasName}`}
                  onClick={(event) => {
                    event.stopPropagation()
                    enterSubCanvas(node.id, canvasName)
                  }}
                />
              </g>
            )
          })}
        </>
      )}
    </g>
  )
})
