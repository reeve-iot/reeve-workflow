import type { CanvasPathSegment, DraftAnalysisIndex, WorkflowNode } from '@reeve-iot/workflow-core'
import { literalWriteTerm } from '@reeve-iot/workflow-core'

/** The closed term a literal of `node`'s input `inputName`, declared `declared`, is written in: the node's own type
 * arguments first, so a control follows an argument the moment it is set; then those arguments with the last analysis
 * answer filling the parameters they leave open; then the analysis port. */
export function inputWriteTerm(
  declared: string, node: WorkflowNode, inputName: string, analysis: DraftAnalysisIndex, canvasPath: readonly CanvasPathSegment[],
): string | undefined {
  const own = node.typeArguments ?? {}
  return literalWriteTerm(declared, undefined, own)
    ?? literalWriteTerm(declared, undefined, { ...analysis.node(canvasPath, node.id)?.typeArguments, ...own })
    ?? literalWriteTerm(declared, analysis.port(canvasPath, node.id, 'inputs', inputName), undefined)
}
