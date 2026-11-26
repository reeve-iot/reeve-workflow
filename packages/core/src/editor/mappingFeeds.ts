import type { WorkflowEdge } from '../model/workflow.js'
import { readMapping } from '../model/workflow.js'

const BLANK = /^[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]*$/

/** Whether this edge's mapping into `input` is one the compiler's ReadMappings keeps: a literal always, a wired
 * mapping only when it names a source output that is not blank. */
export function isActiveMapping(edge: WorkflowEdge, input: string): boolean {
  const mapping = edge.mappings[input]
  if (!mapping) return false
  const read = readMapping(mapping)
  return read.literal || !BLANK.test(read.source)
}

/** Whether the compiler's WiredInputNames counts this mapping as feeding `input`, `nodeIds` being the ids on the
 * edge's canvas: an active literal whatever its value or wire's source, a wired mapping when its source is there. */
export function feedsInput(edge: WorkflowEdge, input: string, nodeIds: ReadonlySet<string>): boolean {
  const mapping = edge.mappings[input]
  if (!mapping || !isActiveMapping(edge, input)) return false
  return readMapping(mapping).literal || nodeIds.has(edge.sourceNodeId)
}
