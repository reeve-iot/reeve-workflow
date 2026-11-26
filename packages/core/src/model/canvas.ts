import type { WorkflowEdge, WorkflowNode } from './workflow.js'

/** One entry on the sub-canvas navigation stack: the canvas the editor descended FROM, plus the node and
 * slot it descended through. */
export interface CanvasFrame {
  parentNodeId: string
  subCanvasName: string
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
}
