import { useMemo } from 'react'
import type { ReactNode } from 'react'
import type { PortTermDisplay, PortTypeName, WorkflowEdge } from '@reeve-iot/workflow-core'
import { ERROR_OUTPUT_NAME, ERROR_OUTPUT_TYPE, WORKFLOW_INPUT_BLOCK_TYPE_ID, addsErrorOutput, parseDynamicPorts, useWorkflowEditorStore } from '@reeve-iot/workflow-core'
import { useAnalysedPortTerm } from '../canvas/analysedPortTerm.js'
import { useNodePorts } from '../canvas/nodePorts.js'

/** True for the two terms a projection reads. */
export const isJsonTerm = (type: string): boolean => type === 'Json' || type === 'Json?'

/** An output a wire can read, as the dialog lists it: `type` is absent where the editor does not know the term, as for a
 * passthrough name, whose `shown` term is the server's. */
export interface WireSourceOutput {
  name: string
  displayName: string
  isRequired: boolean
  type?: PortTypeName
  shown?: PortTermDisplay
}

/** The outputs a wire from `sourceNodeId` can read on the open canvas: the one list the mapping dialog offers and the
 * node panel's projection slot reads, so the two cannot disagree. */
export function useWireSourceOutputs(sourceNodeId: string | undefined): WireSourceOutput[] {
  const nodes = useWorkflowEditorStore((s) => s.nodes)
  const blockTypes = useWorkflowEditorStore((s) => s.blockTypes)
  const canvasStack = useWorkflowEditorStore((s) => s.canvasStack)
  const workflowInputParams = useWorkflowEditorStore((s) => s.workflowInputParams)
  const sourceNode = nodes.find((n) => n.id === sourceNodeId)
  const sourceBlockType = blockTypes.find((bt) => bt.blockTypeId === sourceNode?.blockTypeId)
  const sourceHostPorts = useNodePorts(sourceNode ?? null, sourceBlockType)
  const frame = canvasStack[canvasStack.length - 1]
  const isSourceEntry = sourceNode?.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID
  const analysedTerm = useAnalysedPortTerm()

  return useMemo<WireSourceOutput[]>(() => {
    if (isSourceEntry) {
      if (!frame) {
        return workflowInputParams.map((p) => ({ name: p.name, displayName: p.displayName, isRequired: false, type: p.type }))
      }
      const parentNode = frame.nodes.find((n) => n.id === frame.parentNodeId)
      const parentBlockType = blockTypes.find((bt) => bt.blockTypeId === parentNode?.blockTypeId)
      const declared = parentBlockType?.subCanvasInputs?.[frame.subCanvasName] ?? []
      const forwarded = (parentNode?.passthrough ?? [])
        .filter((name) => !declared.some((p) => p.name === name))
        .map((name): WireSourceOutput => ({ name, displayName: name, isRequired: false, shown: analysedTerm(sourceNode?.id ?? '', 'outputs', name) }))
      return [...declared.map((p) => ({ name: p.name, displayName: p.displayName, isRequired: p.isRequired, type: p.type })), ...forwarded]
    }
    const base = sourceBlockType?.hasDynamicPorts
      ? parseDynamicPorts(sourceNode?.configuration['OutputPorts'] as string)
      : (sourceBlockType?.outputs ?? []).map((o) => ({ name: o.name, displayName: o.displayName, isRequired: false, type: o.type }))
    for (const output of sourceHostPorts.hostOutputs) {
      if (!base.some((existing) => existing.name === output.name)) {
        base.push({ name: output.name, displayName: output.displayName, isRequired: false, type: output.type })
      }
    }
    if (sourceNode && addsErrorOutput(sourceNode, base)) {
      base.push({ name: ERROR_OUTPUT_NAME, displayName: 'Error', isRequired: false, type: ERROR_OUTPUT_TYPE })
    }
    const forwarded = (sourceNode?.passthrough ?? [])
      .filter((name) => !base.some((o) => o.name === name))
      .map((name): WireSourceOutput => ({ name, displayName: name, isRequired: false, shown: analysedTerm(sourceNode?.id ?? '', 'outputs', name) }))
    return [...base, ...forwarded]
  }, [isSourceEntry, frame, workflowInputParams, blockTypes, sourceBlockType, sourceNode, sourceHostPorts, analysedTerm])
}

/** A `Json` or `Json?` output a wire into a node offers, and the node it leaves. */
export interface WiredJsonOutput {
  sourceNodeId: string
  name: string
  type: PortTypeName
}

/** Renders `children` with the `Json` outputs every wire in `edges` offers, each read through `useWireSourceOutputs`:
 * one component per wire, so the hook order holds whatever the number of wires. */
export function WiredJsonOutputs({ edges, children, found = [] }: {
  edges: readonly WorkflowEdge[]
  children: (outputs: readonly WiredJsonOutput[]) => ReactNode
  found?: readonly WiredJsonOutput[]
}): ReactNode {
  const [first, ...rest] = edges
  if (first === undefined) return children(found)
  return <WireJsonOutputs edge={first} rest={rest} found={found}>{children}</WireJsonOutputs>
}

function WireJsonOutputs({ edge, rest, found, children }: {
  edge: WorkflowEdge
  rest: readonly WorkflowEdge[]
  found: readonly WiredJsonOutput[]
  children: (outputs: readonly WiredJsonOutput[]) => ReactNode
}): ReactNode {
  const outputs = useWireSourceOutputs(edge.sourceNodeId)
  const json = useMemo(() => [
    ...found,
    ...outputs.flatMap((o) => o.type !== undefined && isJsonTerm(o.type) && !found.some((f) => f.sourceNodeId === edge.sourceNodeId && f.name === o.name)
      ? [{ sourceNodeId: edge.sourceNodeId, name: o.name, type: o.type }] : []),
  ], [found, outputs, edge.sourceNodeId])
  return <WiredJsonOutputs edges={rest} found={json}>{children}</WiredJsonOutputs>
}
