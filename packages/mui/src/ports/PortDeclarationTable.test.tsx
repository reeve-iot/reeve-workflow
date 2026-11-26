import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { DeclaredOutputPort } from '@reeve-iot/workflow-core'
import { DeclaredOutputPortTable, PortDeclarationTable } from './PortDeclarationTable.js'
import { editorProviders, renderInEditor } from '../testUtils.js'

function Controlled({ initial, onChange }: { initial: string; onChange?: (json: string) => void }) {
  const [json, setJson] = useState(initial)
  return <PortDeclarationTable label="Input ports" value={json} onChange={(next) => { onChange?.(next); setJson(next) }} />
}

function ControlledOutputs({ initial, onChange, branches = ['Then'] }: { initial: DeclaredOutputPort[]; onChange?: (ports: DeclaredOutputPort[]) => void; branches?: string[] }) {
  const [ports, setPorts] = useState(initial)
  return (
    <DeclaredOutputPortTable
      label="Declared outputs" subCanvasNames={branches} value={ports}
      onChange={(next) => { onChange?.(next); setPorts(next) }}
    />
  )
}

function Swapping({ value, onChange }: { value: string; onChange?: (json: string) => void }) {
  const [json, setJson] = useState(value)
  const [from, setFrom] = useState(value)
  if (from !== value) {
    setFrom(value)
    setJson(value)
  }
  return <PortDeclarationTable label="Input ports" value={json} onChange={(next) => { onChange?.(next); setJson(next) }} />
}

function SwappingOutputs({ value, onChange }: { value: DeclaredOutputPort[]; onChange?: (ports: DeclaredOutputPort[]) => void }) {
  const [ports, setPorts] = useState(value)
  const [from, setFrom] = useState(value)
  if (from !== value) {
    setFrom(value)
    setPorts(value)
  }
  return (
    <DeclaredOutputPortTable
      label="Declared outputs" subCanvasNames={['Then']} value={ports}
      onChange={(next) => { onChange?.(next); setPorts(next) }}
    />
  )
}

const onePort = JSON.stringify([{ name: 'A', displayName: 'Port A', type: 'String', isRequired: false }])

describe('PortDeclarationTable', () => {
  it('says so when a block declares no ports', () => {
    renderInEditor(<PortDeclarationTable label="Input ports" value={null} onChange={() => {}} />)
    expect(screen.getByText('No ports declared')).toBeInTheDocument()
  })

  it.each([
    ['{ not json', 'rule L1'],
    ['[{"name":"A","type":"Int"},{"name":5}]', "rule L7, at '#/1/name'"],
    ['[{"name":"A","name":"B"}]', "rule L3, at '#/0/name'"],
  ])('shows the fault of %s and offers no edit', (value, rule) => {
    const onChange = vi.fn()
    renderInEditor(<PortDeclarationTable label="Input ports" value={value} onChange={onChange} />)
    expect(screen.getByRole('alert')).toHaveTextContent(`This port list cannot be read:`)
    expect(screen.getByRole('alert')).toHaveTextContent(rule)
    expect(screen.queryByRole('button', { name: /Add Port/ })).toBeNull()
    expect(screen.queryByText('No ports declared')).toBeNull()
    expect(onChange).not.toHaveBeenCalled()
  })

  it.each([[5], [[{ name: 'A', type: 'Int' }]], [{ name: 'A' }]])('shows a value that is not a string, %j, as a fault and offers no edit', (value) => {
    const onChange = vi.fn()
    renderInEditor(<PortDeclarationTable label="Input ports" value={value as unknown as string} onChange={onChange} />)
    expect(screen.getByRole('alert')).toHaveTextContent("a value that is not a string where a port list written as a string is required (rule L7, at '')")
    expect(screen.queryByRole('button', { name: /Add Port/ })).toBeNull()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('shows an empty array as no ports, with the button to add one', () => {
    renderInEditor(<PortDeclarationTable label="Input ports" value="[]" onChange={() => {}} />)
    expect(screen.getByText('No ports declared')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Add Port/ })).toBeInTheDocument()
  })

  it('shows an empty text as a fault and offers no edit, since the stored form of no ports is the empty array', () => {
    renderInEditor(<PortDeclarationTable label="Input ports" value="" onChange={() => {}} />)
    expect(screen.getByRole('alert')).toHaveTextContent('rule L1')
    expect(screen.queryByRole('button', { name: /Add Port/ })).toBeNull()
  })

  it('offers a type the HOST registered, which is the whole point of the open vocabulary', async () => {
    renderInEditor(<Controlled initial={onePort} />)
    await userEvent.click(screen.getByLabelText('Port 1 type'))
    expect(screen.getByRole('option', { name: 'Device' })).toBeInTheDocument()
  })

  it('offers the collection companion of a host type', async () => {
    renderInEditor(<Controlled initial={onePort} />)
    await userEvent.click(screen.getByLabelText('Port 1 type'))
    expect(screen.getByRole('option', { name: 'Collection<Device>' })).toBeInTheDocument()
  })

  it('keeps the caret in the field while a name is typed', async () => {
    renderInEditor(<Controlled initial={onePort} />)
    const field = screen.getByLabelText('Port 1 name')
    await userEvent.clear(field)
    await userEvent.type(field, 'Temperature')
    expect(screen.getByLabelText('Port 1 name')).toHaveValue('Temperature')
    expect(screen.getByLabelText('Port 1 name')).toHaveFocus()
  })

  it('writes the declaration back as JSON without editor-only fields', async () => {
    const onChange = vi.fn()
    renderInEditor(<PortDeclarationTable label="Input ports" value={null} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Add Port' }))
    expect(JSON.parse(onChange.mock.calls[0]![0] as string)).toEqual([
      { name: 'NewPort', displayName: 'New Port', type: 'String', isRequired: false },
    ])
  })

  it('writes a cleared label as no label, not as an empty one', async () => {
    const onChange = vi.fn()
    renderInEditor(<Controlled initial={onePort} onChange={onChange} />)
    await userEvent.clear(screen.getByLabelText('Port 1 label'))
    expect(JSON.parse(onChange.mock.calls.at(-1)![0] as string)).toEqual([{ name: 'A', type: 'String', isRequired: false }])
  })

  it('removes the row the delete button belongs to', async () => {
    const two = JSON.stringify([
      { name: 'A', displayName: 'A', type: 'String', isRequired: false },
      { name: 'B', displayName: 'B', type: 'String', isRequired: false },
    ])
    const onChange = vi.fn()
    renderInEditor(<PortDeclarationTable label="Input ports" value={two} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: 'Remove port 1' }))
    expect(JSON.parse(onChange.mock.calls[0]![0] as string).map((p: { name: string }) => p.name)).toEqual(['B'])
  })
})

describe('DeclaredOutputPortTable — adding a port', () => {
  const added = () => {
    const onChange = vi.fn()
    renderInEditor(
      <DeclaredOutputPortTable
        label="Declared outputs" subCanvasNames={['Then', 'Else']}
        value={[{ name: 'output', displayName: 'Output', type: 'String', sources: { Then: 'output', Else: 'output' } }]}
        onChange={onChange}
      />,
    )
    return onChange
  }

  it('gives the new port a name no sibling holds', async () => {
    const onChange = added()
    await userEvent.click(screen.getByRole('button', { name: /Add Output Port/i }))
    expect(onChange.mock.calls[0]?.[0]?.[1]).toMatchObject({ name: 'output2' })
  })

  it('gives the new port no type, so the author picks one and nothing is minted for them', async () => {
    const onChange = added()
    await userEvent.click(screen.getByRole('button', { name: /Add Output Port/i }))
    expect(onChange.mock.calls[0]?.[0]?.[1]).toMatchObject({ name: 'output2', type: '' })
  })

  it('seeds a source key for every branch', async () => {
    const onChange = added()
    await userEvent.click(screen.getByRole('button', { name: /Add Output Port/i }))
    expect(onChange.mock.calls[0]?.[0]?.[1]).toMatchObject({ sources: { Then: 'output2', Else: 'output2' } })
  })

  it('names the first port plainly', async () => {
    const onChange = vi.fn()
    renderInEditor(<DeclaredOutputPortTable label="Declared outputs" subCanvasNames={['Then']} value={[]} onChange={onChange} />)
    await userEvent.click(screen.getByRole('button', { name: /Add Output Port/i }))
    expect(onChange).toHaveBeenCalledWith([expect.objectContaining({ name: 'output', sources: { Then: 'output' } })])
  })
})

describe('DeclaredOutputPortTable — empty sources', () => {
  it('omits sources after every branch field is emptied', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(
      <ControlledOutputs
        initial={[{ name: 'result', displayName: 'Result', type: 'String', sources: { Then: 'thenResult', Else: 'elseResult' } }]}
        branches={['Then', 'Else']} onChange={onChange}
      />,
    )

    await user.clear(screen.getByRole('textbox', { name: 'Then →' }))
    await user.clear(screen.getByRole('textbox', { name: 'Else →' }))
    expect(onChange.mock.calls.at(-1)?.[0]).toEqual([{ name: 'result', displayName: 'Result', type: 'String' }])
  })
})

describe('PortDeclarationTable — composing a collection', () => {
  const composer = () => screen.getByRole('button', { name: 'Port 1 type wrapped in a collection' })
  const withType = (type: string) =>
    JSON.stringify([{ name: 'A', displayName: 'Port A', type, isRequired: false }])

  it('wraps the port type in one more collection level, to any depth', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(<Controlled initial={onePort} onChange={onChange} />)

    await user.click(composer())
    expect(JSON.parse(onChange.mock.calls[0]![0] as string)).toEqual([
      { name: 'A', displayName: 'Port A', type: 'Collection<String>', isRequired: false },
    ])
    await user.click(composer())
    expect(JSON.parse(onChange.mock.calls[1]![0] as string)).toEqual([
      { name: 'A', displayName: 'Port A', type: 'Collection<Collection<String>>', isRequired: false },
    ])
    expect(screen.getByLabelText('Port 1 type')).toHaveTextContent('Collection<Collection<String>>')
  })

  it.each(['device', 'Thermostat', 'Collection<Thermostat>', 'Collection<device>'])(
    'shows %s as it is written and offers no composition over it',
    (stored) => {
      const onChange = vi.fn()
      renderInEditor(<PortDeclarationTable label="Input ports" value={withType(stored)} onChange={onChange} />)

      expect(screen.getByLabelText('Port 1 type')).toHaveTextContent(stored)
      expect(composer()).toBeDisabled()
      fireEvent.click(composer())
      expect(onChange).not.toHaveBeenCalled()
    },
  )

  it.each([
    ['String', 'Collection<String>'],
    ['Collection<String>', 'Collection<Collection<String>>'],
    ['Collection<Collection<Device>>', 'Collection<Collection<Collection<Device>>>'],
  ])('wraps %s, which the vocabulary holds under that spelling, into %s', async (stored, wrapped) => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(<PortDeclarationTable label="Input ports" value={withType(stored)} onChange={onChange} />)

    await user.click(composer())
    expect(JSON.parse(onChange.mock.calls[0]![0] as string)[0]).toMatchObject({ type: wrapped })
  })

  it.each(['Any', 'Collection<Any>', 'Collection<Collection<Any>>'])(
    'offers no collection around Any, whether picked or read from a document: %s',
    (stored) => {
      const onChange = vi.fn()
      renderInEditor(<PortDeclarationTable label="Input ports" value={withType(stored)} onChange={onChange} />)
      expect(composer()).toBeDisabled()
      fireEvent.click(composer())
      expect(onChange).not.toHaveBeenCalled()
    },
  )

  it('shows a stored Collection<Any> as written, as any unregistered name, and offers nothing built on it', async () => {
    const user = userEvent.setup()
    renderInEditor(<PortDeclarationTable label="Input ports" value={withType('Collection<Any>')} onChange={() => {}} />)

    await user.click(screen.getByLabelText('Port 1 type'))
    const options = within(screen.getByRole('listbox')).getAllByRole('option').map((o) => o.textContent)
    expect(options.filter((o) => o?.includes('Any'))).toEqual(['Collection<Any>'])
  })

  it('shows a nested type a document already holds, which the registry does not list', () => {
    renderInEditor(<PortDeclarationTable label="Input ports" value={withType('Collection<Collection<Device>>')} onChange={() => {}} />)

    expect(screen.getByLabelText('Port 1 type')).toHaveTextContent('Collection<Collection<Device>>')
  })

  it.each(['Collection', 'CollectionDevice', 'Collection<', ''])(
    'builds on no type the grammar refuses or holds none of: %s',
    (stored) => {
      const onChange = vi.fn()
      renderInEditor(<PortDeclarationTable label="Input ports" value={withType(stored)} onChange={onChange} />)

      expect(composer()).toBeDisabled()
      fireEvent.click(composer())
      expect(onChange).not.toHaveBeenCalled()
    },
  )
})

describe('DeclaredOutputPortTable — composing a collection', () => {
  const composer = () => screen.getByRole('button', { name: 'Output 1 type wrapped in a collection' })

  it('starts disabled on a new row, which has no type, and wraps once a type is picked', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(
      <ControlledOutputs
        initial={[{ name: 'out', displayName: 'Out', type: '', sources: { Then: 'out' } }]}
        onChange={onChange}
      />,
    )
    expect(composer()).toBeDisabled()

    await user.click(screen.getByLabelText('Output 1 type'))
    await user.click(within(screen.getByRole('listbox')).getByRole('option', { name: 'String' }))
    await user.click(composer())

    expect(onChange).toHaveBeenLastCalledWith([expect.objectContaining({ type: 'Collection<String>' })])
  })
})

describe('PortDeclarationTable — names that hold a dot or nothing', () => {
  it('keeps a dotted name in the field, says why, and writes nothing', () => {
    const onChange = vi.fn()
    renderInEditor(<PortDeclarationTable label="Input ports" value={onePort} onChange={onChange} />)

    fireEvent.change(screen.getByLabelText('Port 1 name'), { target: { value: 'a.b' } })

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('a.b')
    expect(screen.getByText(/may not hold a dot/)).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('says the reader refuses a blank name, and writes nothing', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(<PortDeclarationTable label="Input ports" value={onePort} onChange={onChange} />)

    await user.clear(screen.getByLabelText('Port 1 name'))

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('')
    expect(screen.getByText(/reader refuses a blank name/)).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('writes a name that holds no dot and is not blank', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(<Controlled initial={onePort} onChange={onChange} />)

    await user.clear(screen.getByLabelText('Port 1 name'))
    await user.type(screen.getByLabelText('Port 1 name'), 'Temperature')

    expect(JSON.parse(onChange.mock.calls.at(-1)![0] as string)[0]).toMatchObject({ name: 'Temperature' })
  })

  it('reports the reader refusal for a stored dotted name, and writes nothing on mount', () => {
    const onChange = vi.fn()
    const dotted = JSON.stringify([{ name: 'a.b', displayName: 'Port A', type: 'String', isRequired: false }])
    renderInEditor(<PortDeclarationTable label="Input ports" value={dotted} onChange={onChange} />)

    expect(screen.getByRole('alert')).toHaveTextContent(/rule dotted-name, at '#\/0\/name'/)
    expect(screen.getByRole('alert')).toHaveTextContent("name 'a.b' contains a dot")
    expect(screen.queryByLabelText('Port 1 name')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Add Port' })).not.toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('drops the draft with the row it was typed in', async () => {
    const user = userEvent.setup()
    const two = JSON.stringify([
      { name: 'A', displayName: 'A', type: 'String', isRequired: false },
      { name: 'B', displayName: 'B', type: 'String', isRequired: false },
    ])
    renderInEditor(<Controlled initial={two} />)

    fireEvent.change(screen.getByLabelText('Port 1 name'), { target: { value: 'a.b' } })
    await user.click(screen.getByRole('button', { name: 'Remove port 1' }))

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('B')
  })
})

describe('the name tables — a draft belongs to one document, not to a row', () => {
  const stored = (name: string) =>
    JSON.stringify([{ name, displayName: name, type: 'String', isRequired: false }])
  const out = (name: string): DeclaredOutputPort =>
    ({ name, displayName: 'Out', type: 'String', sources: { Then: name } })
  const coincident = (second: string) =>
    JSON.stringify([
      { name: 'Rows', displayName: 'Rows', type: 'String', isRequired: false },
      { name: second, displayName: second, type: 'String', isRequired: false },
    ])

  it('shows the names of the node that is now selected, and writes nothing on the switch', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const { rerender } = render(editorProviders(<Swapping value={stored('Alpha')} onChange={onChange} />))
    fireEvent.change(screen.getByLabelText('Port 1 name'), { target: { value: 'AlphaX' } })
    expect(screen.getByLabelText('Port 1 name')).toHaveValue('AlphaX')
    onChange.mockClear()

    rerender(editorProviders(<Swapping value={stored('Bravo')} onChange={onChange} />))

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('Bravo')
    expect(onChange).not.toHaveBeenCalled()
    await user.type(screen.getByLabelText('Port 1 name'), 'Z')
    expect(JSON.parse(onChange.mock.calls[0]![0] as string)[0]).toMatchObject({ name: 'BravoZ' })
  })

  it('does not show a dotted draft typed on another node, and writes it nowhere', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const { rerender } = render(editorProviders(<Swapping value={stored('Alpha')} onChange={onChange} />))
    fireEvent.change(screen.getByLabelText('Port 1 name'), { target: { value: 'alpha.b' } })
    expect(onChange).not.toHaveBeenCalled()

    rerender(editorProviders(<Swapping value={stored('Bravo')} onChange={onChange} />))

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('Bravo')
    await user.type(screen.getByLabelText('Port 1 name'), 'Z')
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(JSON.parse(onChange.mock.calls[0]![0] as string)[0]).toMatchObject({ name: 'BravoZ' })
  })

  it('does not show a blank draft over a name the next document stores too', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const { rerender } = render(editorProviders(<Swapping value={coincident('Alpha')} onChange={onChange} />))
    await user.clear(screen.getByLabelText('Port 1 name'))
    expect(screen.getByLabelText('Port 1 name')).toHaveValue('')
    onChange.mockClear()

    rerender(editorProviders(<Swapping value={coincident('Bravo')} onChange={onChange} />))

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('Rows')
    await user.type(screen.getByLabelText('Port 1 name'), 'Z')
    expect(onChange).toHaveBeenCalledTimes(1)
    expect(JSON.parse(onChange.mock.calls[0]![0] as string)[0]).toMatchObject({ name: 'RowsZ' })
  })

  it('does not show a dotted draft over a name the next document stores too', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const { rerender } = render(editorProviders(<Swapping value={coincident('Alpha')} onChange={onChange} />))
    fireEvent.change(screen.getByLabelText('Port 1 name'), { target: { value: 'rows.b' } })
    expect(screen.getByLabelText('Port 1 name')).toHaveValue('rows.b')
    expect(onChange).not.toHaveBeenCalled()

    rerender(editorProviders(<Swapping value={coincident('Bravo')} onChange={onChange} />))

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('Rows')
    await user.type(screen.getByLabelText('Port 1 name'), 'Z')
    expect(JSON.parse(onChange.mock.calls[0]![0] as string)[0]).toMatchObject({ name: 'RowsZ' })
  })

  it('drops a draft when a port is added, which is a document this field did not write', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(editorProviders(<Swapping value={stored('A')} onChange={onChange} />))
    fireEvent.change(screen.getByLabelText('Port 1 name'), { target: { value: 'a.b' } })
    expect(screen.getByLabelText('Port 1 name')).toHaveValue('a.b')

    await user.click(screen.getByRole('button', { name: 'Add Port' }))

    expect(screen.getByLabelText('Port 1 name')).toHaveValue('A')
    expect(JSON.parse(onChange.mock.calls.at(-1)![0] as string)).toHaveLength(2)
  })

  it('shows the declared outputs of the node that is now selected, and writes nothing on the switch', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const { rerender } = render(editorProviders(<SwappingOutputs value={[out('OutAlpha')]} onChange={onChange} />))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'OutAlphaX' } })
    expect(screen.getByLabelText('Name')).toHaveValue('OutAlphaX')
    onChange.mockClear()

    rerender(editorProviders(<SwappingOutputs value={[out('OutBravo')]} onChange={onChange} />))

    expect(screen.getByLabelText('Name')).toHaveValue('OutBravo')
    expect(onChange).not.toHaveBeenCalled()
    await user.type(screen.getByLabelText('Name'), 'Z')
    expect(onChange.mock.calls[0]![0][0]).toMatchObject({ name: 'OutBravoZ' })
  })

  it('does not show a blank draft over a declared output the next document stores too', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const both = (second: string) => [out('Rows'), out(second)]
    const { rerender } = render(editorProviders(<SwappingOutputs value={both('Alpha')} onChange={onChange} />))
    await user.clear(screen.getAllByLabelText('Name')[0]!)
    expect(screen.getAllByLabelText('Name')[0]).toHaveValue('')
    onChange.mockClear()

    rerender(editorProviders(<SwappingOutputs value={both('Bravo')} onChange={onChange} />))

    expect(screen.getAllByLabelText('Name')[0]).toHaveValue('Rows')
    await user.type(screen.getAllByLabelText('Name')[0]!, 'Z')
    expect(onChange.mock.calls[0]![0][0]).toMatchObject({ name: 'RowsZ' })
  })

  it('drops a draft when a declared output is added', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(editorProviders(<SwappingOutputs value={[out('A')]} onChange={onChange} />))
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'a.b' } })

    await user.click(screen.getByRole('button', { name: 'Add Output Port' }))

    expect(screen.getAllByLabelText('Name')[0]).toHaveValue('A')
  })
})

describe('DeclaredOutputPortTable — names that hold a dot or nothing', () => {
  const oneOutput: DeclaredOutputPort[] = [{ name: 'out', displayName: 'Out', type: 'String', sources: { Then: 'out' } }]

  it('keeps a dotted name in the field, says why, and writes nothing', () => {
    const onChange = vi.fn()
    renderInEditor(<DeclaredOutputPortTable label="Declared outputs" subCanvasNames={['Then']} value={oneOutput} onChange={onChange} />)

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'a.b' } })

    expect(screen.getByLabelText('Name')).toHaveValue('a.b')
    expect(screen.getByText(/may not hold a dot/)).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('says a blank name is one the reader takes but the field never writes, and writes nothing', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(<DeclaredOutputPortTable label="Declared outputs" subCanvasNames={['Then']} value={oneOutput} onChange={onChange} />)

    await user.clear(screen.getByLabelText('Name'))

    expect(screen.getByLabelText('Name')).toHaveValue('')
    expect(screen.getByText(/accepts a blank name, but this field writes none/)).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })

  it('writes a name that holds no dot and is not blank', async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    renderInEditor(<ControlledOutputs initial={oneOutput} onChange={onChange} />)

    await user.clear(screen.getByLabelText('Name'))
    await user.type(screen.getByLabelText('Name'), 'Result')

    expect(onChange.mock.calls.at(-1)![0][0]).toMatchObject({ name: 'Result' })
  })

  it('opens a document already holding a dotted name, and writes nothing on mount', () => {
    const onChange = vi.fn()
    renderInEditor(
      <DeclaredOutputPortTable
        label="Declared outputs" subCanvasNames={['Then']}
        value={[{ name: 'a.b', displayName: 'Out', type: 'String', sources: { Then: 'a.b' } }]}
        onChange={onChange}
      />,
    )

    expect(screen.getByLabelText('Name')).toHaveValue('a.b')
    expect(onChange).not.toHaveBeenCalled()
  })

  it('renders a declared output the reader took without a name, which holds no name to show', () => {
    renderInEditor(
      <DeclaredOutputPortTable
        label="Declared outputs" subCanvasNames={['Then']}
        value={[{ displayName: 'Out', type: 'String', sources: { Then: 'out' } } as unknown as DeclaredOutputPort]}
        onChange={() => {}}
      />,
    )

    expect(screen.getByLabelText('Name')).toHaveValue('')
    expect(screen.getByText(/accepts a blank name, but this field writes none/)).toBeInTheDocument()
  })
})
