import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { IncomingVariable } from '@reeve-iot/workflow-core'
import { PassthroughSection } from './PassthroughSection.js'

const show = (onToggle = vi.fn()) => {
  render(<PassthroughSection incoming={[]} passthrough={[]} onToggle={onToggle} />)
  return onToggle
}

describe('PassthroughSection — the name typed by hand', () => {
  it('adds a name that holds no dot', async () => {
    const user = userEvent.setup()
    const onToggle = show()

    await user.type(screen.getByLabelText('Add variable name'), 'MyValue')
    await user.click(screen.getByRole('button', { name: 'Add passthrough variable' }))

    expect(onToggle).toHaveBeenCalledWith('MyValue')
  })

  it('refuses a dotted name at Add, and says why', async () => {
    const user = userEvent.setup()
    const onToggle = show()

    await user.type(screen.getByLabelText('Add variable name'), 'a.b')
    expect(screen.getByText(/may not hold a dot/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Add passthrough variable' }))

    expect(onToggle).not.toHaveBeenCalled()
  })

  it('says nothing about an empty field nobody has touched yet', () => {
    show()

    expect(screen.queryByText(/blank name/)).toBeNull()
  })

  it('does not carry a draft typed on one node onto another', async () => {
    const user = userEvent.setup()
    const onToggle = vi.fn()
    const names = (name: string): IncomingVariable[] => [{ name, type: 'String' }]
    const { rerender } = render(<PassthroughSection incoming={names('Alpha')} passthrough={[]} onToggle={onToggle} />)
    await user.type(screen.getByLabelText('Add variable name'), 'Extra')
    expect(screen.getByLabelText('Add variable name')).toHaveValue('Extra')

    rerender(<PassthroughSection incoming={names('Bravo')} passthrough={[]} onToggle={onToggle} />)

    expect(screen.getByLabelText('Add variable name')).toHaveValue('')
    await user.click(screen.getByRole('button', { name: 'Add passthrough variable' }))
    expect(onToggle).not.toHaveBeenCalled()
  })
})
