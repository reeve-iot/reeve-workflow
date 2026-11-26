import { createContext, useCallback, useContext, useSyncExternalStore } from 'react'
import type { CanvasFrame, WorkflowEdge, WorkflowEditorStore, WorkflowNode } from '@reeve-iot/workflow-core'
import { useWorkflowEditorStoreApi } from '@reeve-iot/workflow-core'

/** The canvas's nodes, edges and frame stack with position churn filtered out: the object keeps its
 * identity across a drag frame, so a node's `position` in here may be stale — read that from props. */
export interface GraphTopology {
  readonly nodes: WorkflowNode[]
  readonly edges: WorkflowEdge[]
  readonly canvasStack: CanvasFrame[]
}

export const GraphTopologyContext = createContext<GraphTopology | null>(null)

const sameButForPositions = (previous: WorkflowNode[], next: WorkflowNode[]): boolean =>
  previous.length === next.length &&
  previous.every((p, index) => {
    const n = next[index]!
    return (
      p === n ||
      (p.id === n.id && p.blockTypeId === n.blockTypeId && p.configuration === n.configuration &&
        p.passthrough === n.passthrough && p.outputPorts === n.outputPorts && p.subCanvases === n.subCanvases)
    )
  })

const snapshots = new WeakMap<WorkflowEditorStore, GraphTopology>()
const NONE: GraphTopology = { nodes: [], edges: [], canvasStack: [] }

/** What a node needs of the graph without re-rendering on every drag frame. Inside a WorkflowCanvas the
 * canvas derives it once and provides it; anywhere else the hook derives it from the store itself. */
export function useGraphTopology(): GraphTopology {
  const provided = useContext(GraphTopologyContext)
  const store = useWorkflowEditorStoreApi()
  const getSnapshot = useCallback((): GraphTopology => {
    if (provided) return NONE
    const { nodes, edges, canvasStack } = store.getState()
    const previous = snapshots.get(store)
    if (previous && previous.edges === edges && previous.canvasStack === canvasStack && sameButForPositions(previous.nodes, nodes)) {
      return previous
    }
    const next: GraphTopology = { nodes, edges, canvasStack }
    snapshots.set(store, next)
    return next
  }, [provided, store])
  const own = useSyncExternalStore(store.subscribe, getSnapshot, getSnapshot)
  return provided ?? own
}
