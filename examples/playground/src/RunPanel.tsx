import { Box, List, ListItem, Typography } from '@mui/material'
import type { WorkflowExecutionStepResponse } from '@reeve-iot/workflow-client'

export interface RunResult {
  readonly outputs: unknown
  readonly executionStatus: string | null
  readonly steps: readonly WorkflowExecutionStepResponse[]
}

export function RunPanel({ result }: { result: RunResult | null }) {
  if (result === null) return null

  return (
    <Box sx={{ p: 1, minHeight: 0, overflow: 'auto' }}>
      <Typography variant="subtitle2">Run output</Typography>
      {result.executionStatus !== null && (
        <Typography variant="body2" data-testid="run-status">{`Status: ${result.executionStatus}`}</Typography>
      )}
      <Box component="pre" data-testid="run-output" sx={{ fontSize: 12, m: 0, overflowX: 'auto' }}>
        {JSON.stringify(result.outputs, null, 2)}
      </Box>
      <Typography variant="subtitle2" sx={{ mt: 1 }}>Node trace</Typography>
      <List dense aria-label="Node trace" data-testid="run-steps">
        {result.steps.length === 0 ? (
          <ListItem>No steps recorded.</ListItem>
        ) : (
          result.steps.map((step) => (
            <ListItem key={step.id} data-node-id={step.nodeId} data-status={step.status}>
              {`${step.nodeType} (${step.nodeId}) — ${step.status}`}
            </ListItem>
          ))
        )}
      </List>
    </Box>
  )
}
