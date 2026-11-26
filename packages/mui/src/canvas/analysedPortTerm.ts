import { useMemo } from 'react'
import type { PortTermDisplay } from '@reeve-iot/workflow-core'
import { displayPortTerm, draftAnalysisFreshness, indexDraftAnalysis, useWorkflowEditorStore } from '@reeve-iot/workflow-core'

/** The term the server's draft analysis gives a port of a node on the open canvas, for a name only a passthrough entry gives:
 * nothing before an answer, the last answer marked stale once the draft has moved on. */
export function useAnalysedPortTerm(): (nodeId: string, side: 'inputs' | 'outputs', name: string) => PortTermDisplay | undefined {
  const analysis = useWorkflowEditorStore((s) => s.analysis)
  const revision = useWorkflowEditorStore((s) => s.revision)
  const canvasStack = useWorkflowEditorStore((s) => s.canvasStack)
  return useMemo(() => {
    const index = indexDraftAnalysis(analysis)
    const freshness = draftAnalysisFreshness(analysis, revision)
    const canvasPath = canvasStack.map((f) => ({ nodeId: f.parentNodeId, branchName: f.subCanvasName }))
    return (nodeId, side, name) =>
      displayPortTerm(undefined, index.port(canvasPath, nodeId, side, name), freshness, index.findings(canvasPath, nodeId, name))
  }, [analysis, revision, canvasStack])
}

/** A term as text for a place that cannot style it: an answer older than the draft says so. */
export const termText = (display: PortTermDisplay): string => display.stale ? `${display.text} (stale)` : display.text
