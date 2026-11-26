import { useState } from 'react'
import type { ReactNode } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { PortTypeName, WorkflowConcurrency, WorkflowParameter } from '@reeve-iot/workflow-core'
import { ParameterDeclarationDialog } from './ParameterDeclarationDialog.js'
import { editorProviders, renderInEditor } from '../testUtils.js'

interface HarnessProps {
  initialInputs?: WorkflowParameter[]
  initialOutputs?: WorkflowParameter[]
  concurrency?: WorkflowConcurrency
  execution?: boolean
  initialRetry?: string | null
  parameterTypes?: PortTypeName[]
  generalSlot?: ReactNode
  onDescription?: (value: string | null) => void
  onInputs?: (params: WorkflowParameter[]) => void
  onOutputs?: (params: WorkflowParameter[]) => void
}

function Harness({
  initialInputs = [], initialOutputs = [], concurrency, execution, initialRetry = null,
  parameterTypes, generalSlot, onDescription, onInputs, onOutputs,
}: HarnessProps) {
  const [inputs, setInputs] = useState(initialInputs)
  const [outputs, setOutputs] = useState(initialOutputs)
  const [name, setName] = useState('My workflow')
  const [description, setDescription] = useState<string | null>('A description')
  const [conc, setConc] = useState(concurrency)
  const [timeout, setTimeoutSeconds] = useState<number | null>(null)
  const [retry, setRetry] = useState<string | null>(initialRetry)
  const [checkpoint, setCheckpoint] = useState<string | null>(null)
  return (
    <ParameterDeclarationDialog
      open onClose={vi.fn()}
      inputParameters={inputs} onInputParametersChange={(next) => { onInputs?.(next); setInputs(next) }}
      outputParameters={outputs} onOutputParametersChange={(next) => { onOutputs?.(next); setOutputs(next) }}
      name={name} onNameChange={setName}
      description={description}
      onDescriptionChange={(value) => { onDescription?.(value); setDescription(value) }}
      concurrency={conc} onConcurrencyChange={concurrency ? setConc : undefined}
      executionTimeoutSeconds={timeout}
      onExecutionTimeoutSecondsChange={execution ? setTimeoutSeconds : undefined}
      retryPolicyJson={retry}
      onRetryPolicyJsonChange={execution ? setRetry : undefined}
      checkpointPolicyJson={checkpoint}
      onCheckpointPolicyJsonChange={execution ? setCheckpoint : undefined}
      parameterTypes={parameterTypes} generalSlot={generalSlot}
    />
  )
}

function Swapped({ params, onInputs }: { params: WorkflowParameter[]; onInputs: (params: WorkflowParameter[]) => void }) {
  return (
    <ParameterDeclarationDialog
      open onClose={vi.fn()}
      inputParameters={params} onInputParametersChange={onInputs}
      outputParameters={[]} onOutputParametersChange={vi.fn()}
      name="My workflow" onNameChange={vi.fn()}
      description={null} onDescriptionChange={vi.fn()}
    />
  )
}

const param = (name: string, extra: Partial<WorkflowParameter> = {}): WorkflowParameter =>
  ({ name, displayName: name, type: 'String', isRequired: false, ...extra })

const show = (props: HarnessProps = {}) => renderInEditor(<Harness {...props} />)

const tab = async (user: ReturnType<typeof userEvent.setup>, label: string) => {
  await user.click(screen.getByRole('tab', { name: label }))
}

const nameFields = (side: 'input' | 'output') =>
  screen.getAllByLabelText(new RegExp(`^${side} \\d+ name$`)).map((el) => (el as HTMLInputElement).value)

describe('ParameterDeclarationDialog', () => {
  it('offers a tab per section, and drops Concurrency when the host does not supply it', () => {
    show()
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['General', 'Inputs', 'Outputs'])
  })

  it('offers the Concurrency tab when the host supplies the settings', async () => {
    const user = userEvent.setup()
    show({ concurrency: { mode: 'Queued', maxConcurrentRuns: 2, maxQueuedRuns: 5, overflowBehavior: 'Drop' } })
    expect(screen.getAllByRole('tab').map((t) => t.textContent)).toEqual(['General', 'Inputs', 'Outputs', 'Concurrency'])
    await tab(user, 'Concurrency')
    expect(screen.getByLabelText('Concurrency mode')).toBeInTheDocument()
  })

  it('renders whatever the host adds to the General tab', () => {
    show({ generalSlot: <div>Folder picker</div> })
    expect(screen.getByText('Folder picker')).toBeInTheDocument()
  })

  describe('the workflow row\'s own settings', () => {
    const tabs = () => screen.getAllByRole('tab').map((t) => t.textContent)

    it('puts the execution controls in the tab beside the concurrency settings', async () => {
      const user = userEvent.setup()
      show({ concurrency: { mode: 'Queued', maxConcurrentRuns: 2, maxQueuedRuns: 5, overflowBehavior: 'Drop' }, execution: true })
      expect(tabs()).toEqual(['General', 'Inputs', 'Outputs', 'Concurrency'])
      await tab(user, 'Concurrency')
      expect(screen.getByLabelText('Concurrency mode')).toBeInTheDocument()
      expect(screen.getByTestId('execution-timeout-seconds')).toBeInTheDocument()
    })

    it('names the tab for execution where the host supplies no concurrency', () => {
      show({ execution: true })
      expect(tabs()).toEqual(['General', 'Inputs', 'Outputs', 'Execution'])
    })

    it('adds no tab where the host supplies none of the controls', () => {
      show()
      expect(tabs()).toEqual(['General', 'Inputs', 'Outputs'])
    })

    it('writes what the author chooses through the host\'s own state', async () => {
      const user = userEvent.setup()
      show({ execution: true, initialRetry: '{"MaxAttempts":3}' })
      await tab(user, 'Execution')
      await user.click(screen.getByLabelText('Retry backoff strategy'))
      await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: 'Exponential backoff' }))
      expect(screen.getByLabelText('Retry backoff strategy')).toHaveTextContent('Exponential backoff')
      expect(screen.getByTestId('retry-policy-max-attempts')).toHaveValue('3')
    })
  })

  it('clears the description to null rather than to an empty string', async () => {
    const user = userEvent.setup()
    const onDescription = vi.fn()
    show({ onDescription })
    await user.clear(screen.getByLabelText('Description'))
    expect(onDescription).toHaveBeenLastCalledWith(null)
  })

  it('says so when a side has no parameters', async () => {
    const user = userEvent.setup()
    show()
    await tab(user, 'Inputs')
    expect(screen.getByText('No parameters defined')).toBeInTheDocument()
  })

  it('adds a parameter with a free name even after an earlier one was removed', async () => {
    const user = userEvent.setup()
    show()
    await tab(user, 'Inputs')
    await user.click(screen.getByRole('button', { name: 'Add Parameter' }))
    await user.click(screen.getByRole('button', { name: 'Add Parameter' }))
    expect(nameFields('input')).toEqual(['param0', 'param1'])
    await user.click(screen.getByRole('button', { name: 'Remove input 0' }))
    await user.click(screen.getByRole('button', { name: 'Add Parameter' }))
    expect(nameFields('input')).toEqual(['param1', 'param0'])
  })

  it('removes the row the button belongs to', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha'), param('beta'), param('gamma')] })
    await tab(user, 'Inputs')
    await user.click(screen.getByRole('button', { name: 'Remove input 1' }))
    expect(nameFields('input')).toEqual(['alpha', 'gamma'])
  })

  it('edits a parameter name, display name and required flag in place', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha'), param('beta')] })
    await tab(user, 'Inputs')
    await user.clear(screen.getByLabelText('input 1 name'))
    await user.type(screen.getByLabelText('input 1 name'), 'renamed')
    await user.type(screen.getByLabelText('input 1 display name'), '!')
    await user.click(screen.getByLabelText('input 1 required'))
    expect(nameFields('input')).toEqual(['alpha', 'renamed'])
    expect(screen.getByLabelText('input 1 display name')).toHaveValue('beta!')
    expect(screen.getByLabelText('input 1 required')).toBeChecked()
    expect(screen.getByLabelText('input 0 required')).not.toBeChecked()
  })

  it('gives output parameters the Required column too, which the exit node\'s own inputs read', async () => {
    const user = userEvent.setup()
    show({ initialOutputs: [param('result')] })
    await tab(user, 'Outputs')
    expect(within(screen.getAllByRole('row')[0]!).getByText('Required')).toBeInTheDocument()
    expect(screen.getByLabelText('output 0 required')).not.toBeChecked()
    await user.click(screen.getByLabelText('output 0 required'))
    expect(screen.getByLabelText('output 0 required')).toBeChecked()
  })

  it('keeps the two sides\' required flags apart', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha')], initialOutputs: [param('result')] })
    await tab(user, 'Outputs')
    await user.click(screen.getByLabelText('output 0 required'))
    await tab(user, 'Inputs')
    expect(screen.getByLabelText('input 0 required')).not.toBeChecked()
  })

  it('offers the host-registered types and never the bare collection', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha')] })
    await tab(user, 'Inputs')
    await user.click(screen.getByLabelText('input 0 type'))
    const options = within(screen.getByRole('listbox')).getAllByRole('option').map((o) => o.textContent)
    expect(options).toContain('Device')
    expect(options).toContain('Collection<Device>')
    expect(options).toContain('String')
    expect(options).not.toContain('Collection')
  })

  it('lets the host narrow the type picker to its own shortlist', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha')], parameterTypes: ['String', 'Device'] })
    await tab(user, 'Inputs')
    await user.click(screen.getByLabelText('input 0 type'))
    expect(within(screen.getByRole('listbox')).getAllByRole('option').map((o) => o.textContent)).toEqual(['String', 'Device'])
  })

  it('shows a saved type spelled in the host dialect against its canonical option', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha', { type: 'device' })] })
    await tab(user, 'Inputs')
    expect(screen.getByLabelText('input 0 type')).toHaveTextContent('Device')
  })

  it('writes the chosen type onto the parameter', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha')] })
    await tab(user, 'Inputs')
    await user.click(screen.getByLabelText('input 0 type'))
    await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: 'Device' }))
    expect(screen.getByLabelText('input 0 type')).toHaveTextContent('Device')
  })

  it('keeps the two sides apart', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha')] })
    await tab(user, 'Outputs')
    await user.click(screen.getByRole('button', { name: 'Add Parameter' }))
    expect(nameFields('output')).toEqual(['param0'])
    await tab(user, 'Inputs')
    expect(nameFields('input')).toEqual(['alpha'])
  })

  it('wraps an input parameter’s type in one more collection level, to any depth', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('alpha')], onInputs })
    await tab(user, 'Inputs')

    await user.click(screen.getByRole('button', { name: 'input 0 type wrapped in a collection' }))
    expect(onInputs.mock.calls.at(-1)![0][0]).toMatchObject({ type: 'Collection<String>' })

    await user.click(screen.getByRole('button', { name: 'input 0 type wrapped in a collection' }))
    expect(onInputs.mock.calls.at(-1)![0][0]).toMatchObject({ type: 'Collection<Collection<String>>' })
    expect(screen.getByLabelText('input 0 type')).toHaveTextContent('Collection<Collection<String>>')
  })

  it('wraps an output parameter’s type, and leaves the other side alone', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    const onOutputs = vi.fn()
    show({ initialInputs: [param('alpha')], initialOutputs: [param('beta')], onInputs, onOutputs })
    await tab(user, 'Outputs')

    await user.click(screen.getByRole('button', { name: 'output 0 type wrapped in a collection' }))

    expect(onOutputs.mock.calls.at(-1)![0][0]).toMatchObject({ type: 'Collection<String>' })
    expect(onInputs).not.toHaveBeenCalled()
  })

  it('offers no collection around Any, which the compiler mints and no picker lists', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha', { type: 'Any' }), param('beta', { type: 'Collection<Any>' })] })
    await tab(user, 'Inputs')

    expect(screen.getByRole('button', { name: 'input 0 type wrapped in a collection' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'input 1 type wrapped in a collection' })).toBeDisabled()
    await user.click(screen.getByLabelText('input 0 type'))
    const options = within(screen.getByRole('listbox')).getAllByRole('option').map((o) => o.textContent)
    expect(options).not.toContain('Collection<Any>')
  })

  it('shows a stored type no host registered as it is written, and offers no composition over it', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('alpha', { type: 'Thermostat' })], onInputs })
    await tab(user, 'Inputs')

    expect(screen.getByLabelText('input 0 type')).toHaveTextContent('Thermostat')
    expect(screen.getByRole('button', { name: 'input 0 type wrapped in a collection' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'input 0 type wrapped in a collection' }))
    expect(onInputs).not.toHaveBeenCalled()
  })

  it('composes a stored alias under the canonical name the picker shows', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('alpha', { type: 'device' })], onInputs })
    await tab(user, 'Inputs')

    await user.click(screen.getByRole('button', { name: 'input 0 type wrapped in a collection' }))
    expect(onInputs.mock.calls.at(-1)![0][0]).toMatchObject({ type: 'Collection<Device>' })
  })

  it('keeps a dotted parameter name in the field, says why, and writes nothing', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('alpha')], onInputs })
    await tab(user, 'Inputs')

    fireEvent.change(screen.getByLabelText('input 0 name'), { target: { value: 'a.b' } })

    expect(screen.getByLabelText('input 0 name')).toHaveValue('a.b')
    expect(screen.getByText(/may not hold a dot/)).toBeInTheDocument()
    expect(onInputs).not.toHaveBeenCalled()
  })

  it('says why a blank parameter name cannot be written: the reader refuses that one today', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('alpha')], onInputs })
    await tab(user, 'Inputs')

    await user.clear(screen.getByLabelText('input 0 name'))

    expect(screen.getByLabelText('input 0 name')).toHaveValue('')
    expect(screen.getByText(/reader refuses a blank name/)).toBeInTheDocument()
    expect(onInputs).not.toHaveBeenCalled()
  })

  it('writes a parameter name that holds no dot and is not blank', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('alpha')], onInputs })
    await tab(user, 'Inputs')

    await user.clear(screen.getByLabelText('input 0 name'))
    await user.type(screen.getByLabelText('input 0 name'), 'renamed')

    expect(onInputs.mock.calls.at(-1)![0][0]).toMatchObject({ name: 'renamed' })
  })

  it('offers no collection to build on a type the grammar refuses or holds none of', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('alpha', { type: 'Collection' }), param('beta', { type: '' })], onInputs })
    await tab(user, 'Inputs')

    expect(screen.getByRole('button', { name: 'input 0 type wrapped in a collection' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'input 1 type wrapped in a collection' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'input 1 type wrapped in a collection' }))
    expect(onInputs).not.toHaveBeenCalled()
  })

  it('does not show or write a draft typed over another workflow’s parameter list', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    const loaded = (name: string) => editorProviders(<Swapped params={[param(name)]} onInputs={onInputs} />)
    const { rerender } = render(loaded('alpha'))
    await tab(user, 'Inputs')
    fireEvent.change(screen.getByLabelText('input 0 name'), { target: { value: 'alpha.b' } })
    expect(screen.getByLabelText('input 0 name')).toHaveValue('alpha.b')

    rerender(loaded('beta'))

    expect(screen.getByLabelText('input 0 name')).toHaveValue('beta')
    fireEvent.change(screen.getByLabelText('input 0 name'), { target: { value: 'betaZ' } })
    expect(onInputs).toHaveBeenCalledTimes(1)
    expect(onInputs.mock.calls[0]![0][0]).toMatchObject({ name: 'betaZ' })
  })

  it('does not show a draft over a workflow whose first parameter holds the same name', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    const loaded = (second: string) => editorProviders(
      <Swapped params={[param('alpha'), param(second)]} onInputs={onInputs} />,
    )
    const { rerender } = render(loaded('one'))
    await tab(user, 'Inputs')
    fireEvent.change(screen.getByLabelText('input 0 name'), { target: { value: 'alpha.b' } })
    expect(screen.getByLabelText('input 0 name')).toHaveValue('alpha.b')
    onInputs.mockClear()

    rerender(loaded('two'))

    expect(screen.getByLabelText('input 0 name')).toHaveValue('alpha')
    expect(onInputs).not.toHaveBeenCalled()
  })

  it('drops a draft when a parameter is added, which is a document this field did not write', async () => {
    const user = userEvent.setup()
    show({ initialInputs: [param('alpha')] })
    await tab(user, 'Inputs')
    fireEvent.change(screen.getByLabelText('input 0 name'), { target: { value: 'a.b' } })
    expect(screen.getByLabelText('input 0 name')).toHaveValue('a.b')

    await user.click(screen.getByRole('button', { name: 'Add Parameter' }))

    expect(screen.getByLabelText('input 0 name')).toHaveValue('alpha')
    expect(nameFields('input')).toEqual(['alpha', 'param0'])
  })

  it('opens a document already holding a dotted parameter name, and writes nothing on mount', async () => {
    const user = userEvent.setup()
    const onInputs = vi.fn()
    show({ initialInputs: [param('a.b')], onInputs })
    await tab(user, 'Inputs')

    expect(screen.getByLabelText('input 0 name')).toHaveValue('a.b')
    expect(onInputs).not.toHaveBeenCalled()
  })
})
