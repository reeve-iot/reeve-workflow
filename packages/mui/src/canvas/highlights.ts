/** How a host asks the canvas to tint a node. The library assigns no meaning to `status` beyond
 * choosing a colour; a host that streams run state maps its own vocabulary onto these. */
export type NodeRunStatus = 'running' | 'success' | 'error'

export interface NodeHighlight {
  nodeId: string
  status?: NodeRunStatus
  /** Serialised values to reveal on the node, when the host has them. */
  inputsJson?: string | null
  outputsJson?: string | null
}

/** Structural diff tint, for a host comparing two versions of a graph. */
export type NodeDiffKind = 'added' | 'removed' | 'modified'
