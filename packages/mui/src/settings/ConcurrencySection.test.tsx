import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import type { WorkflowConcurrency } from '@reeve-iot/workflow-core'
import { ConcurrencySection } from './ConcurrencySection.js'

const base: WorkflowConcurrency = { mode: 'Single', maxConcurrentRuns: 1, maxQueuedRuns: 0, overflowBehavior: 'Drop' }

const draw = (value: Partial<WorkflowConcurrency> = {}, onChange = vi.fn()) => {
  render(
    <ThemeProvider theme={createTheme()}>
      <ConcurrencySection value={{ ...base, ...value }} onChange={onChange} />
    </ThemeProvider>,
  )
  return onChange
}

function Controlled({ initial }: { initial: WorkflowConcurrency }) {
  const [value, setValue] = useState(initial)
  return (
    <ThemeProvider theme={createTheme()}>
      <ConcurrencySection value={value} onChange={setValue} />
    </ThemeProvider>
  )
}

describe('ConcurrencySection', () => {
  it('names the mode combobox for its purpose', () => {
    draw()
    expect(screen.getByRole('combobox', { name: 'Concurrency mode' })).toBeInTheDocument()
  })

  it('hides the concurrent-runs field in Single mode, where it is always one', () => {
    draw({ mode: 'Single' })
    expect(screen.queryByTestId('concurrency-max-concurrent-input')).not.toBeInTheDocument()
  })

  it('shows the concurrent-runs field for a mode that can run more than one', () => {
    draw({ mode: 'Parallel' })
    expect(screen.getByTestId('concurrency-max-concurrent-input')).toBeInTheDocument()
  })

  it('shows the queue size only for the queued mode', () => {
    draw({ mode: 'Parallel' })
    expect(screen.queryByTestId('concurrency-max-queued-input')).not.toBeInTheDocument()
  })

  it('shows overflow behaviour only where the limit can be exceeded', () => {
    draw({ mode: 'Restart' })
    expect(screen.queryByTestId('concurrency-overflow-select')).not.toBeInTheDocument()
  })

  it('pins concurrent runs back to one when Single is chosen', async () => {
    render(<Controlled initial={{ ...base, mode: 'Parallel', maxConcurrentRuns: 8 }} />)
    await userEvent.click(screen.getByLabelText('Concurrency mode'))
    await userEvent.click(screen.getByRole('option', { name: 'Single' }))
    await userEvent.click(screen.getByLabelText('Concurrency mode'))
    await userEvent.click(screen.getByRole('option', { name: 'Parallel' }))
    expect(screen.getByTestId('concurrency-max-concurrent-input')).toHaveValue(1)
  })

  it('refuses a concurrent-run count below one instead of saving it', () => {
    const onChange = draw({ mode: 'Parallel', maxConcurrentRuns: 2 })
    fireEvent.change(screen.getByTestId('concurrency-max-concurrent-input'), { target: { value: '0' } })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('accepts a concurrent-run count at the limit', () => {
    const onChange = draw({ mode: 'Parallel', maxConcurrentRuns: 2 })
    fireEvent.change(screen.getByTestId('concurrency-max-concurrent-input'), { target: { value: '1' } })
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ maxConcurrentRuns: 1 }))
  })

  it('explains the chosen mode rather than leaving the reader to guess', () => {
    draw({ mode: 'Restart' })
    expect(screen.getByText(/Cancel the running run/)).toBeInTheDocument()
  })
})
