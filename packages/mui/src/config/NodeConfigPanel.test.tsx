import { describe, expect, it } from 'vitest'
import { act, fireEvent, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor, NodeErrorPolicy, WorkflowDefinition, WorkflowEditorStore, WorkflowNode } from '@reeve-iot/workflow-core'
import { BUILTIN_PORT_TYPES, WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_INPUT_NODE_ID, createPortTypeRegistry, readStoredDefinitionText, useWorkflowEditorStore } from '@reeve-iot/workflow-core'
import { NodeConfigPanel } from './NodeConfigPanel.js'
import { TypeArgumentPicker } from './TypeArgumentPicker.js'
import type { NodeConfigPanelProps } from './NodeConfigPanel.js'
import { NodeConfigCustomizationContext } from './nodeConfigCustomization.js'
import type { NodeConfigCustomization } from './nodeConfigCustomization.js'
import { renderInEditor } from '../testUtils.js'
import { BlockNode } from '../canvas/BlockNode.js'
import { NodePortCustomizationContext } from '../canvas/portCustomization.js'
import type { NodePortCustomization } from '../canvas/portCustomization.js'

const widget: BlockTypeDescriptor = {
  blockTypeId: 'Widget', displayName: 'Widget',
  inputs: [{ name: 'DeviceId', displayName: 'Device', isRequired: true, type: 'Device' }],
  outputs: [{ name: 'Result', displayName: 'Result', isRequired: false, type: 'String' }],
  configurationSchema: [
    { name: 'DeviceId', label: 'Device', type: 'String', required: true },
    { name: 'Label', label: 'Label', type: 'String', required: false, description: 'What to call it' },
    { name: 'Enabled', label: 'Enabled', type: 'Boolean', required: false, defaultValue: 'true' },
    { name: 'Verbose', label: 'Verbose', type: 'Boolean', required: false },
  ],
}

const dynamic: BlockTypeDescriptor = {
  blockTypeId: 'Dynamic', displayName: 'Dynamic', inputs: [], outputs: [], hasDynamicPorts: true,
  configurationSchema: [
    { name: 'InputPorts', label: 'Input Ports', type: 'String', required: false },
    { name: 'OutputPorts', label: 'Output Ports', type: 'String', required: false },
    { name: 'Label', label: 'Label', type: 'String', required: false },
  ],
}

const branching: BlockTypeDescriptor = {
  blockTypeId: 'Branch', displayName: 'Branch', inputs: [], outputs: [],
  subCanvasNames: ['Body'], supportsDeclaredOutputs: true,
}

const entry: BlockTypeDescriptor = { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] }

const timed: BlockTypeDescriptor = {
  blockTypeId: 'Timed', displayName: 'Timed', inputs: [], outputs: [],
  configurationSchema: [
    { name: 'DurationMs', label: 'Duration (ms)', type: 'Int', required: false, defaultValue: '1000' },
    { name: 'Path', label: 'JSONPath', type: 'String', required: false, defaultValue: '$.value' },
  ],
}

const expression: BlockTypeDescriptor = {
  blockTypeId: 'Expr', displayName: 'Expr', inputs: [], outputs: [],
  configurationSchema: [
    { name: 'Expression', label: 'Expression', type: 'String', required: false, defaultValue: 'expr: ' },
  ],
}

const at = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })

const oneWidget = (extra: Partial<WorkflowNode> = {}): WorkflowDefinition =>
  ({ nodes: [at('w', 'Widget', extra)], edges: [] })

function show(
  definition: WorkflowDefinition = oneWidget(),
  custom: NodeConfigCustomization = {},
  selected: string | null = 'w',
  catalogue: BlockTypeDescriptor[] = [widget, dynamic, branching, entry],
  portValueEditor?: NodeConfigPanelProps['portValueEditor'],
  extra: Partial<NodeConfigPanelProps> = {},
) {
  return renderInEditor(
    <NodeConfigCustomizationContext.Provider value={custom}>
      <NodeConfigPanel portValueEditor={portValueEditor} {...extra} />
    </NodeConfigCustomizationContext.Provider>,
    {
      preload: (store) => {
        store.getState().setBlockTypes(catalogue)
        store.getState().loadWorkflow(definition)
        store.getState().setSelectedNode(selected)
      },
    },
  )
}

function WorkflowCanvasNodes() {
  const nodes = useWorkflowEditorStore((s) => s.nodes)
  return (
    <>
      {nodes.map((node) => (
        <BlockNode
          key={node.id} node={node} isSelected={false} isConnecting={false}
          onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}}
        />
      ))}
    </>
  )
}

const configOf = (view: { editor: () => WorkflowEditorStore }, id = 'w') =>
  view.editor().getState().nodes.find((n) => n.id === id)?.configuration

describe('NodeConfigPanel', () => {
  it('keeps both fields when a host writes two of them from one render', async () => {
    const user = userEvent.setup()
    const writers = new Map<string, (value: string) => void>()
    const view = show(oneWidget(), {
      renderConfigField: ({ field, onChange }) => {
        writers.set(field.name, (value) => { onChange(value) })
        return (
          <button
            type="button"
            onClick={() => { writers.get('Label')?.('one'); writers.get('Verbose')?.('two') }}
          >
            {`write-${field.name}`}
          </button>
        )
      },
    })

    await user.click(screen.getByText('write-Label'))

    expect(configOf(view)?.['Label']).toBe('one')
    expect(configOf(view)?.['Verbose']).toBe('two')
  })

  it('refuses a host field writing null at that field, and what is saved still opens', async () => {
    const user = userEvent.setup()
    const loose = (value: unknown) => value as string
    const view = show(oneWidget({ configuration: { Label: 'kept' } }), {
      renderConfigField: ({ field, onChange }) => (field.name === 'Label'
        ? <button type="button" onClick={() => { onChange(loose(null)) }}>clear-Label</button>
        : undefined),
    })

    await user.click(screen.getByText('clear-Label'))

    expect(screen.getByRole('alert')).toHaveTextContent('Label cannot be null')
    expect(configOf(view)?.['Label']).toBe('kept')
    expect(readStoredDefinitionText(JSON.stringify(view.editor().getState().getWorkflowDefinition())).ok).toBe(true)
  })

  it('refuses a host section writing a null into the whole configuration', async () => {
    const user = userEvent.setup()
    const loose = (value: unknown) => value as Record<string, string>
    const view = show(oneWidget({ configuration: { Label: 'kept' } }), {
      renderNodeSections: ({ onConfigurationChange }) => (
        <button type="button" onClick={() => { onConfigurationChange(loose({ Picked: null })) }}>pick</button>
      ),
    })

    await user.click(screen.getByText('pick'))

    expect(screen.getByRole('alert')).toHaveTextContent('Picked cannot be null')
    expect(configOf(view)).toEqual({ Label: 'kept' })
  })

  it('renders nothing until a node is selected', () => {
    show(oneWidget(), {}, null)
    expect(screen.queryByTestId('node-config-panel')).not.toBeInTheDocument()
  })

  it('shows a node whose block type the catalogue does not carry read-only, in the host\'s words where it gives them', () => {
    show({ nodes: [at('w', 'Ghost', { configuration: { Code: 'return 1;' } })], edges: [] }, {}, 'w')
    const panel = screen.getByTestId('node-config-panel')
    expect(panel).toHaveAttribute('data-unavailable', 'true')
    expect(screen.getByTestId('unavailable-block-reason')).toHaveTextContent("Block type 'Ghost' is not available on this host")
    expect(screen.getByTestId('unavailable-block-configuration')).toHaveTextContent('"Code": "return 1;"')
    expect(within(panel).queryByRole('textbox')).not.toBeInTheDocument()
  })

  it('keeps a node of an unavailable block type exactly as stored, drawn and inspected, while the rest of the workflow is edited', async () => {
    const user = userEvent.setup()
    const ghost: WorkflowNode = at('g', 'Ghost', {
      position: { x: 300, y: 120 },
      configuration: { Code: 'return 1;', Retries: 2, Options: { a: [1, 2] } },
      typeArguments: { T: 'Int' }, passthrough: ['Carry'], errorPolicy: { policy: 'Skip' },
      outputPorts: [{ name: 'Out', type: 'String', sources: { Body: 'Done' } }],
      subCanvases: { Body: { nodes: [at('inner', 'Ghost')], edges: [] } },
    })
    const edges: WorkflowDefinition['edges'] = [
      { id: 'in', sourceNodeId: 'w', targetNodeId: 'g', mappings: { Value: { type: 'literal', literalValue: 'true' }, Carry: { type: 'direct', sourceOutputName: 'Result' } } },
      { id: 'out', sourceNodeId: 'g', targetNodeId: 'w', mappings: { DeviceId: { type: 'direct', sourceOutputName: 'Out' } } },
    ]
    const view = renderInEditor(
      <>
        <svg><WorkflowCanvasNodes /></svg>
        <NodeConfigPanel showWireLiterals />
      </>,
      {
        preload: (store) => {
          store.getState().setBlockTypes([widget])
          store.getState().loadWorkflow({ nodes: [at('w', 'Widget'), structuredClone(ghost)], edges: structuredClone(edges) })
          store.getState().setSelectedNode('g')
        },
      },
    )
    expect(view.editor().getState().loadedDocumentFault).toBeNull()
    expect(screen.getByTestId('workflow-node-Ghost')).toHaveAttribute('data-unavailable', 'true')
    expect(screen.getByTestId('node-config-panel')).toHaveAttribute('data-unavailable', 'true')
    expect(screen.getByTestId('unavailable-block-wires')).toHaveTextContent('Value = "true" (from w)')
    expect(screen.getByTestId('unavailable-block-wires')).toHaveTextContent('w.DeviceId ↳ Out')

    act(() => { view.editor().getState().setSelectedNode('w') })
    await user.type(screen.getByTestId('port-value-input-Label'), 'x')

    const saved = view.editor().getState().getWorkflowDefinition()
    expect(saved.nodes?.find((n) => n.id === 'w')?.configuration).toEqual({ Label: 'x' })
    expect(saved.nodes?.find((n) => n.id === 'g')).toEqual(ghost)
    expect(saved.edges).toEqual(edges)
  })

  const setter: BlockTypeDescriptor = {
    blockTypeId: 'Setter', displayName: 'Setter', outputs: [], typeParameters: [{ name: 'T' }],
    inputs: [
      { name: 'Value', displayName: 'Value', isRequired: true, type: '$T' },
      { name: 'Count', displayName: 'Count', isRequired: false, type: 'Int' },
      { name: 'Mode', displayName: 'Mode', isRequired: false, type: 'String', allowedValues: ['On', 'Off'] },
      { name: 'Label', displayName: 'Label', isRequired: false, type: 'String' },
    ],
    configurationSchema: [{ name: 'Label', label: 'Label', type: 'String', required: false }],
  }
  const wiredSetter = (mappings: WorkflowDefinition['edges'][number]['mappings'], extra: Partial<WorkflowNode> = {}): WorkflowDefinition => ({
    nodes: [at('w', 'Widget'), at('s', 'Setter', extra)],
    edges: [{ id: 'e', sourceNodeId: 'w', targetNodeId: 's', mappings }],
  })
  const showSetter = (definition: WorkflowDefinition, custom: NodeConfigCustomization = {}) =>
    show(definition, custom, 's', [widget, setter], undefined, { showWireLiterals: true })
  const wireOf = (view: { editor: () => WorkflowEditorStore }, input: string) => view.editor().getState().edges[0]?.mappings[input]
  const answer = (view: { drive: (act: (store: WorkflowEditorStore) => void) => void }, effective: string) => {
    view.drive((store) => {
      store.setState({
        analysis: {
          revision: store.getState().revision, registryGeneration: 1, diagnostics: [],
          nodes: [{
            canvasPath: [], nodeId: 's', typeArguments: { T: effective }, outputs: [],
            inputs: [{ name: 'Value', declared: '$T', effective, state: 'bound', unbound: [] }],
          }],
        },
      })
    })
  }

  it('draws no wire literal unless the host turns them on', () => {
    show(wiredSetter({ Count: { type: 'literal', literalValue: 2 } }), {}, 's', [widget, setter])
    expect(screen.queryByTestId('wire-literal-Count')).not.toBeInTheDocument()
  })

  it('draws an input fed by a wire literal with its value editor, which writes the wire\'s mapping', async () => {
    const user = userEvent.setup()
    const view = showSetter(wiredSetter({ Value: { type: 'literal', literalValue: 'on' }, Count: { type: 'literal', literalValue: 2 } }))

    expect(screen.getByTestId('wire-literal-Value')).toHaveTextContent('not chosen yet')
    const count = within(screen.getByTestId('wire-literal-Count')).getByTestId('port-value-input-Count')
    expect(count).toHaveValue('2')
    await user.clear(count)
    await user.type(count, '5')
    expect(wireOf(view, 'Count')).toEqual({ type: 'literal', literalValue: 5 })

    act(() => { view.editor().getState().setNodeTypeArguments('s', { T: 'Boolean' }) })
    const refused = screen.getByTestId('port-value-refused-Value')
    expect(refused).toHaveTextContent('The stored value "on" cannot be read as Boolean.')
    expect(within(screen.getByTestId('wire-literal-Value')).queryByRole('switch')).not.toBeInTheDocument()

    await user.click(within(refused).getByRole('button', { name: 'Replace' }))
    expect(wireOf(view, 'Value')).toEqual({ type: 'literal', literalValue: false })
    expect(within(screen.getByTestId('wire-literal-Value')).getByRole('switch')).not.toBeChecked()
  })

  it('follows a type argument the moment it is set, over an analysis answer that bound the input otherwise', () => {
    const view = showSetter(wiredSetter({ Value: { type: 'literal', literalValue: 'true' } }))
    answer(view, 'String')
    expect(within(screen.getByTestId('wire-literal-Value')).getByTestId('port-value-input-Value')).toHaveValue('true')

    act(() => { view.editor().getState().setNodeTypeArguments('s', { T: 'Boolean' }) })

    expect(wireOf(view, 'Value')).toEqual({ type: 'literal', literalValue: true })
    expect(screen.queryByTestId('port-value-refused-Value')).not.toBeInTheDocument()
    expect(within(screen.getByTestId('wire-literal-Value')).getByRole('switch')).toBeChecked()
  })

  it('shows a value refused again when the term changes after a Replace, even one the editor wrote', async () => {
    const user = userEvent.setup()
    const view = showSetter(wiredSetter({ Value: { type: 'literal', literalValue: 'on' } }, { typeArguments: { T: 'Boolean' } }))
    await user.click(within(screen.getByTestId('port-value-refused-Value')).getByRole('button', { name: 'Replace' }))

    act(() => { view.editor().getState().setNodeTypeArguments('s', { T: 'String' }) })
    const text = within(screen.getByTestId('wire-literal-Value')).getByTestId('port-value-input-Value')
    expect(text).toHaveValue('false')
    await user.clear(text)
    await user.type(text, 'abc')
    act(() => { view.editor().getState().setNodeTypeArguments('s', { T: 'Boolean' }) })

    expect(wireOf(view, 'Value')).toEqual({ type: 'literal', literalValue: 'abc' })
    expect(screen.getByTestId('port-value-refused-Value')).toHaveTextContent('"abc"')
    expect(within(screen.getByTestId('wire-literal-Value')).queryByRole('switch')).not.toBeInTheDocument()
  })

  it('draws a wire literal with allowed values as their select, and one into a schema field in that field\'s place', async () => {
    const user = userEvent.setup()
    const view = showSetter(wiredSetter(
      { Mode: { type: 'literal', literalValue: 'On' }, Label: { type: 'literal', literalValue: 'from the wire' } },
      { configuration: { Label: 'from the node' } },
    ))

    expect(within(screen.getByTestId('wire-literal-Mode')).getByTestId('port-value-input-Mode')).toHaveValue('On')
    const label = within(screen.getByTestId('wire-literal-Label')).getByTestId('port-value-input-Label')
    expect(label).toHaveValue('from the wire')
    expect(screen.getAllByTestId('port-value-input-Label')).toHaveLength(1)

    await user.type(label, '!')
    expect(wireOf(view, 'Label')).toEqual({ type: 'literal', literalValue: 'from the wire!' })
    expect(configOf(view, 's')).toEqual({ Label: 'from the node' })
  })

  it('leaves a wire literal the host draws itself to the host', () => {
    const view = showSetter(wiredSetter({ Count: { type: 'literal', literalValue: 2 } }), { renderWireLiteral: ({ input }) => (input.name === 'Count' ? null : undefined) })
    expect(screen.queryByTestId('wire-literal-Count')).not.toBeInTheDocument()
    expect(wireOf(view, 'Count')).toEqual({ type: 'literal', literalValue: 2 })
  })

  it('renders nothing for the workflow entry anchor, which has no configuration of its own', () => {
    show({ nodes: [at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID)], edges: [] }, {}, WORKFLOW_INPUT_NODE_ID)
    expect(screen.queryByTestId('node-config-panel')).not.toBeInTheDocument()
  })

  it('names the block and the node it is editing', () => {
    show()
    expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Widget')
    expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Node: w')
  })

  it('shows the sub-canvas path once the editor has descended into one', () => {
    const view = show({ nodes: [at('b', 'Branch', { subCanvases: { Body: { nodes: [at('w', 'Widget')], edges: [] } } })], edges: [] }, {}, null)
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Body') })
    view.drive((s) => { s.getState().setSelectedNode('w') })
    expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Path: Main Workflow / Body')
  })

  it('edits a scalar field through the port editor', async () => {
    const user = userEvent.setup()
    const view = show()
    await user.type(screen.getByTestId('port-value-input-Label'), 'hi')
    expect(configOf(view)?.['Label']).toBe('hi')
  })

  it('draws an Enum field as a select over its names, writes the name, and clears the key on the blank entry', () => {
    const enumBlock: BlockTypeDescriptor = {
      blockTypeId: 'Setter', displayName: 'Setter', inputs: [{ name: 'Kind', displayName: 'Kind', isRequired: false, type: 'String' }], outputs: [],
      configurationSchema: [{ name: 'Kind', label: 'Kind', type: 'Enum', required: false, allowedValues: ['Toggle', 'Dim'] }],
    }
    const view = show({ nodes: [at('w', 'Setter', { configuration: { Kind: 'Toggle' } })], edges: [] }, {}, 'w', [enumBlock, entry])
    const select = screen.getByTestId('port-value-input-Kind') as HTMLSelectElement

    expect(select.tagName).toBe('SELECT')
    fireEvent.change(select, { target: { value: 'Dim' } })
    expect(configOf(view)?.['Kind']).toBe('Dim')
    fireEvent.change(select, { target: { value: '' } })
    expect(configOf(view)).not.toHaveProperty('Kind')
  })

  it('offers a declared default as a hint, never as a value the node holds', () => {
    const view = show(
      { nodes: [at('w', 'Timed')], edges: [] }, {}, 'w', [timed, entry],
      { renderExpressionEditor: () => <div data-testid="host-expression" /> },
    )

    expect(configOf(view)).toEqual({})
    expect(screen.getByTestId('port-value-input-DurationMs')).toHaveValue('')
    expect(screen.getByTestId('port-value-input-DurationMs')).toHaveAttribute('placeholder', '1000')
    expect(screen.getByTestId('port-value-input-Path')).toHaveValue('')
    expect(screen.getByTestId('port-value-input-Path')).toHaveAttribute('placeholder', '$.value')
    expect(screen.getByTestId('mode-value-Path')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByTestId('host-expression')).not.toBeInTheDocument()
  })

  it('shows a stored value a default is also declared for, and keeps it', () => {
    const view = show({ nodes: [at('w', 'Timed', { configuration: { DurationMs: 1500 } })], edges: [] }, {}, 'w', [timed, entry])

    expect(screen.getByTestId('port-value-input-DurationMs')).toHaveValue('1500')
    expect(screen.getByTestId('port-value-input-DurationMs')).toHaveAttribute('placeholder', '1000')
    expect(configOf(view)).toEqual({ DurationMs: 1500 })
  })

  it('opens a field whose declared default is an expression in Expression mode, storing nothing', async () => {
    const user = userEvent.setup()
    const view = show(
      { nodes: [at('w', 'Expr')], edges: [] }, {}, 'w', [expression, entry],
      {
        renderExpressionEditor: ({ expressionBody, fieldName, onChange }) => (
          <textarea
            data-testid={`expr-body-${fieldName}`}
            value={expressionBody}
            onChange={(event) => { onChange(`expr: ${event.target.value}`) }}
          />
        ),
      },
    )

    expect(screen.getByTestId('mode-expression-Expression')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByTestId('expr-body-Expression')).toHaveValue('')
    expect(configOf(view)).toEqual({})

    await user.type(screen.getByTestId('expr-body-Expression'), 'a')
    expect(configOf(view)?.['Expression']).toBe('expr: a')
  })

  it('keeps an expression-defaulted field in Connection mode while a wire feeds it', () => {
    show({
      nodes: [at('p', 'Widget'), at('w', 'Expr')],
      edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: { Expression: { type: 'direct', sourceOutputName: 'Result' } } }],
    }, {}, 'w', [expression, widget, entry], { renderExpressionEditor: () => <div data-testid="host-expression" /> })

    expect(screen.getByTestId('mode-connection-Expression')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByTestId('host-expression')).not.toBeInTheDocument()
  })

  it('keeps a stored literal in Value mode even where the declared default is an expression', () => {
    show(
      { nodes: [at('w', 'Expr', { configuration: { Expression: 'plain' } })], edges: [] }, {}, 'w', [expression, entry],
      { renderExpressionEditor: () => <div data-testid="host-expression" /> },
    )

    expect(screen.getByTestId('mode-value-Expression')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByTestId('port-value-input-Expression')).toHaveValue('plain')
  })

  it('leaves an expression-defaulted field in Value mode when the host offers no expression editor', () => {
    show({ nodes: [at('w', 'Expr')], edges: [] }, {}, 'w', [expression, entry])

    expect(screen.getByTestId('mode-value-Expression')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByTestId('port-value-input-Expression')).toHaveAttribute('placeholder', 'expr: ')
  })

  it('reads a boolean field off until the node is given a value, a declared default included', async () => {
    const user = userEvent.setup()
    const view = show()
    expect(screen.getByLabelText('Enabled')).not.toBeChecked()
    expect(screen.getByLabelText('Verbose')).not.toBeChecked()
    await user.click(screen.getByLabelText('Enabled'))
    expect(configOf(view)?.['Enabled']).toBe(true)
  })

  describe('a field whose port is typed by a bare type parameter', () => {
    const generic: BlockTypeDescriptor = {
      ...widget,
      inputs: [{ name: 'Enabled', displayName: 'Enabled', isRequired: false, type: '$T' }],
    }
    const answer = (view: ReturnType<typeof show>, effective: string | null) => {
      view.drive((s) => {
        s.setState({
          analysis: {
            revision: s.getState().revision, registryGeneration: 1, diagnostics: [],
            nodes: [{
              canvasPath: [], nodeId: 'w', typeArguments: {}, outputs: [],
              inputs: [{
                name: 'Enabled', declared: '$T', effective, state: effective === null ? 'unbound' : 'bound', unbound: effective === null ? ['T'] : [],
              }],
            }],
          },
        })
      })
    }

    it('is text while nothing binds the parameter, and writes the text as a string', () => {
      const view = show(oneWidget(), {}, 'w', [generic, dynamic, branching, entry])
      expect(screen.queryByRole('switch', { name: 'Enabled' })).not.toBeInTheDocument()
      fireEvent.change(screen.getByTestId('port-value-input-Enabled'), { target: { value: '7' } })
      expect(configOf(view)?.['Enabled']).toBe('7')
    })

    it('is a switch once the analysis binds the parameter to Boolean', () => {
      const view = show(oneWidget(), {}, 'w', [generic, dynamic, branching, entry])
      answer(view, 'Boolean')
      expect(screen.getByRole('switch', { name: 'Enabled' })).toBeInTheDocument()
    })

    it('writes a number in the effective term, and refuses text that is no literal of it at the field', () => {
      const view = show(oneWidget(), {}, 'w', [generic, dynamic, branching, entry])
      answer(view, 'Int')
      fireEvent.change(screen.getByTestId('port-value-input-Enabled'), { target: { value: '5.5' } })
      expect(screen.getByTestId('port-value-error-Enabled')).toHaveAttribute('data-rule-id', 'literal-port-value-invalid')
      expect(configOf(view)?.['Enabled']).toBeUndefined()
      fireEvent.change(screen.getByTestId('port-value-input-Enabled'), { target: { value: '5' } })
      expect(configOf(view)?.['Enabled']).toBe(5)
      expect(screen.queryByTestId('port-value-error-Enabled')).not.toBeInTheDocument()
    })

    it('keeps the numeric control and writes numbers for two keystrokes in a row, the answer going stale between them', () => {
      const view = show(oneWidget(), {}, 'w', [generic, dynamic, branching, entry])
      answer(view, 'Int')
      fireEvent.change(screen.getByTestId('port-value-input-Enabled'), { target: { value: '1' } })
      expect(configOf(view)?.['Enabled']).toBe(1)
      expect(view.editor().getState().analysis?.revision).toBeLessThan(view.editor().getState().revision)
      fireEvent.change(screen.getByTestId('port-value-input-Enabled'), { target: { value: '10' } })
      expect(configOf(view)?.['Enabled']).toBe(10)
    })

    it('writes a list term as parsed JSON, as the mapping dialog does', () => {
      const listed: BlockTypeDescriptor = { ...widget, inputs: [{ name: 'Enabled', displayName: 'Enabled', isRequired: false, type: 'Collection<$T>' }] }
      const view = show(oneWidget(), {}, 'w', [listed, dynamic, branching, entry])
      fireEvent.change(screen.getByTestId('port-value-input-Enabled'), { target: { value: '[1, 2]' } })
      expect(configOf(view)?.['Enabled']).toEqual([1, 2])
    })
  })

  it('puts a field into connection mode for a literal mapping too, not only a wired one', () => {
    show({
      nodes: [at('p', 'Widget'), at('w', 'Widget')],
      edges: [
        { id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: { Label: { type: 'direct', sourceOutputName: 'Result' } } },
        { id: 'e2', sourceNodeId: 'p', targetNodeId: 'w', mappings: { DeviceId: { type: 'literal', literalValue: 'abc' } } },
      ],
    })
    expect(screen.getByTestId('mode-connection-Label')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByTestId('mode-connection-DeviceId')).toHaveAttribute('aria-pressed', 'true')
    expect(screen.queryByTestId('port-value-input-DeviceId')).not.toBeInTheDocument()
  })

  it('leaves a field editable when its only wire comes from a node absent from the canvas, which never fires', () => {
    const seen: Record<string, string | undefined> = {}
    show(
      {
        nodes: [at('p', 'Widget'), at('w', 'Widget')],
        edges: [
          { id: 'e1', sourceNodeId: 'ghost', targetNodeId: 'w', mappings: { Label: { type: 'direct', sourceOutputName: 'Result' } } },
          { id: 'e2', sourceNodeId: 'ghost', targetNodeId: 'w', mappings: { DeviceId: { type: 'literal', literalValue: 'abc' } } },
          { id: 'e3', sourceNodeId: 'p', targetNodeId: 'w', mappings: { Verbose: { type: 'direct', sourceOutputName: '  ' } } },
        ],
      },
      { renderConfigField: ({ field, edgeBinding }) => { seen[field.name] = edgeBinding; return undefined } },
    )
    expect(screen.getByTestId('mode-connection-Label')).toHaveAttribute('aria-pressed', 'false')
    expect(screen.getByTestId('mode-connection-Label')).toBeDisabled()
    expect(screen.getByTestId('mode-connection-Verbose')).toBeDisabled()
    expect(screen.getByTestId('mode-connection-DeviceId')).toHaveAttribute('aria-pressed', 'true')
    expect(seen).toEqual({ DeviceId: '= abc', Label: undefined, Enabled: undefined, Verbose: undefined })
  })

  it('offers the dynamic port tables only for a block that declares its ports, and never twice', () => {
    show({ nodes: [at('d', 'Dynamic')], edges: [] }, {}, 'd')
    expect(screen.getByText('Input Ports')).toBeInTheDocument()
    expect(screen.queryByTestId('port-value-editor-InputPorts')).not.toBeInTheDocument()
    expect(screen.queryByTestId('port-value-editor-OutputPorts')).not.toBeInTheDocument()
    expect(screen.getByTestId('port-value-editor-Label')).toBeInTheDocument()
  })

  it('leaves the dynamic port tables out for a block with fixed ports', () => {
    show()
    expect(screen.queryByText('Input Ports')).not.toBeInTheDocument()
  })

  it('does not carry a port name draft from one node to another holding the same ports', async () => {
    const user = userEvent.setup()
    const ports = JSON.stringify([{ name: 'Rows', displayName: 'Rows', type: 'String', isRequired: false }])
    const view = show(
      { nodes: [at('a', 'Dynamic', { configuration: { InputPorts: ports } }), at('b', 'Dynamic', { configuration: { InputPorts: ports } })], edges: [] },
      {}, 'a',
    )
    await user.clear(screen.getAllByLabelText('Port 1 name')[0]!)
    expect(screen.getAllByLabelText('Port 1 name')[0]).toHaveValue('')

    view.drive((s) => { s.getState().setSelectedNode('b') })

    expect(screen.getAllByLabelText('Port 1 name')[0]).toHaveValue('Rows')
    await user.type(screen.getAllByLabelText('Port 1 name')[0]!, 'Z')
    expect(JSON.parse(String(configOf(view, 'b')?.['InputPorts']))[0]).toMatchObject({ name: 'RowsZ' })
    expect(JSON.parse(String(configOf(view, 'a')?.['InputPorts']))[0]).toMatchObject({ name: 'Rows' })
  })

  it('does not carry a custom passthrough draft from one node to another forwarding the same names', async () => {
    const user = userEvent.setup()
    const view = show({ nodes: [at('a', 'Dynamic'), at('b', 'Dynamic')], edges: [] }, {}, 'a')
    await user.type(screen.getByLabelText('Add variable name'), 'kept')
    expect(screen.getByLabelText('Add variable name')).toHaveValue('kept')

    view.drive((s) => { s.getState().setSelectedNode('b') })

    expect(screen.getByLabelText('Add variable name')).toHaveValue('')
  })

  it('does not carry a number box draft from one node to another holding a different timeout', async () => {
    const view = show(
      {
        nodes: [
          at('a', 'Dynamic', { configuration: { timeoutSeconds: 45 } }),
          at('b', 'Dynamic', { configuration: { timeoutSeconds: 90 } }),
        ],
        edges: [],
      },
      {}, 'a',
    )
    const box = () => screen.getByLabelText('Timeout (seconds)') as HTMLInputElement
    fireEvent.change(box(), { target: { value: '120' } })
    expect(box().value).toBe('120')

    view.drive((s) => { s.getState().setSelectedNode('b') })

    expect(box().value).toBe('90')
    fireEvent.blur(box())
    expect(configOf(view, 'b')?.['timeoutSeconds']).toBe(90)
    expect(configOf(view, 'a')?.['timeoutSeconds']).toBe(45)
  })

  it('offers the declared-output table only for a block that supports it', () => {
    show({ nodes: [at('b', 'Branch')], edges: [] }, {}, 'b')
    expect(screen.getByText('Declared Outputs (mapped per branch)')).toBeInTheDocument()
  })

  describe('host customization', () => {
    it('lets the host render a field itself', () => {
      show(oneWidget(), { renderConfigField: ({ field }) => (field.name === 'DeviceId' ? <div>Device picker</div> : undefined) })
      expect(screen.getByText('Device picker')).toBeInTheDocument()
      expect(screen.queryByTestId('port-value-editor-DeviceId')).not.toBeInTheDocument()
      expect(screen.getByTestId('port-value-editor-Label')).toBeInTheDocument()
    })

    it('hides a field the host owns elsewhere when it returns null', () => {
      show(oneWidget(), { renderConfigField: ({ field }) => (field.name === 'Label' ? null : undefined) })
      expect(screen.queryByTestId('port-value-editor-Label')).not.toBeInTheDocument()
    })

    it('tells the host the port type behind the field', () => {
      const seen: Record<string, string | undefined> = {}
      show(oneWidget(), {
        renderConfigField: ({ field, portType }) => {
          seen[field.name] = portType
          return undefined
        },
      })
      expect(seen['DeviceId']).toBe('Device')
      expect(seen['Label']).toBeUndefined()
    })

    it('tells the host what an incoming wire binds the field to', () => {
      const seen: Record<string, string | undefined> = {}
      show(
        {
          nodes: [at('p', 'Widget'), at('w', 'Widget')],
          edges: [{
            id: 'e1', sourceNodeId: 'p', targetNodeId: 'w',
            mappings: {
              DeviceId: { type: 'direct', sourceOutputName: 'Result' },
              Label: { type: 'literal', literalValue: 'pinned' },
            },
          }],
        },
        {
          renderConfigField: ({ field, edgeBinding }) => {
            seen[field.name] = edgeBinding
            return undefined
          },
        },
      )
      expect(seen['DeviceId']).toBe('↳ Result')
      expect(seen['Label']).toBe('= pinned')
      expect(seen['Enabled']).toBeUndefined()
    })

    it('tells the host the binding of a wired mapping and of a literal as they are stored', () => {
      const seen: Record<string, string | undefined> = {}
      show(
        {
          nodes: [at('p', 'Widget'), at('w', 'Widget')],
          edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: {
            DeviceId: { type: 'direct', sourceOutputName: 'Result' }, Label: { type: 'literal', literalValue: 'pinned' },
          } }],
        },
        { renderConfigField: ({ field, edgeBinding }) => { seen[field.name] = edgeBinding; return undefined } },
      )
      expect([seen['DeviceId'], seen['Label']]).toEqual(['↳ Result', '= pinned'])
    })

    it('lets a host field write the whole configuration at once', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget(), {
        renderConfigField: ({ field, onConfigurationChange }) =>
          field.name === 'DeviceId'
            ? <button onClick={() => { onConfigurationChange({ DeviceId: 'd1', Label: 'derived' }) }}>Pick</button>
            : undefined,
      })
      await user.click(screen.getByRole('button', { name: 'Pick' }))
      expect(configOf(view)).toEqual({ DeviceId: 'd1', Label: 'derived' })
    })

    describe('the type-argument picker slot', () => {
      const generic: BlockTypeDescriptor = { ...widget, blockTypeId: 'Generic', displayName: 'Generic', typeParameters: [{ name: 'T', constraints: ['Orderable'] }] }
      const picker = (context: Parameters<NonNullable<NodeConfigCustomization['renderTypeArgumentPicker']>>[0]) => (
        <>
          <div data-testid="seen">{JSON.stringify({ node: context.node.id, parameters: context.typeParameters, arguments: context.typeArguments })}</div>
          <button type="button" onClick={() => { context.onChange({ ...context.typeArguments, T: 'Int' }) }}>choose</button>
          <button type="button" onClick={() => { context.onChange(null) }}>clear</button>
        </>
      )
      const genericNode = (extra: Partial<WorkflowNode> = {}): WorkflowDefinition => ({ nodes: [at('w', 'Generic', extra)], edges: [] })
      const typeArgumentsOf = (view: { editor: () => WorkflowEditorStore }) => view.editor().getState().nodes.find((n) => n.id === 'w')?.typeArguments

      it('is called for a block that declares type parameters, and writes and clears the stored key', async () => {
        const user = userEvent.setup()
        const view = show(genericNode(), { renderTypeArgumentPicker: picker }, 'w', [generic, entry])
        expect(JSON.parse(screen.getByTestId('seen').textContent ?? '')).toEqual({ node: 'w', parameters: [{ name: 'T', constraints: ['Orderable'] }], arguments: {} })

        await user.click(screen.getByRole('button', { name: 'choose' }))
        expect(typeArgumentsOf(view)).toEqual({ T: 'Int' })

        await user.click(screen.getByRole('button', { name: 'clear' }))
        expect(view.editor().getState().nodes.find((n) => n.id === 'w')).not.toHaveProperty('typeArguments')
      })

      it('hands the stored arguments back', () => {
        show(genericNode({ typeArguments: { T: 'String' } }), { renderTypeArgumentPicker: picker }, 'w', [generic, entry])
        expect(JSON.parse(screen.getByTestId('seen').textContent ?? '')).toMatchObject({ arguments: { T: 'String' } })
      })

      it('is not called for a block with no type parameters', () => {
        show(oneWidget(), { renderTypeArgumentPicker: picker })
        expect(screen.queryByTestId('seen')).not.toBeInTheDocument()
      })

      it('draws nothing where the host provides no slot, or answers null', () => {
        show(genericNode(), {}, 'w', [generic, entry])
        expect(screen.queryByRole('combobox', { name: 'Type argument T' })).not.toBeInTheDocument()
        expect(screen.queryByRole('button', { name: 'choose' })).not.toBeInTheDocument()
      })

      describe('the library picker, mounted by a host in the slot', () => {
        const numeric: BlockTypeDescriptor = { ...widget, blockTypeId: 'Numeric', displayName: 'Numeric', typeParameters: [{ name: 'T', constraints: ['Numeric'] }, { name: 'R' }] }
        const numericNode = (extra: Partial<WorkflowNode> = {}): WorkflowDefinition => ({ nodes: [at('w', 'Numeric', extra)], edges: [] })
        const mounted: NodeConfigCustomization = { renderTypeArgumentPicker: (context) => <TypeArgumentPicker context={context} /> }
        const optionsOf = async (user: ReturnType<typeof userEvent.setup>, name: string) => {
          await user.click(screen.getByRole('combobox', { name }))
          const listed = screen.getAllByRole('option').map((option) => option.textContent)
          await user.keyboard('{Escape}')
          return listed
        }

        it('offers a Numeric parameter only the numeric terms, and any term but Json for a parameter with no constraint', async () => {
          const user = userEvent.setup()
          show(numericNode(), mounted, 'w', [numeric, entry])

          expect((await optionsOf(user, 'Type argument T')).filter((option) => option !== 'Inferred').sort()).toEqual(['Int', 'Number'])
          const free = await optionsOf(user, 'Type argument R')
          expect(free).toEqual(expect.arrayContaining(['Inferred', 'String', 'Int', 'Collection<Int>']))
          expect(free.some((option) => option?.includes('Json'))).toBe(false)
        })

        it('writes the picked term and Clear removes the key, leaving the other parameter alone', async () => {
          const user = userEvent.setup()
          const view = show(numericNode({ typeArguments: { R: 'String' } }), mounted, 'w', [numeric, entry])

          await user.click(screen.getByRole('combobox', { name: 'Type argument T' }))
          await user.click(screen.getByRole('option', { name: 'Int' }))
          expect(typeArgumentsOf(view)).toEqual({ T: 'Int', R: 'String' })

          await user.click(screen.getByRole('button', { name: 'Clear type argument T' }))
          expect(typeArgumentsOf(view)).toEqual({ R: 'String' })

          await user.click(screen.getByRole('button', { name: 'Clear type argument R' }))
          expect(view.editor().getState().nodes.find((n) => n.id === 'w')).not.toHaveProperty('typeArguments')
        })

        it('wraps the choice in a collection or a nullable level only where the constraints still hold', async () => {
          const user = userEvent.setup()
          const view = show(numericNode({ typeArguments: { T: 'Int', R: 'Int' } }), mounted, 'w', [numeric, entry])

          expect(screen.getByRole('button', { name: 'Type argument T in a collection' })).toBeDisabled()
          expect(screen.getByRole('button', { name: 'Type argument T nullable' })).toBeDisabled()

          await user.click(screen.getByRole('button', { name: 'Type argument R nullable' }))
          expect(typeArgumentsOf(view)).toEqual({ T: 'Int', R: 'Int?' })
          await user.click(screen.getByRole('button', { name: 'Type argument R in a collection' }))
          expect(typeArgumentsOf(view)).toEqual({ T: 'Int', R: 'Collection<Int?>' })
        })

        it('shows a stored empty term as itself, not as Inferred', () => {
          show(numericNode({ typeArguments: { T: '' } }), mounted, 'w', [numeric, entry])
          expect(screen.getByRole('combobox', { name: 'Type argument T' })).toHaveTextContent('(empty)')
          expect(screen.getByRole('button', { name: 'Clear type argument T' })).toBeEnabled()
        })

        it('shows a stored term the constraints do not admit instead of hiding it, and can clear it', async () => {
          const user = userEvent.setup()
          const view = show(numericNode({ typeArguments: { T: 'String' } }), mounted, 'w', [numeric, entry])

          expect(screen.getByRole('combobox', { name: 'Type argument T' })).toHaveTextContent('String')
          await user.click(screen.getByRole('button', { name: 'Clear type argument T' }))
          expect(view.editor().getState().nodes.find((n) => n.id === 'w')).not.toHaveProperty('typeArguments')
        })
      })
    })

    describe('the projection control slot', () => {
      const parse: BlockTypeDescriptor = {
        blockTypeId: 'Parse', displayName: 'Parse', inputs: [],
        outputs: [{ name: 'Value', displayName: 'Value', isRequired: false, type: 'Json?' }, { name: 'Ok', displayName: 'Ok', isRequired: false, type: 'Boolean' }],
      }
      const control = (context: Parameters<NonNullable<NodeConfigCustomization['renderProjectionControl']>>[0]) => (
        <>
          <div data-testid="projection">{JSON.stringify({ placement: context.placement, field: context.fieldName, target: context.targetType, json: context.jsonOutputs, body: context.expressionBody })}</div>
          <button type="button" onClick={() => { context.onExpressionChange('${previous.p.Value}') }}>assist</button>
        </>
      )
      const wired = (label: string): WorkflowDefinition => ({
        nodes: [at('p', 'Parse'), at('w', 'Widget', { configuration: { Label: label } })],
        edges: [{ id: 'e', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }],
      })

      it('is called under an expression configuration value with the Json outputs of the wired sources, and writes the body back', async () => {
        const user = userEvent.setup()
        const view = show(wired('expr:${previous.p.Ok}'), { renderProjectionControl: control }, 'w', [widget, parse, entry])
        expect(JSON.parse(screen.getByTestId('projection').textContent ?? '')).toEqual({
          placement: 'configuration', field: 'Label', target: 'String',
          json: [{ sourceNodeId: 'p', name: 'Value', type: 'Json?' }], body: '${previous.p.Ok}',
        })

        await user.click(screen.getByRole('button', { name: 'assist' }))
        expect(configOf(view)?.['Label']).toBe('expr:${previous.p.Value}')
      })

      it('is called under a blank field the editor opens in Expression mode from its declared default', () => {
        show({
          nodes: [at('p', 'Parse'), at('w', 'Expr')],
          edges: [{ id: 'e', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }],
        }, { renderProjectionControl: control }, 'w', [expression, parse, entry], { renderExpressionEditor: () => <div>host editor</div> })
        expect(JSON.parse(screen.getByTestId('projection').textContent ?? '')).toMatchObject({ field: 'Expression', body: '' })
      })

      it('lists a Json output a host answers for a wired source, as the mapping dialog does', () => {
        const host: BlockTypeDescriptor = { blockTypeId: 'Host', displayName: 'Host', inputs: [], outputs: [] }
        const answered: NodePortCustomization = {
          useDiscoveredPorts: (asked) => (asked?.id === 'h' ? { outputs: [{ name: 'Answer', displayName: 'Answer', isRequired: false, type: 'Json' }] } : {}),
        }
        renderInEditor(
          <NodePortCustomizationContext.Provider value={answered}>
            <NodeConfigCustomizationContext.Provider value={{ renderProjectionControl: control }}>
              <NodeConfigPanel />
            </NodeConfigCustomizationContext.Provider>
          </NodePortCustomizationContext.Provider>,
          {
            preload: (store) => {
              store.getState().setBlockTypes([widget, parse, host, entry])
              store.getState().loadWorkflow({
                nodes: [at('p', 'Parse'), at('h', 'Host'), at('w', 'Widget', { configuration: { Label: 'expr:1' } })],
                edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }, { id: 'e2', sourceNodeId: 'h', targetNodeId: 'w', mappings: {} }],
              })
              store.getState().setSelectedNode('w')
            },
          },
        )
        expect((JSON.parse(screen.getByTestId('projection').textContent ?? '') as { json: unknown }).json).toEqual([
          { sourceNodeId: 'p', name: 'Value', type: 'Json?' },
          { sourceNodeId: 'h', name: 'Answer', type: 'Json' },
        ])
      })

      it('lists the _error output of a wired source under EmitError among the Json outputs', () => {
        renderInEditor(
          <NodeConfigCustomizationContext.Provider value={{ renderProjectionControl: control }}>
            <NodeConfigPanel />
          </NodeConfigCustomizationContext.Provider>,
          {
            preload: (store) => {
              store.getState().setBlockTypes([widget, parse, entry])
              store.getState().loadWorkflow({
                nodes: [at('p', 'Parse', { errorPolicy: { policy: 'EmitError' } }), at('w', 'Widget', { configuration: { Label: 'expr:1' } })],
                edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }],
              })
              store.getState().setSelectedNode('w')
            },
          },
        )
        expect((JSON.parse(screen.getByTestId('projection').textContent ?? '') as { json: unknown }).json).toEqual([
          { sourceNodeId: 'p', name: 'Value', type: 'Json?' },
          { sourceNodeId: 'p', name: '_error', type: 'Json' },
        ])
      })

      it('is not called for a value that is not an expression, nor where no wired source offers Json', () => {
        show(wired('plain'), { renderProjectionControl: control }, 'w', [widget, parse, entry])
        expect(screen.queryByTestId('projection')).not.toBeInTheDocument()
        show(oneWidget({ configuration: { Label: 'expr:1' } }), { renderProjectionControl: control })
        expect(screen.queryByTestId('projection')).not.toBeInTheDocument()
      })
    })

    it('renders the extra sections the host appends', () => {
      show(oneWidget(), { renderNodeSections: ({ blockType }) => <div>Hints for {blockType.displayName}</div> })
      expect(screen.getByText('Hints for Widget')).toBeInTheDocument()
    })
  })

  describe('resiliency', () => {
    it('saves a timeout, and takes the key off the node when the field is cleared', async () => {
      const user = userEvent.setup()
      const view = show()
      await user.clear(screen.getByLabelText('Timeout (seconds)'))
      expect(configOf(view)?.['timeoutSeconds']).toBeUndefined()
      await user.tab()
      expect(configOf(view)).not.toHaveProperty('timeoutSeconds')
      await user.tripleClick(screen.getByLabelText('Timeout (seconds)'))
      await user.keyboard('45')
      await user.tab()
      expect(configOf(view)?.['timeoutSeconds']).toBe(45)
    })

    it('shows a leaf the deadline the engine will apply as a hint, not as a value', () => {
      show()
      expect(screen.getByLabelText('Timeout (seconds)')).toHaveValue(null)
      expect(screen.getByLabelText('Timeout (seconds)')).toHaveAttribute('placeholder', '30')
    })

    it('leaves the box empty on a container, which the engine gives no deadline of its own', () => {
      show({ nodes: [at('b', 'Branch')], edges: [] }, {}, 'b')
      expect(screen.getByLabelText('Timeout (seconds)')).toHaveValue(null)
      expect(screen.getByLabelText('Timeout (seconds)')).not.toHaveAttribute('placeholder', '30')
    })

    it('clearing the box on a container leaves it without a deadline rather than setting thirty', async () => {
      const user = userEvent.setup()
      const view = show({ nodes: [at('b', 'Branch', { configuration: { timeoutSeconds: 45 } })], edges: [] }, {}, 'b')
      await user.clear(screen.getByLabelText('Timeout (seconds)'))
      await user.tab()
      expect(configOf(view, 'b')).not.toHaveProperty('timeoutSeconds')
    })

    it('shows a container the number it really has, even one the engine will refuse', () => {
      show({ nodes: [at('b', 'Branch', { configuration: { timeoutSeconds: '45' } })], edges: [] }, {}, 'b')
      expect(screen.getByLabelText('Timeout (seconds)')).toHaveValue(45)
    })

    it('does not tell a container it has no deadline, which only the host knows', () => {
      show({ nodes: [at('b', 'Branch')], edges: [] }, {}, 'b')
      expect(screen.getByText('Empty means this block sets no deadline of its own')).toBeInTheDocument()
    })

    it('leaves retry to the block until the author says otherwise', async () => {
      const user = userEvent.setup()
      const view = show()
      expect(screen.getByLabelText('Retry')).toHaveValue('default')
      expect(screen.queryByLabelText('Retry Count')).not.toBeInTheDocument()
      await user.selectOptions(screen.getByLabelText('Retry'), 'on')
      expect(configOf(view)?.['retryEnabled']).toBe(true)
      expect(screen.getByLabelText('Retry Count')).toBeInTheDocument()
    })

    it('takes every retry key off the node when the author goes back to the block default', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget({
        configuration: { retryEnabled: true, retryCount: 5, retryBackoffSeconds: 2 },
      }))
      await user.selectOptions(screen.getByLabelText('Retry'), 'default')
      expect(configOf(view)).not.toHaveProperty('retryEnabled')
      expect(configOf(view)).not.toHaveProperty('retryCount')
      expect(configOf(view)).not.toHaveProperty('retryBackoffSeconds')
    })
  })

  describe('error handling', () => {
    const openErrorHandling = async (user: ReturnType<typeof userEvent.setup>) => {
      await user.click(screen.getByText('Error Handling'))
    }
    const choose = async (user: ReturnType<typeof userEvent.setup>, label: string) => {
      await user.click(screen.getByLabelText('Error Policy'))
      await user.click(within(screen.getByRole('listbox')).getByText(label))
    }
    const policyOf = (view: { editor: () => WorkflowEditorStore }) =>
      view.editor().getState().nodes.find((n) => n.id === 'w')?.errorPolicy

    it('writes the policy onto the node, where the run reads it, and offers the default output only for the policy that uses it', async () => {
      const user = userEvent.setup()
      const view = show()
      await openErrorHandling(user)
      expect(screen.queryByLabelText('Default Output (JSON)')).not.toBeInTheDocument()
      await choose(user, 'Continue With Default Output')
      expect(policyOf(view)).toEqual({ policy: 'ContinueWithDefault' })
      expect(configOf(view)).not.toHaveProperty('errorPolicy')
      expect(screen.getByLabelText('Default Output (JSON)')).toBeInTheDocument()
    })

    it('reads the node\'s own policy back', async () => {
      const user = userEvent.setup()
      show(oneWidget({ errorPolicy: { policy: 'Skip' } }))
      await openErrorHandling(user)
      expect(screen.getByLabelText('Error Policy')).toHaveTextContent('Skip Node & Downstream')
    })

    it('takes the node back to no policy when the author resets, a stale configuration key with it', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget({ errorPolicy: { policy: 'Skip' }, configuration: { Label: 'x', errorPolicy: 'Skip' } }))
      await openErrorHandling(user)
      await user.click(screen.getByRole('button', { name: 'Reset to default' }))
      expect(policyOf(view)).toBeUndefined()
      expect(configOf(view)).toEqual({ Label: 'x' })
      expect(screen.getByLabelText('Error Policy')).toHaveTextContent('Fail Workflow (Default)')
    })

    it('flags unparseable JSON, keeps it in the box, and writes none of it', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget({ errorPolicy: { policy: 'ContinueWithDefault' } }))
      await openErrorHandling(user)
      fireEvent.change(screen.getByLabelText('Default Output (JSON)'), { target: { value: '{"o":1' } })
      expect(screen.getByText('Must be valid JSON')).toBeInTheDocument()
      expect(screen.getByLabelText('Default Output (JSON)')).toHaveValue('{"o":1')
      expect(policyOf(view)).toEqual({ policy: 'ContinueWithDefault' })
    })

    it('keeps the rest of the policy when one part of it changes', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget({ errorPolicy: { policy: 'ContinueWithDefault', defaultOutputJson: '{"o":1}' } }))
      await openErrorHandling(user)
      await choose(user, 'Skip Node & Downstream')
      expect(policyOf(view)).toEqual({ policy: 'Skip', defaultOutputJson: '{"o":1}' })
    })

    const withRoute = (policy: NodeErrorPolicy) => {
      const view = show(oneWidget())
      act(() => { view.editor().getState().updateNodeErrorPolicy('w', { ...policy, routeToErrorPort: true }) })
      return view
    }

    it('offers no route to the error port and drops one a node carries, since no run reads it', async () => {
      const user = userEvent.setup()
      const view = withRoute({ policy: 'Skip' })
      await openErrorHandling(user)
      expect(screen.queryByLabelText('Route to Error Port')).not.toBeInTheDocument()
      await choose(user, 'Emit Error to Virtual Port')
      expect(policyOf(view)).toEqual({ policy: 'EmitError' })
    })

    it('drops a route the node carries when only the default output is edited', async () => {
      const user = userEvent.setup()
      const view = withRoute({ policy: 'ContinueWithDefault' })
      await openErrorHandling(user)
      fireEvent.change(screen.getByLabelText('Default Output (JSON)'), { target: { value: '{"o":1}' } })
      expect(policyOf(view)).toEqual({ policy: 'ContinueWithDefault', defaultOutputJson: '{"o":1}' })
    })

    it('takes a policy an older editor kept in the configuration off it once the author sets the node\'s', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget({ configuration: { Label: 'x', errorPolicy: 'Skip' } }))
      await openErrorHandling(user)
      expect(screen.getByLabelText('Error Policy')).toHaveTextContent('Fail Workflow (Default)')
      await choose(user, 'Skip Node & Downstream')
      expect(policyOf(view)).toEqual({ policy: 'Skip' })
      expect(configOf(view)).toEqual({ Label: 'x' })
    })

    it.each([{ policy: 'FailWorkflow' }, '', 0, false])('clears a configuration key holding %j when the author chooses the policy already shown', async (stale) => {
      const user = userEvent.setup()
      const view = show(oneWidget({ configuration: { Label: 'x', errorPolicy: stale } }))
      await openErrorHandling(user)
      await choose(user, 'Fail Workflow (Default)')
      expect(policyOf(view)).toEqual({ policy: 'FailWorkflow' })
      expect(configOf(view)).toEqual({ Label: 'x' })
    })

    it('takes a choice made with the keyboard', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget({ configuration: { Label: 'x', errorPolicy: '' } }))
      await openErrorHandling(user)
      screen.getByLabelText('Error Policy').focus()
      await user.keyboard('{Enter}')
      await user.keyboard('{ArrowDown}{Enter}')
      expect(policyOf(view)).toEqual({ policy: 'ContinueWithDefault' })
      expect(configOf(view)).toEqual({ Label: 'x' })
    })

    it('keeps the node\'s policy when the author chooses it again', async () => {
      const user = userEvent.setup()
      const view = show(oneWidget({ errorPolicy: { policy: 'Skip' }, configuration: { Label: 'x', errorPolicy: { policy: 'EmitError' } } }))
      await openErrorHandling(user)
      await choose(user, 'Skip Node & Downstream')
      expect(policyOf(view)).toEqual({ policy: 'Skip' })
      expect(configOf(view)).toEqual({ Label: 'x' })
    })
  })

  describe('passthrough', () => {
    const wired = (extra: Partial<WorkflowNode> = {}): WorkflowDefinition => ({
      nodes: [at('p', 'Widget'), at('w', 'Widget', extra)],
      edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }],
    })

    it('lists what the incoming wires carry, with the type each keeps', () => {
      show(wired())
      expect(screen.getByLabelText('Forward Result')).toBeInTheDocument()
      expect(screen.getByTestId('node-config-panel')).toHaveTextContent('String')
    })

    it('forwards a variable and takes it back', async () => {
      const user = userEvent.setup()
      const view = show(wired())
      await user.click(screen.getByLabelText('Forward Result'))
      expect(view.editor().getState().nodes.find((n) => n.id === 'w')?.passthrough).toEqual(['Result'])
      await user.click(screen.getByLabelText('Forward Result'))
      expect(view.editor().getState().nodes.find((n) => n.id === 'w')?.passthrough).toEqual([])
    })

    it('accepts a name no wire supplies, and marks it as hand-written', async () => {
      const user = userEvent.setup()
      const view = show(wired())
      await user.type(screen.getByLabelText('Add variable name'), 'Manual{Enter}')
      expect(view.editor().getState().nodes.find((n) => n.id === 'w')?.passthrough).toEqual(['Manual'])
      expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Custom')
    })

    it('refuses to add the same name twice', async () => {
      const user = userEvent.setup()
      const view = show(wired({ passthrough: ['Manual'] }))
      await user.type(screen.getByLabelText('Add variable name'), 'Manual{Enter}')
      expect(view.editor().getState().nodes.find((n) => n.id === 'w')?.passthrough).toEqual(['Manual'])
    })

    describe('the term of a forwarded name is the server\'s', () => {
      const named = (name: string, effective: string | null) =>
        ({ name, declared: null, effective, state: 'bound' as const, unbound: [] })
      const answer = (view: ReturnType<typeof show>, nodes: { nodeId: string; inputs?: ReturnType<typeof named>[]; outputs?: ReturnType<typeof named>[] }[], offset = 0) => {
        view.drive((store) => {
          store.setState({
            analysis: {
              revision: store.getState().revision + offset, registryGeneration: 1, diagnostics: [],
              nodes: nodes.map((n) => ({ canvasPath: [], typeArguments: {}, inputs: [], outputs: [], ...n })),
            },
          })
        })
      }

      it('shows the term of a ticked name from the node\'s own port, since its feed decides it', () => {
        const view = show({
          nodes: [at('p', 'Widget'), at('w', 'Widget', { passthrough: ['Result'] })],
          edges: [{
            id: 'e1', sourceNodeId: 'p', targetNodeId: 'w',
            mappings: { Result: { type: 'direct', sourceOutputName: 'Result', projection: { path: '$.n', type: 'Int' } } },
          }],
        })
        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('String')

        answer(view, [{ nodeId: 'w', inputs: [named('Result', 'Int')] }])

        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Int')
        expect(screen.getByTestId('node-config-panel')).not.toHaveTextContent('String')
      })

      it('shows the term of a name written by hand, which no wire offers', () => {
        const view = show(wired({ passthrough: ['Manual'] }))
        expect(screen.getByTestId('node-config-panel')).toHaveTextContent(/Custom(?! \()/)

        answer(view, [{ nodeId: 'w', inputs: [named('Manual', 'Number')] }])

        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Custom (Number)')
      })

      it('marks the term of an older answer stale for a ticked name, a hand-written one and an unticked incoming one', () => {
        const view = show({
          nodes: [at('p', 'Widget', { passthrough: ['Carried'] }), at('w', 'Widget', { passthrough: ['Manual', 'Carried'] })],
          edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }],
        })
        answer(view, [{ nodeId: 'w', inputs: [named('Manual', 'Number'), named('Carried', 'Boolean')] }], -1)
        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Custom (Number (stale))')
        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Boolean (stale)')
        answer(view, [{ nodeId: 'w', inputs: [named('Manual', 'Number'), named('Carried', 'Boolean')] }])
        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Custom (Number)')
        expect(screen.getByTestId('node-config-panel')).not.toHaveTextContent('stale')
      })

      it('marks the incoming-variable type stale for a name the source forwards', () => {
        const view = show({
          nodes: [at('p', 'Widget', { passthrough: ['Carried'] }), at('w', 'Widget')],
          edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }],
        })
        answer(view, [{ nodeId: 'p', outputs: [named('Carried', 'Boolean')] }], -1)
        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Boolean (stale)')
      })

      it('lists a name the source forwards with the term the server gave that output of the source', () => {
        const view = show({
          nodes: [at('p', 'Widget', { passthrough: ['Carried'] }), at('w', 'Widget')],
          edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'w', mappings: {} }],
        })
        expect(screen.getByLabelText('Forward Carried')).toBeInTheDocument()
        expect(screen.getByTestId('node-config-panel')).not.toHaveTextContent('Boolean')

        answer(view, [{ nodeId: 'p', outputs: [named('Carried', 'Boolean')] }])

        expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Boolean')
      })
    })
  })

  it('goes inert while a palette block is being dragged over it', () => {
    const view = show()
    expect(screen.getByTestId('node-config-panel')).toHaveStyle({ pointerEvents: 'auto' })
    view.drive((s) => { s.getState().setPaletteDragActive(true) })
    expect(screen.getByTestId('node-config-panel')).toHaveStyle({ pointerEvents: 'none' })
  })
})

describe('NodeConfigPanel — forwarding a variable', () => {
  const source: BlockTypeDescriptor = {
    blockTypeId: 'Source', displayName: 'Source', inputs: [],
    outputs: [{ name: 'Temp', displayName: 'Temp', isRequired: false, type: 'Number' }],
  }
  const forwarder: BlockTypeDescriptor = {
    blockTypeId: 'Forwarder', displayName: 'Forwarder', inputs: [], outputs: [], subCanvasNames: ['Then', 'Else'],
  }

  const wired = (extra: Partial<WorkflowNode> = {}): WorkflowDefinition => ({
    nodes: [at('p', 'Source'), at('f', 'Forwarder', extra)],
    edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'f', mappings: {} }],
  })
  const showForwarder = (definition: WorkflowDefinition) => show(definition, {}, 'f', [source, forwarder, entry])
  const mappingsOf = (view: { editor: () => WorkflowEditorStore }) =>
    view.editor().getState().edges.find((e) => e.id === 'e1')?.mappings

  it('maps a forwarded variable on the wire that offers it, not only on the node', async () => {
    const user = userEvent.setup()
    const view = showForwarder(wired())
    await user.click(screen.getByLabelText('Forward Temp'))
    expect(mappingsOf(view)).toEqual({ Temp: { type: 'direct', sourceOutputName: 'Temp' } })
  })

  it('takes that mapping back off the wire when the variable is un-ticked', async () => {
    const user = userEvent.setup()
    const view = showForwarder({
      nodes: [at('p', 'Source'), at('f', 'Forwarder', { passthrough: ['Temp'] })],
      edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'f', mappings: { Temp: { type: 'direct', sourceOutputName: 'Temp' } } }],
    })
    await user.click(screen.getByLabelText('Forward Temp'))
    expect(mappingsOf(view)).toEqual({})
  })

  it('never overwrites a mapping the operator already made', async () => {
    const user = userEvent.setup()
    const view = showForwarder({
      nodes: [at('p', 'Source'), at('f', 'Forwarder')],
      edges: [{ id: 'e1', sourceNodeId: 'p', targetNodeId: 'f', mappings: { Temp: { type: 'literal', literalValue: 4 } } }],
    })
    await user.click(screen.getByLabelText('Forward Temp'))
    expect(mappingsOf(view)).toEqual({ Temp: { type: 'literal', literalValue: 4 } })
  })

  it('names the branches a forwarded variable also reaches', () => {
    showForwarder(wired())
    expect(screen.getByTestId('node-config-panel')).toHaveTextContent('Each also reaches Then / Else')
  })

  it('offers a value editor for a forwarded name, so it can be given one with no wire feeding it', () => {
    showForwarder(wired({ passthrough: ['Mode'] }))
    expect(screen.getByTestId('port-value-editor-Mode')).toBeInTheDocument()
  })

  it('writes that value onto the node configuration, which is what the sub-canvas is seeded from', async () => {
    const user = userEvent.setup()
    const view = showForwarder(wired({ passthrough: ['Mode'] }))
    await user.type(screen.getByTestId('port-value-input-Mode'), 'eco')
    expect(configOf(view, 'f')?.['Mode']).toBe('eco')
  })

  it('offers no second editor for a name the config schema already owns', () => {
    const owned: BlockTypeDescriptor = {
      ...forwarder,
      configurationSchema: [{ name: 'Threshold', label: 'Threshold', type: 'Number', required: false }],
    }
    show(wired({ passthrough: ['Threshold'] }), {}, 'f', [source, owned, entry])
    expect(screen.getAllByTestId('port-value-editor-Threshold')).toHaveLength(1)
  })

  it('offers no editor for a resilience key, which the engine strips before the merge', () => {
    showForwarder(wired({ passthrough: ['timeoutSeconds'] }))
    expect(screen.queryByTestId('port-value-editor-timeoutSeconds')).not.toBeInTheDocument()
  })

  it('offers no editor for a design-time key, which never reaches the block either', () => {
    showForwarder(wired({ passthrough: ['__note'] }))
    expect(screen.queryByTestId('port-value-editor-__note')).not.toBeInTheDocument()
  })
})

describe('NodeConfigPanel — a Json configuration value', () => {
  const emitter: BlockTypeDescriptor = {
    blockTypeId: 'Emitter', displayName: 'Emitter',
    inputs: [{ name: 'Body', displayName: 'Body', isRequired: false, type: 'Json' }],
    outputs: [{ name: 'Echo', displayName: 'Echo', isRequired: false, type: 'Json' }],
    configurationSchema: [{ name: 'Body', label: 'Body', type: 'Json', required: false }],
  }
  const forwarder: BlockTypeDescriptor = {
    blockTypeId: 'Forwarder', displayName: 'Forwarder', inputs: [], outputs: [],
  }
  const oneEmitter = (): WorkflowDefinition => ({ nodes: [at('e', 'Emitter')], edges: [] })
  const wired = (): WorkflowDefinition => ({
    nodes: [at('e', 'Emitter'), at('f', 'Forwarder', { passthrough: ['Echo'] })],
    edges: [{ id: 'e1', sourceNodeId: 'e', targetNodeId: 'f', mappings: {} }],
  })

  it('writes the object a schema field of type Json is given, not the text it was typed as', () => {
    const view = show(oneEmitter(), {}, 'e', [emitter, entry])

    fireEvent.change(screen.getByTestId('port-value-input-Body'), { target: { value: '{"code": "E1"}' } })

    expect(configOf(view, 'e')?.['Body']).toEqual({ code: 'E1' })
  })

  it('writes it for a forwarded variable too, where the wire is what types the value', () => {
    const view = show(wired(), {}, 'f', [emitter, forwarder, entry])

    fireEvent.change(screen.getByTestId('port-value-input-Echo'), { target: { value: '{"rows": 2}' } })

    expect(configOf(view, 'f')?.['Echo']).toEqual({ rows: 2 })
  })

  it('shows each node its own text, never what was typed into another node of the same type', () => {
    const view = show(
      { nodes: [at('e1', 'Emitter'), at('e2', 'Emitter')], edges: [] },
      {}, 'e1', [emitter, entry],
    )
    fireEvent.change(screen.getByTestId('port-value-input-Body'), { target: { value: '{"node": "e1"}' } })

    view.drive((s) => { s.getState().setSelectedNode('e2') })
    expect(screen.getByTestId('port-value-input-Body')).toHaveValue('')

    fireEvent.change(screen.getByTestId('port-value-input-Body'), { target: { value: '{"node": "e2"}' } })
    expect(configOf(view, 'e2')?.['Body']).toEqual({ node: 'e2' })
    expect(configOf(view, 'e1')?.['Body']).toEqual({ node: 'e1' })
  })
})

describe('NodeConfigPanel — a Map<String,String> value', () => {
  const sender: BlockTypeDescriptor = {
    blockTypeId: 'Sender', displayName: 'Sender',
    inputs: [
      { name: 'Tags', displayName: 'Tags', isRequired: false, type: 'Map<String,String>' },
      { name: 'Weights', displayName: 'Weights', isRequired: false, type: 'Map<String,Int>' },
      { name: 'Headers', displayName: 'Headers', isRequired: false, type: 'Map<String,String>' },
    ],
    outputs: [],
    configurationSchema: [
      { name: 'Tags', label: 'Tags', type: 'String', required: false },
      { name: 'Weights', label: 'Weights', type: 'String', required: false },
    ],
  }
  const oneSender = (extra: Partial<WorkflowNode> = {}): WorkflowDefinition => ({ nodes: [at('s', 'Sender', extra)], edges: [] })

  it('is edited as rows, and the object lands on the node', async () => {
    const user = userEvent.setup()
    const view = show(oneSender(), {}, 's', [sender, entry])

    await user.click(screen.getByRole('button', { name: 'Add Tags entry' }))
    await user.type(screen.getByLabelText('Tags key 1'), 'b')
    await user.type(screen.getByLabelText('Tags value 1'), '1')
    await user.click(screen.getByRole('button', { name: 'Add Tags entry' }))
    await user.type(screen.getByLabelText('Tags key 2'), 'a')

    expect(configOf(view, 's')?.['Tags']).toEqual({ b: '1', a: '' })
  })

  it('opens the JSON text editor for a map of another value, and writes the JSON typed', () => {
    const view = show(oneSender(), {}, 's', [sender, entry])
    expect(screen.queryByRole('button', { name: 'Add Weights entry' })).not.toBeInTheDocument()

    fireEvent.change(screen.getByTestId('port-value-input-Weights'), { target: { value: '{"a": 1}' } })
    expect(configOf(view, 's')?.['Weights']).toEqual({ a: 1 })
  })

  it('draws rows for a map input the block gives no field, and keeps the node free of forwarded names', async () => {
    const user = userEvent.setup()
    const view = show(oneSender(), {}, 's', [sender, entry])

    await user.click(screen.getByRole('button', { name: 'Add Headers entry' }))
    await user.type(screen.getByLabelText('Headers key 1'), 'Accept')
    await user.type(screen.getByLabelText('Headers value 1'), 'text/plain')

    const node = view.editor().getState().nodes.find((n) => n.id === 's')
    expect(node?.configuration['Headers']).toEqual({ Accept: 'text/plain' })
    expect(node?.passthrough ?? []).toEqual([])
  })

  it('marks a header pasted whole on a secret-capable map input and writes nothing for it, where another map accepts it', async () => {
    const user = userEvent.setup()
    const secret: BlockTypeDescriptor = {
      ...sender, blockTypeId: 'SecretSender',
      inputs: sender.inputs.map((i) => (i.name === 'Headers' ? { ...i, secretValues: true } : i)),
    }
    const view = show({ nodes: [at('s', 'SecretSender')], edges: [] }, {}, 's', [secret, entry])

    await user.click(screen.getByRole('button', { name: 'Add Headers entry' }))
    await user.click(screen.getByLabelText('Headers key 1'))
    await user.paste('Authorization: Bearer hunter2')
    await user.click(screen.getByRole('button', { name: 'Add Tags entry' }))
    await user.click(screen.getByLabelText('Tags key 1'))
    await user.paste('Accept: x')

    expect(screen.getByLabelText('Headers key 1')).toBeInvalid()
    expect(screen.getByLabelText('Tags key 1')).toBeValid()
    expect(configOf(view, 's')).not.toHaveProperty('Headers')
    expect(configOf(view, 's')?.['Tags']).toEqual({ 'Accept: x': '' })
  })

  describe('on a secret-capable input', () => {
    const secret: BlockTypeDescriptor = {
      ...sender, blockTypeId: 'SecretSender',
      inputs: sender.inputs.map((i) => (i.name === 'Headers' ? { ...i, secretValues: true } : i)),
    }
    const showWith = (secretCodec: boolean, configuration: WorkflowNode['configuration']) => {
      const holder: { store?: WorkflowEditorStore } = {}
      renderInEditor(<NodeConfigPanel />, {
        portTypes: createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Device', aliases: ['device'], literal: { gradedAs: 'String' } }], [], { secretCodec }),
        preload: (store) => {
          holder.store = store
          store.getState().setBlockTypes([secret, entry])
          store.getState().loadWorkflow({ nodes: [at('s', 'SecretSender', { configuration })], edges: [] })
          store.getState().setSelectedNode('s')
        },
      })
      return () => holder.store?.getState().nodes.find((n) => n.id === 's')?.configuration
    }

    it('offers the hide toggle when the host has a codec, and the entry is sent for the host to protect', async () => {
      const user = userEvent.setup()
      const configuration = showWith(true, { Headers: { 'X-Account': 'acct-1' } })

      await user.click(screen.getByRole('switch', { name: 'Hide value of Headers entry 1' }))

      expect(configuration()?.['Headers']).toEqual({ 'X-Account': 'protect:acct-1' })
    })

    it('offers no hide toggle when the host has no codec, on this map or on another', () => {
      showWith(false, { Headers: { 'X-Account': 'acct-1' }, Tags: { a: 'b' } })

      expect(screen.queryByRole('switch', { name: /^Hide / })).toBeNull()
    })

    it('shows a masked entry as hidden whether or not the host has a codec, and keeps it when another entry changes', async () => {
      const user = userEvent.setup()
      const configuration = showWith(false, { Headers: { Authorization: '***', Accept: 'a' } })

      expect(screen.getByLabelText('Headers value 1')).toHaveAttribute('type', 'password')
      await user.type(screen.getByLabelText('Headers value 2'), 'b')

      expect(configuration()?.['Headers']).toEqual({ Authorization: '***', Accept: 'ab' })
    })
  })

  it('takes the key off the node when the last row of such a map is removed', async () => {
    const user = userEvent.setup()
    const view = show(oneSender({ configuration: { Headers: { Accept: 'text/plain' } } }), {}, 's', [sender, entry])

    await user.click(screen.getByRole('button', { name: 'Remove Headers entry 1' }))

    expect(configOf(view, 's')).not.toHaveProperty('Headers')
  })

  it('marks a required map input as required', () => {
    const required: BlockTypeDescriptor = { ...sender, blockTypeId: 'Strict', inputs: sender.inputs.map((i) => (i.name === 'Headers' ? { ...i, isRequired: true } : i)) }
    show({ nodes: [at('s', 'Strict')], edges: [] }, {}, 's', [required, entry])
    expect(within(screen.getByTestId('port-value-editor-Headers')).getByText('Headers *')).toBeInTheDocument()
  })

  it('gives such a map one editor even when its name is also forwarded', () => {
    show(oneSender({ passthrough: ['Headers'] }), {}, 's', [sender, entry])
    expect(screen.getAllByRole('button', { name: 'Add Headers entry' })).toHaveLength(1)
  })
})
