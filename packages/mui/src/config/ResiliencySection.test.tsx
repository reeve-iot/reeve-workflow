import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ResiliencySection } from './ResiliencySection.js'

const put = (label: string, text: string) => {
  fireEvent.change(screen.getByLabelText(label), { target: { value: text } })
}

const box = (label: string) => screen.getByLabelText(label) as HTMLInputElement

describe('ResiliencySection', () => {
  it('never leaves the timeout box unable to answer, even without somewhere to clear to', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} isContainer />)

    await user.clear(screen.getByLabelText('Timeout (seconds)'))
    await user.tab()

    expect(onChange).toHaveBeenCalled()
  })

  it('clears through the seam when the host gave one', async () => {
    const user = userEvent.setup()
    const onClear = vi.fn()
    render(
      <ResiliencySection
        configuration={{ timeoutSeconds: 45 }} onChange={vi.fn()} isContainer onClear={onClear}
      />,
    )

    await user.clear(screen.getByLabelText('Timeout (seconds)'))
    await user.tab()

    expect(onClear).toHaveBeenCalledWith(['timeoutSeconds'])
  })

  it('takes the key off a leaf too, rather than storing the deadline the engine applies anyway', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const onClear = vi.fn()
    render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} onClear={onClear} />)

    await user.clear(screen.getByLabelText('Timeout (seconds)'))
    await user.tab()

    expect(onClear).toHaveBeenCalledWith(['timeoutSeconds'])
    expect(onChange).not.toHaveBeenCalled()
  })

  it('draws the deadline a leaf will get as a hint, not as its value', () => {
    render(<ResiliencySection configuration={{}} onChange={vi.fn()} onClear={vi.fn()} />)

    expect(box('Timeout (seconds)').value).toBe('')
    expect(box('Timeout (seconds)').placeholder).toBe('30')
  })

  it('gives a container no hint at all, since the engine applies it no deadline', () => {
    render(<ResiliencySection configuration={{}} onChange={vi.fn()} onClear={vi.fn()} isContainer />)

    expect(box('Timeout (seconds)').placeholder).toBe('')
  })

  describe('says what the engine will do with retries, not the opposite', () => {
    const choice = () => screen.getByLabelText('Retry') as HTMLSelectElement

    it('leaves a node that says nothing on the block own declaration', () => {
      render(<ResiliencySection configuration={{}} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(choice().value).toBe('default')
      expect(screen.queryByLabelText('Retry Count')).toBeNull()
    })

    it.each([[3], [0], [-1]])(
      'does not read a stored count of %s as anybody having decided anything',
      (retryCount) => {
        render(<ResiliencySection configuration={{ retryCount }} onChange={vi.fn()} onClear={vi.fn()} />)

        expect(choice().value).toBe('default')
      },
    )

    it('writes the flag when the author turns retries on', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{}} onChange={onChange} onClear={vi.fn()} />)

      fireEvent.change(choice(), { target: { value: 'on' } })

      expect(onChange).toHaveBeenCalledWith('retryEnabled', true)
    })

    it('writes the refusal when the author turns retries off', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{}} onChange={onChange} onClear={vi.fn()} />)

      fireEvent.change(choice(), { target: { value: 'off' } })

      expect(onChange).toHaveBeenCalledWith('retryEnabled', false)
    })

    it('shows a node that refused retries as refusing them, whatever count it carries', () => {
      render(
        <ResiliencySection configuration={{ retryEnabled: false, retryCount: 5 }} onChange={vi.fn()} onClear={vi.fn()} />,
      )

      expect(choice().value).toBe('off')
    })

    it('says so when the stored flag is one the engine cannot read', () => {
      render(<ResiliencySection configuration={{ retryEnabled: 'yes' }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(choice().value).toBe('unreadable')
      expect(screen.getByText(/cannot read/i)).toBeTruthy()
    })

    it('treats a null count as no count at all, as the engine does', () => {
      render(<ResiliencySection configuration={{ retryCount: null }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.queryByLabelText('Retry Count')).toBeNull()
    })

    it('does not promise retries for a count of zero the author left on an enabled node', () => {
      render(
        <ResiliencySection configuration={{ retryEnabled: true, retryCount: 0 }} onChange={vi.fn()} onClear={vi.fn()} />,
      )

      expect(screen.getByText(/0 means none/i)).toBeTruthy()
    })

    it('takes every retry key off the node when the author goes back to the default', () => {
      const onClear = vi.fn()
      const onChange = vi.fn()
      render(
        <ResiliencySection
          configuration={{ retryEnabled: false, retryCount: 5, retryBackoffSeconds: 2 }}
          onChange={onChange} onClear={onClear}
        />,
      )

      fireEvent.change(choice(), { target: { value: 'default' } })

      expect(onClear).toHaveBeenCalledWith(['retryEnabled', 'retryCount', 'retryBackoffSeconds'])
      expect(onChange).not.toHaveBeenCalled()
    })

    it('cannot offer the default to a host with nowhere to clear to', () => {
      render(<ResiliencySection configuration={{ retryEnabled: true }} onChange={vi.fn()} />)

      expect(screen.getByRole('option', { name: /default/i })).toBeDisabled()
    })
  })

  describe('commits what was typed rather than every keystroke', () => {
    it('saves nothing while the author is still typing', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

      put('Timeout (seconds)', '1')
      put('Timeout (seconds)', '10')
      put('Timeout (seconds)', '100')

      expect(onChange).not.toHaveBeenCalled()
      expect(box('Timeout (seconds)').value).toBe('100')
    })

    it('saves the number the author wrote, not its first digit', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

      put('Timeout (seconds)', '1e3')
      fireEvent.blur(box('Timeout (seconds)'))

      expect(onChange).toHaveBeenCalledWith('timeoutSeconds', 1000)
    })

    it('commits on Enter without waiting for the field to be left', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

      put('Timeout (seconds)', '90')
      expect(onChange).not.toHaveBeenCalled()
      fireEvent.keyDown(box('Timeout (seconds)'), { key: 'Enter' })

      expect(onChange).toHaveBeenCalledWith('timeoutSeconds', 90)
    })

    it('abandons the edit on Escape and shows the stored number again', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

      put('Timeout (seconds)', '99')
      fireEvent.keyDown(box('Timeout (seconds)'), { key: 'Escape' })

      expect(onChange).not.toHaveBeenCalled()
      expect(box('Timeout (seconds)').value).toBe('45')
    })
  })

  describe('says when a value already stored is one the engine refuses', () => {
    it('flags a count stored as text', () => {
      render(
        <ResiliencySection configuration={{ retryEnabled: true, retryCount: '7' }} onChange={vi.fn()} onClear={vi.fn()} />,
      )

      expect(screen.getByText(/not a number/i)).toBeTruthy()
    })

    it('flags a fractional count the engine reads with GetInt32', () => {
      render(
        <ResiliencySection configuration={{ retryEnabled: true, retryCount: 2.5 }} onChange={vi.fn()} onClear={vi.fn()} />,
      )

      expect(screen.getByText(/whole number/i)).toBeTruthy()
    })

    it('flags a stored timeout the engine cannot read, on a node that otherwise looks healthy', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: '30' }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.getByText(/not a number/i)).toBeTruthy()
    })

    it('leaves a stored value the engine accepts unflagged', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.queryByText(/not a number/i)).toBeNull()
      expect(screen.queryByText(/whole number/i)).toBeNull()
    })

    it('shows the author what their node stores, not a default the engine will never reach', () => {
      render(
        <ResiliencySection configuration={{ retryEnabled: true, retryCount: 'three' }} onChange={vi.fn()} onClear={vi.fn()} />,
      )

      expect(box('Retry Count').value).toBe('three')
    })

    it('shows a stored timeout the engine refuses rather than the block default', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: '45' }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(box('Timeout (seconds)').value).toBe('45')
    })

    it.each([
      ['0x10', 'text'],
      ['  30', 'text'],
      [45, 'number'],
    ])('renders %s in a box that can actually show it', (stored, type) => {
      render(<ResiliencySection configuration={{ timeoutSeconds: stored }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(box('Timeout (seconds)').type).toBe(type)
    })

    it.each([
      [true, /true\/false/i],
      [{ seconds: 30 }, /an object/i],
      [[30], /a list/i],
    ])('names what %s actually is rather than calling everything text', (value, expected) => {
      render(<ResiliencySection configuration={{ timeoutSeconds: value }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.getByText(expected)).toBeTruthy()
    })
  })

  describe('separates a number the engine rewrites from one that fails the run', () => {
    it('says what a stored value below the engine minimum will actually run as', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: 0 }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.getByText(/runs anything lower as 1/i)).toBeTruthy()
      expect(box('Timeout (seconds)').getAttribute('aria-invalid')).not.toBe('true')
    })

    it('marks a stored value that stops the run as the fault it is', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: 2.5 }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.getByText(/fails at this block/i)).toBeTruthy()
      expect(box('Timeout (seconds)').getAttribute('aria-invalid')).toBe('true')
    })

    it('says the engine would rewrite a number it refuses to save, not that it did', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={vi.fn()} />)

      put('Timeout (seconds)', '0')
      fireEvent.blur(box('Timeout (seconds)'))

      expect(screen.getByText(/would run anything lower as 1/i)).toBeTruthy()
    })

    it('treats a stored number outside the integer the engine reads as refused, not as rewritten', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: -3000000000 }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.getByText(/fails at this block/i)).toBeTruthy()
      expect(box('Timeout (seconds)').getAttribute('aria-invalid')).toBe('true')
    })
  })

  describe('keeps to the longest wait the engine can actually apply', () => {
    it('refuses a timeout longer than a day', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

      put('Timeout (seconds)', '604800')
      fireEvent.blur(box('Timeout (seconds)'))

      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText(/86400|one day/i)).toBeTruthy()
    })

    it('refuses a retry delay longer than a day', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ retryEnabled: true }} onChange={onChange} onClear={vi.fn()} />)

      put('Backoff (seconds)', '90000')
      fireEvent.blur(box('Backoff (seconds)'))

      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText(/86400|one day/i)).toBeTruthy()
    })

    it('flags a stored timeout past the ceiling instead of showing it as healthy', () => {
      render(<ResiliencySection configuration={{ timeoutSeconds: 604800 }} onChange={vi.fn()} onClear={vi.fn()} />)

      expect(screen.getByText(/86400|one day/i)).toBeTruthy()
      expect(box('Timeout (seconds)').getAttribute('aria-invalid')).toBe('true')
    })

    it('keeps the ceiling off a delay the engine will never hand to Polly', () => {
      render(
        <ResiliencySection
          configuration={{ retryEnabled: false, retryBackoffSeconds: 90000 }} onChange={vi.fn()} onClear={vi.fn()}
        />,
      )

      expect(screen.queryByText(/86400|one day/i)).toBeNull()
      expect(box('Backoff (seconds)').getAttribute('aria-invalid')).not.toBe('true')
    })

    it('keeps it off a delay whose node asks for no attempts either', () => {
      render(
        <ResiliencySection
          configuration={{ retryEnabled: true, retryCount: 0, retryBackoffSeconds: 90000 }}
          onChange={vi.fn()} onClear={vi.fn()}
        />,
      )

      expect(screen.queryByText(/86400|one day/i)).toBeNull()
    })

    it('says which box a refused wait is in, not always the timeout', () => {
      render(<ResiliencySection configuration={{ retryEnabled: true, retryBackoffSeconds: 90000 }} onChange={vi.fn()} onClear={vi.fn()} />)

      const said = screen.getByText(/86400|one day/i).textContent ?? ''
      expect(said).not.toMatch(/timeout/i)
    })

    it('keeps the ceiling off the retry count, which is a plain integer to the engine', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ retryEnabled: true, retryCount: 3 }} onChange={onChange} onClear={vi.fn()} />)

      put('Retry Count', '100000')
      fireEvent.blur(box('Retry Count'))

      expect(onChange).toHaveBeenCalledWith('retryCount', 100000)
    })
  })

  describe('refuses what the engine would refuse', () => {
    it.each([['0'], ['-5']])(
      'refuses a timeout of %s, which the engine would raise to a second without saying so',
      (text) => {
        const onChange = vi.fn()
        render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

        put('Timeout (seconds)', text)
        fireEvent.blur(box('Timeout (seconds)'))

        expect(onChange).not.toHaveBeenCalled()
        expect(screen.getByText(/at least 1/)).toBeTruthy()
        expect(box('Timeout (seconds)').value).toBe(text)
      },
    )

    it('refuses a fractional timeout, which the engine reads with GetInt32 and fails the definition over', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

      put('Timeout (seconds)', '1.5')
      fireEvent.blur(box('Timeout (seconds)'))

      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText(/whole number/i)).toBeTruthy()
    })

    it('refuses a timeout wider than the integer the engine reads it into', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ timeoutSeconds: 45 }} onChange={onChange} />)

      put('Timeout (seconds)', '1e10')
      fireEvent.blur(box('Timeout (seconds)'))

      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText(/too large/i)).toBeTruthy()
    })

    it('refuses a negative retry count, which the engine reads as no retries at all', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ retryCount: 3 }} onChange={onChange} />)

      put('Retry Count', '-1')
      fireEvent.blur(box('Retry Count'))

      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText(/at least 0/)).toBeTruthy()
    })

    it('refuses a fractional retry count rather than quietly flooring it', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ retryCount: 3 }} onChange={onChange} />)

      put('Retry Count', '2.5')
      fireEvent.blur(box('Retry Count'))

      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText(/whole number/i)).toBeTruthy()
    })

    it('refuses a negative backoff, which the engine would raise to zero without saying so', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ retryBackoffSeconds: 1 }} onChange={onChange} />)

      put('Backoff (seconds)', '-1')
      fireEvent.blur(box('Backoff (seconds)'))

      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByText(/at least 0/)).toBeTruthy()
    })

    it('keeps a fractional backoff, the one resilience number the engine reads as a double', () => {
      const onChange = vi.fn()
      render(<ResiliencySection configuration={{ retryBackoffSeconds: 1 }} onChange={onChange} />)

      put('Backoff (seconds)', '1.5')
      fireEvent.blur(box('Backoff (seconds)'))

      expect(onChange).toHaveBeenCalledWith('retryBackoffSeconds', 1.5)
    })
  })
})
