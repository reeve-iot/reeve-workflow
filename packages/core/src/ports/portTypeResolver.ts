import type { BlockTypeDescriptor } from '../model/blockType.js'
import { declaredOutputTerm } from '../model/blockType.js'
import type { CanvasFrame } from '../model/canvas.js'
import type { WorkflowEdge, WorkflowNode, WorkflowParameter } from '../model/workflow.js'
import { ERROR_OUTPUT_NAME, ERROR_OUTPUT_TYPE, offersErrorOutput, WORKFLOW_INPUT_BLOCK_TYPE_ID } from '../model/workflow.js'
import { isClosedPortTerm } from '../portTypes/closedTerm.js'
import type { PortTypeName } from '../portTypes/name.js'
import { parseDynamicPorts } from './dynamicPorts.js'

interface ScopeParentLink {
  nodeId: string
  subCanvasName: string
}

/** One canvas level: its nodes and edges, and (unless top-level) how it attaches to the next-outer scope. */
export interface PortTypeScope {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  parent?: ScopeParentLink
}

/** The closed term a port's declaration gives. `undefined` is unknown to the editor (type parameter, malformed type,
 * passthrough-only name, no such port): never an error or a type. */
export interface PortTypeResolver {
  /** The type of `nodeId`'s OUTPUT port `portName` within scope `scopeIndex` (0 = current canvas). */
  resolveOutputType: (scopeIndex: number, nodeId: string, portName: string) => PortTypeName | undefined
  /** The type of `nodeId`'s INPUT port `portName` within scope `scopeIndex` (0 = current canvas). */
  resolveInputType: (scopeIndex: number, nodeId: string, portName: string) => PortTypeName | undefined
}

/** Builds the scope chain (innermost canvas first, top-level last) from the current canvas plus its
 * ancestor frames, mirroring how the editor nests sub-canvases. */
export function buildPortTypeScopes(args: {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  canvasStack: CanvasFrame[]
}): PortTypeScope[] {
  const { nodes, edges, canvasStack } = args
  const depth = canvasStack.length
  const scopes: PortTypeScope[] = []

  const link = (frame: CanvasFrame | undefined): ScopeParentLink | undefined =>
    frame ? { nodeId: frame.parentNodeId, subCanvasName: frame.subCanvasName } : undefined

  scopes.push({ nodes, edges, parent: depth > 0 ? link(canvasStack[depth - 1]) : undefined })

  for (let s = 1; s <= depth; s++) {
    const frame = canvasStack[depth - s]
    if (!frame) continue
    scopes.push({
      nodes: frame.nodes,
      edges: frame.edges,
      parent: depth - s > 0 ? link(canvasStack[depth - s - 1]) : undefined,
    })
  }

  return scopes
}

type Traced = PortTypeName | undefined

const closed = (type: PortTypeName): Traced => (isClosedPortTerm(type) ? type : undefined)

const own = <T>(map: Record<string, T> | undefined, key: string): T | undefined =>
  map && Object.prototype.hasOwnProperty.call(map, key) ? map[key] : undefined

interface ResolverInput {
  scopes: PortTypeScope[]
  blockTypes: BlockTypeDescriptor[]
  workflowInputParams: WorkflowParameter[]
}

/** Creates a resolver over a fixed scope chain, block-type catalogue and top-level input parameters. Pure, and it
 * always terminates. It answers a closed declared term or `_error`'s `Json`; every other port is `undefined`, since the
 * compiler alone binds a type parameter, types a literal and types a name a passthrough entry gives. */
export function createPortTypeResolver(input: ResolverInput): PortTypeResolver {
  const { scopes, blockTypes, workflowInputParams } = input
  const blockTypeById = new Map(blockTypes.map((bt) => [bt.blockTypeId, bt]))

  const dynamicPorts = (node: WorkflowNode, key: 'InputPorts' | 'OutputPorts') =>
    parseDynamicPorts(node.configuration[key] as string)

  const workflowInputOutputType = (scopeIndex: number, portName: string): Traced => {
    const scope = scopes[scopeIndex]
    if (!scope) return undefined

    const parent = scope.parent
    if (!parent) {
      const param = workflowInputParams.find((p) => p.name === portName)
      return param ? closed(param.type) : undefined
    }

    const parentNode = scopes[scopeIndex + 1]?.nodes.find((n) => n.id === parent.nodeId)
    const parentBlockType = parentNode ? blockTypeById.get(parentNode.blockTypeId) : undefined
    const declaredPort = own(parentBlockType?.subCanvasInputs, parent.subCanvasName)?.find((p) => p.name === portName)
    if (declaredPort) return closed(declaredPort.type)
    return parentNode?.passthrough?.includes(portName) ? resolveInputType(scopeIndex + 1, parent.nodeId, portName) : undefined
  }

  const resolveOutputType = (scopeIndex: number, nodeId: string, portName: string): Traced => {
    const node = scopes[scopeIndex]?.nodes.find((n) => n.id === nodeId)
    if (!node) return undefined

    if (node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID) return workflowInputOutputType(scopeIndex, portName)

    const blockType = blockTypeById.get(node.blockTypeId)
    if (blockType?.hasDynamicPorts) {
      const dyn = dynamicPorts(node, 'OutputPorts').find((p) => p.name === portName)
      if (dyn) return closed(dyn.type)
    }
    const declaredOutput = blockType?.outputs.find((o) => o.name === portName)
    if (declaredOutput) return closed(declaredOutput.type)
    const authored = blockType && node.outputPorts?.find((p) => p.name === portName)
    if (blockType && authored) return closed(declaredOutputTerm(blockType, authored))

    if (portName === ERROR_OUTPUT_NAME && offersErrorOutput(node)) return ERROR_OUTPUT_TYPE

    return node.passthrough?.includes(portName) ? resolveInputType(scopeIndex, nodeId, portName) : undefined
  }

  const resolveInputType = (scopeIndex: number, nodeId: string, portName: string): Traced => {
    const node = scopes[scopeIndex]?.nodes.find((n) => n.id === nodeId)
    if (!node) return undefined

    const blockType = blockTypeById.get(node.blockTypeId)
    const declaredInput = blockType?.inputs.find((i) => i.name === portName)
    if (declaredInput) return closed(declaredInput.type)
    if (blockType?.hasDynamicPorts) {
      const dyn = dynamicPorts(node, 'InputPorts').find((p) => p.name === portName)
      if (dyn) return closed(dyn.type)
    }
    return undefined
  }

  return { resolveOutputType, resolveInputType }
}
