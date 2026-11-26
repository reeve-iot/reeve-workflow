import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowDefinition, WorkflowNode } from '../model/workflow.js'
import {
  WORKFLOW_INPUT_BLOCK_TYPE_ID,
  WORKFLOW_INPUT_NODE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID,
  WORKFLOW_OUTPUT_NODE_ID,
} from '../model/workflow.js'

const own = <T>(record: Record<string, T> | undefined, key: string): T | undefined =>
  record && Object.hasOwn(record, key) ? record[key] : undefined

export const entryNode = (): WorkflowNode =>
  ({ id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 50, y: 100 }, configuration: {} })

export const exitNode = (): WorkflowNode =>
  ({ id: WORKFLOW_OUTPUT_NODE_ID, blockTypeId: WORKFLOW_OUTPUT_BLOCK_TYPE_ID, position: { x: 800, y: 100 }, configuration: {} })

/** Whether a branch of `node` holds an exit node: its block declares outputs for it, or a declared output of the node
 * takes a key from it. */
export function writesExit(node: WorkflowNode | undefined, blockType: BlockTypeDescriptor, branch: string): boolean {
  return (own(blockType.subCanvasOutputs, branch) ?? []).length > 0 || (node?.outputPorts ?? []).some((p) => !!own(p.sources, branch))
}

/** The branch the editor writes when the author opens `branch` and draws nothing. The run, the compiler and the version
 * diff read a declared branch the document lacks as this one (DeclaredBranches.cs). */
export function emptyBranch(node: WorkflowNode | undefined, blockType: BlockTypeDescriptor, branch: string): WorkflowDefinition {
  return { nodes: writesExit(node, blockType, branch) ? [entryNode(), exitNode()] : [entryNode()], edges: [] }
}

/** `definition` with each declared branch a node lacks written as `emptyBranch`, after the node's own; `definition`
 * itself where none is lacking. Nothing in it is changed. */
export function withDeclaredBranches(definition: WorkflowDefinition, blockTypes: readonly BlockTypeDescriptor[]): WorkflowDefinition {
  let changed = false
  const nodes = definition.nodes.map((node) => {
    const filled = filledNode(node, blockTypes)
    if (filled !== node) changed = true
    return filled
  })
  return changed ? { ...definition, nodes } : definition
}

function filledNode(node: WorkflowNode, blockTypes: readonly BlockTypeDescriptor[]): WorkflowNode {
  let branches: Record<string, WorkflowDefinition> | undefined
  for (const [name, canvas] of Object.entries(node.subCanvases ?? {})) {
    const below = withDeclaredBranches(canvas, blockTypes)
    if (below !== canvas) (branches ??= { ...node.subCanvases })[name] = below
  }
  const blockType = blockTypes.find((b) => b.blockTypeId === node.blockTypeId)
  if (blockType) {
    for (const name of blockType.subCanvasNames ?? []) {
      if (Object.hasOwn(node.subCanvases ?? {}, name)) continue
      branches ??= { ...node.subCanvases }
      branches[name] = emptyBranch(node, blockType, name)
    }
  }
  return branches ? { ...node, subCanvases: branches } : node
}
