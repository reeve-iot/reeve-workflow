import { describe, expect, it } from 'vitest'
import { screen } from '@testing-library/react'
import type { BlockTypeDescriptor, PortInfo, WorkflowDefinition, WorkflowNode } from '@reeve-iot/workflow-core'
import { WorkflowReferenceResolverContext } from '@reeve-iot/workflow-core'
import { NodePortCustomizationContext } from './portCustomization.js'
import type { WorkflowReferenceResolver } from '@reeve-iot/workflow-core'
import { BlockNode } from './BlockNode.js'
import { MappingDialog } from '../mapping/MappingDialog.js'
import { renderInEditor } from '../testUtils.js'

const runner: BlockTypeDescriptor = {
  blockTypeId: 'RunWorkflow', displayName: 'Run Workflow',
  inputs: [{ name: 'WorkflowId', displayName: 'Workflow', isRequired: true, type: 'String' }],
  outputs: [],
  runsWorkflowFrom: 'WorkflowId',
}

const source: BlockTypeDescriptor = {
  blockTypeId: 'Source', displayName: 'Source', inputs: [],
  outputs: [{ name: 'Result', displayName: 'Result', isRequired: false, type: 'String' }],
}

const child = { inputs: [{ name: 'Room', displayName: 'Room', isRequired: true, type: 'String' }], outputs: [{ name: 'Done', displayName: 'Done', isRequired: false, type: 'Boolean' }] }

const resolver: WorkflowReferenceResolver = (workflowId) =>
  (workflowId === 'child-1' ? child : { inputs: [] as PortInfo[], outputs: [] as PortInfo[] })

const at = (id: string, blockTypeId: string, extra: Partial<WorkflowNode> = {}): WorkflowNode =>
  ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {}, ...extra })

function drawNode(node: WorkflowNode, definition: WorkflowDefinition) {
  return renderInEditor(
    <WorkflowReferenceResolverContext.Provider value={resolver}>
      <svg>
        <BlockNode
          node={node} isSelected={false} isConnecting={false}
          onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}}
        />
      </svg>
    </WorkflowReferenceResolverContext.Provider>,
    { preload: (store) => { store.getState().setBlockTypes([runner, source]); store.getState().loadWorkflow(definition) } },
  )
}

const port = (nodeId: string, side: 'input' | 'output', name: string) =>
  screen.queryByTestId(`workflow-port-${nodeId}-${side}-${name}`)

describe('a block that runs another workflow', () => {
  it('draws the referenced workflow parameters as its own ports', () => {
    const node = at('r', 'RunWorkflow', { configuration: { WorkflowId: 'child-1' } })
    drawNode(node, { nodes: [node], edges: [] })
    expect(port('r', 'input', 'Room')).toBeInTheDocument()
    expect(port('r', 'output', 'Done')).toBeInTheDocument()
  })

  it('never draws the reference field itself as a wireable port', () => {
    const node = at('r', 'RunWorkflow', { configuration: { WorkflowId: 'child-1' } })
    drawNode(node, { nodes: [node], edges: [] })
    expect(port('r', 'input', 'WorkflowId')).not.toBeInTheDocument()
  })

  it('draws no extra ports while nothing is chosen', () => {
    const node = at('r', 'RunWorkflow')
    drawNode(node, { nodes: [node], edges: [] })
    expect(port('r', 'input', 'Room')).not.toBeInTheDocument()
    expect(port('r', 'input', 'WorkflowId')).not.toBeInTheDocument()
  })

  it('draws no extra ports for a reference that resolves to nothing', () => {
    const node = at('r', 'RunWorkflow', { configuration: { WorkflowId: 'deleted' } })
    drawNode(node, { nodes: [node], edges: [] })
    expect(port('r', 'input', 'Room')).not.toBeInTheDocument()
  })

  it('draws a host override in the same spelling a declared type arrives in', () => {
    const node = at('s', 'Source')
    renderInEditor(
      <NodePortCustomizationContext.Provider
        value={{ overrideInputPortType: () => 'Collection<String>' }}
      >
        <svg>
          <BlockNode
            node={node} isSelected={false} isConnecting={false}
            onDragStart={() => {}} onConnectionStart={() => {}} onConnectionEnd={() => {}} onSelect={() => {}}
          />
        </svg>
      </NodePortCustomizationContext.Provider>,
      {
        preload: (store) => {
          store.getState().setBlockTypes([{
            blockTypeId: 'Source', displayName: 'Source',
            inputs: [{ name: 'Feed', displayName: 'Feed', isRequired: false, type: 'String' }],
            outputs: [],
          }])
          store.getState().loadWorkflow({ nodes: [node], edges: [] })
        },
      },
    )

    expect(screen.getByTestId('workflow-port-label-s-input-Feed').textContent).toBe('Feed (Collection<String>)')
  })

  it('offers the same ports in the mapping dialog as the canvas draws', () => {
    const target = at('r', 'RunWorkflow', { configuration: { WorkflowId: 'child-1' } })
    renderInEditor(
      <WorkflowReferenceResolverContext.Provider value={resolver}>
        <MappingDialog edgeId="e1" onClose={() => {}} />
      </WorkflowReferenceResolverContext.Provider>,
      {
        preload: (store) => {
          store.getState().setBlockTypes([runner, source])
          store.getState().loadWorkflow({
            nodes: [at('s', 'Source'), target],
            edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 'r', mappings: {} }],
          })
        },
      },
    )
    const rows = screen.getAllByRole('row').slice(1).map((r) => r.querySelectorAll('td')[0]?.textContent ?? '')
    expect(rows).toEqual(['Room *(String)'])
  })
})
