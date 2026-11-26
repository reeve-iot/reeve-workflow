import { Box, Button, List, ListItem, ListItemText, Typography } from '@mui/material'
import type { WorkflowVersionSummaryResponse } from '@reeve-iot/workflow-client'

export function VersionList({
  versions, currentVersionNumber, busy, onRollback,
}: {
  versions: readonly WorkflowVersionSummaryResponse[]
  currentVersionNumber: number | null
  busy: boolean
  onRollback: (versionNumber: number) => void
}) {
  return (
    <Box sx={{ p: 1, minHeight: 0, overflow: 'auto' }}>
      <Typography variant="subtitle2">Versions</Typography>
      <List dense aria-label="Versions" data-testid="versions">
        {versions.length === 0 ? (
          <ListItem>No versions yet.</ListItem>
        ) : (
          versions.map((version) => {
            const states = [version.isPublished ? 'published' : 'draft']
            if (version.versionNumber === currentVersionNumber) states.push('current')
            return (
              <ListItem
                key={version.id}
                data-version={version.versionNumber}
                data-published={version.isPublished ? 'true' : 'false'}
                data-current={version.versionNumber === currentVersionNumber ? 'true' : 'false'}
                sx={{ flexDirection: 'column', alignItems: 'flex-start', gap: 0.5 }}
              >
                <ListItemText primary={`Version ${String(version.versionNumber)} (${states.join(', ')})`} />
                <Button
                  size="small"
                  disabled={busy || version.versionNumber === currentVersionNumber}
                  onClick={() => { onRollback(version.versionNumber) }}
                >
                  {`Roll back to version ${String(version.versionNumber)}`}
                </Button>
              </ListItem>
            )
          })
        )}
      </List>
    </Box>
  )
}
