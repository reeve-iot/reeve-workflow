import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkflowBlockTypeResponse } from '@reeve-iot/workflow-client'
import { DispatchEditor } from './DispatchEditor.js'

function server() {
  const blocks: WorkflowBlockTypeResponse[] = [
    {
      blockTypeId: 'PullManifest',
      displayName: 'Pull manifest',
      description: 'Everything booked out of a depot today',
      category: 'Freight',
      inputs: [{ name: 'Depot', displayName: 'Depot', type: 'depot', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] }],
      outputs: [{ name: 'Freight', displayName: 'Freight', type: 'Collection<Shipment>', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }],
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
      blockTypeId: 'Dispatch',
      displayName: 'Dispatch',
      description: 'Hands freight to a carrier',
      category: 'Freight',
      inputs: [
        { name: 'Freight', displayName: 'Freight', type: 'shipment', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] },
        { name: 'Carrier', displayName: 'Carrier', type: 'carrier', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] },
        { name: 'Depot', displayName: 'Depot', type: 'depot', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] },
        { name: 'Hazard', displayName: 'Hazard', type: 'hazardclass', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] },
      ],
      outputs: [{ name: 'Consignment', displayName: 'Consignment', type: 'String', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }],
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
  ]

  const plan = {
    id: 'plan',
    name: 'Morning run',
    definitionJson: JSON.stringify({
      nodes: [
        { id: 'pull', blockTypeId: 'PullManifest', position: { x: 40, y: 40 }, configuration: {} },
        {
          id: 'ship',
          blockTypeId: 'Dispatch',
          position: { x: 300, y: 40 },
          configuration: { Consolidated: true, HazardClass: 3, Depot: 'WAW' },
        },
      ],
      edges: [],
    }),
    inputParametersJson: JSON.stringify([
      { name: 'Origin', displayName: 'Origin', type: 'depot', isRequired: true },
    ]),
    outputParametersJson: '[]',
  }

  const bodies: Record<string, unknown> = {
    '/api/workflow-blocks': blocks,
    '/api/workflows/plan': plan,
  }

  const fetch = vi.fn(async (input: RequestInfo | URL) => {
    const path = new URL(String(input)).pathname
    const body = bodies[path]
    if (body === undefined) throw new Error(`the dispatch host asked for ${path}, which this server does not serve`)
    return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }) as unknown as typeof globalThis.fetch

  return { fetch }
}

function renderDispatch() {
  const api = server()
  render(<DispatchEditor baseUrl="https://freight.example" fetch={api.fetch} workflowId="plan" />)
  return api
}

const wholePortLabel = (testId: string) =>
  screen.getByTestId(testId).parentElement?.querySelector(':scope > title')?.textContent?.split('\n')[0]

describe('a host whose domain this library has never seen', () => {
  it('draws a port in a type the library does not ship', async () => {
    renderDispatch()

    await waitFor(() => { expect(wholePortLabel('workflow-port-label-ship-input-Carrier')).toBe('Carrier* (Carrier)') })
  })

  it('derives the collection companion of a host type without being told to', async () => {
    renderDispatch()

    await waitFor(() => {
      expect(wholePortLabel('workflow-port-label-pull-output-Freight')).toBe('Freight (Collection<Shipment>)')
    })
  })

  it('takes a port type from the node configuration rather than the catalogue', async () => {
    renderDispatch()

    await waitFor(() => {
      expect(wholePortLabel('workflow-port-label-ship-input-Freight')).toBe('Freight* (Collection<Shipment>)')
    })
  })

  it('hides the port the side panel owns', async () => {
    renderDispatch()

    await waitFor(() => { expect(screen.getByTestId('workflow-node-Dispatch')).toBeInTheDocument() })

    expect(screen.queryByTestId('workflow-port-label-ship-input-Depot')).not.toBeInTheDocument()
    expect(screen.getByTestId('workflow-port-label-pull-input-Depot')).toBeInTheDocument()
  })

  it('declares a workflow parameter in a host type', async () => {
    renderDispatch()

    await waitFor(() => {
      expect(wholePortLabel('workflow-port-label-__workflow_input__-output-Origin')).toBe('Origin (Depot)')
    })
  })

  it('reports the hosts own rule against the hosts own configuration', async () => {
    renderDispatch()

    await waitFor(() => {
      expect(screen.getByText(/only GDN is bonded/)).toBeInTheDocument()
    })
  })

  it('keeps the rest of the configuration when the panel writes one field', async () => {
    renderDispatch()

    await waitFor(() => { expect(screen.getByTestId('workflow-node-body-ship')).toBeInTheDocument() })
    fireEvent.click(screen.getByTestId('workflow-node-body-ship'))
    fireEvent.click(await screen.findByRole('button', { name: 'Gdańsk' }))

    await waitFor(() => { expect(screen.queryByText(/only GDN is bonded/)).not.toBeInTheDocument() })
    expect(wholePortLabel('workflow-port-label-ship-input-Freight')).toBe('Freight* (Collection<Shipment>)')
  })
})
