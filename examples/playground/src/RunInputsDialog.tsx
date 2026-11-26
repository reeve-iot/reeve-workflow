import { useState } from 'react'
import { Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, TextField } from '@mui/material'
import type { WorkflowParameter } from '@reeve-iot/workflow-core'
import { describeRepeatedMember, findRepeatedMember } from '@reeve-iot/workflow-core'

export function RunInputsDialog({
  open, parameters, onCancel, onRun,
}: {
  open: boolean
  parameters: readonly WorkflowParameter[]
  onCancel: () => void
  onRun: (values: Record<string, unknown> | null) => void
}) {
  const [textByName, setTextByName] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)

  const submit = () => {
    const values: Record<string, unknown> = {}
    for (const parameter of parameters) {
      const text = textByName[parameter.name] ?? ''
      if (text.trim() === '') continue
      try {
        values[parameter.name] = JSON.parse(text) as unknown
      } catch {
        setError(`Run input '${parameter.name}' must contain valid JSON, or be left empty to omit it.`)
        return
      }
      const repeat = findRepeatedMember(text)
      if (repeat !== undefined) {
        setError(`Run input '${parameter.name}' is ambiguous JSON: ${describeRepeatedMember(repeat)}.`)
        return
      }
    }
    setError(null)
    onRun(Object.keys(values).length === 0 ? null : values)
  }

  return (
    <Dialog open={open} onClose={onCancel} fullWidth maxWidth="sm">
      <DialogTitle>Run input parameters</DialogTitle>
      <DialogContent>
        {parameters.map((parameter) => {
          const name = parameter.displayName || parameter.name
          return (
            <TextField
              key={parameter.name}
              fullWidth
              margin="dense"
              label={`${name} (JSON)`}
              slotProps={{ htmlInput: { 'aria-label': `Run input ${name}` } }}
              value={textByName[parameter.name] ?? ''}
              onChange={(event) => {
                setTextByName((current) => ({ ...current, [parameter.name]: event.target.value }))
                setError(null)
              }}
            />
          )
        })}
        {error !== null && <Alert severity="error">{error}</Alert>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onCancel}>Cancel</Button>
        <Button variant="contained" onClick={submit}>Run workflow</Button>
      </DialogActions>
    </Dialog>
  )
}
