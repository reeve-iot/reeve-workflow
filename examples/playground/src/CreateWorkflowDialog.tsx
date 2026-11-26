import { useState } from 'react'
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, TextField } from '@mui/material'

export function CreateWorkflowDialog({
  open, busy, onCancel, onCreate,
}: {
  open: boolean
  busy: boolean
  onCancel: () => void
  onCreate: (name: string) => void
}) {
  const [name, setName] = useState('')
  const trimmed = name.trim()

  return (
    <Dialog open={open} onClose={onCancel} fullWidth maxWidth="xs">
      <DialogTitle>New workflow</DialogTitle>
      <DialogContent>
        <TextField
          autoFocus fullWidth margin="dense"
          label="Name"
          slotProps={{ htmlInput: { 'aria-label': 'New workflow name', 'data-testid': 'new-workflow-name' } }}
          value={name}
          onChange={(event) => { setName(event.target.value) }}
        />
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel}>Cancel</Button>
        <Button
          variant="contained"
          disabled={busy || trimmed === ''}
          onClick={() => { onCreate(trimmed) }}
        >
          Create
        </Button>
      </DialogActions>
    </Dialog>
  )
}
