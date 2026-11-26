import type { DraftAnalysisDocument } from './draftAnalysis.js'

type Plain = Record<string, unknown>

const isPlain = (value: unknown): value is Plain => typeof value === 'object' && value !== null && !Array.isArray(value)

function withoutPositions(canvas: unknown): unknown {
  if (!isPlain(canvas) || !Array.isArray(canvas['nodes'])) return canvas
  return {
    ...canvas,
    nodes: canvas['nodes'].map((node: unknown) => {
      if (!isPlain(node)) return node
      const kept = Object.fromEntries(Object.entries(node).filter(([key]) => key !== 'position'))
      const subCanvases = kept['subCanvases']
      return isPlain(subCanvases)
        ? { ...kept, subCanvases: Object.fromEntries(Object.entries(subCanvases).map(([name, sub]) => [name, withoutPositions(sub)])) }
        : kept
    }),
  }
}

/** Text that is equal for two documents exactly when a draft analysis of one answers for the other: the document with
 * node positions left out. */
export function documentSignature(document: DraftAnalysisDocument): string {
  return JSON.stringify({
    definition: withoutPositions(document.definition),
    inputParameters: document.inputParameters,
    outputParameters: document.outputParameters,
  })
}
