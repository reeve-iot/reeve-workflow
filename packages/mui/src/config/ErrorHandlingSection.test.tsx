import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import type { NodeErrorPolicy } from '@reeve-iot/workflow-core'
import { ErrorHandlingSection } from './ErrorHandlingSection.js'

interface HarnessProps {
  initial: NodeErrorPolicy | null
  onChange: (policy: NodeErrorPolicy) => void
  onReset: () => void
  withoutReset?: boolean
}

function Harness({ initial, onChange, onReset, withoutReset }: HarnessProps) {
  const [policy, setPolicy] = useState<NodeErrorPolicy | null>(initial)
  return (
    <ThemeProvider theme={createTheme()}>
      <ErrorHandlingSection
        errorPolicy={policy}
        onChange={(next) => { onChange(next); setPolicy(next) }}
        onReset={withoutReset
          ? undefined
          : () => { onReset(); setPolicy(null) }}
      />
    </ThemeProvider>
  )
}

interface OpenOptions {
  initial?: NodeErrorPolicy | null
  withoutReset?: boolean
}

const open = ({ initial = null, withoutReset = false }: OpenOptions = {}) => {
  const onChange = vi.fn<(policy: NodeErrorPolicy) => void>()
  const onReset = vi.fn<() => void>()
  render(<Harness initial={initial} onChange={onChange} onReset={onReset} withoutReset={withoutReset} />)
  return { onChange, onReset }
}

const expand = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(screen.getByText('Error Handling'))
}

const lastWrite = (onChange: ReturnType<typeof open>['onChange']) => onChange.mock.calls.at(-1)?.[0]

const box = () => screen.getByLabelText('Default Output (JSON)') as HTMLTextAreaElement
const put = (text: string) => { fireEvent.change(box(), { target: { value: text } }) }

const CONTINUE: NodeErrorPolicy = { policy: 'ContinueWithDefault' }

describe('ErrorHandlingSection', () => {
  describe('a node\'s policy back to absent', () => {
    it('offers the four explicit policies and nothing else', async () => {
      const user = userEvent.setup()
      open()
      await expand(user)
      await user.click(screen.getByLabelText('Error Policy'))
      expect(within(screen.getByRole('listbox')).getAllByRole('option').map((o) => o.textContent)).toEqual([
        'Fail Workflow (Default)', 'Continue With Default Output', 'Emit Error to Virtual Port', 'Skip Node & Downstream',
      ])
    })

    it('offers the reset only where the host can drop the policy', () => {
      open({ withoutReset: true })
      expect(screen.queryByRole('button', { name: 'Reset to default' })).not.toBeInTheDocument()
    })

    it('takes the node back to no policy at all when the author resets', async () => {
      const user = userEvent.setup()
      const { onReset } = open({ initial: { policy: 'Skip' } })
      await expand(user)
      await user.click(screen.getByRole('button', { name: 'Reset to default' }))
      expect(onReset).toHaveBeenCalledTimes(1)
      expect(screen.getByLabelText('Error Policy')).toHaveTextContent('Fail Workflow (Default)')
    })
  })

  describe('errorPolicy.defaultOutputJson', () => {
    it('writes the key while the text is a JSON object', async () => {
      const user = userEvent.setup()
      const { onChange } = open({ initial: CONTINUE })
      await expand(user)
      put('{"Result": 42}')
      expect(lastWrite(onChange)).toEqual({ policy: 'ContinueWithDefault', defaultOutputJson: '{"Result": 42}' })
    })

    it('keeps text that does not parse on screen and writes nothing', async () => {
      const user = userEvent.setup()
      const { onChange } = open({ initial: CONTINUE })
      await expand(user)
      put('{"Result": 42')
      expect(onChange).not.toHaveBeenCalled()
      expect(box().value).toBe('{"Result": 42')
      expect(screen.getByText('Must be valid JSON')).toBeInTheDocument()
    })

    it('keeps text that repeats an output name on screen, writes nothing and names the member', async () => {
      const user = userEvent.setup()
      const { onChange } = open({ initial: CONTINUE })
      await expand(user)
      put('{"Result": 1, "Result": 2}')
      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText("Ambiguous JSON: the member name 'Result' occurs more than once in the object at $")).toBeInTheDocument()
    })

    it.each([['1'], ['"text"'], ['[1, 2]'], ['null']])(
      'refuses %s, which a run cannot read as a map of outputs',
      async (text) => {
        const user = userEvent.setup()
        const { onChange } = open({ initial: CONTINUE })
        await expand(user)
        put(text)
        expect(onChange).not.toHaveBeenCalled()
        expect(screen.getByText('Must be a JSON object')).toBeInTheDocument()
      },
    )

    it('removes the key when the box is emptied, never writing an empty string', async () => {
      const user = userEvent.setup()
      const { onChange } = open({ initial: { policy: 'ContinueWithDefault', defaultOutputJson: '{"o":1}' } })
      await expand(user)
      put('')
      const written = lastWrite(onChange)
      expect(written).toEqual({ policy: 'ContinueWithDefault' })
      expect(written).not.toHaveProperty('defaultOutputJson')
    })

    it('shows no parse error for an empty box, which is the absence of the key', async () => {
      const user = userEvent.setup()
      open({ initial: CONTINUE })
      await expand(user)
      put('{')
      expect(screen.getByText('Must be valid JSON')).toBeInTheDocument()
      put('')
      expect(screen.queryByText('Must be valid JSON')).not.toBeInTheDocument()
      expect(box().value).toBe('')
    })

    it('leaves the text the author wrote on screen after a refused edit, not the stored value', async () => {
      const user = userEvent.setup()
      open({ initial: { policy: 'ContinueWithDefault', defaultOutputJson: '{"o":1}' } })
      await expand(user)
      put('{o:1}')
      expect(box().value).toBe('{o:1}')
    })
  })
})
