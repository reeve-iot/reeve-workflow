import { describe, expect, it, vi } from 'vitest'
import { fireEvent, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor, WorkflowDefinition, WorkflowNode } from '@reeve-iot/workflow-core'
import {
  WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_INPUT_NODE_ID,
  WORKFLOW_OUTPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_NODE_ID,
} from '@reeve-iot/workflow-core'
import { BlockNode } from './BlockNode.js'
import { PORT_LABEL_INSET, estimatePortLabelWidth } from './portLabelLayout.js'
import { NODE_WIDTH } from './geometry.js'
import { NodePortCustomizationContext } from './portCustomization.js'
import type { NodePortCustomization } from './portCustomization.js'
import { renderInEditor } from '../testUtils.js'

const widget: BlockTypeDescriptor = {
  blockTypeId: 'Widget',
  displayName: 'Widget',
  category: 'Test',
  inputs: [
    { name: 'DeviceId', displayName: 'Device', isRequired: true, type: 'Device' },
    { name: 'Value', displayName: 'Value', isRequired: false, type: 'Json' },
  ],
  outputs: [{ name: 'Status', displayName: 'Status', isRequired: false, type: 'String' }],
}

const branching: BlockTypeDescriptor = {
  blockTypeId: 'IfElse', displayName: 'If / Else', inputs: [], outputs: [], subCanvasNames: ['Then', 'Else'],
}

const node = (extra: Partial<WorkflowNode> = {}): WorkflowNode => ({
  id: 'w', blockTypeId: 'Widget', position: { x: 0, y: 0 }, configuration: {}, ...extra,
})

function draw(target: WorkflowNode, definition: WorkflowDefinition, custom: NodePortCustomization = {}, catalogue = [widget, branching]) {
  const view = renderInEditor(
    <NodePortCustomizationContext.Provider value={custom}>
      <svg>
        <BlockNode
          node={target} isSelected={false} isConnecting={false}
          onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}}
        />
      </svg>
    </NodePortCustomizationContext.Provider>,
  )
  view.seed(catalogue, definition)
  return view
}

const port = (nodeId: string, side: 'input' | 'output', name: string) => screen.queryByTestId(`workflow-port-${nodeId}-${side}-${name}`)
const hint = (label: Element) => label.parentElement?.querySelector(':scope > title') ?? null

describe('BlockNode', () => {
  it('draws the ports its descriptor declares', () => {
    draw(node(), { nodes: [node()], edges: [] })
    expect(port('w', 'input', 'DeviceId')).toBeInTheDocument()
    expect(port('w', 'output', 'Status')).toBeInTheDocument()
  })

  it('marks a required input and names its type', () => {
    draw(node(), { nodes: [node()], edges: [] })
    expect(screen.getByTestId('workflow-port-label-w-input-DeviceId')).toHaveTextContent('Device* (Device)')
  })

  it('draws a placeholder for a block type the catalogue does not carry, with the ports its wires name', () => {
    const ghost = node({ id: 'g', blockTypeId: 'Unknown', position: { x: 40, y: 80 } })
    const before = node({ id: 'a' })
    const after = node({ id: 'b' })
    draw(ghost, {
      nodes: [before, ghost, after],
      edges: [
        { id: 'e1', sourceNodeId: 'a', targetNodeId: 'g', mappings: { Source: { type: 'direct', sourceOutputName: 'Status' }, Limit: { type: 'literal', literalValue: 3 } } },
        { id: 'e2', sourceNodeId: 'g', targetNodeId: 'b', mappings: { DeviceId: { type: 'direct', sourceOutputName: 'Result' } } },
      ],
    })

    const placeholder = screen.getByTestId('workflow-node-Unknown')
    expect(placeholder).toHaveAttribute('data-unavailable', 'true')
    expect(placeholder).toHaveAttribute('transform', 'translate(40, 80)')
    expect(screen.getByTestId('workflow-node-unavailable-g')).toHaveTextContent("Block type 'Unknown' is not available on this host")
    expect(port('g', 'input', 'Source')).toBeInTheDocument()
    expect(port('g', 'input', 'Limit')).toBeInTheDocument()
    expect(port('g', 'output', 'Result')).toBeInTheDocument()
  })

  it('takes the host\'s wording for a placeholder', () => {
    const ghost = node({ id: 'g', blockTypeId: 'Script' })
    renderInEditor(
      <svg>
        <BlockNode
          node={ghost} isSelected={false} isConnecting={false}
          onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}}
          describeUnavailableBlock={(n) => (n.blockTypeId === 'Script' ? { title: 'Script (off)', reason: 'Scripting is switched off.' } : undefined)}
        />
      </svg>,
    ).seed([widget], { nodes: [ghost], edges: [] })

    expect(screen.getByTestId('workflow-node-Script')).toHaveTextContent('Script (off)')
    expect(screen.getByTestId('workflow-node-unavailable-g')).toHaveTextContent('Scripting is switched off.')
  })

  it('grows a start anchor on the entry node so the mandatory first edge can be drawn', () => {
    const entry = { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} }
    draw(entry, { nodes: [entry], edges: [] })
    expect(port(WORKFLOW_INPUT_NODE_ID, 'output', '__start__')).toBeInTheDocument()
  })

  it('yields the start anchor once the workflow declares parameters of its own', () => {
    const entry = { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} }
    const view = draw(entry, { nodes: [entry], edges: [] })
    view.drive((s) => {
      s.getState().setWorkflowParams([{ name: 'Trigger', displayName: 'Trigger', isRequired: true, type: 'Device' }], [])
    })
    expect(port(WORKFLOW_INPUT_NODE_ID, 'output', 'Trigger')).toBeInTheDocument()
    expect(port(WORKFLOW_INPUT_NODE_ID, 'output', '__start__')).not.toBeInTheDocument()
  })

  it('hides an input the host owns elsewhere', () => {
    draw(node(), { nodes: [node()], edges: [] }, { isInputPortHidden: (_, name) => name === 'DeviceId' })
    expect(port('w', 'input', 'DeviceId')).not.toBeInTheDocument()
    expect(port('w', 'input', 'Value')).toBeInTheDocument()
  })

  it('lets the host retype a port from the node configuration', () => {
    draw(node({ configuration: { Kind: 'Boolean' } }), { nodes: [node()], edges: [] }, {
      overrideInputPortType: (n, p) => (p.name === 'Value' ? (n.configuration['Kind'] as string) : null),
    })
    expect(screen.getByTestId('workflow-port-label-w-input-Value')).toHaveTextContent('(Boolean)')
  })

  it('draws ports the host discovered at runtime', () => {
    draw(node(), { nodes: [node()], edges: [] }, {
      useDiscoveredPorts: () => ({ inputs: [{ name: 'FromChild', displayName: 'From Child', isRequired: false, type: 'String' }] }),
    })
    expect(port('w', 'input', 'FromChild')).toBeInTheDocument()
  })

  it('never draws a discovered port twice when it duplicates a declared one', () => {
    draw(node(), { nodes: [node()], edges: [] }, {
      useDiscoveredPorts: () => ({ inputs: [{ name: 'Value', displayName: 'Value', isRequired: false, type: 'String' }] }),
    })
    expect(screen.getAllByTestId('workflow-port-w-input-Value')).toHaveLength(1)
  })

  it('grows the error port under the node\'s EmitError policy and under nothing the run ignores', () => {
    draw(node(), { nodes: [node()], edges: [] })
    expect(port('w', 'output', '_error')).not.toBeInTheDocument()
    draw(node({ errorPolicy: { policy: 'Skip', routeToErrorPort: true } }), { nodes: [node()], edges: [] })
    expect(port('w', 'output', '_error')).not.toBeInTheDocument()
    draw(node({ configuration: { errorPolicy: { policy: 'EmitError' } } }), { nodes: [node()], edges: [] })
    expect(port('w', 'output', '_error')).not.toBeInTheDocument()
    draw(node({ errorPolicy: { policy: 'EmitError' } }), { nodes: [node()], edges: [] })
    expect(port('w', 'output', '_error')).toBeInTheDocument()
  })

  it('draws one _error handle where the node already has a port of that name, as the dialog and the compiler offer one', () => {
    const dynamic: BlockTypeDescriptor = { blockTypeId: 'Dyn', displayName: 'Dyn', inputs: [], outputs: [], hasDynamicPorts: true }
    const ports = JSON.stringify([{ name: '_error', displayName: 'Mine', type: 'String', isRequired: false }])
    const declared = node({ blockTypeId: 'Dyn', errorPolicy: { policy: 'EmitError' }, configuration: { OutputPorts: ports } })
    const passed = node({ errorPolicy: { policy: 'EmitError' }, passthrough: ['_error'] })
    const complaint = vi.spyOn(console, 'error').mockImplementation(() => {})
    try {
      for (const target of [declared, passed]) {
        const view = draw(target, { nodes: [target], edges: [] }, {}, [widget, dynamic])
        expect(screen.getAllByTestId('workflow-port-w-output-_error')).toHaveLength(1)
        view.unmount()
      }
      expect(complaint).not.toHaveBeenCalled()
    } finally {
      complaint.mockRestore()
    }
  })

  it('offers a way into every branch a branching block declares', () => {
    const ifNode = node({ id: 'if1', blockTypeId: 'IfElse' })
    draw(ifNode, { nodes: [ifNode], edges: [] })
    expect(screen.getByText('▶ Enter: Then')).toBeInTheDocument()
    expect(screen.getByText('▶ Enter: Else')).toBeInTheDocument()
  })

  it('descends into the branch whose button was pressed', async () => {
    const ifNode = node({ id: 'if1', blockTypeId: 'IfElse', subCanvases: { Then: { nodes: [], edges: [] }, Else: { nodes: [], edges: [] } } })
    const view = draw(ifNode, { nodes: [ifNode], edges: [] })
    await userEvent.click(document.querySelector('[data-subcanvas-entry="if1:Else"]')!)
    expect(view.editor().getState().canvasStack[0]?.subCanvasName).toBe('Else')
  })

  it('marks a node the host set a breakpoint on', () => {
    renderInEditor(
      <svg>
        <BlockNode node={node()} isSelected={false} isConnecting={false} isBreakpoint onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}} />
      </svg>,
    ).drive((store) => store.getState().setBlockTypes([widget]))
    expect(screen.getByLabelText('Breakpoint')).toBeInTheDocument()
  })

  it('starts a wire from an output port', async () => {
    const onConnectionStart = vi.fn()
    const view = renderInEditor(
      <svg>
        <BlockNode node={node()} isSelected={false} isConnecting={false} onDragStart={() => {}} onConnectionStart={onConnectionStart} onConnectionEnd={() => {}} onSelect={() => {}} />
      </svg>,
    )
    view.drive((store) => store.getState().setBlockTypes([widget]))
    await userEvent.pointer({ keys: '[MouseLeft>]', target: port('w', 'output', 'Status')! })
    expect(onConnectionStart).toHaveBeenCalledWith('w', expect.any(Number), expect.any(Number))
  })

  it('refuses to start a wire in read-only mode', async () => {
    const onConnectionStart = vi.fn()
    const view = renderInEditor(
      <svg>
        <BlockNode node={node()} isSelected={false} isConnecting={false} readOnly onDragStart={() => {}} onConnectionStart={onConnectionStart} onConnectionEnd={() => {}} onSelect={() => {}} />
      </svg>,
    )
    view.drive((store) => store.getState().setBlockTypes([widget]))
    await userEvent.pointer({ keys: '[MouseLeft>]', target: port('w', 'output', 'Status')! })
    expect(onConnectionStart).not.toHaveBeenCalled()
  })
})

describe('BlockNode — the exit anchor of a sub-canvas', () => {
  const parent = (): WorkflowNode => ({
    id: 'b', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {},
    outputPorts: [
      { name: 'Result', displayName: 'Result', type: 'String', sources: { Then: 'BranchResult' } },
      { name: 'ElseOnly', displayName: 'Else Only', type: 'String', sources: { Else: 'ElseValue' } },
    ],
  })
  const exit = (): WorkflowNode =>
    ({ id: WORKFLOW_OUTPUT_NODE_ID, blockTypeId: WORKFLOW_OUTPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} })

  it("draws the parent's declared branch outputs as its inputs", () => {
    const view = draw(exit(), { nodes: [parent()], edges: [] })
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Then') })
    expect(port(WORKFLOW_OUTPUT_NODE_ID, 'input', 'BranchResult')).toBeInTheDocument()
  })

  it('names each port by the BRANCH source key, which is what the engine reads back', () => {
    const view = draw(exit(), { nodes: [parent()], edges: [] })
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Then') })
    expect(port(WORKFLOW_OUTPUT_NODE_ID, 'input', 'Result')).not.toBeInTheDocument()
  })

  it('leaves out a port another branch produces', () => {
    const view = draw(exit(), { nodes: [parent()], edges: [] })
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Then') })
    expect(port(WORKFLOW_OUTPUT_NODE_ID, 'input', 'ElseValue')).not.toBeInTheDocument()
  })

  it('does not draw a second port over one the branch itself declares', () => {
    const declaring: BlockTypeDescriptor = {
      ...branching,
      subCanvasOutputs: { Then: [{ name: 'BranchResult', displayName: 'Branch Result', isRequired: false, type: 'String' }] },
    }
    const view = draw(exit(), { nodes: [parent()], edges: [] }, {}, [widget, declaring])
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Then') })
    expect(screen.getAllByTestId(`workflow-port-${WORKFLOW_OUTPUT_NODE_ID}-input-BranchResult`)).toHaveLength(1)
  })

  it('draws one port when two declared outputs share a branch source key', () => {
    const twice = parent()
    twice.outputPorts = [
      { name: 'A', displayName: 'A', type: 'String', sources: { Then: 'Shared' } },
      { name: 'B', displayName: 'B', type: 'String', sources: { Then: 'Shared' } },
    ]
    const view = draw(exit(), { nodes: [twice], edges: [] })
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Then') })
    expect(screen.getAllByTestId(`workflow-port-${WORKFLOW_OUTPUT_NODE_ID}-input-Shared`)).toHaveLength(1)
  })
})

describe('BlockNode — the entry anchor of a sub-canvas', () => {
  const branchWith = (extra: Partial<BlockTypeDescriptor>): BlockTypeDescriptor =>
    ({ ...branching, subCanvasNames: ['Then'], subCanvasInputs: { Then: [] }, subCanvasOutputs: { Then: [] }, ...extra })
  const parent = (extra: Partial<WorkflowNode> = {}): WorkflowNode =>
    ({ id: 'b', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {}, ...extra })
  const entry = (): WorkflowNode =>
    ({ id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} })

  const inBranch = (catalogue: BlockTypeDescriptor[], parentNode: WorkflowNode) => {
    const view = draw(entry(), { nodes: [parentNode], edges: [] }, {}, catalogue)
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Then') })
    return view
  }

  it('offers a name the parent forwards, even though the branch declares no inputs', () => {
    inBranch([widget, branchWith({})], parent({ passthrough: ['TargetDevice'] }))
    expect(port(WORKFLOW_INPUT_NODE_ID, 'output', 'TargetDevice')).toBeInTheDocument()
  })

  it('draws one port for a name that is both declared and forwarded', () => {
    const declared = branchWith({
      subCanvasInputs: { Then: [{ name: 'TargetDevice', displayName: 'Target Device', isRequired: false, type: 'Device' }] },
    })
    inBranch([widget, declared], parent({ passthrough: ['TargetDevice'] }))
    expect(screen.getAllByTestId(`workflow-port-${WORKFLOW_INPUT_NODE_ID}-output-TargetDevice`)).toHaveLength(1)
  })

  it('yields the start anchor once the branch actually has a port to drag from', () => {
    inBranch([widget, branchWith({})], parent({ passthrough: ['TargetDevice'] }))
    expect(port(WORKFLOW_INPUT_NODE_ID, 'output', '__start__')).not.toBeInTheDocument()
  })
})

describe('BlockNode — declared output ports on the parent', () => {
  const declaring: BlockTypeDescriptor = {
    ...branching, subCanvasNames: ['Then'], supportsDeclaredOutputs: true,
    outputs: [{ name: 'Status', displayName: 'Status', isRequired: false, type: 'String' }],
  }
  const withPorts = (extra: Partial<WorkflowNode> = {}): WorkflowNode => ({
    id: 'b', blockTypeId: 'IfElse', position: { x: 0, y: 0 }, configuration: {},
    outputPorts: [{ name: 'Result', displayName: 'Result', type: 'String', sources: { Then: 'Result' } }],
    ...extra,
  })

  it('grows an output handle for a port the node declares', () => {
    draw(withPorts(), { nodes: [withPorts()], edges: [] }, {}, [widget, declaring])
    expect(port('b', 'output', 'Result')).toBeInTheDocument()
  })

  it('draws one handle when a declared port shares a name with a static output', () => {
    const clash = withPorts({
      outputPorts: [{ name: 'Status', displayName: 'Status', type: 'String', sources: { Then: 'Status' } }],
    })
    draw(clash, { nodes: [clash], edges: [] }, {}, [widget, declaring])
    expect(screen.getAllByTestId('workflow-port-b-output-Status')).toHaveLength(1)
  })

  it('shows a collecting block\'s declared output as the collection of its stored type, and any other block\'s as stored', () => {
    const stored = withPorts({
      outputPorts: [
        { name: 'Total', displayName: 'Total', type: 'Number', sources: { Then: 'Total' } },
        { name: 'Maybe', displayName: 'Maybe', type: 'Int?', sources: { Then: 'Maybe' } },
      ],
    })
    const { unmount } = draw(stored, { nodes: [stored], edges: [] }, {}, [widget, { ...declaring, declaredOutputsCollect: true }])
    expect(screen.getByText('Total (Collection<Number?>)', { selector: 'text' })).toBeInTheDocument()
    expect(screen.getByText('Maybe (Collection<Int?>)', { selector: 'text' })).toBeInTheDocument()
    unmount()

    draw(stored, { nodes: [stored], edges: [] }, {}, [widget, declaring])
    expect(screen.getByText('Total (Number)', { selector: 'text' })).toBeInTheDocument()
    expect(screen.getByText('Maybe (Int?)', { selector: 'text' })).toBeInTheDocument()
  })

  it('draws one handle when a declared port shares a name with a forwarded one', () => {
    const clash = withPorts({ passthrough: ['Result'] })
    draw(clash, { nodes: [clash], edges: [] }, {}, [widget, declaring])
    expect(screen.getAllByTestId('workflow-port-b-output-Result')).toHaveLength(1)
  })
})

describe('BlockNode — flashing', () => {
  it('draws the ring the canvas asked for', () => {
    renderInEditor(
      <svg>
        <BlockNode
          node={node()} isSelected={false} isConnecting={false} isFlashing flashKey={1}
          onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}}
        />
      </svg>,
    ).seed([widget], { nodes: [node()], edges: [] })
    expect(screen.getByTestId('node-flash-ring')).toBeInTheDocument()
  })

  it('draws no ring otherwise, so a flash means something', () => {
    draw(node(), { nodes: [node()], edges: [] })
    expect(screen.queryByTestId('node-flash-ring')).not.toBeInTheDocument()
  })
})

describe('BlockNode — a forwarded value keeps its type', () => {
  const forwarder: BlockTypeDescriptor = {
    blockTypeId: 'Wait', displayName: 'Wait',
    inputs: [{ name: 'DurationMs', displayName: 'Duration', isRequired: true, type: 'Number' }],
    outputs: [],
  }
  const waiting = (): WorkflowNode =>
    ({ id: 'w', blockTypeId: 'Wait', position: { x: 0, y: 0 }, configuration: {}, passthrough: ['Lock'] })
  const entry = () =>
    ({ id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} })

  it('draws a forwarded name with no type before any answer, though a typed source feeds it', () => {
    const view = draw(waiting(), {
      nodes: [entry(), waiting()],
      edges: [{ id: 'e1', sourceNodeId: WORKFLOW_INPUT_NODE_ID, targetNodeId: 'w', mappings: { Lock: { type: 'direct', sourceOutputName: 'Lock' } } }],
    }, {}, [forwarder])
    view.drive((s) => {
      s.getState().setWorkflowParams([{ name: 'Lock', displayName: 'Lock', isRequired: true, type: 'Device' }], [])
    })
    expect(screen.getByTestId('workflow-port-label-w-input-Lock')).toHaveTextContent(/^Lock$/)
    expect(screen.getByTestId('workflow-port-label-w-output-Lock')).toHaveTextContent(/^Lock$/)
  })

  it.each(['$T', 'Collection<$T?>'])('draws a forwarded name with no type when its source output is declared %s', (declared) => {
    const generic: BlockTypeDescriptor = {
      blockTypeId: 'Generic', displayName: 'Generic', inputs: [], typeParameters: [{ name: 'T' }],
      outputs: [{ name: 'Lock', displayName: 'Lock', isRequired: false, type: declared }],
    }
    const source = (): WorkflowNode =>
      ({ id: 'b', blockTypeId: 'Generic', position: { x: 0, y: 0 }, configuration: {} })

    draw(waiting(), {
      nodes: [source(), waiting()],
      edges: [{ id: 'e1', sourceNodeId: 'b', targetNodeId: 'w', mappings: { Lock: { type: 'direct', sourceOutputName: 'Lock' } } }],
    }, {}, [forwarder, generic])

    expect(screen.getByTestId('workflow-port-label-w-input-Lock')).toHaveTextContent(/^Lock$/)
    expect(screen.getByTestId('workflow-port-label-w-output-Lock')).toHaveTextContent(/^Lock$/)
  })

  it('draws a forwarded name fed by a literal with no type, since the compiler types the literal', () => {
    draw(waiting(), {
      nodes: [entry(), waiting()],
      edges: [{ id: 'e1', sourceNodeId: WORKFLOW_INPUT_NODE_ID, targetNodeId: 'w', mappings: { Lock: { type: 'literal', literalValue: 'door' } } }],
    }, {}, [forwarder])

    expect(screen.getByTestId('workflow-port-label-w-input-Lock')).toHaveTextContent(/^Lock$/)
  })

  it('names the declared term of a port the block declares before any answer, a parameter as unbound', () => {
    const generic: BlockTypeDescriptor = {
      blockTypeId: 'Generic', displayName: 'Generic', typeParameters: [{ name: 'T' }],
      inputs: [{ name: 'Item', displayName: 'Item', isRequired: false, type: '$T' }],
      outputs: [{ name: 'Many', displayName: 'Many', isRequired: false, type: 'Collection<$T?>' }],
    }
    const placed = (): WorkflowNode => ({ id: 'g', blockTypeId: 'Generic', position: { x: 0, y: 0 }, configuration: {} })
    draw(placed(), { nodes: [placed()], edges: [] }, {}, [generic])

    expect(screen.getByTestId('workflow-port-label-g-input-Item')).toHaveTextContent('Item (T (unbound))')
    const many = screen.getByTestId('workflow-port-label-g-output-Many')
    expect(many).toHaveTextContent(/^Many$/)
    expect(hint(many)).toHaveTextContent(/^Many \(Collection<T\?> \(unbound\)\)/)
  })
})

describe('BlockNode — a forwarded name takes the term the server gives it', () => {
  const forwarder: BlockTypeDescriptor = { blockTypeId: 'Wait', displayName: 'Wait', inputs: [], outputs: [] }
  const waiting = (): WorkflowNode => ({ id: 'w', blockTypeId: 'Wait', position: { x: 0, y: 0 }, configuration: {}, passthrough: ['Name'] })
  const fedByProjection = (): WorkflowDefinition => ({
    nodes: [
      { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} },
      waiting(),
    ],
    edges: [{
      id: 'e1', sourceNodeId: WORKFLOW_INPUT_NODE_ID, targetNodeId: 'w',
      mappings: { Name: { type: 'direct', sourceOutputName: 'Payload', projection: { path: '$.name', type: 'String' } } },
    }],
  })
  const label = (side: 'input' | 'output') => screen.getByTestId(`workflow-port-label-w-${side}-Name`)

  const answer = (view: ReturnType<typeof draw>, effective: string | null, state: 'bound' | 'refused', offset = 0, findings: string[] = []) => {
    const named = { name: 'Name', declared: null, effective, state, unbound: [] }
    view.drive((s) => {
      s.setState({
        analysis: {
          revision: s.getState().revision + offset, registryGeneration: 1,
          diagnostics: findings.map((message) => ({ ruleId: 'port-type-unresolved', message, nodeId: 'w', portName: 'Name', canvasPath: [] })),
          nodes: [{ canvasPath: [], nodeId: 'w', typeArguments: {}, inputs: [named], outputs: [named] }],
        },
      })
    })
  }

  it('draws the term the compiler gave a name a projection wire feeds, not the Json its source carries', () => {
    const view = draw(waiting(), fedByProjection(), {}, [forwarder])
    view.drive((s) => {
      s.getState().setWorkflowParams([{ name: 'Payload', displayName: 'Payload', isRequired: true, type: 'Json' }], [])
    })
    expect(label('input')).toHaveTextContent(/^Name$/)

    answer(view, 'String', 'bound')

    expect(label('input')).toHaveTextContent('Name (String)')
    expect(label('output')).toHaveTextContent('Name (String)')
    expect(label('input')).toHaveAttribute('data-term-knowledge', 'bound')
    expect(label('input')).toHaveAttribute('data-analysis', 'current')
    expect(label('input')).not.toHaveTextContent('Json')
  })

  it('marks the term of an older answer stale', () => {
    const view = draw(waiting(), fedByProjection(), {}, [forwarder])
    answer(view, 'String', 'bound', -1)

    expect(label('input')).toHaveTextContent('Name (String)')
    expect(label('input')).toHaveAttribute('data-analysis', 'stale')
  })

  it('marks a name the server gave no term as refused, with the finding', () => {
    const view = draw(waiting(), fedByProjection(), {}, [forwarder])
    answer(view, null, 'refused', 0, ['The projection states no type'])

    expect(label('input')).toHaveAttribute('data-term-knowledge', 'refused')
    expect(label('input')).toHaveTextContent('Name (refused)')
    expect(hint(label('input'))).toHaveTextContent('The projection states no type')
  })

  it('draws the term on the entry node of a sub-canvas for a name its owner forwards', () => {
    const owner: BlockTypeDescriptor = { blockTypeId: 'Loop', displayName: 'Loop', inputs: [], outputs: [], subCanvasNames: ['Body'], subCanvasInputs: { Body: [] }, subCanvasOutputs: { Body: [] } }
    const parent: WorkflowNode = { id: 'b', blockTypeId: 'Loop', position: { x: 0, y: 0 }, configuration: {}, passthrough: ['Name'] }
    const entry: WorkflowNode = { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} }
    const view = draw(entry, { nodes: [parent], edges: [] }, {}, [owner])
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Body') })
    const named = { name: 'Name', declared: null, effective: 'String', state: 'bound' as const, unbound: [] }
    view.drive((s) => {
      s.setState({
        analysis: {
          revision: s.getState().revision, registryGeneration: 1, diagnostics: [],
          nodes: [{ canvasPath: [{ nodeId: 'b', branchName: 'Body' }], nodeId: WORKFLOW_INPUT_NODE_ID, typeArguments: {}, inputs: [], outputs: [named] }],
        },
      })
    })

    expect(screen.getByTestId(`workflow-port-label-${WORKFLOW_INPUT_NODE_ID}-output-Name`)).toHaveTextContent('Name (String)')
  })

  it('draws the owner\'s declared term on the entry node for a declared input the owner forwards, before any answer', () => {
    const owner: BlockTypeDescriptor = {
      blockTypeId: 'Loop', displayName: 'Loop', outputs: [], subCanvasNames: ['Body'], subCanvasInputs: { Body: [] }, subCanvasOutputs: { Body: [] },
      inputs: [{ name: 'Count', displayName: 'Count', isRequired: true, type: 'Int' }],
    }
    const parent: WorkflowNode = { id: 'b', blockTypeId: 'Loop', position: { x: 0, y: 0 }, configuration: {}, passthrough: ['Count'] }
    const entry: WorkflowNode = { id: WORKFLOW_INPUT_NODE_ID, blockTypeId: WORKFLOW_INPUT_BLOCK_TYPE_ID, position: { x: 0, y: 0 }, configuration: {} }
    const view = draw(entry, { nodes: [parent], edges: [] }, {}, [owner])
    view.drive((s) => { s.getState().enterSubCanvas('b', 'Body') })

    expect(screen.getByTestId(`workflow-port-label-${WORKFLOW_INPUT_NODE_ID}-output-Count`)).toHaveTextContent('Count (Int)')
  })
})

describe('BlockNode — the server-inferred type of a port', () => {
  const generic: BlockTypeDescriptor = {
    blockTypeId: 'Generic', displayName: 'Generic', typeParameters: [{ name: 'T' }],
    inputs: [{ name: 'Item', displayName: 'Item', isRequired: false, type: '$T' }],
    outputs: [{ name: 'Many', displayName: 'Many', isRequired: false, type: 'Collection<$T?>' }],
  }
  const placed = (): WorkflowNode => ({ id: 'g', blockTypeId: 'Generic', position: { x: 0, y: 0 }, configuration: {} })
  const label = (side: 'input' | 'output', name: string) => screen.getByTestId(`workflow-port-label-g-${side}-${name}`)

  const answered = (view: ReturnType<typeof draw>, ports: { input: [string, string | null]; output: [string, string | null] }, revisionOffset = 0, diagnostics: string[] = []) => {
    const analysed = (name: string, declared: string, effective: string | null, refused: boolean) => ({
      name, declared, effective, state: refused ? 'refused' as const : effective === null ? 'unbound' as const : 'bound' as const,
      unbound: effective === null && !refused ? ['T'] : [],
    })
    view.drive((s) => {
      s.setState({
        analysis: {
          revision: s.getState().revision + revisionOffset, registryGeneration: 1,
          diagnostics: diagnostics.map((message) => ({ ruleId: 'port-type-mismatch', message, nodeId: 'g', portName: 'Item', canvasPath: [] })),
          nodes: [{
            canvasPath: [], nodeId: 'g', typeArguments: {},
            inputs: [analysed('Item', '$T', ports.input[1], diagnostics.length > 0)],
            outputs: [analysed('Many', 'Collection<$T?>', ports.output[1], false)],
          }],
        },
      })
    })
  }

  it('shows the effective term of the current answer, marked as bound', () => {
    const view = draw(placed(), { nodes: [placed()], edges: [] }, {}, [generic])
    answered(view, { input: ['Item', 'Int'], output: ['Many', 'Collection<Int?>'] })

    expect(label('input', 'Item')).toHaveTextContent('Item (Int)')
    expect(label('input', 'Item')).toHaveAttribute('data-term-knowledge', 'bound')
    expect(label('input', 'Item')).toHaveAttribute('data-analysis', 'current')
    expect(label('output', 'Many')).toHaveTextContent('Many (Collection<Int?>)')
  })

  it('keeps naming a parameter nothing binds as unbound', () => {
    const view = draw(placed(), { nodes: [placed()], edges: [] }, {}, [generic])
    answered(view, { input: ['Item', null], output: ['Many', null] })

    expect(label('input', 'Item')).toHaveTextContent('Item (T (unbound))')
    expect(label('input', 'Item')).toHaveAttribute('data-term-knowledge', 'unbound')
  })

  it('marks an answer older than the draft stale, and keeps showing it', () => {
    const view = draw(placed(), { nodes: [placed()], edges: [] }, {}, [generic])
    answered(view, { input: ['Item', 'Int'], output: ['Many', 'Collection<Int?>'] }, -1)

    expect(label('input', 'Item')).toHaveTextContent('Item (Int)')
    expect(label('input', 'Item')).toHaveAttribute('data-analysis', 'stale')
  })

  it('marks a port a finding stands in front of as refused, and carries the finding', () => {
    const view = draw(placed(), { nodes: [placed()], edges: [] }, {}, [generic])
    answered(view, { input: ['Item', null], output: ['Many', null] }, 0, ['String is not assignable to Int'])

    expect(label('input', 'Item')).toHaveAttribute('data-term-knowledge', 'refused')
    expect(label('input', 'Item')).toHaveTextContent('Item (T (refused))')
    expect(hint(label('input', 'Item'))).toHaveTextContent('String is not assignable to Int')
  })
})

describe('BlockNode — port labels', () => {
  const logger: BlockTypeDescriptor = {
    blockTypeId: 'Logger', displayName: 'Logger',
    inputs: [
      { name: 'Message', displayName: 'Message Template', isRequired: true, type: 'String' },
      { name: 'Previous', displayName: 'Previous Log Entry', isRequired: false, type: 'String' },
    ],
    outputs: [{ name: 'Done', displayName: 'Done', isRequired: false, type: 'Flow' }],
  }
  const logging = (): WorkflowNode => ({ id: 'l', blockTypeId: 'Logger', position: { x: 0, y: 0 }, configuration: {} })
  const label = (side: 'input' | 'output', name: string) => screen.getByTestId(`workflow-port-label-l-${side}-${name}`)
  const clipOf = (text: Element) => {
    const id = /^url\(#(.+)\)$/.exec(text.getAttribute('clip-path') ?? '')?.[1]
    const rect = id === undefined ? null : document.getElementById(id)?.querySelector('rect')
    if (!rect) throw new Error('The label names no clip it is drawn in.')
    const x = Number(rect.getAttribute('x'))
    return { start: x, end: x + Number(rect.getAttribute('width')) }
  }

  it('lets a label with nothing across its row run past the middle of the node', () => {
    draw(logging(), { nodes: [logging()], edges: [] }, {}, [logger])
    const lone = label('input', 'Previous')
    expect(clipOf(lone).end).toBeGreaterThan(clipOf(label('input', 'Message')).end)
    expect(estimatePortLabelWidth(lone.textContent ?? '')).toBeGreaterThan(NODE_WIDTH / 2)
    expect(clipOf(lone).end - PORT_LABEL_INSET).toBeGreaterThanOrEqual(estimatePortLabelWidth(lone.textContent ?? ''))
  })

  it('keeps the short label of a shared row whole and gives the long one the rest', () => {
    draw(logging(), { nodes: [logging()], edges: [] }, {}, [logger])
    const input = label('input', 'Message')
    const output = label('output', 'Done')
    expect(NODE_WIDTH - PORT_LABEL_INSET - clipOf(output).start).toBeGreaterThanOrEqual(estimatePortLabelWidth(output.textContent ?? ''))
    expect(clipOf(input).end).toBeGreaterThan(NODE_WIDTH / 2)
    expect(clipOf(input).end).toBeLessThan(clipOf(output).start)
  })

  it('draws the port name alone when the whole label does not fit, and carries the whole label in a hover title', () => {
    draw(logging(), { nodes: [logging()], edges: [] }, {}, [logger])
    expect(label('input', 'Message')).toHaveTextContent(/^Message Template\*$/)
    expect(hint(label('input', 'Message'))).toHaveTextContent(/^Message Template\* \(String\)$/)
    expect(label('output', 'Done')).toHaveTextContent(/^Done \(Flow\)$/)
    expect(hint(label('output', 'Done'))).toHaveTextContent(/^Done \(Flow\)$/)
  })

  it('never draws more of a label than its share of the row holds', () => {
    draw(logging(), { nodes: [logging()], edges: [] }, {}, [logger])
    for (const drawn of [label('input', 'Message'), label('input', 'Previous'), label('output', 'Done')]) {
      const clip = clipOf(drawn)
      const room = drawn === label('output', 'Done') ? NODE_WIDTH - PORT_LABEL_INSET - clip.start : clip.end - PORT_LABEL_INSET
      expect(estimatePortLabelWidth(drawn.textContent ?? '')).toBeLessThanOrEqual(room)
    }
  })

  it('adds the node tooltip to a label title, so hovering a label hides nothing the body shows', () => {
    const view = renderInEditor(
      <svg>
        <BlockNode
          node={logging()} isSelected={false} isConnecting={false} highlight={{ nodeId: 'l', status: 'success', inputsJson: '{"Message":"hi"}' }}
          onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}}
        />
      </svg>,
    )
    view.seed([logger], { nodes: [logging()], edges: [] })
    expect(hint(label('output', 'Done'))).toHaveTextContent('Done (Flow)')
    expect(hint(label('output', 'Done'))).toHaveTextContent('{"Message":"hi"}')
  })

  it('drags and selects the node from a label, as from its body', async () => {
    const onDragStart = vi.fn()
    const onSelect = vi.fn()
    const view = renderInEditor(
      <svg>
        <BlockNode node={logging()} isSelected={false} isConnecting={false} onDragStart={onDragStart} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={onSelect} />
      </svg>,
    )
    view.seed([logger], { nodes: [logging()], edges: [] })
    await userEvent.click(label('input', 'Previous'))
    expect(onDragStart).toHaveBeenCalledWith(expect.anything(), 'l')
    expect(onSelect).toHaveBeenCalledWith('l')
  })

  it('ends a wire dropped on a label, as on the body', () => {
    const onConnectionEnd = vi.fn()
    const view = renderInEditor(
      <svg>
        <BlockNode node={logging()} isSelected={false} isConnecting onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={onConnectionEnd} onSelect={() => {}} />
      </svg>,
    )
    view.seed([logger], { nodes: [logging()], edges: [] })
    fireEvent.mouseUp(label('output', 'Done'))
    expect(onConnectionEnd).toHaveBeenCalledWith('l')
  })
})
