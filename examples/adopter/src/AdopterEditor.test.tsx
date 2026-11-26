import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkflowBlockTypeResponse } from '@reeve-iot/workflow-client'
import type { BlockTypeDescriptor } from '@reeve-iot/workflow-mui'
import { AdopterEditor } from './AdopterEditor.js'

function server() {
  const seen: string[] = []

  const blocks: WorkflowBlockTypeResponse[] = [
    {
      blockTypeId: 'Delay',
      displayName: 'Delay',
      description: 'Waits a while',
      category: 'Flow',
      inputs: [{ name: 'Ms', displayName: 'Milliseconds', type: 'Number', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] }],
      outputs: [{ name: 'Done', displayName: 'Done', type: 'Boolean', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }],
      configurationSchema: [],
      subCanvasNames: [],
      subCanvasInputs: {},
      subCanvasOutputs: {},
      hasDynamicPorts: false,
      supportsDeclaredOutputs: false,
      declaredOutputsCollect: false,
      runsWorkflowFrom: null,
      typeParameters: [],
    },
    {
      blockTypeId: 'RunWorkflow',
      displayName: 'Run Workflow',
      description: 'Runs another workflow',
      category: 'Flow',
      inputs: [{ name: 'WorkflowId', displayName: 'Workflow', type: 'String', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] }],
      outputs: [],
      configurationSchema: [],
      subCanvasNames: [],
      subCanvasInputs: {},
      subCanvasOutputs: {},
      hasDynamicPorts: false,
      supportsDeclaredOutputs: false,
      declaredOutputsCollect: false,
      runsWorkflowFrom: 'WorkflowId',
      typeParameters: [],
    },
  ]

  const child = {
    id: 'child',
    name: 'Child workflow',
    definitionJson: '{"nodes":[],"edges":[]}',
    inputParametersJson: JSON.stringify([{ name: 'Brightness', displayName: 'Brightness', type: 'number', isRequired: true }]),
    outputParametersJson: JSON.stringify([{ name: 'Applied', displayName: 'Applied', type: 'boolean' }]),
  }

  const main = {
    id: 'main',
    name: 'Evening lights',
    definitionJson: JSON.stringify({
      nodes: [
        { id: 'n1', blockTypeId: 'Delay', position: { x: 40, y: 40 }, configuration: {} },
        { id: 'n2', blockTypeId: 'RunWorkflow', position: { x: 260, y: 40 }, configuration: { WorkflowId: 'child' } },
      ],
      edges: [],
    }),
    inputParametersJson: JSON.stringify([{ name: 'Room', displayName: 'Room', type: 'string', isRequired: true }]),
    outputParametersJson: '[]',
  }

  const bodies: Record<string, unknown> = {
    '/api/workflow-blocks': blocks,
    '/api/workflows': [
      { id: 'main', name: 'Evening lights' },
      { id: 'child', name: 'Child workflow' },
    ],
    '/api/workflows/main': main,
    '/api/workflows/child': child,
  }

  const fetch = vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(String(input)).pathname
    seen.push(path)
    const body = bodies[path]
    if (body === undefined) throw new Error(`the adopter asked for ${path}, which this server does not serve`)
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }) as unknown as typeof globalThis.fetch

  return { fetch, seen, blocks }
}

function renderAdopter(overrides: { workflowId?: string | null; adaptBlockType?: (blockType: BlockTypeDescriptor) => BlockTypeDescriptor } = {}) {
  const api = server()
  render(
    <AdopterEditor
      baseUrl="https://reeve.example"
      fetch={api.fetch}
      workflowId={overrides.workflowId === undefined ? 'main' : overrides.workflowId}
      portTypes={[{ name: 'Device' }]}
      adaptBlockType={overrides.adaptBlockType}
    />,
  )
  return api
}

const portLabel = (testId: string) => screen.getByTestId(testId).textContent

describe('an adopter that wires only the ready-made adapters', () => {
  it('reports a refused adapted block and still loads the rest of the palette', async () => {
    renderAdopter({ adaptBlockType: (block) => block.blockTypeId === 'Delay'
      ? { ...block, outputs: [{ name: 'result.value', displayName: 'Result', type: 'Boolean', isRequired: false }] }
      : block })

    expect(await screen.findByText(/Block type 'Delay'.*result\.value.*dotted-name/)).toBeInTheDocument()
    fireEvent.click(await screen.findByTestId('palette-category-Flow'))
    expect(screen.getByTestId('workflow-block-RunWorkflow')).toBeInTheDocument()
    expect(screen.queryByTestId('workflow-block-Delay')).not.toBeInTheDocument()
  })

  it('fills the palette from the server catalogue', async () => {
    renderAdopter()

    fireEvent.click(await screen.findByTestId('palette-category-Flow'))

    expect(screen.getByTestId('workflow-block-Delay')).toBeInTheDocument()
    expect(screen.getByTestId('workflow-block-RunWorkflow')).toBeInTheDocument()
  })

  it('draws the workflow the server holds', async () => {
    renderAdopter()

    await waitFor(() => { expect(screen.getByTestId('workflow-node-Delay')).toBeInTheDocument() })
    expect(screen.getByTestId('workflow-node-RunWorkflow')).toBeInTheDocument()
  })

  it('draws the entry node from the workflow parameters, which arrive as JSON text', async () => {
    renderAdopter()

    await waitFor(() => {
      expect(portLabel('workflow-port-label-__workflow_input__-output-Room')).toBe('Room (String)')
    })
  })

  it('draws a block port under the display name and the type the catalogue declared', async () => {
    renderAdopter()

    await waitFor(() => { expect(portLabel('workflow-port-label-n1-input-Ms')).toBe('Milliseconds* (Number)') })
    expect(portLabel('workflow-port-label-n1-output-Done')).toBe('Done (Boolean)')
  })

  it('takes a referencing node ports from the child workflow, through the resolver', async () => {
    renderAdopter()

    await waitFor(() => {
      expect(portLabel('workflow-port-label-n2-input-Brightness')).toBe('Brightness* (Number)')
    })
    expect(portLabel('workflow-port-label-n2-output-Applied')).toBe('Applied (Boolean)')
  })

  it('never offers a wire into the input a block runs a workflow from, as the catalogue says', async () => {
    renderAdopter()

    await waitFor(() => { expect(screen.getByTestId('workflow-port-n2-input-Brightness')).toBeInTheDocument() })
    expect(screen.queryByTestId('workflow-port-n2-input-WorkflowId')).not.toBeInTheDocument()
  })

  it('offers every workflow in the picker', async () => {
    renderAdopter()

    const picker = await screen.findByLabelText('Workflow')
    await waitFor(() => { expect(within(picker).getAllByRole('option')).toHaveLength(3) })
    expect(within(picker).getByRole('option', { name: 'Evening lights' })).toBeInTheDocument()
  })

  it('reaches only the four URLs the adapters declare, and each workflow once', async () => {
    const api = renderAdopter()

    await waitFor(() => {
      expect(screen.getByTestId('workflow-port-label-n2-input-Brightness')).toBeInTheDocument()
    })

    expect([...new Set(api.seen)].sort()).toEqual([
      '/api/workflow-blocks',
      '/api/workflows',
      '/api/workflows/child',
      '/api/workflows/main',
    ])
    expect(api.seen.filter((p) => p === '/api/workflows/child')).toHaveLength(1)
  })

  it('draws a full palette and no workflow when none is open', async () => {
    renderAdopter({ workflowId: null })

    fireEvent.click(await screen.findByTestId('palette-category-Flow'))

    expect(screen.getByTestId('workflow-block-Delay')).toBeInTheDocument()
    expect(screen.queryByTestId('workflow-node-Delay')).not.toBeInTheDocument()
  })
})
