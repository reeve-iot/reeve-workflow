import type { DraftAnalysisFreshness, DraftAnalysisIndex } from '../analysis/draftAnalysis.js'
import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { CanvasFrame } from '../model/canvas.js'
import type { PortTypeName } from '../portTypes/name.js'
import type { WorkflowEdge, WorkflowNode, WorkflowParameter } from '../model/workflow.js'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID } from '../model/workflow.js'
import { parseDynamicPorts } from '../ports/dynamicPorts.js'
import type { PortTypeResolver } from '../ports/portTypeResolver.js'

export interface IncomingVariable {
  name: string
  /** The closed term the source gives this name: its declaration's, or the server's for a passthrough-only name. */
  type: PortTypeName | undefined
  /** True when `type` comes from an answer older than the draft; show it marked stale. */
  stale?: boolean
  /** The edge that offers this name. Forwarding a variable has to map it on that wire too, or the value
   * never reaches the node. Undefined only if no edge claimed the name, which cannot happen here. */
  edgeId?: string
}

export interface IncomingVariablesInput {
  node: WorkflowNode
  nodes: readonly WorkflowNode[]
  edges: readonly WorkflowEdge[]
  blockTypes: readonly BlockTypeDescriptor[]
  canvasStack: readonly CanvasFrame[]
  workflowInputParams: readonly WorkflowParameter[]
  resolver: PortTypeResolver
  /** The server's draft analysis, which types the names only a passthrough entry gives. */
  analysis?: DraftAnalysisIndex
  /** How current that analysis is; a term taken from a stale one is flagged. */
  freshness?: DraftAnalysisFreshness
}

/** Everything reaching `node` over its incoming wires, whatever the wires currently map. A name is listed
 * once; the first source to offer it decides its type, because a passthrough carries one value per name.
 *
 * The type is the resolver's closed declared term; for a name only a passthrough entry gives it is the term the analysis
 * answers for that output. Where neither has one it is undefined. */
export function computeIncomingVariables({
  node, nodes, edges, blockTypes, canvasStack, workflowInputParams, resolver, analysis, freshness,
}: IncomingVariablesInput): IncomingVariable[] {
  const variables = new Map<string, { type: PortTypeName | undefined; stale: boolean }>()
  const offeredBy = new Map<string, string>()
  const canvasPath = canvasStack.map((f) => ({ nodeId: f.parentNodeId, branchName: f.subCanvasName }))
  let currentEdgeId = ''
  const add = (sourceId: string, name: string) => {
    if (variables.has(name)) return
    const given = analysis?.port(canvasPath, sourceId, 'outputs', name)
    const answered = given?.declared === null && given.state === 'bound' ? given.effective : null
    const type = answered === null ? resolver.resolveOutputType(0, sourceId, name) : (answered as PortTypeName)
    variables.set(name, { type, stale: answered !== null && freshness === 'stale' })
    offeredBy.set(name, currentEdgeId)
  }

  for (const edge of edges.filter((e) => e.targetNodeId === node.id)) {
    const source = nodes.find((n) => n.id === edge.sourceNodeId)
    if (!source) continue
    currentEdgeId = edge.id

    if (source.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID) {
      const frame = canvasStack[canvasStack.length - 1]
      if (!frame) {
        for (const p of workflowInputParams) add(source.id, p.name)
        continue
      }
      const parentNode = frame.nodes.find((n) => n.id === frame.parentNodeId)
      const parentBlockType = blockTypes.find((bt) => bt.blockTypeId === parentNode?.blockTypeId)
      if (!parentBlockType) continue
      const declared = parentBlockType.subCanvasInputs?.[frame.subCanvasName] ?? []
      const forwarded = (parentNode?.passthrough ?? []).filter((name) => !declared.some((p) => p.name === name))
      for (const name of [...declared.map((p) => p.name), ...forwarded]) add(source.id, name)
      continue
    }

    const blockType = blockTypes.find((bt) => bt.blockTypeId === source.blockTypeId)
    if (!blockType) continue
    const outputs = blockType.hasDynamicPorts
      ? parseDynamicPorts(source.configuration['OutputPorts'] as string)
      : blockType.outputs
    for (const output of outputs) add(source.id, output.name)
    for (const name of source.passthrough ?? []) add(source.id, name)
  }

  return [...variables.entries()].map(([name, { type, stale }]) => ({ name, type, ...(stale ? { stale } : {}), edgeId: offeredBy.get(name) }))
}
