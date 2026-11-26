import { act, render, screen } from '@testing-library/react'
import type { BlockTypeDescriptor } from '@reeve-iot/workflow-core'
import { describe, expect, it, vi } from 'vitest'
import { ValidationFindings } from './ValidationFindings.js'
import { editorProviders, renderInEditor } from '../testUtils.js'

describe('ValidationFindings', () => {
  it('renders editor findings and publish diagnostics with host translations and code attributes', () => {
    const findings = [
      { nodeId: 'n1', code: 'required-input-unfed', params: { nodeLabel: 'Notify', portLabel: 'Body' }, message: 'editor fallback' },
      { nodeId: null, ruleId: 'publish-rule', portName: null, canvasPath: [], params: { field: 'Name' }, message: 'publish fallback' },
    ]
    renderInEditor(<ValidationFindings findings={findings} catalogue={{
      'required-input-unfed': (params) => `Host editor: ${String(params.portLabel)}`,
      'publish-rule': (params) => `Host publish: ${String(params.field)}`,
    }} />)

    expect(screen.getByText('Host editor: Body').closest('[data-code]')).toHaveAttribute('data-code', 'required-input-unfed')
    expect(screen.getByText('Host publish: Name').closest('[data-code]')).toHaveAttribute('data-code', 'publish-rule')
    expect(screen.getByText('Host editor: Body').closest('[data-code]')).toHaveAttribute('data-source', 'editor')
    expect(screen.getByText('Host publish: Name').closest('[data-code]')).toHaveAttribute('data-source', 'publish')
  })

  it('reads a publish diagnostic that shares a rule id with an editor template as the server worded it', () => {
    renderInEditor(<ValidationFindings
      findings={[{ ruleId: 'required-input-unfed', message: "Node 'Notify' is missing a required mapping for 'Body'.", nodeId: 'n1' }]}
      catalogue={{ 'required-input-unfed': () => 'Host translation' }}
    />)
    expect(screen.getByText("Node 'Notify' is missing a required mapping for 'Body'.")).toBeTruthy()
    expect(screen.queryByText('Host translation')).toBeNull()
  })

  it('reads a publish diagnostic whose code\'s template needs no params through the catalogue, as the editor row does', () => {
    const catalogue = { 'input-has-incoming': () => 'Host: nothing may feed Workflow Input' }
    renderInEditor(<ValidationFindings
      findings={[
        { code: 'input-has-incoming', ruleId: 'input-has-incoming', nodeId: null, message: 'editor wording' },
        { ruleId: 'input-has-incoming', message: 'server wording.', nodeId: null },
      ]}
      catalogue={catalogue}
    />)
    const rows = screen.getAllByText('Host: nothing may feed Workflow Input').map((row) => row.closest('[data-code]'))
    expect(rows.map((row) => row?.getAttribute('data-source'))).toEqual(['editor', 'publish'])
    expect(screen.queryByText('server wording.')).toBeNull()
  })

  it('renders a graph-wide finding as plain text and a node finding as a button, warnings in their own severity', () => {
    renderInEditor(<ValidationFindings
      findings={[{ nodeId: null, code: 'cycle', params: { location: '' }, message: 'Workflow contains a cycle' }]}
      warnings={[{ nodeId: 'n1', code: 'host-rule', message: 'careful' }]}
    />)
    expect(screen.queryByRole('button', { name: 'Workflow contains a cycle' })).toBeNull()
    expect(screen.getByText('Workflow contains a cycle').closest('[data-code]')).toHaveAttribute('data-severity', 'error')
    expect(screen.getByRole('button', { name: 'careful' }).closest('[data-code]')).toHaveAttribute('data-severity', 'warning')
  })

  it('shows the empty text only when there is nothing to list', () => {
    const { rerender } = render(editorProviders(<ValidationFindings findings={[]} emptyText="No findings." />))
    expect(screen.getByText('No findings.')).toBeTruthy()
    rerender(editorProviders(<ValidationFindings findings={[{ nodeId: null, code: 'host-rule', message: 'one' }]} emptyText="No findings." />))
    expect(screen.queryByText('No findings.')).toBeNull()
  })

  it('opens the canvas a finding is on and selects the node it names, then calls back', () => {
    const LOOP: BlockTypeDescriptor = {
      blockTypeId: 'loop', displayName: 'Loop', inputs: [], outputs: [], subCanvasNames: ['Body'], subCanvasInputs: {}, subCanvasOutputs: {},
    }
    const select = vi.fn()
    const { editor } = renderInEditor(<ValidationFindings
      findings={[{ nodeId: 'inner', code: 'host-rule', message: 'inside the body', canvasPath: [{ nodeId: 'loop', branchName: 'Body' }] }]}
      onSelectNode={select}
    />, {
      preload: (s) => {
        s.getState().setBlockTypes([LOOP])
        s.getState().loadWorkflow({
          nodes: [
            { id: 'loop', blockTypeId: 'loop', position: { x: 0, y: 0 }, configuration: {}, subCanvases: { Body: {
              nodes: [{ id: 'inner', blockTypeId: 'loop', position: { x: 0, y: 0 }, configuration: {} }], edges: [],
            } } },
          ],
          edges: [],
        })
      },
    })
    expect(editor().getState().canvasStack).toHaveLength(0)
    act(() => { screen.getByRole('button', { name: 'inside the body' }).click() })
    expect(editor().getState().canvasStack.map((frame) => frame.parentNodeId)).toEqual(['loop'])
    expect(editor().getState().selectedNodeId).toBe('inner')
    expect(select).toHaveBeenCalledWith('inner', [{ nodeId: 'loop', branchName: 'Body' }])
  })

  it('leaves the selection alone when the node is not on the canvas it names', () => {
    const { editor } = renderInEditor(<ValidationFindings findings={[{ nodeId: 'ghost', code: 'host-rule', message: 'gone' }]} />, {
      preload: (s) => { s.getState().loadWorkflow({ nodes: [{ id: 'a', blockTypeId: 'x', position: { x: 0, y: 0 }, configuration: {} }], edges: [] }) },
    })
    act(() => { screen.getByRole('button', { name: 'gone' }).click() })
    expect(editor().getState().selectedNodeId).toBeNull()
  })
})
