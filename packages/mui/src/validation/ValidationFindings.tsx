import { List, ListItem, ListItemButton, ListItemText } from '@mui/material'
import type { ValidationFinding, ValidationMessageCatalogue } from '@reeve-iot/workflow-core'
import { useValidationMessageCatalogue, useWorkflowEditorStore, validationMessage } from '@reeve-iot/workflow-core'

/** Structural shape of a compiler diagnostic, kept client-package independent. */
export interface ValidationDiagnostic {
  ruleId: string
  message: string
  nodeId?: string | null
  portName?: string | null
  /** The server sends none today; a diagnostic that does is rendered by its code's template like a finding. */
  params?: Readonly<Record<string, unknown>>
  canvasPath?: ValidationFinding['canvasPath']
}

export type DisplayedFinding = ValidationFinding | ValidationDiagnostic

function asFinding(diagnostic: DisplayedFinding): { finding: ValidationFinding; source: 'editor' | 'publish' } {
  if ('code' in diagnostic) return { finding: diagnostic, source: 'editor' }
  return {
    source: 'publish',
    finding: {
      ...diagnostic,
      code: diagnostic.ruleId,
      nodeId: diagnostic.nodeId ?? null,
      portName: diagnostic.portName ?? undefined,
    },
  }
}

/**
 * Renders editor findings and publish diagnostics through the message catalogue. A row carries `data-code`, and a
 * row that names a node is a button that opens that node's canvas and selects it; graph-wide rows are plain text.
 */
export function ValidationFindings({
  findings,
  warnings = [],
  catalogue,
  onSelectNode,
  emptyText,
}: {
  findings: readonly DisplayedFinding[]
  /** Findings that do not block a save; they read in the warning colour. */
  warnings?: readonly DisplayedFinding[]
  /** Overrides the provider's catalogue for this list. */
  catalogue?: ValidationMessageCatalogue
  /** Called after a row's node has been selected. */
  onSelectNode?: (nodeId: string, canvasPath: ValidationFinding['canvasPath']) => void
  /** Shown as the only row when there is nothing to list. */
  emptyText?: string
}) {
  const providerCatalogue = useValidationMessageCatalogue()
  const messages = catalogue ?? providerCatalogue
  const revealNode = useWorkflowEditorStore((s) => s.revealNode)
  const rows = [
    ...findings.map((input) => ({ ...asFinding(input), severity: 'error' as const })),
    ...warnings.map((input) => ({ ...asFinding(input), severity: 'warning' as const })),
  ]
  return <List dense aria-label="Findings">
    {rows.length === 0 && emptyText !== undefined ? <ListItem>{emptyText}</ListItem> : null}
    {rows.map(({ finding, source, severity }, index) => {
      const nodeId = finding.nodeId
      const text = <ListItemText
        primary={validationMessage(finding, messages)}
        slotProps={{ primary: { color: severity === 'error' ? 'error' : 'warning.main' } }}
      />
      return <ListItem
        key={`${severity}:${finding.code}:${String(index)}`}
        disablePadding={nodeId !== null}
        data-code={finding.code}
        data-source={source}
        data-severity={severity}
        data-node-id={nodeId ?? ''}
      >
        {nodeId === null
          ? text
          : <ListItemButton onClick={() => {
            revealNode(nodeId, finding.canvasPath)
            onSelectNode?.(nodeId, finding.canvasPath)
          }}>{text}</ListItemButton>}
      </ListItem>
    })}
  </List>
}
