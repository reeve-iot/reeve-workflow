import type { CanvasPathSegment, ValidationFinding } from '../model/workflow.js'

/** One node's worth of validation findings, ready to render as a labelled group. */
export interface ValidationGroup {
  /** Canvas node the messages concern, or null for graph-wide findings. */
  nodeId: string | null
  /** The canvas that node is on, root first; empty for the root. Two canvases may hold nodes with one id. */
  canvasPath: readonly CanvasPathSegment[]
  nodeLabel: string
  /** Deduplicated findings for this node; render each through `validationMessage`. */
  findings: ValidationFinding[]
}

export interface ValidationSummary {
  groupedByNode: ValidationGroup[]
  groupedWarningsByNode: ValidationGroup[]
  /** Raw finding counts, before per-node deduplication. */
  totalErrorCount: number
  totalWarningCount: number
  /** Distinct (node, code, params) findings — what the user actually has to fix. */
  uniqueErrorCount: number
  uniqueWarningCount: number
}

type LabelResolver = (nodeId: string | null, canvasPath: readonly CanvasPathSegment[]) => string

function groupByNode(
  findings: ValidationFinding[],
  resolveLabel: LabelResolver,
): { groups: ValidationGroup[]; uniqueCount: number } {
  const groups = new Map<string, { nodeId: string | null; canvasPath: readonly CanvasPathSegment[]; findings: ValidationFinding[]; seen: Set<string> }>()
  let uniqueCount = 0

  for (const finding of findings) {
    const canvasPath = finding.canvasPath ?? []
    const key = JSON.stringify([canvasPath.map((s) => [s.nodeId, s.branchName]), finding.nodeId])
    let group = groups.get(key)
    if (!group) {
      group = { nodeId: finding.nodeId, canvasPath, findings: [], seen: new Set() }
      groups.set(key, group)
    }
    const params = finding.params ?? {}
    const identity = JSON.stringify([finding.code, params, Object.keys(params).length === 0 ? finding.message : null])
    if (!group.seen.has(identity)) {
      group.seen.add(identity)
      group.findings.push(finding)
      uniqueCount++
    }
  }

  return {
    groups: Array.from(groups.values()).map((g) => ({
      nodeId: g.nodeId,
      canvasPath: g.canvasPath,
      nodeLabel: resolveLabel(g.nodeId, g.canvasPath),
      findings: g.findings,
    })),
    uniqueCount,
  }
}

/** Groups findings by node and canvas rather than by message text, so blocks of the same type stay separate, and
 * keeps the findings for the caller to render. `resolveLabel` belongs to the caller because only the editor knows
 * the live node list. */
export function buildValidationSummary(
  errors: ValidationFinding[],
  warnings: ValidationFinding[] = [],
  resolveLabel: LabelResolver = (id) => id ?? 'General',
): ValidationSummary {
  const errorGroups = groupByNode(errors, resolveLabel)
  const warningGroups = groupByNode(warnings, resolveLabel)
  return {
    groupedByNode: errorGroups.groups,
    groupedWarningsByNode: warningGroups.groups,
    totalErrorCount: errors.length,
    totalWarningCount: warnings.length,
    uniqueErrorCount: errorGroups.uniqueCount,
    uniqueWarningCount: warningGroups.uniqueCount,
  }
}
