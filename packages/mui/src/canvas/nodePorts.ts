import type { BlockTypeDescriptor, PortInfo, PortTypeName, WorkflowNode } from '@reeve-iot/workflow-core'
import { readWorkflowReference, usePortTypes, useWorkflowReferenceResolver } from '@reeve-iot/workflow-core'
import { useNodePortCustomization } from './portCustomization.js'

export interface NodePorts {
  /** Ports contributed from outside the descriptor: the host's discovery seam, plus the parameters of a
   * workflow this node runs. */
  hostInputs: PortInfo[]
  hostOutputs: PortInfo[]
  /** True for an input the descriptor declares but nothing may wire: filled from the node's own
   * configuration instead. */
  isInputHidden: (portName: string) => boolean
  /** The type an input really has once the node's configuration is taken into account. */
  inputType: (port: PortInfo) => PortTypeName
}

/** One answer to "what ports does this node actually have", shared by everything that draws or offers
 * them. Two components deriving this separately is how a canvas ends up drawing a port the mapping dialog
 * refuses to wire.
 *
 * Calls both seams unconditionally — they are hooks, and each decides per node whether it has anything to
 * say. `node` may be null for a caller that has no node yet but must keep its hook order stable. */
export function useNodePorts(node: WorkflowNode | null, blockType: BlockTypeDescriptor | undefined): NodePorts {
  const custom = useNodePortCustomization()
  const portTypes = usePortTypes()
  const resolveWorkflow = useWorkflowReferenceResolver()

  const discovered = custom.useDiscoveredPorts?.(node) ?? {}
  const referenced = resolveWorkflow(node ? readWorkflowReference(node.configuration, blockType?.runsWorkflowFrom) : null)

  const merge = (first: PortInfo[] | undefined, second: PortInfo[]): PortInfo[] => {
    const base = first ?? []
    return [...base, ...second.filter((p) => !base.some((existing) => existing.name === p.name))]
  }

  return {
    hostInputs: merge(discovered.inputs, referenced.inputs),
    hostOutputs: merge(discovered.outputs, referenced.outputs),
    isInputHidden: (portName) =>
      portName === blockType?.runsWorkflowFrom || (node ? (custom.isInputPortHidden?.(node, portName) ?? false) : false),
    inputType: (port) => {
      const overridden = node ? custom.overrideInputPortType?.(node, port) : null
      return overridden === null || overridden === undefined ? port.type : portTypes.normalize(overridden)
    },
  }
}
