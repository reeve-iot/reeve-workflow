import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { CanvasPathSegment, WorkflowDefinition, WorkflowNode } from '../model/workflow.js'
import { withDeclaredBranches } from './declaredBranches.js'

/** One canvas of a document as the compiler walks it: root first, then each branch below its container in order. */
export interface DocumentCanvas {
  canvas: WorkflowDefinition
  path: CanvasPathSegment[]
  /** Appended to a message about this canvas: empty at the root, else which branch of which container. */
  where: string
  /** Undefined at the root. */
  parent?: DocumentCanvas
  /** The compiler holds this canvas's ports: every container above it is catalogued and declares its branch. */
  signatureKnown: boolean
  /** The container and branch this canvas is, when it is a branch. */
  container?: { node: WorkflowNode; blockType: BlockTypeDescriptor | undefined; branchName: string }
}

const pathKey = (path: readonly CanvasPathSegment[]): string => JSON.stringify(path.map((s) => [s.nodeId, s.branchName]))

/** The first id a canvas holds twice, in node order, as the compiler's DuplicateNodeId answers it. */
export function duplicateNodeId(nodes: readonly WorkflowNode[]): string | undefined {
  const seen = new Set<string>()
  for (const n of nodes) {
    if (seen.has(n.id)) return n.id
    seen.add(n.id)
  }
  return undefined
}

/** Whether the run's topological sort of this canvas succeeds: no id twice and no cycle among wires whose ends exist. */
export function canvasSorts(canvas: WorkflowDefinition): boolean {
  if (duplicateNodeId(canvas.nodes) !== undefined) return false
  return sortsWithoutCycle(canvas)
}

export function sortsWithoutCycle(canvas: WorkflowDefinition): boolean {
  const inDegree = new Map<string, number>(canvas.nodes.map((n) => [n.id, 0]))
  const next = new Map<string, string[]>(canvas.nodes.map((n) => [n.id, []]))
  for (const e of canvas.edges) {
    if (!next.has(e.sourceNodeId) || !inDegree.has(e.targetNodeId)) continue
    next.get(e.sourceNodeId)?.push(e.targetNodeId)
    inDegree.set(e.targetNodeId, (inDegree.get(e.targetNodeId) ?? 0) + 1)
  }
  const ready = [...inDegree].filter(([, d]) => d === 0).map(([id]) => id)
  let consumed = 0
  while (ready.length > 0) {
    const id = ready.shift() as string
    consumed++
    for (const target of next.get(id) ?? []) {
      const left = (inDegree.get(target) ?? 0) - 1
      inDegree.set(target, left)
      if (left === 0) ready.push(target)
    }
  }
  return consumed === canvas.nodes.length
}

/** The descriptor the compiler binds each id of a canvas to: that of the last node holding the id whose type the
 * catalogue has, since its binding is keyed by canvas and id. */
export function boundBlockTypes(
  nodes: readonly WorkflowNode[],
  blockTypes: readonly BlockTypeDescriptor[],
): Map<string, BlockTypeDescriptor> {
  const bound = new Map<string, BlockTypeDescriptor>()
  for (const n of nodes) {
    const blockType = blockTypes.find((b) => b.blockTypeId === n.blockTypeId)
    if (blockType) bound.set(n.id, blockType)
  }
  return bound
}

function allPaths(canvas: WorkflowDefinition, path: CanvasPathSegment[], out: string[]): void {
  out.push(pathKey(path))
  for (const n of canvas.nodes) {
    for (const [branchName, sub] of Object.entries(n.subCanvases ?? {})) {
      allPaths(sub, [...path, { nodeId: n.id, branchName }], out)
    }
  }
}

/** Every canvas of `definition` the compiler grades, a declared branch it lacks as `emptyBranch`, in its walk order. A
 * path two canvases share is skipped with everything below it: the compiler keys what it learns by path. */
export function documentCanvases(stored: WorkflowDefinition, blockTypes: readonly BlockTypeDescriptor[]): DocumentCanvas[] {
  const definition = withDeclaredBranches(stored, blockTypes)
  const keys: string[] = []
  allPaths(definition, [], keys)
  const counts = new Map<string, number>()
  for (const k of keys) counts.set(k, (counts.get(k) ?? 0) + 1)
  const label = (id: string) => blockTypes.find((b) => b.blockTypeId === id)?.displayName ?? id

  const out: DocumentCanvas[] = []
  const visit = (frame: DocumentCanvas) => {
    if ((counts.get(pathKey(frame.path)) ?? 0) > 1) return
    out.push(frame)
    const bound = boundBlockTypes(frame.canvas.nodes, blockTypes)
    for (const node of frame.canvas.nodes) {
      for (const [branchName, sub] of Object.entries(node.subCanvases ?? {})) {
        const blockType = bound.get(node.id)
        visit({
          canvas: sub,
          path: [...frame.path, { nodeId: node.id, branchName }],
          where: ` in branch '${branchName}' of '${label(node.blockTypeId)}'${frame.where}`,
          parent: frame,
          signatureKnown: frame.signatureKnown && (blockType?.subCanvasNames ?? []).includes(branchName),
          container: { node, blockType, branchName },
        })
      }
    }
  }
  visit({ canvas: definition, path: [], where: '', signatureKnown: true })
  return out
}
