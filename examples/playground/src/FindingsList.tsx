import { Box, Typography } from '@mui/material'
import type { CompilationDiagnostic } from '@reeve-iot/workflow-client'
import type { StoredDocumentFault, ValidationFinding } from '@reeve-iot/workflow-core'
import { storedDocumentFaultFinding, useWorkflowEditorStore } from '@reeve-iot/workflow-core'
import { ValidationFindings } from '@reeve-iot/workflow-mui'

export function FindingsList({
  openedFault,
  publishDiagnostics,
}: {
  openedFault: StoredDocumentFault | null
  publishDiagnostics: readonly CompilationDiagnostic[]
}) {
  useWorkflowEditorStore((s) => s.nodes)
  useWorkflowEditorStore((s) => s.edges)
  useWorkflowEditorStore((s) => s.canvasStack)
  useWorkflowEditorStore((s) => s.blockTypes)
  useWorkflowEditorStore((s) => s.workflowInputParams)
  useWorkflowEditorStore((s) => s.workflowOutputParams)
  useWorkflowEditorStore((s) => s.loadedDocumentFault)
  const findings = useWorkflowEditorStore((s) => s.validateWorkflowFindings)()
  const structure = useWorkflowEditorStore((s) => s.validateStructureFindings)()
  const warnings = useWorkflowEditorStore((s) => s.validateWorkflowWarningFindings)()

  const opened: ValidationFinding[] = openedFault === null ? [] : [storedDocumentFaultFinding(openedFault, 'The opened file', 'signature-unreadable')]

  return (
    <Box sx={{ p: 1, minHeight: 0, overflow: 'auto' }}>
      <Typography variant="subtitle2">Findings</Typography>
      <ValidationFindings
        findings={[...findings, ...structure, ...publishDiagnostics, ...opened]}
        warnings={warnings}
        emptyText="No findings."
      />
    </Box>
  )
}
