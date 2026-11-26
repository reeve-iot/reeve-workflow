import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor } from '@reeve-iot/workflow-core'
import { WORKFLOW_INPUT_BLOCK_TYPE_ID } from '@reeve-iot/workflow-core'
import { BlockPalette } from './BlockPalette.js'
import { renderInEditor } from '../testUtils.js'

const block = (id: string, extra: Partial<BlockTypeDescriptor> = {}): BlockTypeDescriptor => ({
  blockTypeId: id,
  displayName: id,
  description: '',
  category: 'Logic',
  inputs: [],
  outputs: [],
  ...extra,
})

const catalogue = [
  block('IfElse', { displayName: 'If / Else', description: 'Branch on a condition' }),
  block('Script', { displayName: 'Run Script', category: 'Advanced' }),
  block('Uncategorised', { displayName: 'Loose Block', category: undefined }),
  block(WORKFLOW_INPUT_BLOCK_TYPE_ID, { displayName: 'Workflow Input' }),
]

function mount(props: Parameters<typeof BlockPalette>[0] = {}) {
  const view = renderInEditor(<BlockPalette {...props} />)
  view.drive((store) => store.getState().setBlockTypes(catalogue))
  return view
}

describe('BlockPalette', () => {
  it('groups blocks under the category their descriptor declares', () => {
    mount()
    expect(screen.getByTestId('palette-category-Logic')).toBeInTheDocument()
    expect(screen.getByTestId('palette-category-Advanced')).toBeInTheDocument()
  })

  it('gives a block with no category a heading rather than dropping it', () => {
    mount({ uncategorisedLabel: 'Unsorted' })
    expect(screen.getByTestId('palette-category-Unsorted')).toBeInTheDocument()
  })

  it('never offers the synthetic entry node, which the editor maintains itself', async () => {
    mount()
    await userEvent.click(screen.getByTestId('palette-category-Logic'))
    expect(screen.queryByTestId(`workflow-block-${WORKFLOW_INPUT_BLOCK_TYPE_ID}`)).not.toBeInTheDocument()
  })

  it('filters by display name', async () => {
    mount()
    await userEvent.type(screen.getByLabelText('Search blocks'), 'script')
    expect(screen.getByTestId('palette-category-Advanced')).toBeInTheDocument()
    expect(screen.queryByTestId('palette-category-Logic')).not.toBeInTheDocument()
  })

  it('filters by description, so a block found by what it does still shows up', async () => {
    mount()
    await userEvent.type(screen.getByLabelText('Search blocks'), 'condition')
    expect(screen.getByTestId('palette-category-Logic')).toBeInTheDocument()
    expect(screen.queryByTestId('palette-category-Advanced')).not.toBeInTheDocument()
  })

  it('asks the store to add a block when one is clicked', async () => {
    const view = mount()
    await userEvent.click(screen.getByTestId('palette-category-Logic'))
    await userEvent.click(screen.getByTestId('workflow-block-IfElse'))
    expect(view.editor().getState().quickAddBlockTypeId).toBe('IfElse')
  })

  it('ignores a click on a block the host locked', async () => {
    const view = mount({ isBlockLocked: (bt) => (bt.blockTypeId === 'Script' ? { reason: 'Needs a licence' } : null) })
    await userEvent.click(screen.getByTestId('palette-category-Advanced'))
    await userEvent.click(screen.getByTestId('workflow-block-Script'))
    expect(view.editor().getState().quickAddBlockTypeId).toBeNull()
  })

  it('leaves every block usable when the host gates nothing', async () => {
    const view = mount()
    await userEvent.click(screen.getByTestId('palette-category-Advanced'))
    await userEvent.click(screen.getByTestId('workflow-block-Script'))
    expect(view.editor().getState().quickAddBlockTypeId).toBe('Script')
  })

  it('refuses to make a locked block draggable', async () => {
    mount({ isBlockLocked: () => ({ reason: 'Nope' }) })
    await userEvent.click(screen.getByTestId('palette-category-Logic'))
    expect(screen.getByTestId('workflow-block-IfElse')).not.toHaveAttribute('draggable', 'true')
  })
})
