import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { ValidationFinding, WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID } from '../model/workflow.js'
import type { DocumentCanvas } from './canvasFrames.js'
import { documentCanvases, duplicateNodeId, sortsWithoutCycle } from './canvasFrames.js'
import { isActiveMapping } from './mappingFeeds.js'
import { documentNodeLabels } from './nodeLabels.js'

type Report = (finding: Omit<ValidationFinding, 'canvasPath'>) => void

function rootEntry(nodes: readonly WorkflowNode[], edges: readonly WorkflowEdge[], label: (n: WorkflowNode) => string, report: Report): boolean {
  if (nodes.length === 0) return true
  const inDegree = new Map<string, number>(nodes.map((n) => [n.id, 0]))
  const next = new Map<string, string[]>(nodes.map((n) => [n.id, []]))
  for (const e of edges) {
    if (!next.has(e.sourceNodeId) || !inDegree.has(e.targetNodeId)) continue
    next.get(e.sourceNodeId)?.push(e.targetNodeId)
    inDegree.set(e.targetNodeId, (inDegree.get(e.targetNodeId) ?? 0) + 1)
  }

  const inputNode = nodes.find((n) => n.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID)
  if (!inputNode) {
    report({ code: 'no-start-node', params: {}, ruleId: 'no-start-node', nodeId: null, message: 'Workflow has no start node. Add a Workflow Input node as the single entry point.' })
    return false
  }
  if ((inDegree.get(inputNode.id) ?? 0) !== 0) {
    report({
      code: 'input-has-incoming', ruleId: 'input-has-incoming',
      params: {},
      nodeId: inputNode.id,
      message: 'The Workflow Input node must be the single start point and cannot have incoming connections',
    })
    return false
  }

  const roots = nodes.filter((n) => n.id !== inputNode.id && (inDegree.get(n.id) ?? 0) === 0)
  for (const n of roots) {
    report({
      code: 'extra-root', ruleId: 'extra-root',
      params: { nodeLabel: label(n) },
      nodeId: n.id,
      message: `Node '${label(n)}' has no incoming connection — the workflow must start only from the Workflow Input node`,
    })
  }
  if (roots.length > 0) return false

  const reachable = new Set<string>([inputNode.id])
  const queue = [inputNode.id]
  while (queue.length > 0) {
    for (const target of next.get(queue.shift() as string) ?? []) {
      if (!reachable.has(target)) {
        reachable.add(target)
        queue.push(target)
      }
    }
  }
  const unreachable = nodes.filter((n) => !reachable.has(n.id))
  for (const n of unreachable) {
    report({ code: 'unreachable', ruleId: 'unreachable', params: { nodeLabel: label(n) }, nodeId: n.id, message: `Node '${label(n)}' is not reachable from the Workflow Input start node` })
  }
  return unreachable.length === 0
}

function joinCollisions(nodes: readonly WorkflowNode[], edges: readonly WorkflowEdge[], label: (n: WorkflowNode) => string, where: string, report: Report): void {
  const byId = new Map(nodes.map((n) => [n.id, n]))
  const counts = new Map<string, Map<string, number>>()
  for (const e of edges) {
    if (!byId.has(e.targetNodeId)) continue
    const perInput = counts.get(e.targetNodeId) ?? new Map<string, number>()
    for (const inputName of Object.keys(e.mappings ?? {})) {
      if (isActiveMapping(e, inputName)) perInput.set(inputName, (perInput.get(inputName) ?? 0) + 1)
    }
    counts.set(e.targetNodeId, perInput)
  }

  for (const [targetId, perInput] of counts) {
    for (const [inputName, count] of perInput) {
      if (count < 2) continue
      report({
        code: 'duplicate-input-mapping', ruleId: 'duplicate-input-mapping',
        params: { nodeLabel: label(byId.get(targetId) as WorkflowNode), inputName, location: where },
        nodeId: targetId,
        portName: inputName,
        message: `Node '${label(byId.get(targetId) as WorkflowNode)}' input '${inputName}' is mapped from multiple incoming connections${where} — only one will apply`,
      })
    }
  }
}

function checkCanvas(frame: DocumentCanvas, label: (n: WorkflowNode) => string, findings: ValidationFinding[]): void {
  const { nodes, edges } = frame.canvas
  const report: Report = (finding) => findings.push({ ...finding, canvasPath: frame.path })
  const ids = new Set(nodes.map((n) => n.id))
  for (const e of edges) {
    for (const [end, id] of [['source', e.sourceNodeId], ['target', e.targetNodeId]] as const) {
      if (ids.has(id)) continue
      report({
        code: 'edge-endpoint-unknown', ruleId: 'edge-endpoint-unknown',
        params: { edgeId: e.id ?? '', location: frame.where, end, nodeId: id ?? '' },
        nodeId: null,
        edgeId: e.id,
        message: `Connection '${e.id ?? ''}'${frame.where} names ${end} node '${id ?? ''}', which does not exist on its canvas`,
      })
    }
  }

  const edgePairs = new Set<string>()
  for (const edge of edges) {
    const pair = JSON.stringify([edge.sourceNodeId, edge.targetNodeId])
    if (edgePairs.has(pair)) {
      report({
        code: 'duplicate-edge-pair', ruleId: 'duplicate-edge-pair',
        params: { location: frame.where, sourceNodeId: edge.sourceNodeId, targetNodeId: edge.targetNodeId },
        nodeId: null, edgeId: edge.id,
        message: `More than one connection joins '${edge.sourceNodeId}' to '${edge.targetNodeId}'${frame.where}`,
      })
      break
    }
    edgePairs.add(pair)
  }

  const twice = duplicateNodeId(nodes)
  if (twice !== undefined) {
    report({ code: 'duplicate-node-id', ruleId: 'duplicate-node-id', params: { duplicateId: twice, location: frame.where }, nodeId: twice, message: `Node id '${twice}' is used by more than one node${frame.where}` })
    return
  }
  if (frame.path.length > 0 || rootEntry(nodes, edges, label, report)) joinCollisions(nodes, edges, label, frame.where, report)
  if (!sortsWithoutCycle(frame.canvas)) report({ code: 'cycle', ruleId: 'cycle', params: { location: frame.where }, nodeId: null, message: `Workflow contains a cycle${frame.where}` })
}

/** Graph-shape rules on every canvas the compiler grades, each at the depth it grades it: a wire's ends, ids, one
 * feed per input and no cycle everywhere; one entry point reaching every node on the root alone. Pure: runs without a store. */
export function validateStructureFindings(input: {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  blockTypes: BlockTypeDescriptor[]
}): ValidationFinding[] {
  const { nodes, edges, blockTypes } = input
  const frames = documentCanvases({ nodes, edges }, blockTypes)
  const label = documentNodeLabels(frames[0]?.canvas ?? { nodes }, blockTypes)
  const findings: ValidationFinding[] = []
  for (const frame of frames) checkCanvas(frame, label, findings)
  return findings
}

/** Returns structural findings with stable rule codes. */
export function validateStructure(input: {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  blockTypes: BlockTypeDescriptor[]
}): ValidationFinding[] {
  return validateStructureFindings(input)
}
