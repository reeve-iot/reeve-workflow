import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import { WorkflowExecutionSection } from './WorkflowExecutionSection.js'
import type { WorkflowExecutionSectionProps } from './WorkflowExecutionSection.js'

interface Values {
  timeout: number | null
  retry: string | null
  checkpoint: string | null
}

interface Spies {
  onTimeout?: (next: number | null) => void
  onRetry?: (next: string | null) => void
  onCheckpoint?: (next: string | null) => void
}

function Harness({ initial = {}, onTimeout, onRetry, onCheckpoint }: Spies & { initial?: Partial<Values> }) {
  const [values, setValues] = useState<Values>({ timeout: null, retry: null, checkpoint: null, ...initial })
  return (
    <WorkflowExecutionSection
      executionTimeoutSeconds={values.timeout}
      onExecutionTimeoutSecondsChange={(next) => { onTimeout?.(next); setValues((s) => ({ ...s, timeout: next })) }}
      retryPolicyJson={values.retry}
      onRetryPolicyJsonChange={(next) => { onRetry?.(next); setValues((s) => ({ ...s, retry: next })) }}
      checkpointPolicyJson={values.checkpoint}
      onCheckpointPolicyJsonChange={(next) => { onCheckpoint?.(next); setValues((s) => ({ ...s, checkpoint: next })) }}
    />
  )
}

const mount = (props: WorkflowExecutionSectionProps = {}) =>
  render(<ThemeProvider theme={createTheme()}><WorkflowExecutionSection {...props} /></ThemeProvider>)

const show = (props: Parameters<typeof Harness>[0] = {}) => {
  const onTimeout = vi.fn()
  const onRetry = vi.fn()
  const onCheckpoint = vi.fn()
  render(
    <ThemeProvider theme={createTheme()}>
      <Harness {...props} onTimeout={onTimeout} onRetry={onRetry} onCheckpoint={onCheckpoint} />
    </ThemeProvider>,
  )
  return { onTimeout, onRetry, onCheckpoint }
}

const put = (testId: string, text: string) => {
  const box = screen.getByTestId(testId)
  fireEvent.change(box, { target: { value: text } })
  fireEvent.blur(box)
}

const value = (testId: string) => (screen.getByTestId(testId) as HTMLInputElement).value

const choose = async (user: ReturnType<typeof userEvent.setup>, label: string, option: string) => {
  await user.click(screen.getByLabelText(label))
  await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: option }))
}

const options = () => within(screen.getByRole('listbox')).getAllByRole('option').map((o) => o.textContent)

const last = (spy: ReturnType<typeof vi.fn>) => spy.mock.calls.at(-1)?.[0]

const json = (written: unknown) => JSON.parse(written as string) as Record<string, unknown>

describe('WorkflowExecutionSection', () => {
  it('names retry and checkpoint comboboxes for their purpose', () => {
    mount({ onRetryPolicyJsonChange: vi.fn(), onCheckpointPolicyJsonChange: vi.fn() })
    expect(screen.getByRole('combobox', { name: 'Retry backoff strategy' })).toBeInTheDocument()
    expect(screen.getByRole('combobox', { name: 'Checkpoint policy mode' })).toBeInTheDocument()
  })

  describe('what the host supplies', () => {
    it('renders nothing at all where the host supplies no control', () => {
      mount()
      expect(screen.queryByTestId('workflow-execution-section')).not.toBeInTheDocument()
    })

    it('renders only the control whose callback it was given', () => {
      mount({ onCheckpointPolicyJsonChange: vi.fn() })
      expect(screen.getByLabelText('Checkpoint policy mode')).toBeInTheDocument()
      expect(screen.queryByTestId('execution-timeout-seconds')).not.toBeInTheDocument()
      expect(screen.queryByLabelText('Retry backoff strategy')).not.toBeInTheDocument()
    })
  })

  describe('the workflow\'s execution timeout', () => {
    it('writes the positive number of seconds the author typed', () => {
      const { onTimeout } = show()
      put('execution-timeout-seconds', '900')
      expect(last(onTimeout)).toBe(900)
    })

    it('writes no timeout when the box is emptied, never a zero', () => {
      const { onTimeout } = show({ initial: { timeout: 900 } })
      put('execution-timeout-seconds', '')
      expect(last(onTimeout)).toBeNull()
    })

    it.each([['0'], ['-5']])('refuses a timeout of %s rather than storing what the run rewrites', (text) => {
      const { onTimeout } = show({ initial: { timeout: 900 } })
      put('execution-timeout-seconds', text)
      expect(onTimeout).not.toHaveBeenCalled()
      expect(value('execution-timeout-seconds')).toBe(text)
      expect(screen.getByText('Must be 1 or more — the run reads a smaller number as no timeout at all')).toBeInTheDocument()
    })

    it.each([['90.5'], ['1e3'], ['0x10'], ['900s']])(
      'refuses %s, which is not the whole number the column takes',
      (text) => {
        const { onTimeout } = show({ initial: { timeout: 900 } })
        put('execution-timeout-seconds', text)
        expect(onTimeout).not.toHaveBeenCalled()
      },
    )

    it('takes a number the author padded with spaces', () => {
      const { onTimeout } = show({ initial: { timeout: 900 } })
      put('execution-timeout-seconds', ' 900 ')
      expect(last(onTimeout)).toBe(900)
    })

    it('says what the column holds rather than what a save would make of it', () => {
      show({ initial: { timeout: 900 } })
      expect(value('execution-timeout-seconds')).toBe('900')
    })

    it('flags a stored timeout the run reads as none, and writes no such value back', () => {
      show({ initial: { timeout: 0 } })
      expect(screen.getByText(/The run reads it as no timeout at all; this editor writes no such key/)).toBeInTheDocument()
    })

    it('takes the longest deadline .NET can hand a run: 4294967 seconds', () => {
      const { onTimeout } = show()
      put('execution-timeout-seconds', '4294967')
      expect(last(onTimeout)).toBe(4294967)
    })

    it('refuses the second past it, which every run would throw on before it started', () => {
      const { onTimeout } = show({ initial: { timeout: 900 } })
      put('execution-timeout-seconds', '4294968')
      expect(onTimeout).not.toHaveBeenCalled()
      expect(value('execution-timeout-seconds')).toBe('4294968')
      expect(screen.getByText('At most 4294967 seconds — .NET refuses to start a run with a longer deadline')).toBeInTheDocument()
    })

    it('flags a stored deadline no run could be given, and writes no such value back', () => {
      show({ initial: { timeout: 4294968 } })
      expect(screen.getByText(/Stored beyond what a run can be given; this editor writes no such value/)).toBeInTheDocument()
    })
  })

  describe('the workflow\'s retry policy', () => {
    it('offers exactly the two strategies the run reads, and no policy', async () => {
      const user = userEvent.setup()
      show()
      await user.click(screen.getByLabelText('Retry backoff strategy'))
      expect(options()).toEqual([
        'None — the workflow names no retry policy', 'Fixed delay', 'Exponential backoff',
      ])
    })

    it('writes a policy the model deserialises into WorkflowRetryPolicy', async () => {
      const user = userEvent.setup()
      const { onRetry } = show()
      await choose(user, 'Retry backoff strategy', 'Fixed delay')
      put('retry-policy-max-attempts', '4')
      put('retry-policy-initial-delay-ms', '250')
      expect(json(last(onRetry))).toEqual({ BackoffStrategy: 'fixed', MaxAttempts: 4, InitialDelayMs: 250 })
    })

    it('writes the exponential spelling the reader reads', async () => {
      const user = userEvent.setup()
      const { onRetry } = show({ initial: { retry: '{"MaxAttempts":2}' } })
      await choose(user, 'Retry backoff strategy', 'Exponential backoff')
      expect(json(last(onRetry))).toEqual({ BackoffStrategy: 'exponential', MaxAttempts: 2 })
    })

    it('takes the policy off the column when the author chooses none', async () => {
      const user = userEvent.setup()
      const { onRetry } = show({ initial: { retry: '{"BackoffStrategy":"exponential","MaxAttempts":3,"InitialDelayMs":250}' } })
      await choose(user, 'Retry backoff strategy', 'None — the workflow names no retry policy')
      expect(last(onRetry)).toBeNull()
    })

    it.each([['0'], ['-1']])('never writes a MaxAttempts of %s, which the reader refuses', (text) => {
      const { onRetry } = show({ initial: { retry: '{"BackoffStrategy":"fixed","MaxAttempts":3}' } })
      put('retry-policy-max-attempts', text)
      expect(onRetry).not.toHaveBeenCalled()
      expect(screen.getByText('Must be 1 or more — the run makes one attempt whatever a lower number says')).toBeInTheDocument()
    })

    it('never writes an InitialDelayMs of zero, which the reader refuses', () => {
      const { onRetry } = show({ initial: { retry: '{"BackoffStrategy":"fixed","InitialDelayMs":250}' } })
      put('retry-policy-initial-delay-ms', '0')
      expect(onRetry).not.toHaveBeenCalled()
      expect(screen.getByText('Must be 1 or more — the run reads a delay of 0 or less as no delay at all')).toBeInTheDocument()
    })

    it('omits a count the author left out rather than writing a zero', () => {
      const { onRetry } = show({ initial: { retry: '{"BackoffStrategy":"fixed","MaxAttempts":3,"InitialDelayMs":250}' } })
      put('retry-policy-max-attempts', '')
      const written = json(last(onRetry))
      expect(written).toEqual({ BackoffStrategy: 'fixed', InitialDelayMs: 250 })
      expect(written).not.toHaveProperty('MaxAttempts')
    })

    it('writes the policy that names only what the author named', () => {
      const { onRetry } = show()
      put('retry-policy-initial-delay-ms', '500')
      expect(json(last(onRetry))).toEqual({ BackoffStrategy: 'fixed', InitialDelayMs: 500 })
    })

    it('reads a policy naming no strategy as fixed, which is what the run reads it as', async () => {
      const user = userEvent.setup()
      show({ initial: { retry: '{"MaxAttempts":3}' } })
      await user.click(screen.getByLabelText('Retry backoff strategy'))
      expect(screen.getByLabelText('Retry backoff strategy')).toHaveTextContent('Fixed delay')
    })

    it('reads `Exponential` as the exponential policy the run reads it as', () => {
      show({ initial: { retry: '{"BackoffStrategy":"Exponential","MaxAttempts":2}' } })
      expect(screen.getByLabelText('Retry backoff strategy')).toHaveTextContent('Exponential backoff')
    })

    it('reads the three keys the deserialiser matches without regard to case', () => {
      show({ initial: { retry: '{"maxAttempts":4,"initialDelayMs":50,"backoffStrategy":"exponential"}' } })
      expect(screen.getByLabelText('Retry backoff strategy')).toHaveTextContent('Exponential backoff')
      expect(value('retry-policy-max-attempts')).toBe('4')
      expect(value('retry-policy-initial-delay-ms')).toBe('50')
    })

    it('reads keys written in any other case too', () => {
      show({ initial: { retry: '{"MAXATTEMPTS":3,"backOFFStrategy":"fixed"}' } })
      expect(value('retry-policy-max-attempts')).toBe('3')
      expect(screen.getByLabelText('Retry backoff strategy')).toHaveTextContent('Fixed delay')
    })

    it('carries a camel-case policy\'s own values through an edit, in the server\'s one spelling', () => {
      const { onRetry } = show({
        initial: { retry: '{"maxAttempts":4,"initialDelayMs":50,"backoffStrategy":"exponential"}' },
      })
      put('retry-policy-initial-delay-ms', '75')
      expect(json(last(onRetry))).toEqual({ BackoffStrategy: 'exponential', MaxAttempts: 4, InitialDelayMs: 75 })
    })

    it.each([
      ['{"MaxAttempts":3,"maxAttempts":4}'],
      ['{"BackoffStrategy":"fixed","backoffStrategy":"exponential"}'],
      ['{"InitialDelayMs":50,"initialdelayms":60}'],
      ['{"MaxAttempts":3,"MaxAttempts":4}'],
    ])('says so for %s, one name written twice', (stored) => {
      show({ initial: { retry: stored } })
      expect(screen.getByLabelText('Retry backoff strategy')).toHaveTextContent('Unreadable — the run reads no policy here')
    })

    it.each([['nope'], ['[1]'], ['{"MaxAttempts":"3"}'], ['{"BackoffStrategy":5}'], ['{"MaxAttempts":2.5}']])(
      'says so for %s, which the run reads as no policy at all',
      (stored) => {
        show({ initial: { retry: stored } })
        expect(screen.getByLabelText('Retry backoff strategy')).toHaveTextContent('Unreadable — the run reads no policy here')
      },
    )

    it('replaces text the run cannot read with a policy it can, once the author names a count', () => {
      const { onRetry } = show({ initial: { retry: '{"MaxAttempts":"3"}' } })
      put('retry-policy-max-attempts', '5')
      expect(json(last(onRetry))).toEqual({ BackoffStrategy: 'fixed', MaxAttempts: 5 })
    })

    it('shows a stored count the run rewrites rather than silently dropping it', () => {
      show({ initial: { retry: '{"MaxAttempts":0}' } })
      expect(value('retry-policy-max-attempts')).toBe('0')
      expect(screen.getByText(/The run reads it as one attempt; this editor writes no such key/)).toBeInTheDocument()
    })

    it('writes nothing while the author is still typing, on every box', () => {
      const { onRetry, onTimeout } = show()
      const attempts = screen.getByTestId('retry-policy-max-attempts')
      fireEvent.change(attempts, { target: { value: '1' } })
      fireEvent.change(attempts, { target: { value: '12' } })
      fireEvent.change(screen.getByTestId('execution-timeout-seconds'), { target: { value: '6' } })
      expect(onRetry).not.toHaveBeenCalled()
      expect(onTimeout).not.toHaveBeenCalled()
    })
  })

  describe('the workflow\'s checkpoint policy', () => {
    it('offers exactly the modes WorkflowCheckpointPolicy.Read names', async () => {
      const user = userEvent.setup()
      show()
      await user.click(screen.getByLabelText('Checkpoint policy mode'))
      expect(options()).toEqual([
        'None — the engine decides', 'Every block', 'Before non-idempotent blocks only', 'Heartbeat',
      ])
    })

    it('writes the mode by the name the reader parses', async () => {
      const user = userEvent.setup()
      const { onCheckpoint } = show()
      await choose(user, 'Checkpoint policy mode', 'Before non-idempotent blocks only')
      expect(json(last(onCheckpoint))).toEqual({ Mode: 'BeforeNonIdempotentOnly' })
    })

    it('takes the policy off the column when the author chooses none', async () => {
      const user = userEvent.setup()
      const { onCheckpoint } = show({ initial: { checkpoint: '{"Mode":"EveryBlock"}' } })
      await choose(user, 'Checkpoint policy mode', 'None — the engine decides')
      expect(last(onCheckpoint)).toBeNull()
    })

    it('writes a heartbeat only once an interval makes it a policy the reader takes', async () => {
      const user = userEvent.setup()
      const { onCheckpoint } = show()
      await choose(user, 'Checkpoint policy mode', 'Heartbeat')
      expect(onCheckpoint).not.toHaveBeenCalled()
      expect(screen.getByTestId('checkpoint-policy-heartbeat-seconds')).toBeInTheDocument()
      put('checkpoint-policy-heartbeat-seconds', '30')
      expect(json(last(onCheckpoint))).toEqual({ Mode: 'Heartbeat', HeartbeatSeconds: 30 })
    })

    it('never writes an interval of zero or less, which the reader refuses', () => {
      const { onCheckpoint } = show({ initial: { checkpoint: '{"Mode":"Heartbeat","HeartbeatSeconds":30}' } })
      put('checkpoint-policy-heartbeat-seconds', '0')
      expect(onCheckpoint).not.toHaveBeenCalled()
      expect(screen.getByText('Must be 1 or more — an interval of zero or less is not one the reader takes')).toBeInTheDocument()
    })

    it('carries no interval onto a mode that writes on none', async () => {
      const user = userEvent.setup()
      const { onCheckpoint } = show({ initial: { checkpoint: '{"Mode":"Heartbeat","HeartbeatSeconds":30}' } })
      await choose(user, 'Checkpoint policy mode', 'Every block')
      expect(json(last(onCheckpoint))).toEqual({ Mode: 'EveryBlock' })
    })

    it('keeps the interval on screen when the box is emptied, writing nothing', () => {
      const { onCheckpoint } = show({ initial: { checkpoint: '{"Mode":"Heartbeat","HeartbeatSeconds":30}' } })
      put('checkpoint-policy-heartbeat-seconds', '')
      expect(onCheckpoint).not.toHaveBeenCalled()
      expect(value('checkpoint-policy-heartbeat-seconds')).toBe('')
    })

    it.each([
      ['nope'],
      ['[1]'],
      ['{"Mode":3}'],
      ['{"Mode":"everyblock"}'],
      ['{"Mode":"Unknown"}'],
      ['{"Mode":"Heartbeat"}'],
      ['{"Mode":"Heartbeat","HeartbeatSeconds":0}'],
      ['{"Mode":"EveryBlock","HeartbeatSeconds":30}'],
    ])('says so for %s, which WorkflowCheckpointPolicy.Read refuses', (stored) => {
      show({ initial: { checkpoint: stored } })
      expect(screen.getByLabelText('Checkpoint policy mode'))
        .toHaveTextContent('Unreadable — the server refuses this policy at the next save')
    })

    it('reads a policy with a mode and nothing else as that mode', () => {
      show({ initial: { checkpoint: '{"Mode":"EveryBlock"}' } })
      expect(screen.getByLabelText('Checkpoint policy mode')).toHaveTextContent('Every block')
      expect(screen.queryByTestId('checkpoint-policy-heartbeat-seconds')).not.toBeInTheDocument()
    })

    it('reads the two keys the deserialiser matches without regard to case', () => {
      show({ initial: { checkpoint: '{"mode":"EveryBlock"}' } })
      expect(screen.getByLabelText('Checkpoint policy mode')).toHaveTextContent('Every block')
    })

    it('reads a camel-case heartbeat policy and carries it through an edit of its interval', () => {
      const { onCheckpoint } = show({ initial: { checkpoint: '{"mode":"Heartbeat","heartbeatSeconds":30}' } })
      expect(screen.getByLabelText('Checkpoint policy mode')).toHaveTextContent('Heartbeat')
      expect(value('checkpoint-policy-heartbeat-seconds')).toBe('30')
      put('checkpoint-policy-heartbeat-seconds', '45')
      expect(json(last(onCheckpoint))).toEqual({ Mode: 'Heartbeat', HeartbeatSeconds: 45 })
    })

    it.each([['{"Mode":"EveryBlock","mode":"Heartbeat"}'], ['{"Mode":"EveryBlock","Mode":"Heartbeat"}']])(
      'says so for %s, a policy naming one key twice',
      (stored) => {
        show({ initial: { checkpoint: stored } })
        expect(screen.getByLabelText('Checkpoint policy mode'))
          .toHaveTextContent('Unreadable — the server refuses this policy at the next save')
      },
    )
  })
})
