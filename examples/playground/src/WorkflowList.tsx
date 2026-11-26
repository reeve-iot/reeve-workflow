import { Box, Button, List, ListItemButton, Typography } from '@mui/material'
import type { WorkflowSummaryResponse } from '@reeve-iot/workflow-client'

export function WorkflowList({
  workflows, selectedId, busy, onSelect, onRefresh,
}: {
  workflows: readonly WorkflowSummaryResponse[]
  selectedId: string | null
  busy: boolean
  onSelect: (id: string) => void
  onRefresh: () => void
}) {
  return (
    <Box sx={{ width: 240, height: '100%', overflowY: 'auto', borderRight: 1, borderColor: 'divider', p: 1 }}>
      <Typography variant="subtitle2" sx={{ mt: 1 }}>Workflows</Typography>
      <Button fullWidth size="small" disabled={busy} onClick={onRefresh}>Refresh workflows</Button>
      <List dense aria-label="Workflows">
        {workflows.length === 0 ? (
          <ListItemButton disabled>No workflows yet.</ListItemButton>
        ) : (
          workflows.map((workflow) => (
            <ListItemButton
              key={workflow.id}
              selected={workflow.id === selectedId}
              data-testid={`workflow-${workflow.id}`}
              data-version={workflow.currentVersionNumber ?? ''}
              onClick={() => { onSelect(workflow.id) }}
            >
              {workflow.name}
            </ListItemButton>
          ))
        )}
      </List>
    </Box>
  )
}
