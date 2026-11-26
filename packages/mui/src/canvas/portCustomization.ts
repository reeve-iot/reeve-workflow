import { createContext, useContext } from 'react'
import type { PortInfo, PortTypeName, WorkflowNode } from '@reeve-iot/workflow-core'

/** The three ways a host's domain reaches a node's ports. Without any of them a node shows exactly the
 * ports its descriptor declares, which is all the library can know by itself. */
export interface NodePortCustomization {
  /** Ports the host owns somewhere else — a side panel writes them into the node's configuration — so
   * drawing them as connectable would offer a wire that the host would then ignore. */
  isInputPortHidden?: (node: WorkflowNode, portName: string) => boolean
  /** A declared port whose real type depends on the node's own configuration. Return null to keep the
   * declared type. */
  overrideInputPortType?: (node: WorkflowNode, port: PortInfo) => PortTypeName | null
  /** Ports the host discovers at runtime, typically by following a reference the node configuration
   * holds. **Called as a React hook, once per node**, so it may use hooks itself — which also means the
   * function identity must stay stable for the editor's lifetime. The node is null where the caller has
   * no node to ask about, which still has to call the hook to keep its own hook order stable. */
  useDiscoveredPorts?: (node: WorkflowNode | null) => { inputs?: PortInfo[]; outputs?: PortInfo[] }
}

const NONE: NodePortCustomization = {}

export const NodePortCustomizationContext = createContext<NodePortCustomization>(NONE)

export function useNodePortCustomization(): NodePortCustomization {
  return useContext(NodePortCustomizationContext)
}
