import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { MappingAsRead, WorkflowEdge, WorkflowNode } from '../model/workflow.js'
import { ERROR_OUTPUT_NAME, offersErrorOutput, readMapping, WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID } from '../model/workflow.js'
import { INPUT_PORTS_KEY, OUTPUT_PORTS_KEY, readPortList } from '../model/storedDocument.js'
import { parsePortTypeName } from '../portTypes/grammar.js'
import { parseDynamicPorts } from '../ports/dynamicPorts.js'
import { isActiveMapping } from './mappingFeeds.js'

/** One canvas as the passthrough trace reads it: its nodes and wires, the descriptor each id binds to, and the
 * names its entry node carries (undefined where the editor cannot know them). */
export interface TraceCanvas {
  nodes: readonly WorkflowNode[]
  edges: readonly WorkflowEdge[]
  bound: ReadonlyMap<string, BlockTypeDescriptor>
  entryPorts: readonly string[] | undefined
}

const KEPT_BACK = new Set(['timeoutSeconds', 'retryCount', 'retryEnabled', 'retryBackoffSeconds'])
const DESIGN_TIME_PREFIX = '__'

const own = (config: WorkflowNode['configuration'] | null | undefined, key: string): unknown =>
  config && Object.hasOwn(config, key) ? config[key] : undefined

function dynamicNames(node: WorkflowNode, blockType: BlockTypeDescriptor, key: string): string[] {
  const text = own(node.configuration, key)
  return blockType.hasDynamicPorts && typeof text === 'string' ? parseDynamicPorts(text).map((p) => p.name) : []
}

/** Whether a stored type name on the node is one the grammar refuses: the compiler then takes a trace reaching the
 * node as traced, and leaves each wire end at the node unchecked. */
export function hasMalformedStoredPortType(node: WorkflowNode, blockType: BlockTypeDescriptor | undefined): boolean {
  if ((node.outputPorts ?? []).some((p) => !parsePortTypeName(p.type).ok)) return true
  if (!blockType?.hasDynamicPorts) return false
  return [INPUT_PORTS_KEY, OUTPUT_PORTS_KEY].some((key) => {
    const text = own(node.configuration, key)
    const read = typeof text === 'string' ? readPortList(text, 'input') : undefined
    return read?.ok === true && read.sites.some((s) => !parsePortTypeName(s.rawType).ok)
  })
}

function configForwards(node: WorkflowNode, name: string): boolean {
  return !KEPT_BACK.has(name) && !name.startsWith(DESIGN_TIME_PREFIX) && !!node.configuration && Object.hasOwn(node.configuration, name)
}

/** The passthrough names the compiler makes ports, laddered as ResolvePassthroughType: a declared input, the last mapping
 * into the name, then a forwarded configuration value. A name the editor cannot trace (a block it lacks) counts as traced. */
export function tracedPassthrough(canvas: TraceCanvas, node: WorkflowNode): Set<string> {
  const byId = new Map(canvas.nodes.map((n) => [n.id, n]))
  const visiting = new Set<string>()

  const outputTraces = (sourceId: string, output: string): boolean | 'nothing' => {
    const source = byId.get(sourceId)
    if (!source) return 'nothing'
    const blockType = canvas.bound.get(source.id)
    if (source.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID) {
      if (canvas.entryPorts === undefined || canvas.entryPorts.includes(output)) return true
    } else if (source.blockTypeId !== WORKFLOW_OUTPUT_BLOCK_TYPE_ID) {
      if (!blockType || blockType.runsWorkflowFrom || hasMalformedStoredPortType(source, blockType)) return true
      if ((blockType.outputs ?? []).some((p) => p.name === output) || dynamicNames(source, blockType, OUTPUT_PORTS_KEY).includes(output)) return true
      if (output === ERROR_OUTPUT_NAME && offersErrorOutput(source)) return true
    }
    if ((source.outputPorts ?? []).some((p) => p.name === output)) return true
    return (source.passthrough ?? []).includes(output) ? traces(source, output) : 'nothing'
  }

  const traces = (at: WorkflowNode, name: string): boolean => {
    const key = `${at.id}\u0000${name}`
    if (visiting.has(key)) return false
    visiting.add(key)
    try {
      const blockType = canvas.bound.get(at.id)
      if (!blockType || at.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID || at.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID) return true
      if (blockType.runsWorkflowFrom || hasMalformedStoredPortType(at, blockType)) return true
      if ((blockType.inputs ?? []).some((p) => p.name === name) || dynamicNames(at, blockType, INPUT_PORTS_KEY).includes(name)) return true
      let feeding: { edge: WorkflowEdge; read: MappingAsRead } | undefined
      for (const e of canvas.edges) {
        const mapping = e.mappings[name]
        if (e.targetNodeId !== at.id || !mapping || !isActiveMapping(e, name)) continue
        feeding = { edge: e, read: readMapping(mapping) }
      }
      if (feeding?.read.literal) return true
      if (feeding && !feeding.read.literal && outputTraces(feeding.edge.sourceNodeId, feeding.read.source) === true) return true
      return configForwards(at, name)
    } finally {
      visiting.delete(key)
    }
  }

  return new Set((node.passthrough ?? []).filter((name) => traces(node, name)))
}
