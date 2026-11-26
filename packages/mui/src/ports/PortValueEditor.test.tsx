import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ThemeProvider, createTheme } from '@mui/material/styles'
import { PortValueEditor, coercePortValue } from './PortValueEditor.js'
import type { PortValueEditorProps } from './PortValueEditor.js'
import { VALUE_HIDDEN_REQUIRED, VALUE_REPLACEMENT_REQUIRED } from './KeyValueRowsField.js'

const theme = createTheme()

function draw(props: Partial<PortValueEditorProps> = {}) {
  const onChange = props.onChange ?? vi.fn()
  render(
    <ThemeProvider theme={theme}>
      <PortValueEditor fieldName="Value" label="Value" rawValue={props.rawValue ?? ''} hasConnection={props.hasConnection ?? false} {...props} onChange={onChange} />
    </ThemeProvider>,
  )
  return onChange as ReturnType<typeof vi.fn>
}

function Controlled(props: Partial<PortValueEditorProps>) {
  const [value, setValue] = useState<unknown>(props.rawValue ?? '')
  return (
    <ThemeProvider theme={theme}>
      <PortValueEditor fieldName="Value" label="Value" hasConnection={false} {...props} rawValue={value} onChange={setValue} />
    </ThemeProvider>
  )
}

describe('PortValueEditor', () => {
  it('edits a literal as text', async () => {
    const onChange = draw()
    await userEvent.type(screen.getByTestId('port-value-input-Value'), 'hi')
    expect(onChange).toHaveBeenLastCalledWith('i')
  })

  it('parses a numeric port into a number rather than a string', async () => {
    const onChange = draw({ fieldType: 'Int', rawValue: '' })
    await userEvent.type(screen.getByTestId('port-value-input-Value'), '7')
    expect(onChange).toHaveBeenLastCalledWith(7)
  })

  it('writes nothing for text that is not a number, since no text is read into an Int', () => {
    const onChange = draw({ fieldType: 'Int', rawValue: '' })
    fireEvent.change(screen.getByTestId('port-value-input-Value'), { target: { value: 'not-a-number' } })
    expect(onChange).not.toHaveBeenCalled()
  })

  it('holds the text of a number being typed, writing only what that text parses to', async () => {
    const onChange = draw({ fieldType: 'Number', rawValue: 7 })
    const input = screen.getByTestId('port-value-input-Value')

    fireEvent.change(input, { target: { value: '-' } })
    expect(input).toHaveValue('-')
    expect(onChange).not.toHaveBeenCalled()

    fireEvent.change(input, { target: { value: '-1' } })
    expect(onChange).toHaveBeenLastCalledWith(-1)
  })

  it('never repairs a non-integer into an Int, keeping the text instead', async () => {
    const onChange = draw({ fieldType: 'Int', rawValue: 7 })

    fireEvent.change(screen.getByTestId('port-value-input-Value'), { target: { value: '7.5' } })

    expect(onChange).not.toHaveBeenCalled()
    expect(screen.getByTestId('port-value-input-Value')).toHaveValue('7.5')
  })



  it('starts in connection mode when a wire feeds the port', () => {
    draw({ hasConnection: true })
    expect(screen.getByText(/Driven by an incoming connection/)).toBeInTheDocument()
  })

  it('refuses to enter connection mode by hand, since that means drawing a wire', async () => {
    draw({ hasConnection: false })
    expect(screen.getByTestId('mode-connection-Value')).toBeDisabled()
  })

  it('does not offer expression mode when the host has no expression language', () => {
    draw()
    expect(screen.queryByTestId('mode-expression-Value')).not.toBeInTheDocument()
  })

  it('offers expression mode once the host supplies an editor', () => {
    draw({ renderExpressionEditor: () => <div>editor</div> })
    expect(screen.getByTestId('mode-expression-Value')).toBeInTheDocument()
  })

  it('hands the host the expression body without its marker prefix', () => {
    const renderExpressionEditor: NonNullable<PortValueEditorProps['renderExpressionEditor']> = vi.fn(() => <div>editor</div>)
    draw({ rawValue: 'expr:a + b', renderExpressionEditor })
    expect(renderExpressionEditor).toHaveBeenCalledWith(expect.objectContaining({ expressionBody: 'a + b' }))
  })

  it('keeps what was typed when switching a literal into an expression', async () => {
    render(<Controlled rawValue="42" renderExpressionEditor={({ expressionBody }) => <div data-testid="expr">{expressionBody}</div>} />)
    await userEvent.click(screen.getByTestId('mode-expression-Value'))
    expect(screen.getByTestId('expr')).toHaveTextContent('42')
  })

  it('strips the marker when switching an expression back to a literal', async () => {
    render(<Controlled rawValue="expr:42" renderExpressionEditor={() => <div />} />)
    await userEvent.click(screen.getByTestId('mode-value-Value'))
    expect(screen.getByTestId('port-value-input-Value')).toHaveValue('42')
  })

  it('lets the host render its own editor for a type the library knows nothing about', () => {
    draw({ fieldType: 'Device', renderTypeEditor: (ctx) => (ctx.fieldType === 'Device' ? <div data-testid="device-picker" /> : null) })
    expect(screen.getByTestId('device-picker')).toBeInTheDocument()
    expect(screen.queryByTestId('port-value-input-Value')).not.toBeInTheDocument()
  })

  it('falls back to the text field for a type the host declined to render', () => {
    draw({ fieldType: 'String', renderTypeEditor: () => null })
    expect(screen.getByTestId('port-value-input-Value')).toBeInTheDocument()
  })
})

describe('coercePortValue', () => {
  it('leaves a string port alone', () => {
    expect(coercePortValue('7', 'String')).toBe('7')
  })

  it('parses a float port', () => {
    expect(coercePortValue('7.5', 'Number')).toBe(7.5)
  })

  it('parses an integer port', () => {
    expect(coercePortValue('7', 'Int')).toBe(7)
  })

  it('passes unparseable text through instead of saving NaN', () => {
    expect(coercePortValue('abc', 'Number')).toBe('abc')
  })

  it('passes a lone minus sign through, so a negative number can still be typed', () => {
    expect(coercePortValue('-', 'Int')).toBe('-')
  })

  it('leaves a port type it knows nothing about untouched', () => {
    expect(coercePortValue('device-1', 'Device')).toBe('device-1')
  })
})

describe('PortValueEditor — a Boolean port', () => {
  it('is a switch, and emits a real boolean rather than a string', async () => {
    const onChange = draw({ fieldType: 'Boolean', rawValue: false })
    await userEvent.click(screen.getByRole('switch', { name: 'Value' }))
    expect(onChange).toHaveBeenCalledWith(true)
  })

  it("reads the string default a config schema carries without turning itself on", () => {
    draw({ fieldType: 'Boolean', rawValue: 'false' })
    expect(screen.getByRole('switch', { name: 'Value' })).not.toBeChecked()
  })

  it.each(['true', ' TRUE\t'])('shows the switch off for the text %j, which is no Boolean literal', (text) => {
    draw({ fieldType: 'Boolean', rawValue: text })
    expect(screen.getByRole('switch', { name: 'Value' })).not.toBeChecked()
  })

  describe('a literal in the form its type declares', () => {
    it('writes a number for a host type whose form is graded as Int', () => {
      const onChange = draw({ fieldType: 'Tally', literalForm: { gradedAs: 'Int' }, rawValue: '' })
      fireEvent.change(screen.getByTestId('port-value-input-Value'), { target: { value: '12' } })
      expect(onChange).toHaveBeenLastCalledWith(12)
      fireEvent.change(screen.getByTestId('port-value-input-Value'), { target: { value: '1.5' } })
      expect(onChange).toHaveBeenCalledTimes(1)
    })

    it('is a switch for a host type whose form is graded as Boolean, and writes a boolean', async () => {
      const onChange = draw({ fieldType: 'Flag', literalForm: { gradedAs: 'Boolean' }, rawValue: false })
      await userEvent.click(screen.getByRole('switch'))
      expect(onChange).toHaveBeenLastCalledWith(true)
    })

    it('is a text field for a string form, with or without a format', async () => {
      const onChange = draw({ fieldType: 'Ref', literalForm: { gradedAs: 'String', format: 'Uuid' }, rawValue: '' })
      await userEvent.type(screen.getByTestId('port-value-input-Value'), 'a')
      expect(onChange).toHaveBeenLastCalledWith('a')
    })

    it('offers a JSON field for a Json form once a host says where an object goes', () => {
      draw({ fieldType: 'Blob', literalForm: { gradedAs: 'Json' }, rawValue: '', onObjectChange: vi.fn() })
      expect(screen.getByTestId('port-value-input-Value')).toHaveAttribute('aria-label', 'Value')
      expect(screen.getByPlaceholderText('{"key": "value"}')).toBeInTheDocument()
    })

    it('offers no value field for a type that declares no form', () => {
      draw({ fieldType: 'Scene', literalForm: null, rawValue: '' })
      expect(screen.queryByTestId('port-value-input-Value')).toBeNull()
      expect(screen.getByTestId('port-value-none-Value')).toHaveTextContent('takes no literal value')
    })
  })

  it('starts an expression empty, because a switch position is nothing to start from', async () => {
    const onChange = draw({ fieldType: 'Boolean', rawValue: true, renderExpressionEditor: () => <div /> })
    await userEvent.click(screen.getByTestId('mode-expression-Value'))
    expect(onChange).toHaveBeenCalledWith('expr:')
  })

  it('reads the expression back as the constant it was when the switch returns', async () => {
    const onChange = draw({ fieldType: 'Boolean', rawValue: 'expr: true ' })
    await userEvent.click(screen.getByTestId('mode-value-Value'))
    expect(onChange).toHaveBeenCalledWith(true)
  })

  it('leaves a non-boolean expression body as text when the editor returns to value mode', async () => {
    const onChange = draw({ fieldType: 'String', rawValue: 'expr:true' })
    await userEvent.click(screen.getByTestId('mode-value-Value'))
    expect(onChange).toHaveBeenCalledWith('true')
  })
})

describe('PortValueEditor — a Json field', () => {
  function drawJson(props: Partial<PortValueEditorProps> = {}) {
    const onChange = vi.fn()
    const onObjectChange = vi.fn()
    render(
      <ThemeProvider theme={theme}>
        <PortValueEditor
          fieldName="Payload" label="Payload" fieldType="Json" rawValue="" hasConnection={false}
          {...props} onChange={onChange} onObjectChange={onObjectChange}
        />
      </ThemeProvider>,
    )
    return { onChange, onObjectChange }
  }

  const put = (text: string) => {
    fireEvent.change(screen.getByTestId('port-value-input-Payload'), { target: { value: text } })
  }

  it('writes the object the field is given, and not the text it was typed as', () => {
    const { onChange, onObjectChange } = drawJson()

    put('{"retries": 3}')

    expect(onObjectChange).toHaveBeenCalledWith({ retries: 3 })
    expect(onChange).not.toHaveBeenCalled()
  })

  it.each([
    ['a list', '[1, "a"]', [1, 'a']],
    ['an empty list', '[]', []],
    ['text', '"abc"', 'abc'],
    ['a number', '3', 3],
    ['a boolean', 'true', true],
  ])('writes %s, which a Json form takes as it takes an object', (_, text, expected) => {
    const { onChange, onObjectChange } = drawJson()

    put(text)

    expect(onObjectChange).toHaveBeenCalledWith(expected)
    expect(onChange).not.toHaveBeenCalled()
  })

  it.each([['{', 'Must be valid JSON'], ['null', 'null is not a literal value; use an expression instead']])(
    'keeps %s in the field, writes nothing and says why', (text, why) => {
      const { onChange, onObjectChange } = drawJson()

      put(text)

      expect(onObjectChange).not.toHaveBeenCalled()
      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByTestId('port-value-input-Payload')).toHaveValue(text)
      expect(screen.getByText(why)).toBeInTheDocument()
    })

  it.each([['{"a": 1, "a": 2}', "'a' occurs more than once in the object at $"], ['{"m": [{"x": {"b": 1, "b": 2}}]}', "'b' occurs more than once in the object at $.m[0].x"]])(
    'keeps %s in the field, writes nothing and names the repeated member', (text, where) => {
      const { onChange, onObjectChange } = drawJson()

      put(text)

      expect(onObjectChange).not.toHaveBeenCalled()
      expect(onChange).not.toHaveBeenCalled()
      expect(screen.getByTestId('port-value-input-Payload')).toHaveValue(text)
      expect(screen.getByText(`Ambiguous JSON: the member name ${where}`)).toBeInTheDocument()
    })

  it('clears the value when the field is emptied, as the plain field does', () => {
    const { onChange, onObjectChange } = drawJson({ rawValue: { retries: 3 } })

    put('')

    expect(onChange).toHaveBeenCalledWith('')
    expect(onObjectChange).not.toHaveBeenCalled()
  })

  it('shows a stored object as JSON, so it can be edited as the document holds it', () => {
    drawJson({ rawValue: { retries: 3 } })

    expect(screen.getByTestId('port-value-input-Payload')).toHaveValue('{\n  "retries": 3\n}')
  })

  it('starts an expression empty for an object, rather than spelling it out as [object Object]', async () => {
    const { onChange } = drawJson({ rawValue: { retries: 3 }, renderExpressionEditor: () => <div /> })

    await userEvent.click(screen.getByTestId('mode-expression-Payload'))

    expect(onChange).toHaveBeenCalledWith('expr:')
  })

  it('is not offered when the host says nothing about where an object goes', () => {
    const onChange = vi.fn()
    render(
      <ThemeProvider theme={theme}>
        <PortValueEditor fieldName="Payload" label="Payload" fieldType="Json" rawValue="" hasConnection={false} onChange={onChange} />
      </ThemeProvider>,
    )

    fireEvent.change(screen.getByTestId('port-value-input-Payload'), { target: { value: '{"retries": 3}' } })

    expect(onChange).toHaveBeenLastCalledWith('{"retries": 3}')
  })

  it("leaves a host's own editor for the Json type in charge", () => {
    drawJson({ renderTypeEditor: () => <div data-testid="host-json-editor" /> })

    expect(screen.getByTestId('host-json-editor')).toBeInTheDocument()
    expect(screen.queryByText('Must be valid JSON')).not.toBeInTheDocument()
  })
})

describe('PortValueEditor, an Enum field', () => {
  const names = ['Toggle', 'Dim', 'Scene']

  it('lists the names in the order given and writes the chosen name, never its position', () => {
    const onChange = draw({ fieldType: 'Enum', allowedValues: names, rawValue: '' })
    const select = screen.getByTestId('port-value-input-Value') as HTMLSelectElement

    expect([...select.options].map((option) => option.value)).toEqual(['', ...names])
    fireEvent.change(select, { target: { value: 'Scene' } })

    expect(onChange).toHaveBeenLastCalledWith('Scene')
  })

  it('shows the stored name as chosen', () => {
    draw({ fieldType: 'Enum', allowedValues: names, rawValue: 'Dim' })

    expect((screen.getByTestId('port-value-input-Value') as HTMLSelectElement).value).toBe('Dim')
  })

  it('shows a stored value outside the names as it is, marked, and offers it as no choice', () => {
    draw({ fieldType: 'Enum', allowedValues: names, rawValue: 1 })
    const select = screen.getByTestId('port-value-input-Value') as HTMLSelectElement

    expect(select.value).toBe('1')
    const stored = [...select.options].find((option) => option.value === '1')
    expect(stored?.textContent).toBe('1 (not allowed)')
    expect(stored?.disabled).toBe(true)
  })

  it('asks to take the value off the node when the blank entry is chosen, where the host gave a way', () => {
    const onClear = vi.fn()
    const onChange = draw({ fieldType: 'Enum', allowedValues: names, rawValue: 'Dim', onClear })

    fireEvent.change(screen.getByTestId('port-value-input-Value'), { target: { value: '' } })

    expect(onClear).toHaveBeenCalledTimes(1)
    expect(onChange).not.toHaveBeenCalled()
  })

  it('stays a text field for a type that is not Enum, whatever names it is handed', () => {
    draw({ fieldType: 'String', allowedValues: names, rawValue: '' })

    expect(screen.getByTestId('port-value-input-Value').tagName).toBe('INPUT')
  })
})


describe('PortValueEditor — a Map<String,String> field', () => {
  function Rows(props: Partial<PortValueEditorProps> & { onStored?: (value: unknown) => void }) {
    const { onStored, rawValue: initial, ...rest } = props
    const [value, setValue] = useState<unknown>(initial ?? '')
    const store = (next: unknown) => { setValue(next); onStored?.(next) }
    return (
      <ThemeProvider theme={theme}>
        <PortValueEditor
          fieldName="Headers" label="Headers" fieldType="Map<String,String>" hasConnection={false}
          {...rest} rawValue={value}
          onChange={store} onObjectChange={store} onClear={() => { store('') }}
        />
      </ThemeProvider>
    )
  }
  const key = (position: number) => screen.getByLabelText(`Headers key ${String(position)}`)
  const value = (position: number) => screen.getByLabelText(`Headers value ${String(position)}`)
  const add = () => userEvent.click(screen.getByRole('button', { name: 'Add Headers entry' }))

  it('writes the rows as an object', async () => {
    const stored = vi.fn()
    render(<Rows onStored={stored} />)
    await add()
    await userEvent.type(key(1), 'X-Zeta')
    await userEvent.type(value(1), 'one')
    await add()
    await userEvent.type(key(2), 'Accept')
    await userEvent.type(value(2), 'two')

    expect(stored.mock.lastCall?.[0]).toEqual({ 'X-Zeta': 'one', Accept: 'two' })
  })

  it('shows a stored object as one row per member', () => {
    render(<Rows rawValue={{ b: '2', a: '1' }} />)
    expect([key(1), key(2)].map((input) => (input as HTMLInputElement).value)).toEqual(['b', 'a'])
    expect([value(1), value(2)].map((input) => (input as HTMLInputElement).value)).toEqual(['2', '1'])
  })

  it('marks a repeated key on both rows, keeps both, and writes nothing until one is changed', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ a: '1' }} onStored={stored} />)
    await add()
    await userEvent.type(key(2), 'a')
    await userEvent.type(value(2), '2')

    expect(key(1)).toBeInvalid()
    expect(key(2)).toBeInvalid()
    expect(screen.getAllByText('Another row has this key')).toHaveLength(2)
    expect(value(1)).toHaveValue('1')
    expect(value(2)).toHaveValue('2')
    expect(stored).not.toHaveBeenCalled()

    await userEvent.type(key(2), 'b')
    expect(key(1)).toBeValid()
    expect(stored.mock.lastCall?.[0]).toEqual({ a: '1', ab: '2' })
  })

  it('marks a row with no key and writes nothing for it', async () => {
    const stored = vi.fn()
    render(<Rows onStored={stored} />)
    await add()
    await userEvent.type(value(1), 'v')

    expect(key(1)).toBeInvalid()
    expect(screen.getByText('A key is required')).toBeInTheDocument()
    expect(stored).not.toHaveBeenCalled()
  })

  it('marks a header pasted whole as a key on a secret-capable map, names no part of it, and writes nothing for it', async () => {
    const stored = vi.fn()
    render(<Rows secretValues onStored={stored} />)
    await add()
    await userEvent.click(key(1))
    await userEvent.paste('Authorization: Bearer hunter2')

    expect(key(1)).toBeInvalid()
    expect(screen.getByText(/A name is letters, digits/)).toBeInTheDocument()
    expect(screen.queryByText(/Bearer|hunter2/)).toBeNull()
    expect(key(1)).toHaveValue('Authorization: Bearer hunter2')
    expect(stored).not.toHaveBeenCalled()
  })

  it('writes every token character as a key on a secret-capable map, and any key on another map', async () => {
    const stored = vi.fn()
    const { unmount } = render(<Rows secretValues onStored={stored} />)
    await add()
    await userEvent.click(key(1))
    await userEvent.paste("X-Api_Key.1~|`^'*+&%$#!")
    expect(key(1)).toBeValid()
    expect(stored.mock.lastCall?.[0]).toEqual({ "X-Api_Key.1~|`^'*+&%$#!": '' })
    unmount()

    const other = vi.fn()
    render(<Rows onStored={other} />)
    await add()
    await userEvent.click(key(1))
    await userEvent.paste('Accept: x')
    expect(key(1)).toBeValid()
    expect(other.mock.lastCall?.[0]).toEqual({ 'Accept: x': '' })
  })

  it('marks a stored key that is no field name on a secret-capable map and leaves the document as it is', () => {
    const stored = vi.fn()
    render(<Rows secretValues rawValue={{ 'Bearer x': 'v' }} onStored={stored} />)

    expect(key(1)).toBeInvalid()
    expect(stored).not.toHaveBeenCalled()
  })

  it('keeps a key as typed, spaces and case included', async () => {
    const stored = vi.fn()
    render(<Rows onStored={stored} />)
    await add()
    await userEvent.type(key(1), ' Mixed Case ')
    expect(stored.mock.lastCall?.[0]).toEqual({ ' Mixed Case ': '' })
  })

  it('writes a key that is a whole number like any other', async () => {
    const stored = vi.fn()
    render(<Rows onStored={stored} />)
    await add()
    await userEvent.type(key(1), '2')
    await userEvent.type(value(1), 'x')

    expect(key(1)).toBeValid()
    expect(stored.mock.lastCall?.[0]).toEqual({ 2: 'x' })
  })

  it('removes a row and clears the value with the last one', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ a: '1', b: '2' }} onStored={stored} />)

    await userEvent.click(screen.getByRole('button', { name: 'Remove Headers entry 1' }))
    expect(stored).toHaveBeenLastCalledWith({ b: '2' })
    expect(screen.queryByLabelText('Headers key 2')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Remove Headers entry 1' }))
    expect(stored).toHaveBeenLastCalledWith('')
    expect(screen.getByText('No entries')).toBeInTheDocument()
  })

  it('moves focus to the new key, and to Add after a removal, so the keyboard keeps its place', async () => {
    render(<Rows rawValue={{ a: '1' }} />)
    await add()
    expect(key(2)).toHaveFocus()

    await userEvent.click(screen.getByRole('button', { name: 'Remove Headers entry 2' }))
    expect(screen.getByRole('button', { name: 'Add Headers entry' })).toHaveFocus()
  })

  it('reaches every control by keyboard', async () => {
    render(<Rows rawValue={{ a: '1' }} />)
    await userEvent.tab()
    await userEvent.tab()
    expect(key(1)).toHaveFocus()
    await userEvent.tab()
    expect(value(1)).toHaveFocus()
    await userEvent.tab()
    expect(screen.getByRole('button', { name: 'Remove Headers entry 1' })).toHaveFocus()
    await userEvent.tab()
    await userEvent.keyboard('{Enter}')
    expect(key(2)).toHaveFocus()
  })

  it('shows a stored value rows cannot hold as the JSON it is, not as rows', () => {
    render(<Rows rawValue={{ a: 1 }} />)
    expect(screen.queryByRole('button', { name: 'Add Headers entry' })).not.toBeInTheDocument()
    expect(screen.getByTestId('port-value-input-Headers')).toHaveValue('{\n  "a": 1\n}')
  })

  it.each(['Map<String,Int>', 'Map<String,String>?', 'Map<String,$T>'])(
    'opens the JSON text editor for %s', (fieldType) => {
      render(<Rows fieldType={fieldType as PortValueEditorProps['fieldType']} />)
      expect(screen.queryByRole('button', { name: 'Add Headers entry' })).not.toBeInTheDocument()
      expect(screen.getByTestId('port-value-input-Headers')).toBeInTheDocument()
    })

  it('offers no rows where the host gave no way to write an object', () => {
    render(
      <ThemeProvider theme={theme}>
        <PortValueEditor fieldName="Headers" label="Headers" fieldType="Map<String,String>" rawValue="" hasConnection={false} onChange={vi.fn()} />
      </ThemeProvider>,
    )
    expect(screen.queryByRole('button', { name: 'Add Headers entry' })).not.toBeInTheDocument()
  })
})

describe('PortValueEditor — the JSON text of a map', () => {
  function drawMap(fieldType: string) {
    const onObjectChange = vi.fn()
    render(
      <ThemeProvider theme={theme}>
        <PortValueEditor
          fieldName="Weights" label="Weights" fieldType={fieldType as PortValueEditorProps['fieldType']} rawValue="" hasConnection={false}
          onChange={vi.fn()} onObjectChange={onObjectChange}
        />
      </ThemeProvider>,
    )
    return onObjectChange
  }
  const type = (text: string) => fireEvent.change(screen.getByTestId('port-value-input-Weights'), { target: { value: text } })

  it('refuses a repeated member instead of letting the parse drop one', () => {
    const onObjectChange = drawMap('Map<String,Int>')
    type('{"a": 1, "a": 2}')
    expect(onObjectChange).not.toHaveBeenCalled()
    expect(screen.getByText("Ambiguous JSON: the member name 'a' occurs more than once in the object at $")).toBeInTheDocument()
  })

  it('refuses a repeated member nested in a value', () => {
    const onObjectChange = drawMap('Json')
    type('{"outer": {"a": 1, "a": 2}}')
    expect(onObjectChange).not.toHaveBeenCalled()
  })

  it('writes a map whose members are all distinct, a whole-number key included', () => {
    const onObjectChange = drawMap('Map<String,Int>')
    type('{"b": 1, "2": 2}')
    expect(onObjectChange.mock.lastCall?.[0]).toEqual({ b: 1, 2: 2 })
  })
})

describe('PortValueEditor — an empty map literal is no value', () => {
  function Held(props: Partial<PortValueEditorProps> & { onCleared: () => void; onWritten: (value: unknown) => void }) {
    const { onCleared, onWritten, rawValue: initial, ...rest } = props
    const [value, setValue] = useState<unknown>(initial ?? '')
    return (
      <ThemeProvider theme={theme}>
        <PortValueEditor
          fieldName="Weights" label="Weights" fieldType="Map<String,Int>" hasConnection={false}
          renderExpressionEditor={() => <div />}
          {...rest} rawValue={value}
          onChange={(next) => { setValue(next); onWritten(next) }}
          onObjectChange={(next) => { setValue(next); onWritten(next) }}
          onClear={() => { setValue(''); onCleared() }}
        />
      </ThemeProvider>
    )
  }

  it('takes the value off when the JSON text is emptied', () => {
    const onCleared = vi.fn()
    const onWritten = vi.fn()
    render(<Held rawValue={{ a: 1 }} onCleared={onCleared} onWritten={onWritten} />)
    fireEvent.change(screen.getByTestId('port-value-input-Weights'), { target: { value: '' } })

    expect(onCleared).toHaveBeenCalledTimes(1)
    expect(onWritten).not.toHaveBeenCalled()
  })

  it('takes the value off when an empty expression returns to a value', async () => {
    const onCleared = vi.fn()
    const onWritten = vi.fn()
    render(<Held onCleared={onCleared} onWritten={onWritten} />)
    await userEvent.click(screen.getByTestId('mode-expression-Weights'))
    expect(onWritten).toHaveBeenLastCalledWith('expr:')
    await userEvent.click(screen.getByTestId('mode-value-Weights'))

    expect(onCleared).toHaveBeenCalledTimes(1)
    expect(onWritten).toHaveBeenCalledTimes(1)
  })
})

describe('PortValueEditor — hidden and masked entries of a secret-capable map', () => {
  function Rows({ rawValue, onStored, ...rest }: Partial<PortValueEditorProps> & { onStored?: (value: unknown) => void }) {
    const [value, setValue] = useState<unknown>(rawValue ?? '')
    const store = (next: unknown) => { setValue(next); onStored?.(next) }
    return (
      <ThemeProvider theme={theme}>
        <PortValueEditor
          fieldName="Headers" label="Headers" fieldType="Map<String,String>" hasConnection={false}
          secretValues {...rest} rawValue={value}
          onChange={store} onObjectChange={store} onClear={() => { store('') }}
        />
      </ThemeProvider>
    )
  }
  const value = (position: number) => screen.getByLabelText(`Headers value ${String(position)}`)
  const hideToggle = (position: number) => screen.queryByRole('switch', { name: `Hide value of Headers entry ${String(position)}` })

  it('shows a masked entry as hidden and read-only, with its key fixed, and keeps it through an edit of another entry', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ Authorization: '***', Accept: 'text/plain' }} onStored={stored} />)

    expect(value(1)).toHaveAttribute('type', 'password')
    expect(value(1)).toHaveAttribute('readonly')
    expect(screen.getByLabelText('Headers key 1')).toHaveAttribute('readonly')
    expect(screen.getByLabelText('Headers key 2')).not.toHaveAttribute('readonly')

    await userEvent.type(value(2), '!')

    expect(stored.mock.lastCall?.[0]).toEqual({ Authorization: '***', Accept: 'text/plain!' })
  })

  it('keeps the stored secret while a replacement is empty, marks the row, and writes protect:<value> once one is typed', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ Authorization: '***' }} canProtectSecrets onStored={stored} />)

    await userEvent.click(screen.getByRole('button', { name: 'Replace value of Headers entry 1' }))
    expect(stored).not.toHaveBeenCalled()
    expect(value(1)).toHaveValue('')
    expect(value(1)).toHaveAttribute('type', 'password')
    expect(value(1)).not.toHaveAttribute('readonly')
    expect(hideToggle(1)).toBeChecked()
    expect(screen.getByLabelText('Headers key 1')).toHaveAttribute('readonly')
    expect(screen.getByText(VALUE_REPLACEMENT_REQUIRED)).toBeInTheDocument()

    await userEvent.type(value(1), 'tok')
    expect(stored.mock.lastCall?.[0]).toEqual({ Authorization: 'protect:tok' })
    expect(screen.queryByText(VALUE_REPLACEMENT_REQUIRED)).toBeNull()

    await userEvent.clear(value(1))
    expect(stored.mock.lastCall?.[0]).toEqual({ Authorization: 'protect:tok' })
    expect(screen.getByText(VALUE_REPLACEMENT_REQUIRED)).toBeInTheDocument()
  })

  it('returns to the masked entry when a replacement is cancelled', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ Authorization: '***' }} canProtectSecrets onStored={stored} />)

    await userEvent.click(screen.getByRole('button', { name: 'Replace value of Headers entry 1' }))
    await userEvent.click(screen.getByRole('button', { name: 'Cancel replacing value of Headers entry 1' }))

    expect(stored.mock.lastCall?.[0]).toEqual({ Authorization: '***' })
    expect(value(1)).toHaveAttribute('readonly')
    expect(screen.getByRole('button', { name: 'Replace value of Headers entry 1' })).toBeInTheDocument()
  })

  it('shows an entry masked again when the field is remounted mid-replacement, and hidden once a value was typed', async () => {
    const stored = vi.fn()
    const first = render(<Rows rawValue={{ 'X-Account': '***' }} canProtectSecrets onStored={stored} />)
    await userEvent.click(screen.getByRole('button', { name: 'Replace value of Headers entry 1' }))
    first.unmount()

    const second = render(<Rows rawValue={{ 'X-Account': '***' }} canProtectSecrets onStored={stored} />)
    expect(screen.getByRole('button', { name: 'Replace value of Headers entry 1' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Replace value of Headers entry 1' }))
    await userEvent.type(value(1), 'tok')
    second.unmount()

    render(<Rows rawValue={stored.mock.lastCall?.[0]} canProtectSecrets />)
    expect(value(1)).toHaveValue('tok')
    expect(value(1)).toHaveAttribute('type', 'password')
    expect(hideToggle(1)).toBeChecked()
  })

  it('replaces a masked entry with a hidden one that is written as typed where the host has no codec', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ Authorization: '***' }} onStored={stored} />)

    await userEvent.click(screen.getByRole('button', { name: 'Replace value of Headers entry 1' }))
    expect(stored).not.toHaveBeenCalled()
    await userEvent.type(value(1), 'tok')

    expect(value(1)).toHaveAttribute('type', 'password')
    expect(hideToggle(1)).toBeNull()
    expect(stored.mock.lastCall?.[0]).toEqual({ Authorization: 'tok' })
  })

  it('refuses a stored protect: with no text, marking the row and rewriting nothing', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ Token: 'protect:', Accept: 'a' }} canProtectSecrets onStored={stored} />)

    expect(screen.getByText(VALUE_HIDDEN_REQUIRED)).toBeInTheDocument()
    await userEvent.type(value(2), 'b')

    expect(stored).not.toHaveBeenCalled()
  })

  it('offers the hide toggle only to a secret-capable map on a host with a codec', () => {
    const withCodec = render(<Rows rawValue={{ A: 'x' }} canProtectSecrets />)
    expect(hideToggle(1)).toBeInTheDocument()
    withCodec.unmount()

    const withoutCodec = render(<Rows rawValue={{ A: 'x' }} />)
    expect(hideToggle(1)).toBeNull()
    withoutCodec.unmount()

    render(<Rows rawValue={{ A: 'x' }} secretValues={false} canProtectSecrets />)
    expect(hideToggle(1)).toBeNull()
  })

  it('writes a hidden entry as protect:<value> and a shown one as typed', async () => {
    const stored = vi.fn()
    render(<Rows rawValue={{ 'X-Account': 'abc' }} canProtectSecrets onStored={stored} />)

    await userEvent.click(hideToggle(1) as HTMLElement)
    expect(value(1)).toHaveAttribute('type', 'password')
    expect(value(1)).toHaveValue('abc')
    expect(stored.mock.lastCall?.[0]).toEqual({ 'X-Account': 'protect:abc' })

    await userEvent.click(hideToggle(1) as HTMLElement)
    expect(value(1)).toHaveAttribute('type', 'text')
    expect(stored.mock.lastCall?.[0]).toEqual({ 'X-Account': 'abc' })
  })

  it('reads a stored protect: form as a hidden entry on a host with a codec, and as the text it is on one without', () => {
    const withCodec = render(<Rows rawValue={{ Token: 'protect:secret' }} canProtectSecrets />)
    expect(value(1)).toHaveValue('secret')
    expect(value(1)).toHaveAttribute('type', 'password')
    withCodec.unmount()

    render(<Rows rawValue={{ Token: 'protect:secret' }} />)
    expect(value(1)).toHaveValue('protect:secret')
    expect(value(1)).toHaveAttribute('type', 'text')
  })

  it('shows stars as text in a map that is not secret-capable', () => {
    render(<Rows rawValue={{ A: '***' }} secretValues={false} />)
    expect(value(1)).toHaveValue('***')
    expect(screen.queryByRole('button', { name: 'Replace value of Headers entry 1' })).toBeNull()
  })

  it('keeps stars typed into a shown entry as typed, rather than turning the entry into a masked one', async () => {
    const stored = vi.fn()
    render(<Rows canProtectSecrets onStored={stored} />)
    await userEvent.click(screen.getByRole('button', { name: 'Add Headers entry' }))
    await userEvent.type(screen.getByLabelText('Headers key 1'), 'X-Note')
    await userEvent.type(value(1), '***x')

    expect(value(1)).toHaveValue('***x')
    expect(value(1)).not.toHaveAttribute('readonly')
    expect(stored.mock.lastCall?.[0]).toEqual({ 'X-Note': '***x' })
  })
})

describe('PortValueEditor — a stored value its term refuses', () => {
  it('is shown as stored and invalid instead of in the typed control, and Replace writes false for a Boolean', async () => {
    const onChange = draw({ fieldType: 'Boolean', rawValue: 'true', storedValueRefusal: 'Boolean' })
    expect(screen.getByTestId('port-value-refused-Value')).toHaveTextContent('The stored value "true" cannot be read as Boolean.')
    expect(screen.queryByRole('switch')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Replace' }))
    expect(onChange).toHaveBeenCalledWith(false)
  })

  it('clears any other value through onClear', async () => {
    const onClear = vi.fn()
    draw({ fieldType: 'Int', rawValue: 'many', storedValueRefusal: 'Int', onClear })
    await userEvent.click(screen.getByRole('button', { name: 'Replace' }))
    expect(onClear).toHaveBeenCalledOnce()
  })

  it('does not swap out the control for a value the editor itself wrote under the same type', async () => {
    function Graded() {
      const [value, setValue] = useState<unknown>('[1]')
      const refusal = typeof value === 'string' ? 'Collection<Int>' : undefined
      return (
        <ThemeProvider theme={theme}>
          <PortValueEditor
            fieldName="Value" label="Value" hasConnection={false} fieldType="Collection<Int>" literalForm={undefined}
            rawValue={value} storedValueRefusal={refusal} onChange={setValue} onObjectChange={setValue}
          />
        </ThemeProvider>
      )
    }
    render(<Graded />)
    expect(screen.getByTestId('port-value-refused-Value')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Replace' }))

    const field = screen.getByTestId('port-value-input-Value')
    fireEvent.change(field, { target: { value: '[2]' } })
    fireEvent.change(field, { target: { value: '' } })

    expect(screen.queryByTestId('port-value-refused-Value')).not.toBeInTheDocument()
    expect(screen.getByTestId('port-value-input-Value')).toHaveValue('')
  })

  it('shows a value it wrote as refused once the term changes under the same field type', () => {
    function Retyped() {
      const [value, setValue] = useState<unknown>([1])
      const [term, setTerm] = useState('Collection<Int>')
      const items = Array.isArray(value) ? (value as unknown[]) : []
      const fits = term === 'Collection<Int>' ? items.every((n) => typeof n === 'number') : items.every((n) => typeof n === 'string')
      return (
        <ThemeProvider theme={theme}>
          <button type="button" onClick={() => { setTerm('Collection<String>') }}>retype</button>
          <PortValueEditor
            fieldName="Value" label="Value" hasConnection={false} fieldType="Json" valueTerm={term} literalForm={undefined}
            rawValue={value} storedValueRefusal={fits ? undefined : term} onChange={setValue} onObjectChange={setValue}
          />
        </ThemeProvider>
      )
    }
    render(<Retyped />)
    fireEvent.change(screen.getByTestId('port-value-input-Value'), { target: { value: '[1,2]' } })
    expect(screen.queryByTestId('port-value-refused-Value')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'retype' }))

    expect(screen.getByTestId('port-value-refused-Value')).toHaveTextContent('cannot be read as Collection<String>.')
  })
})

