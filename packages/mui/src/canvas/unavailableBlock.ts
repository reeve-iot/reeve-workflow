import type { WorkflowNode } from '@reeve-iot/workflow-core'

/** What the editor says of a node whose block type is not in the catalogue it was given. */
export interface UnavailableBlockText {
  /** Drawn as the node's header and the panel's heading. */
  title: string
  /** Why the block cannot be edited, drawn under the title. */
  reason: string
}

/** A host's wording for a node whose block type it does not offer, e.g. a block its configuration switched off. Return
 * the members to replace, or undefined to keep the library's generic wording. Passed to `WorkflowCanvas` and
 * `NodeConfigPanel` as `describeUnavailableBlock`. */
export type DescribeUnavailableBlock = (node: WorkflowNode) => Partial<UnavailableBlockText> | undefined

/** The title and reason shown for `node`, whose block type is not in the catalogue: the host's wording where
 * `describe` gives one, the generic wording otherwise. */
export function unavailableBlockText(node: WorkflowNode, describe?: DescribeUnavailableBlock): UnavailableBlockText {
  const hosted = describe?.(node)
  return {
    title: hosted?.title ?? 'Unavailable block',
    reason: hosted?.reason ?? `Block type '${node.blockTypeId}' is not available on this host. The block is kept as stored and cannot be edited.`,
  }
}
