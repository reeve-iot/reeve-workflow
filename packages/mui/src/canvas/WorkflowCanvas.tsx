import type { DragEvent, KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent } from 'react'
import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { Box } from '@mui/material'
import { computeCenteringPan, describeStoredDocumentFault, useWorkflowEditorStore, useWorkflowEditorStoreApi } from '@reeve-iot/workflow-core'
import { useEditorColors } from '../theme/editorColors.js'
import { BLOCK_DRAG_MIME } from '../palette/BlockPalette.js'
import { Connection } from './Connection.js'
import type { DescribeUnavailableBlock } from './unavailableBlock.js'
import { BlockNode } from './BlockNode.js'
import type { BlockNodeProps } from './BlockNode.js'
import { GraphTopologyContext, useGraphTopology } from './graphTopology.js'
import type { NodeDiffKind, NodeHighlight } from './highlights.js'
import { MIN_CENTRING_WIDTH, NODE_CONFIG_PANEL_WIDTH, NODE_HEIGHT, NODE_WIDTH } from './geometry.js'
import { NODE_RIGHT_OVERHANG } from './portGeometry.js'

const PLACEMENT_STACK_STEP = NODE_HEIGHT + 40

function usableCanvasWidth(width: number, panelWillOpen: boolean): number {
  return Math.max(width - (panelWillOpen ? NODE_CONFIG_PANEL_WIDTH : 0), MIN_CENTRING_WIDTH)
}

type NodeHandlers = Pick<BlockNodeProps, 'onDragStart' | 'onConnectionStart' | 'onConnectionEnd' | 'onSelect'>

export interface WorkflowCanvasProps {
  onCanvasClick: () => void
  highlights?: NodeHighlight[]
  diffHighlights?: Record<string, NodeDiffKind>
  readOnly?: boolean
  /** Nodes a host debugger has a breakpoint on. */
  breakpointNodeIds?: string[]
  /** Node a host debugger is currently paused at. */
  pausedNodeId?: string | null
  /** The host's wording for a node whose block type is not in the catalogue. Such a node is drawn as a placeholder, kept
   * where it stands with its wires; without this the placeholder says the block type is not available on this host. */
  describeUnavailableBlock?: DescribeUnavailableBlock
}

export function WorkflowCanvas({
  onCanvasClick,
  highlights,
  diffHighlights,
  readOnly,
  breakpointNodeIds,
  pausedNodeId,
  describeUnavailableBlock,
}: WorkflowCanvasProps) {
  const colors = useEditorColors()
  const containerRef = useRef<HTMLDivElement>(null)
  const svgRef = useRef<SVGSVGElement>(null)
  const storeApi = useWorkflowEditorStoreApi()
  const gridPatternId = `reeve-workflow-grid-${useId().replace(/[^A-Za-z0-9_-]/g, '')}`

  const nodes = useWorkflowEditorStore((s) => s.nodes)
  const edges = useWorkflowEditorStore((s) => s.edges)
  const pan = useWorkflowEditorStore((s) => s.pan)
  const zoom = useWorkflowEditorStore((s) => s.zoom)
  const setPan = useWorkflowEditorStore((s) => s.setPan)
  const addNode = useWorkflowEditorStore((s) => s.addNode)
  const updateNodePosition = useWorkflowEditorStore((s) => s.updateNodePosition)
  const setSelectedNode = useWorkflowEditorStore((s) => s.setSelectedNode)
  const setSelectedEdge = useWorkflowEditorStore((s) => s.setSelectedEdge)
  const addEdge = useWorkflowEditorStore((s) => s.addEdge)
  const setPaletteDragActive = useWorkflowEditorStore((s) => s.setPaletteDragActive)
  const removeNode = useWorkflowEditorStore((s) => s.removeNode)
  const removeEdge = useWorkflowEditorStore((s) => s.removeEdge)
  const selectedNodeId = useWorkflowEditorStore((s) => s.selectedNodeId)
  const selectedEdgeId = useWorkflowEditorStore((s) => s.selectedEdgeId)
  const quickAddBlockTypeId = useWorkflowEditorStore((s) => s.quickAddBlockTypeId)
  const clearQuickAddBlock = useWorkflowEditorStore((s) => s.clearQuickAddBlock)
  const flashSeq = useWorkflowEditorStore((s) => s.flashSeq)
  const loadedDocumentFault = useWorkflowEditorStore((s) => s.loadedDocumentFault)
  const flashNodeId = useWorkflowEditorStore((s) => s.flashNodeId)
  const topology = useGraphTopology()

  const frameRef = useRef<number | null>(null)
  const frameWorkRef = useRef<(() => void) | null>(null)
  const pendingMoveRef = useRef({ dx: 0, dy: 0 })
  const [isPanning, setIsPanning] = useState(false)
  const [dragNodeId, setDragNodeId] = useState<string | null>(null)
  const [dragOffset, setDragOffset] = useState({ x: 0, y: 0 })
  const [pendingConnection, setPendingConnection] = useState<{
    sourceNodeId: string
    sourceX: number
    sourceY: number
    currentX: number
    currentY: number
  } | null>(null)

  const findPlacement = useCallback(
    (baseX: number, baseY: number, stepX = 36, stepY = 36) => {
      const padding = 20
      const collides = (x: number, y: number) =>
        nodes.some(
          (node) =>
            x - padding < node.position.x + NODE_WIDTH &&
            x + NODE_WIDTH + padding > node.position.x &&
            y - padding < node.position.y + NODE_HEIGHT &&
            y + NODE_HEIGHT + padding > node.position.y,
        )
      if (!collides(baseX, baseY)) return { x: baseX, y: baseY }
      for (let i = 1; i <= 20; i += 1) {
        const x = baseX + i * stepX
        const y = baseY + i * stepY
        if (!collides(x, y)) return { x, y }
      }
      return { x: baseX + stepX, y: baseY + stepY }
    },
    [nodes],
  )

  const toSvgLocalPoint = useCallback((clientX: number, clientY: number) => {
    const svg = svgRef.current
    if (typeof svg?.getScreenCTM !== 'function' || typeof svg.createSVGPoint !== 'function') return { x: 0, y: 0 }
    const ctm = svg.getScreenCTM()
    if (!ctm) return { x: 0, y: 0 }
    const point = svg.createSVGPoint()
    point.x = clientX
    point.y = clientY
    const { x, y } = point.matrixTransform(ctm.inverse())
    return { x, y }
  }, [])

  const toWorldPoint = useCallback((clientX: number, clientY: number) => {
    const local = toSvgLocalPoint(clientX, clientY)
    const { pan: currentPan, zoom: currentZoom } = storeApi.getState()
    return { x: (local.x - currentPan.x) / currentZoom, y: (local.y - currentPan.y) / currentZoom }
  }, [storeApi, toSvgLocalPoint])

  const pendingSourceNodeId = pendingConnection?.sourceNodeId ?? null
  const onNodeDragStart = useCallback<NodeHandlers['onDragStart']>(
    (event, nodeId) => {
      if (readOnly) return
      const node = storeApi.getState().nodes.find((n) => n.id === nodeId)
      if (!node) return
      const point = toWorldPoint(event.clientX, event.clientY)
      setDragOffset({ x: point.x - node.position.x, y: point.y - node.position.y })
      setDragNodeId(nodeId)
      setSelectedNode(nodeId)
    },
    [readOnly, storeApi, toWorldPoint, setSelectedNode],
  )
  const onConnectionStart = useCallback<NodeHandlers['onConnectionStart']>(
    (sourceNodeId, x, y) => setPendingConnection({ sourceNodeId, sourceX: x, sourceY: y, currentX: x, currentY: y }),
    [],
  )
  const onConnectionEnd = useCallback<NodeHandlers['onConnectionEnd']>(
    (targetNodeId) => {
      if (pendingSourceNodeId && pendingSourceNodeId !== targetNodeId) addEdge(pendingSourceNodeId, targetNodeId)
      setPendingConnection(null)
    },
    [pendingSourceNodeId, addEdge],
  )

  const endGesture = useCallback(() => {
    if (frameRef.current !== null) {
      cancelAnimationFrame(frameRef.current)
      frameRef.current = null
    }
    const unrun = frameWorkRef.current
    frameWorkRef.current = null
    unrun?.()
    pendingMoveRef.current = { dx: 0, dy: 0 }
    setIsPanning(false)
    setDragNodeId(null)
    setPendingConnection(null)
  }, [])

  const onMouseDown = (event: ReactMouseEvent) => {
    event.preventDefault()
    if (event.button === 0) {
      if (event.target === svgRef.current) onCanvasClick()
    } else if (event.button === 1 || event.button === 2) {
      setIsPanning(true)
    }
  }

  const onMouseMove = (event: ReactMouseEvent) => {
    if (frameRef.current !== null) cancelAnimationFrame(frameRef.current)
    const pending = pendingMoveRef.current
    pending.dx += event.movementX
    pending.dy += event.movementY
    const { clientX, clientY } = event
    const apply = () => {
      const { dx, dy } = pending
      pending.dx = 0
      pending.dy = 0
      if (isPanning) {
        const current = storeApi.getState().pan
        setPan({ x: current.x + dx, y: current.y + dy })
        return
      }
      if (dragNodeId) {
        const point = toWorldPoint(clientX, clientY)
        updateNodePosition(dragNodeId, { x: point.x - dragOffset.x, y: point.y - dragOffset.y })
        return
      }
      if (pendingConnection) {
        const point = toWorldPoint(clientX, clientY)
        setPendingConnection((prev) => (prev ? { ...prev, currentX: point.x, currentY: point.y } : null))
      }
    }
    frameWorkRef.current = apply
    frameRef.current = requestAnimationFrame(() => {
      frameRef.current = null
      frameWorkRef.current = null
      apply()
    })
  }

  useEffect(() => {
    const container = containerRef.current
    if (!container) return
    const onWheel = (event: globalThis.WheelEvent) => {
      event.preventDefault()
      const { zoom: current, pan: currentPan } = storeApi.getState()
      const next = Math.max(0.1, Math.min(event.deltaY > 0 ? current / 1.1 : current * 1.1, 5))
      if (next === current) return
      const rect = container.getBoundingClientRect()
      const cursorX = event.clientX - rect.left
      const cursorY = event.clientY - rect.top
      const ratio = next / current
      storeApi.setState({
        zoom: next,
        pan: { x: cursorX - (cursorX - currentPan.x) * ratio, y: cursorY - (cursorY - currentPan.y) * ratio },
      })
    }
    container.addEventListener('wheel', onWheel, { passive: false })
    return () => container.removeEventListener('wheel', onWheel)
  }, [storeApi])

  const onDrop = (event: DragEvent) => {
    event.preventDefault()
    if (readOnly) return
    const blockTypeId = event.dataTransfer.getData(BLOCK_DRAG_MIME)
    if (blockTypeId) {
      const local = toSvgLocalPoint(event.clientX, event.clientY)
      const { pan: currentPan, zoom: currentZoom, selectedNodeId: currentSelectedNodeId } = storeApi.getState()
      const rect = containerRef.current?.getBoundingClientRect()
      const remainingWidth = (rect?.width ?? 0) - (currentSelectedNodeId === null ? NODE_CONFIG_PANEL_WIDTH : 0)
      const screenLeft = Math.max(NODE_RIGHT_OVERHANG * currentZoom, Math.min(local.x - NODE_WIDTH * currentZoom / 2, remainingWidth - (NODE_WIDTH + NODE_RIGHT_OVERHANG) * currentZoom))
      const x = (screenLeft - currentPan.x) / currentZoom
      const y = (local.y - currentPan.y) / currentZoom - 40
      addNode(blockTypeId, findPlacement(x, y, 0, PLACEMENT_STACK_STEP))
    }
    setPaletteDragActive(false)
  }

  useEffect(
    () => () => {
      if (frameRef.current !== null) cancelAnimationFrame(frameRef.current)
    },
    [],
  )

  useEffect(() => {
    if (!quickAddBlockTypeId || !containerRef.current) return
    const rect = containerRef.current.getBoundingClientRect()
    const panelOpen = storeApi.getState().selectedNodeId !== null
    const usableWidth = usableCanvasWidth(rect.width, !panelOpen)
    const local = toSvgLocalPoint(rect.left + usableWidth / 2, rect.top + rect.height / 2)
    const { pan: currentPan, zoom: currentZoom } = storeApi.getState()
    addNode(quickAddBlockTypeId, findPlacement((local.x - currentPan.x) / currentZoom - NODE_WIDTH / 2, (local.y - currentPan.y) / currentZoom - 40, 0, PLACEMENT_STACK_STEP))
    clearQuickAddBlock()
  }, [quickAddBlockTypeId, addNode, clearQuickAddBlock, findPlacement, toSvgLocalPoint, storeApi])

  useEffect(() => {
    if (flashSeq === 0) return
    const state = storeApi.getState()
    const node = state.nodes.find((n) => n.id === state.flashNodeId)
    const rect = containerRef.current?.getBoundingClientRect()
    if (!node || !rect || rect.width === 0 || rect.height === 0) return
    const usableWidth = usableCanvasWidth(rect.width, state.selectedNodeId === null)
    setPan(
      computeCenteringPan(node.position, { width: NODE_WIDTH, height: NODE_HEIGHT }, { width: usableWidth, height: rect.height }, state.zoom),
    )
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [flashSeq])

  useEffect(() => {
    if (!pendingConnection && !isPanning && !dragNodeId) return
    window.addEventListener('mouseup', endGesture)
    return () => window.removeEventListener('mouseup', endGesture)
  }, [pendingConnection, isPanning, dragNodeId, endGesture])

  const onKeyDown = (event: ReactKeyboardEvent) => {
    const target = event.target as HTMLElement
    if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable) return
    if (event.key !== 'Delete' && event.key !== 'Backspace') return
    if (readOnly) return
    if (selectedNodeId) removeNode(selectedNodeId)
    if (selectedEdgeId) removeEdge(selectedEdgeId)
  }

  return (
    <Box
      ref={containerRef}
      data-testid="workflow-canvas"
      tabIndex={0}
      sx={{
        position: 'relative', width: '100%', height: '100%', overflow: 'hidden', cursor: isPanning ? 'grabbing' : 'crosshair', userSelect: 'none',
        outline: 'none', '&:focus-visible': { boxShadow: `inset 0 0 0 2px ${colors.selected}` },
      }}
      onMouseDownCapture={() => containerRef.current?.focus({ preventScroll: true })}
      onMouseDown={onMouseDown}
      onMouseMove={onMouseMove}
      onMouseUp={endGesture}
      onKeyDown={onKeyDown}
      onDrop={onDrop}
      onDragOver={(event) => {
        event.preventDefault()
        event.dataTransfer.dropEffect = 'move'
      }}
      onContextMenu={(event) => event.preventDefault()}
    >
      {loadedDocumentFault && (
        <Box role="alert" sx={{ position: 'absolute', top: 8, left: 8, right: 8, p: 1, bgcolor: 'error.main', color: 'error.contrastText', borderRadius: 1 }}>
          {`${loadedDocumentFault.rule === 'undrawable' ? 'The editor cannot draw this workflow' : 'The stored workflow cannot be read'}, so it is shown empty and cannot be saved: ${describeStoredDocumentFault(loadedDocumentFault)}.`}
        </Box>
      )}
      <GraphTopologyContext.Provider value={topology}>
      <svg ref={svgRef} width="100%" height="100%" style={{ pointerEvents: 'all', background: colors.canvasBg, userSelect: 'none' }}>
        <defs>
          <pattern id={gridPatternId} width="40" height="40" patternUnits="userSpaceOnUse">
            <path d="M 40 0 L 0 0 0 40" fill="none" stroke={colors.grid} strokeWidth="0.5" />
          </pattern>
        </defs>

        <g transform={`translate(${pan.x}, ${pan.y}) scale(${zoom})`}>
          <rect x={-10000} y={-10000} width={20000} height={20000} fill={`url(#${gridPatternId})`} style={{ pointerEvents: 'none' }} />

          {edges.map((edge) => (
            <Connection
              key={edge.id}
              edge={edge}
              nodes={nodes}
              isSelected={selectedEdgeId === edge.id}
              onClick={(event) => {
                event.stopPropagation()
                setSelectedEdge(edge.id)
              }}
            />
          ))}

          {pendingConnection && (
            <line
              x1={pendingConnection.sourceX}
              y1={pendingConnection.sourceY}
              x2={pendingConnection.currentX}
              y2={pendingConnection.currentY}
              stroke={colors.selected}
              strokeWidth="2"
              strokeDasharray="5,5"
              style={{ pointerEvents: 'none' }}
            />
          )}

          {[...nodes]
            .sort((a, b) => (a.id === selectedNodeId ? 1 : b.id === selectedNodeId ? -1 : 0))
            .map((node) => {
              const highlight = highlights?.find((h) => h.nodeId === node.id)
              return (
                <BlockNode
                  key={node.id}
                  node={node}
                  isSelected={selectedNodeId === node.id}
                  isConnecting={!!pendingConnection}
                  highlight={highlight}
                  diffHighlight={diffHighlights?.[node.id]}
                  readOnly={readOnly}
                  isBreakpoint={breakpointNodeIds?.includes(node.id)}
                  isPausedHere={pausedNodeId === node.id}
                  isFlashing={flashNodeId === node.id}
                  flashKey={flashSeq}
                  onDragStart={onNodeDragStart}
                  onConnectionStart={onConnectionStart}
                  onConnectionEnd={onConnectionEnd}
                  onSelect={setSelectedNode}
                  describeUnavailableBlock={describeUnavailableBlock}
                />
              )
            })}
        </g>
      </svg>
      </GraphTopologyContext.Provider>
    </Box>
  )
}
