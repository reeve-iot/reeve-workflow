import type { ChangeEvent } from 'react'
import { Box, FormControl, FormHelperText, InputLabel, MenuItem, Select, Stack, TextField, Typography } from '@mui/material'
import type { SelectChangeEvent } from '@mui/material'
import type { WorkflowConcurrency, WorkflowConcurrencyMode, WorkflowOverflowBehavior } from '@reeve-iot/workflow-core'

export interface ConcurrencySectionProps {
  value: WorkflowConcurrency
  onChange: (next: WorkflowConcurrency) => void
}

const MODE_OPTIONS: { value: WorkflowConcurrencyMode; label: string; description: string }[] = [
  { value: 'Single', label: 'Single', description: 'Drop new triggers while a run is in progress.' },
  { value: 'Restart', label: 'Restart', description: 'Cancel the running run and start the new one.' },
  { value: 'Queued', label: 'Queued', description: 'Queue new triggers up to the configured limit; apply overflow behaviour once full.' },
  { value: 'Parallel', label: 'Parallel', description: 'Run multiple triggers concurrently up to the configured maximum.' },
]

const OVERFLOW_OPTIONS: { value: WorkflowOverflowBehavior; label: string; description: string }[] = [
  { value: 'Drop', label: 'Drop', description: 'Silently discard overflow triggers.' },
  { value: 'Log', label: 'Log', description: 'Discard overflow triggers and log a warning.' },
  { value: 'Error', label: 'Error', description: 'Throw an error so the trigger fails loudly.' },
]

/** Concurrency settings. The fields that cannot apply to the chosen mode are hidden rather than disabled,
 * so the form never shows a number the engine would ignore. */
export function ConcurrencySection({ value, onChange }: ConcurrencySectionProps) {
  const changeMode = (event: SelectChangeEvent<WorkflowConcurrencyMode>) => {
    const mode = event.target.value as WorkflowConcurrencyMode
    onChange({ ...value, mode, maxConcurrentRuns: mode === 'Single' ? 1 : value.maxConcurrentRuns })
  }

  const changeNumber = (field: 'maxConcurrentRuns' | 'maxQueuedRuns', min: number) => (event: ChangeEvent<HTMLInputElement>) => {
    const parsed = parseInt(event.target.value, 10)
    if (Number.isNaN(parsed) || parsed < min) return
    onChange({ ...value, [field]: parsed })
  }

  const showMaxConcurrent = value.mode !== 'Single'
  const showMaxQueued = value.mode === 'Queued'
  const showOverflow = value.mode === 'Queued' || value.mode === 'Parallel'

  return (
    <Box data-testid="concurrency-section" sx={{ mt: 2 }}>
      <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>Concurrency</Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
        Controls how multiple trigger fires for this workflow are coordinated.
      </Typography>

      <Stack spacing={2}>
        <FormControl fullWidth data-testid="concurrency-mode-select">
          <InputLabel>Mode</InputLabel>
          <Select label="Mode" value={value.mode} onChange={changeMode} slotProps={{ input: { 'aria-label': 'Concurrency mode' } }}>
            {MODE_OPTIONS.map((option) => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
          </Select>
          <FormHelperText>{MODE_OPTIONS.find((m) => m.value === value.mode)?.description ?? ''}</FormHelperText>
        </FormControl>

        {showMaxConcurrent && (
          <TextField
            type="number" label="Max concurrent runs" fullWidth value={value.maxConcurrentRuns}
            onChange={changeNumber('maxConcurrentRuns', 1)}
            slotProps={{ htmlInput: { min: 1, 'data-testid': 'concurrency-max-concurrent-input' } }}
            helperText="Number of runs allowed to execute at the same time. Must be >= 1."
          />
        )}

        {showMaxQueued && (
          <TextField
            type="number" label="Max queued runs" fullWidth value={value.maxQueuedRuns}
            onChange={changeNumber('maxQueuedRuns', 0)}
            slotProps={{ htmlInput: { min: 0, 'data-testid': 'concurrency-max-queued-input' } }}
            helperText="Capacity of the wait queue. Triggers beyond this apply the overflow behaviour. Must be >= 0."
          />
        )}

        {showOverflow && (
          <FormControl fullWidth data-testid="concurrency-overflow-select">
            <InputLabel id="concurrency-overflow-label">Overflow behavior</InputLabel>
            <Select
              labelId="concurrency-overflow-label" label="Overflow behavior" value={value.overflowBehavior}
              onChange={(event: SelectChangeEvent<WorkflowOverflowBehavior>) => onChange({ ...value, overflowBehavior: event.target.value as WorkflowOverflowBehavior })}
              slotProps={{ input: { 'aria-label': 'Overflow behavior' } }}
            >
              {OVERFLOW_OPTIONS.map((option) => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
            </Select>
            <FormHelperText>{OVERFLOW_OPTIONS.find((o) => o.value === value.overflowBehavior)?.description ?? ''}</FormHelperText>
          </FormControl>
        )}
      </Stack>
    </Box>
  )
}
