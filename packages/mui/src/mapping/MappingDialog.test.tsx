import { describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor, PortInfo, WorkflowDefinition, WorkflowEditorStore, WorkflowNode, WorkflowReferenceResolver } from '@reeve-iot/workflow-core'
import {
  BUILTIN_PORT_TYPES, createPortTypeRegistry, readStoredDefinitionText, WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_INPUT_NODE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_NODE_ID, WorkflowReferenceResolverContext,
} from '@reeve-iot/workflow-core'
import { MappingDialog } from './MappingDialog.js'
import { NodePortCustomizationContext } from '../canvas/portCustomization.js'
import type { NodePortCustomization } from '../canvas/portCustomization.js'
import { renderInEditor } from '../testUtils.js'
import { engineReadsMapping, mappingForms } from '../../../core/src/editor/documentGrammar.testkit.js'

const source: BlockTypeDescriptor = {
  blockTypeId: 'Source', displayName: 'Source', inputs: [],
  outputs: [
    { name: 'Result', displayName: 'Result', isRequired: false, type: 'String' },
    { name: 'Count', displayName: 'Count', isRequired: false, type: 'Number' },
  ],
}

const target: BlockTypeDescriptor = {
  blockTypeId: 'Target', displayName: 'Target',
  inputs: [
    { name: 'DeviceId', displayName: 'Device', isRequired: true, type: 'Device' },
    { name: 'Value', displayName: 'Value', isRequired: false, type: 'String' },
    { name: 'Mode', displayName: 'Mode', isRequired: false, type: 'String', allowedValues: ['On', 'Off'] },
  ],
  outputs: [],
}

const inert: BlockTypeDescriptor = { blockTypeId: 'Inert', displayName: 'Inert', inputs: [], outputs: [] }
const referenceSource: BlockTypeDescriptor = {
  blockTypeId: 'ReferenceSource', displayName: 'Reference Source',
  inputs: [{ name: 'WorkflowId', displayName: 'Workflow', isRequired: true, type: 'String' }],
  outputs: [], runsWorkflowFrom: 'WorkflowId',
}
const dynamicReferenceSource: BlockTypeDescriptor = {
  blockTypeId: 'DynamicReferenceSource', displayName: 'Dynamic Reference Source',
  inputs: [], outputs: [], hasDynamicPorts: true, runsWorkflowFrom: 'WorkflowId',
}

const entry: BlockTypeDescriptor = { blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, displayName: 'Input', inputs: [], outputs: [] }
const exit: BlockTypeDescriptor = { blockTypeId: WORKFLOW_OUTPUT_BLOCK_TYPE_ID, displayName: 'Output', inputs: [], outputs: [] }

const at = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })

const pair = (targetBlockTypeId = 'Target', extra: Partial<WorkflowNode> = {}): WorkflowDefinition => ({
  nodes: [at('s', 'Source'), at('t', targetBlockTypeId, extra)],
  edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: {} }],
})

function open(
  definition: WorkflowDefinition,
  custom: NodePortCustomization = {},
  catalogue: BlockTypeDescriptor[] = [source, target, inert, entry, exit],
  params?: (store: WorkflowEditorStore) => void,
  resolver: WorkflowReferenceResolver = () => ({ inputs: [], outputs: [] }),
) {
  const onClose = vi.fn()
  const view = renderInEditor(
    <WorkflowReferenceResolverContext.Provider value={resolver}>
      <NodePortCustomizationContext.Provider value={custom}>
        <MappingDialog edgeId="e1" onClose={onClose} />
      </NodePortCustomizationContext.Provider>
    </WorkflowReferenceResolverContext.Provider>,
    {
      preload: (store) => {
        store.getState().setBlockTypes(catalogue)
        store.getState().loadWorkflow(definition)
        params?.(store)
      },
    },
  )
  return { ...view, onClose }
}

async function choose(user: ReturnType<typeof userEvent.setup>, label: string, option: string) {
  await user.click(screen.getByLabelText(label))
  await user.click(within(screen.getByRole('listbox')).getByText(option))
}

const mappingsOf = (view: ReturnType<typeof open>) =>
  view.editor().getState().edges.find((e) => e.id === 'e1')?.mappings

const rowNames = () =>
  screen.getAllByRole('row').slice(1).map((r) => r.querySelectorAll('td')[0]?.textContent ?? '')

describe('MappingDialog', () => {
  it('offers one row per declared input of the target', () => {
    open(pair())
    expect(rowNames()).toEqual(['Device *(Device)', 'Value (String)', 'Mode (String)'])
  })

  it('offers the source outputs as direct mapping sources', async () => {
    const user = userEvent.setup()
    open(pair())
    await user.click(screen.getByLabelText('Value source'))
    expect(within(screen.getByRole('listbox')).getByText('Result (String)')).toBeInTheDocument()
    expect(within(screen.getByRole('listbox')).getByText('Count (Number)')).toBeInTheDocument()
  })

  it('offers host-resolved source outputs as direct mapping sources', async () => {
    const user = userEvent.setup()
    const resolved: PortInfo = {
      name: 'ChildResult', displayName: 'Child result', isRequired: false, type: 'Boolean',
    }
    const resolver: WorkflowReferenceResolver = (workflowId) => ({
      inputs: [], outputs: workflowId === 'child-1' ? [resolved] : [],
    })
    open({
      nodes: [
        at('s', 'ReferenceSource', { configuration: { WorkflowId: 'child-1' } }),
        at('t', 'Target'),
      ],
      edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: {} }],
    }, {}, [referenceSource, target, inert, entry, exit], undefined, resolver)

    await user.click(screen.getByLabelText('Value source'))
    expect(within(screen.getByRole('listbox')).getByText('Child result (Boolean)')).toBeInTheDocument()
  })

  it('offers dynamic, host-resolved, and passthrough outputs from one source', async () => {
    const user = userEvent.setup()
    const hostOutput: PortInfo = {
      name: 'Resolved', displayName: 'Resolved output', isRequired: false, type: 'Boolean',
    }
    const resolver: WorkflowReferenceResolver = (workflowId) => ({
      inputs: [], outputs: workflowId === 'child-1' ? [hostOutput] : [],
    })
    open({
      nodes: [
        at('s', 'DynamicReferenceSource', {
          configuration: {
            WorkflowId: 'child-1',
            OutputPorts: JSON.stringify([{ name: 'Dynamic', displayName: 'Dynamic output', type: 'String' }]),
          },
          passthrough: ['Forwarded'],
        }),
        at('t', 'Target'),
      ],
      edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: {} }],
    }, {}, [dynamicReferenceSource, target, inert, entry, exit], undefined, resolver)

    await user.click(screen.getByLabelText('Value source'))
    const options = within(screen.getByRole('listbox'))
    expect(options.getByText('Dynamic output (String)')).toBeInTheDocument()
    expect(options.getByText('Resolved output (Boolean)')).toBeInTheDocument()
    expect(options.getByText('Forwarded')).toBeInTheDocument()
  })

  it('saves a direct mapping onto the edge', async () => {
    const user = userEvent.setup()
    const view = open(pair())
    await choose(user, 'Value source', 'Result (String)')
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(mappingsOf(view)).toEqual({ Value: { type: 'direct', sourceOutputName: 'Result' } })
    expect(view.onClose).toHaveBeenCalled()
  })

  it('deletes the mapping when the source is set back to None', async () => {
    const user = userEvent.setup()
    const view = open({ ...pair(), edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: { Value: { type: 'direct', sourceOutputName: 'Result' } } }] })
    await choose(user, 'Value source', 'None')
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(mappingsOf(view)).toEqual({})
  })

  it('leaves no trace of the old source when None is followed by a literal', async () => {
    const user = userEvent.setup()
    const view = open({ ...pair(), edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: { Value: { type: 'direct', sourceOutputName: 'Result' } } }] })
    await choose(user, 'Value source', 'None')
    await choose(user, 'Value mapping type', 'Literal')
    await user.type(screen.getByLabelText('Value literal'), 'x')
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(mappingsOf(view)).toEqual({ Value: { type: 'literal', literalValue: 'x' } })
  })

  it('drops a direct mapping that never picked a source, however it got there', async () => {
    const user = userEvent.setup()
    const view = open(pair())
    await choose(user, 'Value mapping type', 'Literal')
    await user.type(screen.getByLabelText('Value literal'), 'hello')
    await choose(user, 'Value mapping type', 'Direct')
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(mappingsOf(view)).toEqual({})
  })

  it('edits a literal through a free-text box, and through a dropdown where the port constrains it', async () => {
    const user = userEvent.setup()
    const view = open(pair())
    await choose(user, 'Value mapping type', 'Literal')
    await user.type(screen.getByLabelText('Value literal'), 'hi')
    await choose(user, 'Mode mapping type', 'Literal')
    await choose(user, 'Mode literal', 'On')
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(mappingsOf(view)).toEqual({
      Value: { type: 'literal', literalValue: 'hi' },
      Mode: { type: 'literal', literalValue: 'On' },
    })
  })

  it('leaves the edge alone when cancelled', async () => {
    const user = userEvent.setup()
    const view = open(pair())
    await choose(user, 'Value source', 'Result (String)')
    await user.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(mappingsOf(view)).toEqual({})
    expect(view.onClose).toHaveBeenCalled()
  })

  it('removes the connection outright', async () => {
    const user = userEvent.setup()
    const view = open(pair())
    await user.click(screen.getByRole('button', { name: 'Remove Connection' }))
    expect(view.editor().getState().edges).toEqual([])
    expect(view.onClose).toHaveBeenCalled()
  })

  it('says the connection only orders execution when the target takes nothing', () => {
    open(pair('Inert'))
    expect(screen.getByText(/execution ordering only/)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('hides an input the host fills elsewhere, so no wire is offered for it', () => {
    open(pair(), { isInputPortHidden: (_n, name) => name === 'DeviceId' })
    expect(rowNames()).toEqual(['Value (String)', 'Mode (String)'])
  })

  it('shows the type the host derives for an input rather than the declared one', () => {
    open(pair(), { overrideInputPortType: (_n, port) => (port.name === 'Value' ? 'Number' : null) })
    expect(rowNames()).toEqual(['Device *(Device)', 'Value (Number)', 'Mode (String)'])
  })

  it('adds the inputs the host discovers for the target node', () => {
    const discovered: PortInfo[] = [{ name: 'Extra', displayName: 'Extra', isRequired: true, type: 'String' }]
    open(pair(), { useDiscoveredPorts: (node) => (node?.id === 't' ? { inputs: discovered } : {}) })
    expect(rowNames()).toEqual(['Device *(Device)', 'Value (String)', 'Mode (String)', 'Extra *(String)'])
  })

  it('reads the dynamic ports out of the node configuration when the block type has them', () => {
    const dynamic: BlockTypeDescriptor = { blockTypeId: 'Dyn', displayName: 'Dyn', inputs: [], outputs: [], hasDynamicPorts: true }
    open(
      { nodes: [at('s', 'Source'), at('t', 'Dyn', { configuration: { InputPorts: JSON.stringify([{ name: 'Alpha', type: 'String' }, { name: 'Beta', displayName: 'Beta', type: 'Number' }]) } })],
        edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: {} }] },
      {}, [source, dynamic],
    )
    expect(rowNames()).toEqual(['Alpha (String)', 'Beta (Number)'])
  })

  it('carries forwarded variables through as mappable ports on both ends', async () => {
    const user = userEvent.setup()
    open(pair('Target', { passthrough: ['Carried'] }))
    expect(rowNames().map((name) => name.trim())).toContain('Carried')
    await user.click(screen.getByLabelText('Carried source'))
    expect(within(screen.getByRole('listbox')).getByText('Result (String)')).toBeInTheDocument()
  })

  describe('a forwarded name takes the server\'s term', () => {
    const named = (name: string, effective: string | null, state: 'bound' | 'refused' = 'bound') =>
      ({ name, declared: null, effective, state, unbound: [] })
    const answer = (view: ReturnType<typeof open>, nodes: { nodeId: string; inputs?: ReturnType<typeof named>[]; outputs?: ReturnType<typeof named>[] }[], offset = 0) => {
      view.drive((store) => {
        store.setState({
          analysis: {
            revision: store.getState().revision + offset, registryGeneration: 1, diagnostics: [],
            nodes: nodes.map((n) => ({ canvasPath: [], typeArguments: {}, inputs: [], outputs: [], ...n })),
          },
        })
      })
    }

    it('names no type for a forwarded target name before an answer', () => {
      open(pair('Target', { passthrough: ['Carried'] }))
      expect(screen.queryByTestId('mapping-term-Carried')).not.toBeInTheDocument()
    })

    it('shows the term the compiler gave a forwarded target name fed by a projection wire, not Json', () => {
      const projected: WorkflowDefinition = {
        nodes: [at('s', 'Source'), at('t', 'Target', { passthrough: ['Carried'] })],
        edges: [{
          id: 'e1', sourceNodeId: 's', targetNodeId: 't',
          mappings: { Carried: { type: 'direct', sourceOutputName: 'Result', projection: { path: '$.n', type: 'Int' } } },
        }],
      }
      const view = open(projected)
      answer(view, [{ nodeId: 't', inputs: [named('Carried', 'Int')] }])

      expect(screen.getByTestId('mapping-term-Carried')).toHaveTextContent('(Int)')
      expect(screen.getByTestId('mapping-term-Carried')).toHaveAttribute('data-analysis', 'current')
      expect(screen.getByTestId('mapping-term-Carried')).toHaveAttribute('data-term-knowledge', 'bound')
      expect(screen.getByTestId('mapping-term-Carried')).not.toHaveTextContent('Json')
    })

    it('marks the term of an older answer stale, and a refused name as refused', () => {
      const view = open(pair('Target', { passthrough: ['Carried'] }))
      answer(view, [{ nodeId: 't', inputs: [named('Carried', 'String')] }], -1)
      expect(screen.getByTestId('mapping-term-Carried')).toHaveAttribute('data-analysis', 'stale')

      answer(view, [{ nodeId: 't', inputs: [named('Carried', null, 'refused')] }])
      expect(screen.getByTestId('mapping-term-Carried')).toHaveAttribute('data-term-knowledge', 'refused')
      expect(screen.getByTestId('mapping-term-Carried')).toHaveTextContent('(refused)')
    })

    it('lists a name the source forwards with the term the server gave that output', async () => {
      const user = userEvent.setup()
      const view = open({
        nodes: [at('s', 'Source', { passthrough: ['Forwarded'] }), at('t', 'Target')],
        edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: {} }],
      })
      answer(view, [{ nodeId: 's', outputs: [named('Forwarded', 'Boolean')] }])

      await user.click(screen.getByLabelText('Value source'))
      expect(within(screen.getByRole('listbox')).getByText('Forwarded (Boolean)')).toBeInTheDocument()
    })

    it('marks a stale term in the source menu', async () => {
      const user = userEvent.setup()
      const view = open({
        nodes: [at('s', 'Source', { passthrough: ['Forwarded'] }), at('t', 'Target')],
        edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: {} }],
      })
      answer(view, [{ nodeId: 's', outputs: [named('Forwarded', 'Boolean')] }], -1)

      await user.click(screen.getByLabelText('Value source'))
      expect(within(screen.getByRole('listbox')).getByText('Forwarded (Boolean (stale))')).toBeInTheDocument()
    })

    it('leaves the literal control of a forwarded name alone: its term follows the literal, so it cannot choose the control', async () => {
      const user = userEvent.setup()
      const view = open(pair('Target', { passthrough: ['Carried'] }))
      answer(view, [{ nodeId: 't', inputs: [named('Carried', 'Int')] }])

      await choose(user, 'Carried mapping type', 'Literal')

      expect(screen.getByLabelText('Carried literal')).toHaveAttribute('placeholder', 'Literal value')
      expect(screen.queryByTestId('literal-value-Carried')).not.toBeInTheDocument()
    })
  })

  it('offers the workflow parameters when the wire starts at the entry node', async () => {
    const user = userEvent.setup()
    open({
      nodes: [at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID), at('t', 'Target')],
      edges: [{ id: 'e1', sourceNodeId: WORKFLOW_INPUT_NODE_ID, targetNodeId: 't', mappings: {} }],
    }, {}, undefined, (s) => { s.getState().setWorkflowParams([{ name: 'Trigger', displayName: 'Trigger', type: 'String', isRequired: true }], []) })
    await user.click(screen.getByLabelText('Value source'))
    expect(within(screen.getByRole('listbox')).getByText('Trigger (String)')).toBeInTheDocument()
  })

  it('offers the workflow result parameters when the wire ends at the exit node', () => {
    open({
      nodes: [at('s', 'Source'), at(WORKFLOW_OUTPUT_NODE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID)],
      edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: WORKFLOW_OUTPUT_NODE_ID, mappings: {} }],
    }, {}, undefined, (s) => { s.getState().setWorkflowParams([], [{ name: 'Answer', displayName: 'Answer', type: 'Number', isRequired: true }]) })
    expect(rowNames()).toEqual(['Answer *(Number)'])
  })
})

describe('MappingDialog inside a sub-canvas', () => {
  const branch: BlockTypeDescriptor = {
    blockTypeId: 'Branch', displayName: 'Branch', inputs: [], outputs: [],
    subCanvasNames: ['Body'],
    subCanvasInputs: { Body: [{ name: 'Item', displayName: 'Item', isRequired: true, type: 'String' }] },
    subCanvasOutputs: { Body: [{ name: 'Kept', displayName: 'Kept', isRequired: false, type: 'Boolean' }] },
  }

  function openInside(parent: WorkflowNode, edge: { sourceNodeId: string; targetNodeId: string }) {
    const inside = {
      nodes: [
        at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID), at('s', 'Source'), at('t', 'Target'),
        at(WORKFLOW_OUTPUT_NODE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID),
      ],
      edges: [{ id: 'e1', ...edge, mappings: {} }],
    }
    return renderInEditor(<MappingDialog edgeId="e1" onClose={vi.fn()} />, {
      preload: (store) => {
        store.getState().setBlockTypes([source, target, branch, entry, exit])
        store.getState().loadWorkflow({ nodes: [{ ...parent, subCanvases: { Body: inside } }], edges: [] })
        store.getState().enterSubCanvas('p', 'Body')
      },
    })
  }

  it('offers the sub-canvas declared inputs as sources at the branch entry', async () => {
    const user = userEvent.setup()
    openInside(at('p', 'Branch'), { sourceNodeId: WORKFLOW_INPUT_NODE_ID, targetNodeId: 't' })
    await user.click(screen.getByLabelText('Value source'))
    expect(within(screen.getByRole('listbox')).getByText('Item (String)')).toBeInTheDocument()
  })

  it('offers the sub-canvas declared outputs as targets at the branch exit', () => {
    openInside(at('p', 'Branch'), { sourceNodeId: 's', targetNodeId: WORKFLOW_OUTPUT_NODE_ID })
    expect(rowNames()).toEqual(['Kept (Boolean)'])
  })

  it('collapses two declared output ports that read the same branch key into one row', () => {
    openInside(
      at('p', 'Branch', {
        outputPorts: [
          { name: 'A', displayName: 'A', type: 'String', sources: { Body: 'Kept2' } },
          { name: 'B', displayName: 'B', type: 'Number', sources: { Body: 'Kept2' } },
        ],
      }),
      { sourceNodeId: 's', targetNodeId: WORKFLOW_OUTPUT_NODE_ID },
    )
    expect(rowNames()).toEqual(['Kept (Boolean)', 'Kept2 (String)'])
  })
})

describe('MappingDialog on a mapping in a form the editor does not write', () => {
  const stored = (mappings: Record<string, unknown>): WorkflowDefinition =>
    ({ ...pair(), edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: mappings as never }] })
  const readable = (mappings: Record<string, unknown>) => readStoredDefinitionText(JSON.stringify(stored(mappings))).ok

  it('refuses a wire from sourceOutput with no type, and a type in another case', () => {
    expect(readable({ DeviceId: { sourceOutput: 'Result' } })).toBe(false)
    expect(readable({ Value: { type: 'LITERAL', literalValue: 'x' } })).toBe(false)
    expect(readable({ DeviceId: { type: 'direct', sourceOutputName: 'Result' }, Value: { type: 'literal', literalValue: 'x' } })).toBe(true)
  })

  it('shows the stored forms as a run reads them, and a save without a change keeps them', async () => {
    const user = userEvent.setup()
    const mappings = { DeviceId: { type: 'direct', sourceOutputName: 'Result' }, Value: { type: 'literal', literalValue: 'x' } }
    const view = open(stored(mappings))

    expect(screen.getByLabelText('Device mapping type').textContent).toBe('Direct')
    expect(screen.getByLabelText('Device source').textContent).toContain('Result')
    expect(screen.getByLabelText('Value mapping type').textContent).toBe('Literal')
    expect((screen.getByLabelText('Value literal') as HTMLTextAreaElement).value).toBe('x')
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))

    expect(mappingsOf(view)).toEqual(mappings)
  })

  const GENERATED = mappingForms('Result', false)
  const ALL_FORMS = GENERATED.filter((form) => readable({ In: form }))
  const CHUNK_SIZE = 15
  const FORM_CHUNKS: { name: string; form: Record<string, unknown> }[][] = []
  for (let i = 0; i < ALL_FORMS.length; i += CHUNK_SIZE) {
    FORM_CHUNKS.push(ALL_FORMS.slice(i, i + CHUNK_SIZE).map((form, j) => ({ name: `F${String(i + j)}`, form })))
  }

  it('refuses every generated form that is not the stored spelling', () => {
    for (const form of GENERATED) {
      const stored = !('sourceOutput' in form) && (form['type'] === 'direct' || form['type'] === 'literal')
      if (!stored) expect(readable({ In: form }), JSON.stringify(form)).toBe(false)
    }
    expect(ALL_FORMS.length).toBeGreaterThan(0)
  })

  it('the chunks cover every readable generated form exactly once', () => {
    expect(FORM_CHUNKS.flat().map((c) => c.name)).toEqual(ALL_FORMS.map((_, i) => `F${String(i)}`))
  })

  it.each(FORM_CHUNKS.map((chunk, i) => [i, chunk] as const))(
    'shows every form in chunk %i as a run reads it, and a save without a change keeps each mapping a run reads',
    async (_i, chunk) => {
      const errSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
      const user = userEvent.setup()
      const grid: BlockTypeDescriptor = {
        blockTypeId: 'Grid', displayName: 'Grid', outputs: [],
        inputs: chunk.map(({ name }) => ({ name, displayName: name, isRequired: false, type: 'String' })),
      }
      const mappings = Object.fromEntries(chunk.map(({ name, form }) => [name, form]))
      const view = open({ ...stored(mappings), nodes: [at('s', 'Source'), at('t', 'Grid')] }, {}, [source, grid])

      chunk.forEach(({ name, form }) => {
        const read = engineReadsMapping(form)
        expect(screen.getByLabelText(`${name} mapping type`).textContent, name).toBe(read?.literal ? 'Literal' : 'Direct')
      })
      await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
      const saved = mappingsOf(view) ?? {}
      chunk.forEach(({ name, form }) => {
        const read = engineReadsMapping(form)
        if (read === null) return
        expect(saved[name], name).toEqual(read.literal ? { type: 'literal', literalValue: read.value } : { type: 'direct', sourceOutputName: read.source })
      })
      errSpy.mockRestore()
    },
  )
})

describe('MappingDialog — a literal whose target port declares its type', () => {
  const typed: BlockTypeDescriptor = {
    blockTypeId: 'Typed', displayName: 'Typed', outputs: [],
    inputs: [
      { name: 'Count', displayName: 'Count', isRequired: false, type: 'Number' },
      { name: 'Limit', displayName: 'Limit', isRequired: false, type: 'Int' },
      { name: 'Flag', displayName: 'Flag', isRequired: false, type: 'Boolean' },
      { name: 'Bag', displayName: 'Bag', isRequired: false, type: 'Collection<String>' },
      { name: 'Blob', displayName: 'Blob', isRequired: false, type: 'Json' },
      { name: 'Operand', displayName: 'Operand', isRequired: false, type: '$T' },
      { name: 'Label', displayName: 'Label', isRequired: false, type: 'String' },
    ],
  }

  const typedPair = (mappings: Record<string, unknown> = {}): WorkflowDefinition => ({
    nodes: [at('s', 'Source'), at('t', 'Typed', { passthrough: ['Anything'] })],
    edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: mappings as never }],
  })

  const openTyped = (mappings: Record<string, unknown> = {}) => open(typedPair(mappings), {}, [source, typed])

  const put = (label: string, text: string) => {
    fireEvent.change(screen.getByLabelText(label), { target: { value: text } })
  }
  const saveButton = () => screen.getByRole('button', { name: 'Save Mappings' })
  const chooseLiteral = async (user: ReturnType<typeof userEvent.setup>, name: string) => {
    await choose(user, `${name} mapping type`, 'Literal')
  }

  it('writes a number for a Number target and for an Int one', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Count')
    await chooseLiteral(user, 'Limit')
    put('Count literal', '7')
    put('Limit literal', '42')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({
      Count: { type: 'literal', literalValue: 7 },
      Limit: { type: 'literal', literalValue: 42 },
    })
  })

  it('is a switch for a Boolean target, and stores the boolean it shows', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Flag')

    await user.click(screen.getByRole('switch', { name: 'Flag literal' }))
    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Flag: { type: 'literal', literalValue: true } })
  })

  it('keeps the text field for a port with no known type, so a number stays the text it was typed as', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Anything')
    put('Anything literal', '7')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Anything: { type: 'literal', literalValue: '7' } })
  })

  const bindOperand = (view: ReturnType<typeof openTyped>, effective: string | null) => {
    view.drive((s) => {
      s.setState({
        analysis: {
          revision: s.getState().revision, registryGeneration: 1, diagnostics: [],
          nodes: [{
            canvasPath: [], nodeId: 't', typeArguments: {}, outputs: [],
            inputs: [{
              name: 'Operand', declared: '$T', effective, state: effective === null ? 'unbound' : 'bound', unbound: effective === null ? ['T'] : [],
            }],
          }],
        },
      })
    })
  }

  it('writes text as a string while the parameter of a type-parameter target is unbound', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    bindOperand(view, null)
    await chooseLiteral(user, 'Operand')
    put('Operand literal', '2')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Operand: { type: 'literal', literalValue: '2' } })
  })

  it('says at the cell that the type of a type-parameter target is not chosen yet', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    bindOperand(view, null)
    await chooseLiteral(user, 'Operand')
    expect(screen.getByText(/not chosen yet/)).toBeInTheDocument()
  })

  it('shows text stored before the parameter was bound as invalid, not as a typed value, until it is replaced', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Operand: { type: 'literal', literalValue: 'true' }, Flag: { type: 'literal', literalValue: 'yes' } })
    bindOperand(view, 'Boolean')

    expect(screen.getByTestId('literal-refused-Operand')).toHaveTextContent('The stored value "true" cannot be read as Boolean.')
    expect(screen.getByTestId('literal-refused-Flag')).toHaveTextContent('"yes"')
    expect(screen.queryByRole('switch', { name: 'Operand literal' })).not.toBeInTheDocument()

    await user.click(within(screen.getByTestId('literal-refused-Operand')).getByRole('button', { name: 'Replace' }))
    expect(screen.getByRole('switch', { name: 'Operand literal' })).not.toBeChecked()
    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Operand: { type: 'literal', literalValue: false }, Flag: { type: 'literal', literalValue: 'yes' } })
  })

  it('follows the node\'s own type argument over an older answer that bound the input otherwise', () => {
    const view = open(
      { nodes: [at('s', 'Source'), at('t', 'Typed', { typeArguments: { T: 'Boolean' } })], edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: { Operand: { type: 'literal', literalValue: true } } }] },
      {}, [source, typed],
    )
    bindOperand(view, 'String')
    expect(screen.queryByTestId('literal-refused-Operand')).not.toBeInTheDocument()
    expect(screen.getByRole('switch', { name: 'Operand literal' })).toBeChecked()
  })

  it('keeps the JSON field for a list the dialog itself wrote, whatever its elements', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Bag')
    put('Bag literal', '[1]')
    expect(screen.queryByTestId('literal-refused-Bag')).not.toBeInTheDocument()
    await user.click(saveButton())
    expect(mappingsOf(view)?.['Bag']).toEqual({ type: 'literal', literalValue: [1] })
  })

  it('writes a number in the effective term the analysis gives a type-parameter target', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    bindOperand(view, 'Int')
    await chooseLiteral(user, 'Operand')
    put('Operand literal', '2')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Operand: { type: 'literal', literalValue: 2 } })
  })

  it('does not write text that is no literal of the effective term, and says so at the cell', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    bindOperand(view, 'Int')
    await chooseLiteral(user, 'Operand')
    put('Operand literal', '2.5')

    expect(screen.getByTestId('literal-error-Operand')).toHaveAttribute('data-rule-id', 'literal-port-value-invalid')
    await user.click(saveButton())
    expect(mappingsOf(view)?.['Operand']).not.toHaveProperty('literalValue')
  })

  it('writes the list a Collection target is given, parsed', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Bag')
    put('Bag literal', '["a", "b"]')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Bag: { type: 'literal', literalValue: ['a', 'b'] } })
  })

  it('writes the object a Json target is given, parsed', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Blob')
    put('Blob literal', '{"retries": 3}')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Blob: { type: 'literal', literalValue: { retries: 3 } } })
  })


  it('holds the text an operator is part way through typing, and never drops the mapping for it', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Count: { type: 'literal', literalValue: 7 } })

    put('Count literal', '-')
    expect(screen.getByLabelText('Count literal')).toHaveValue('-')
    put('Count literal', '')
    expect(screen.getByLabelText('Count literal')).toHaveValue('')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Count: { type: 'literal', literalValue: 7 } })
  })

  it('writes the number `1.` parses to, and keeps the text as it was typed', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Count')

    put('Count literal', '1.')
    expect(screen.getByLabelText('Count literal')).toHaveValue('1.')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Count: { type: 'literal', literalValue: 1 } })
  })

  it('writes the number a negative text parses to', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Count')
    put('Count literal', '-5')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Count: { type: 'literal', literalValue: -5 } })
  })

  it('writes nothing for a non-integer an Int target is given, and keeps the value it had', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Limit: { type: 'literal', literalValue: 7 } })
    put('Limit literal', '7.5')
    expect(screen.getByLabelText('Limit literal')).toHaveValue('7.5')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Limit: { type: 'literal', literalValue: 7 } })
  })

  it('writes the whole number an Int target is given in exponent form, which parses exactly', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Limit')
    put('Limit literal', '1e3')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Limit: { type: 'literal', literalValue: 1000 } })
  })

  it('keeps the mapping a JSON cell already holds when its text is emptied', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Bag: { type: 'literal', literalValue: ['a'] } })
    put('Bag literal', '')
    expect(saveButton()).toBeEnabled()

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Bag: { type: 'literal', literalValue: ['a'] } })
  })

  it('shows a stored list as JSON, and saves it back as the list it was', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Bag: { type: 'literal', literalValue: ['a', 'b'] } })

    expect(screen.getByLabelText('Bag literal')).toHaveValue('[\n  "a",\n  "b"\n]')
    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Bag: { type: 'literal', literalValue: ['a', 'b'] } })
  })

  it.each([
    ['Count', { Count: { type: 'literal', literalValue: 'expr:input.a * 2' } }, 'input.a * 2'],
    ['Flag', { Flag: { type: 'literal', literalValue: 'expr:isReady' } }, 'isReady'],
    ['Blob', { Blob: { type: 'literal', literalValue: 'expr:payload()' } }, 'payload()'],
  ] as const)('opens a stored expression on a %s target in Expression mode, and an unrelated edit leaves it', async (name, stored, body) => {
    const user = userEvent.setup()
    const view = openTyped(stored)

    expect(screen.getByLabelText(`${name} expression`)).toHaveValue(body)
    await chooseLiteral(user, 'Limit')
    put('Limit literal', '42')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ ...stored, Limit: { type: 'literal', literalValue: 42 } })
  })

  it('starts an Expression cell from the number the cell holds', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Count')
    put('Count literal', '7')

    await user.click(screen.getByTestId('literal-expression-Count'))
    expect(screen.getByLabelText('Count expression')).toHaveValue('7')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Count: { type: 'literal', literalValue: 'expr:7' } })
  })

  it('starts the value cell empty when Expression is switched back, converting nothing', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Count: { type: 'literal', literalValue: 'expr:a + b' } })

    await user.click(screen.getByTestId('literal-value-Count'))
    expect(screen.getByLabelText('Count literal')).toHaveValue('')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Count: { type: 'literal', literalValue: 'expr:a + b' } })
  })

  it('writes an expression on a String target', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await chooseLiteral(user, 'Label')
    put('Label literal', 'hello')

    await user.click(screen.getByTestId('literal-expression-Label'))
    expect(screen.getByLabelText('Label expression')).toHaveValue('')
    put('Label expression', 'jsonPathText(payload, "$.name")')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Label: { type: 'literal', literalValue: 'expr:jsonPathText(payload, "$.name")' } })
  })

  it('opens a stored expression on a String target in Expression mode', () => {
    openTyped({ Label: { type: 'literal', literalValue: 'expr:jsonPathText(payload, "$.name")' } })
    expect(screen.getByLabelText('Label expression')).toHaveValue('jsonPathText(payload, "$.name")')
    expect(screen.queryByLabelText('Label literal')).toBeNull()
  })

  it('keeps a stored expression on a String target through Value and back', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Label: { type: 'literal', literalValue: 'expr:a' } })
    await user.click(screen.getByTestId('literal-value-Label'))
    expect(screen.getByLabelText('Label literal')).toHaveValue('')
    await user.click(screen.getByTestId('literal-expression-Label'))
    expect(screen.getByLabelText('Label expression')).toHaveValue('a')
    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Label: { type: 'literal', literalValue: 'expr:a' } })
  })

  it('writes no expression for a JSON object, which is no expression body', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Blob: { type: 'literal', literalValue: { retries: 3 } } })

    await user.click(screen.getByTestId('literal-expression-Blob'))
    expect(screen.getByLabelText('Blob expression')).toHaveValue('')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Blob: { type: 'literal', literalValue: { retries: 3 } } })
  })
})

describe('MappingDialog — a literal list', () => {
  const typed: BlockTypeDescriptor = {
    blockTypeId: 'Typed', displayName: 'Typed', outputs: [],
    inputs: [
      { name: 'Bag', displayName: 'Bag', isRequired: false, type: 'Collection<String>' },
      { name: 'Open', displayName: 'Open', isRequired: false, type: 'Collection<$T?>' },
      { name: 'Blob', displayName: 'Blob', isRequired: false, type: 'Json' },
      { name: 'Tags', displayName: 'Tags', isRequired: false, type: 'Map<String,String>' },
    ],
  }
  const openTyped = (mappings: Record<string, unknown> = {}) =>
    open(
      { nodes: [at('s', 'Source'), at('t', 'Typed')], edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: mappings as never }] },
      {}, [source, typed],
    )
  const put = (label: string, text: string) => {
    fireEvent.change(screen.getByLabelText(label), { target: { value: text } })
  }
  const saveButton = () => screen.getByRole('button', { name: 'Save Mappings' })

  it('refuses to save a literal that repeats a member name, naming the member and where', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await choose(user, 'Blob mapping type', 'Literal')
    put('Blob literal', '{"outer": {"a": 1, "a": 2}}')

    expect(screen.getByText("Ambiguous JSON: the member name 'a' occurs more than once in the object at $.outer")).toBeInTheDocument()
    expect(saveButton()).toBeDisabled()
    expect(mappingsOf(view)).toEqual({})
  })

  it('refuses to save text that does not parse, and holds the text in the field', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await choose(user, 'Bag mapping type', 'Literal')
    put('Bag literal', '["a",')

    expect(screen.getByText('Must be valid JSON')).toBeInTheDocument()
    expect(screen.getByLabelText('Bag literal')).toHaveValue('["a",')
    expect(saveButton()).toBeDisabled()
    fireEvent.click(saveButton())
    expect(mappingsOf(view)).toEqual({})
  })

  it('refuses a map literal that writes a member twice, which a parse would resolve by dropping one', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await choose(user, 'Tags mapping type', 'Literal')
    put('Tags literal', '{"a":"1","a":"2"}')

    expect(screen.getByText("Ambiguous JSON: the member name 'a' occurs more than once in the object at $")).toBeInTheDocument()
    expect(saveButton()).toBeDisabled()
    expect(mappingsOf(view)).toEqual({})

    put('Tags literal', '{"a":"1","b":"2"}')
    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Tags: { type: 'literal', literalValue: { a: '1', b: '2' } } })
  })

  it.each([
    ['an empty list', '[]', []],
    ['a list of mixed types', '["a", 1]', ['a', 1]],
    ['a list of lists of mixed types', '[[1, "a"]]', [[1, 'a']]],
    ['a list holding an empty list', '[[1], []]', [[1], []]],
    ['a list of lists of one type', '[[1, 2], [3]]', [[1, 2], [3]]],
  ])('writes %s into a collection port, which the compiler types', async (_, text, expected) => {
    const user = userEvent.setup()
    const view = openTyped()
    await choose(user, 'Bag mapping type', 'Literal')
    put('Bag literal', text)

    expect(screen.queryByText(/A list needs at least one value/)).not.toBeInTheDocument()
    expect(saveButton()).toBeEnabled()
    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Bag: { type: 'literal', literalValue: expected } })
  })

  it('writes a list into a port whose term names a type parameter', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await choose(user, 'Open mapping type', 'Literal')
    put('Open literal', '[1, "a"]')

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Open: { type: 'literal', literalValue: [1, 'a'] } })
  })

  it('refuses a null literal, which is a spelling the stored-document grammar refuses', async () => {
    const user = userEvent.setup()
    const view = openTyped()
    await choose(user, 'Blob mapping type', 'Literal')
    put('Blob literal', 'null')

    expect(screen.getByText(/is not a literal value/)).toBeInTheDocument()
    expect(saveButton()).toBeDisabled()
    fireEvent.click(saveButton())
    expect(mappingsOf(view)).toEqual({})
  })

  it('writes a stored empty list back unchanged, also after its row is switched to Expression', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Bag: { type: 'literal', literalValue: [] } })
    expect(saveButton()).toBeEnabled()

    await user.click(screen.getByTestId('literal-expression-Bag'))
    expect(saveButton()).toBeEnabled()
    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Bag: { type: 'literal', literalValue: [] } })
  })

  it.each([
    ['an empty list', '[]', []],
    ['a mixed list', '[1, "a", true]', [1, 'a', true]],
    ['a string', '"text"', 'text'],
    ['a number', '12', 12],
    ['a boolean', 'false', false],
  ])('writes %s to a Json target', async (_, text, expected) => {
    const user = userEvent.setup()
    const view = openTyped()
    await choose(user, 'Blob mapping type', 'Literal')
    put('Blob literal', text)
    expect(saveButton()).toBeEnabled()

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Blob: { type: 'literal', literalValue: expected } })
  })

  it('writes back a stored list a Json target holds', async () => {
    const user = userEvent.setup()
    const view = openTyped({ Blob: { type: 'literal', literalValue: [1, 'a'] } })
    expect(saveButton()).toBeEnabled()

    await user.click(saveButton())
    expect(mappingsOf(view)).toEqual({ Blob: { type: 'literal', literalValue: [1, 'a'] } })
  })
})

describe('MappingDialog — a literal whose target type declares its form', () => {
  const hosted: BlockTypeDescriptor = {
    blockTypeId: 'Hosted', displayName: 'Hosted', outputs: [],
    inputs: [
      { name: 'Tally', displayName: 'Tally', isRequired: false, type: 'Tally' },
      { name: 'Switch', displayName: 'Switch', isRequired: false, type: 'Flag' },
      { name: 'Stamp', displayName: 'Stamp', isRequired: false, type: 'Stamp' },
      { name: 'Scene', displayName: 'Scene', isRequired: false, type: 'Scene' },
    ],
  }
  const portTypes = createPortTypeRegistry([
    ...BUILTIN_PORT_TYPES,
    { name: 'Tally', literal: { gradedAs: 'Int' } },
    { name: 'Flag', literal: { gradedAs: 'Boolean' } },
    { name: 'Stamp', literal: { gradedAs: 'String', format: 'DateTime' } },
    { name: 'Scene' },
  ])
  const openHosted = (mappings: Record<string, unknown> = {}) => {
    const definition: WorkflowDefinition = {
      nodes: [at('s', 'Source'), at('t', 'Hosted')],
      edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings: mappings as never }],
    }
    const view = renderInEditor(<MappingDialog edgeId="e1" onClose={vi.fn()} />, {
      portTypes,
      preload: (store) => {
        store.getState().setBlockTypes([source, hosted, entry, exit])
        store.getState().loadWorkflow(definition)
      },
    })
    return view
  }

  it('writes a number for a type graded as Int and a boolean for one graded as Boolean', async () => {
    const user = userEvent.setup()
    const view = openHosted()
    await choose(user, 'Tally mapping type', 'Literal')
    await choose(user, 'Switch mapping type', 'Literal')
    fireEvent.change(screen.getByLabelText('Tally literal'), { target: { value: '12' } })
    await user.click(screen.getByRole('switch', { name: 'Switch literal' }))

    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(view.editor().getState().edges.find((e) => e.id === 'e1')?.mappings).toEqual({
      Tally: { type: 'literal', literalValue: 12 },
      Switch: { type: 'literal', literalValue: true },
    })
  })

  it('writes text for a string form, whatever its format', async () => {
    const user = userEvent.setup()
    const view = openHosted()
    await choose(user, 'Stamp mapping type', 'Literal')
    fireEvent.change(screen.getByLabelText('Stamp literal'), { target: { value: '2024-01-01' } })

    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(view.editor().getState().edges.find((e) => e.id === 'e1')?.mappings).toEqual({
      Stamp: { type: 'literal', literalValue: '2024-01-01' },
    })
  })

  it('offers no value for a registered type that declares no form, and an expression instead', async () => {
    const user = userEvent.setup()
    openHosted()
    await choose(user, 'Scene mapping type', 'Literal')

    expect(screen.getByTestId('literal-none-Scene')).toHaveTextContent('takes no literal value')
    expect(screen.queryByLabelText('Scene literal')).toBeNull()
    await user.click(screen.getByTestId('literal-expression-Scene'))
    expect(screen.getByLabelText('Scene expression')).toBeInTheDocument()
  })
})

describe('MappingDialog on a wire of a block this host does not offer', () => {
  it('shows what the wire stores, read-only, and can still remove it', async () => {
    const user = userEvent.setup()
    const view = open({
      nodes: [at('s', 'Source'), at('g', 'Ghost')],
      edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 'g', mappings: { Flag: { type: 'literal', literalValue: 'true' }, In: { type: 'direct', sourceOutputName: 'Result' } } }],
    })
    const shown = screen.getByTestId('mapping-dialog-unavailable')
    expect(shown).toHaveTextContent('Flag = "true"')
    expect(shown).toHaveTextContent('In ↳ Result')
    expect(within(shown).queryByRole('textbox')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Remove Connection' }))
    expect(view.editor().getState().edges).toEqual([])
  })

  it('opens nothing for a wire into Workflow Input or out of Workflow Output, which the catalogue never lists', () => {
    for (const definition of [
      { nodes: [at('s', 'Source'), at(WORKFLOW_INPUT_NODE_ID, WORKFLOW_INPUT_BLOCK_TYPE_ID)], edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: WORKFLOW_INPUT_NODE_ID, mappings: {} }] },
      { nodes: [at(WORKFLOW_OUTPUT_NODE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID), at('t', 'Target')], edges: [{ id: 'e1', sourceNodeId: WORKFLOW_OUTPUT_NODE_ID, targetNodeId: 't', mappings: {} }] },
    ]) {
      const view = open(definition, {}, [source, target])
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
      view.unmount()
    }
  })
})
