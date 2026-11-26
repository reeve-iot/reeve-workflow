import type { MouseEvent } from 'react'
import { useMemo } from 'react'
import type { WorkflowEdge, WorkflowNode } from '@reeve-iot/workflow-core'
import { useEditorColors } from '../theme/editorColors.js'
import { NODE_WIDTH, portAnchorY } from './geometry.js'

const WIRE_HIT_WIDTH_PX = 18

export interface ConnectionProps {
  edge: WorkflowEdge
  nodes: WorkflowNode[]
  isSelected: boolean
  onClick: (event: MouseEvent) => void
}

/** One wire between two nodes, drawn as a horizontal cubic so it leaves and enters sideways. */
export function Connection({ edge, nodes, isSelected, onClick }: ConnectionProps) {
  const colors = useEditorColors()
  const source = useMemo(() => nodes.find((n) => n.id === edge.sourceNodeId), [nodes, edge.sourceNodeId])
  const target = useMemo(() => nodes.find((n) => n.id === edge.targetNodeId), [nodes, edge.targetNodeId])

  const path = useMemo(() => {
    if (!source || !target) return ''
    const sourceX = source.position.x + NODE_WIDTH
    const sourceY = portAnchorY(source.position.y)
    const targetX = target.position.x
    const targetY = portAnchorY(target.position.y)
    const dx = Math.abs(targetX - sourceX) * 0.5
    return `M ${sourceX} ${sourceY} C ${sourceX + dx} ${sourceY}, ${targetX - dx} ${targetY}, ${targetX} ${targetY}`
  }, [source, target])

  return (
    <g
      onClick={onClick}
      data-testid={`workflow-edge-${edge.sourceNodeId}-${edge.targetNodeId}`}
      style={{ cursor: 'pointer' }}
    >
      <path d={path} fill="none" stroke={isSelected ? colors.selected : colors.edge} strokeWidth={isSelected ? 4 : 2} pointerEvents="visibleStroke" />
      <path
        data-testid={`workflow-edge-hit-${edge.sourceNodeId}-${edge.targetNodeId}`}
        d={path}
        fill="none"
        stroke="transparent"
        strokeWidth={WIRE_HIT_WIDTH_PX}
        vectorEffect="non-scaling-stroke"
        pointerEvents="stroke"
      />
    </g>
  )
}
