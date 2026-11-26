import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { WorkflowDefinition, WorkflowNode } from '../model/workflow.js'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID } from '../model/workflow.js'

const isBoundary = (node: WorkflowNode): boolean =>
  node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID || node.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID

/** How a finding names a node of `definition`: its block's display name, or its block type id where the catalogue has
 * none. When another node anywhere in the document has the same name, the node id follows in parentheses,
 * `Compare (n2)`; when a node of that name on another canvas also has that id, a node off the root canvas adds its
 * canvas, `Compare (n2 in box→Body)`. An entry or exit node is never suffixed. The compiler's diagnostics use the same
 * text, in quotes. Pass the result as `validateNodes`' `nodeLabel` when calling it directly. A node is placed by identity:
 * ask for the node objects of `definition` itself; any other node is named without its canvas. */
export function documentNodeLabels(
  definition: Pick<WorkflowDefinition, 'nodes'>, blockTypes: readonly BlockTypeDescriptor[],
): (node: WorkflowNode) => string {
  const nameOf = (node: WorkflowNode) => blockTypes.find((b) => b.blockTypeId === node.blockTypeId)?.displayName ?? node.blockTypeId
  const placed: { node: WorkflowNode, name: string, path: string }[] = []
  const work: { nodes: WorkflowNode[], path: string }[] = [{ nodes: definition.nodes, path: '' }]
  for (let canvas = work.pop(); canvas !== undefined; canvas = work.pop()) {
    for (const node of canvas.nodes) {
      placed.push({ node, name: nameOf(node), path: canvas.path })
      for (const [branch, sub] of Object.entries(node.subCanvases ?? {})) {
        work.push({ nodes: sub.nodes, path: `${canvas.path === '' ? '' : `${canvas.path}/`}${node.id}→${branch}` })
      }
    }
  }
  const count = (counts: Map<string, number>, key: string) => counts.set(key, (counts.get(key) ?? 0) + 1)
  const byName = new Map<string, number>()
  const byNameAndId = new Map<string, number>()
  for (const { node, name } of placed) {
    if (isBoundary(node)) continue
    count(byName, name)
    count(byNameAndId, JSON.stringify([name, node.id]))
  }
  const labels = new Map<WorkflowNode, string>()
  for (const { node, name, path } of placed) {
    if (labels.has(node)) continue
    if (isBoundary(node) || (byName.get(name) ?? 0) < 2) labels.set(node, name)
    else if ((byNameAndId.get(JSON.stringify([name, node.id])) ?? 0) < 2 || path === '') labels.set(node, `${name} (${node.id})`)
    else labels.set(node, `${name} (${node.id} in ${path})`)
  }
  return (node) => labels.get(node) ?? ((byName.get(nameOf(node)) ?? 0) > 1 && !isBoundary(node) ? `${nameOf(node)} (${node.id})` : nameOf(node))
}
