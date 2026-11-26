import { describe, expect, it, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { BlockTypeDescriptor, MappingProjection, WorkflowDefinition } from '@reeve-iot/workflow-core'
import { MappingDialog } from './MappingDialog.js'
import { MappingCustomizationContext } from './mappingCustomization.js'
import type { MappingCustomization, ProjectionControlContext } from './mappingCustomization.js'
import { renderInEditor } from '../testUtils.js'

const source: BlockTypeDescriptor = {
  blockTypeId: 'Source', displayName: 'Source', inputs: [],
  outputs: [
    { name: 'Payload', displayName: 'Payload', isRequired: false, type: 'Json' },
    { name: 'Maybe', displayName: 'Maybe', isRequired: false, type: 'Json?' },
    { name: 'Name', displayName: 'Name', isRequired: false, type: 'String' },
  ],
}
const target: BlockTypeDescriptor = {
  blockTypeId: 'Target', displayName: 'Target', outputs: [],
  inputs: [
    { name: 'Count', displayName: 'Count', isRequired: false, type: 'Int' },
    { name: 'Label', displayName: 'Label', isRequired: false, type: 'String' },
  ],
}

const at = (id: string, blockTypeId: string) => ({ id, blockTypeId, position: { x: 0, y: 0 }, configuration: {} })
const definition = (mappings: WorkflowDefinition['edges'][number]['mappings'], policy?: 'EmitError' | 'Skip'): WorkflowDefinition => ({
  nodes: [policy ? { ...at('s', 'Source'), errorPolicy: { policy } } : at('s', 'Source'), at('t', 'Target')],
  edges: [{ id: 'e1', sourceNodeId: 's', targetNodeId: 't', mappings }],
})

function open(mappings: WorkflowDefinition['edges'][number]['mappings'], custom: MappingCustomization, policy?: 'EmitError' | 'Skip') {
  const view = renderInEditor(
    <MappingCustomizationContext.Provider value={custom}>
      <MappingDialog edgeId="e1" onClose={vi.fn()} />
    </MappingCustomizationContext.Provider>,
    { preload: (store) => { store.getState().setBlockTypes([source, target]); store.getState().loadWorkflow(definition(mappings, policy)) } },
  )
  return view
}

const stored = (view: ReturnType<typeof open>) => view.editor().getState().edges.find((e) => e.id === 'e1')?.mappings

function recorder() {
  const seen: ProjectionControlContext[] = []
  const custom: MappingCustomization = {
    renderProjectionControl: (context) => {
      seen.push(context)
      if (context.placement === 'wire') {
        return (
          <div data-testid={`control-${context.inputName}`}>
            <button onClick={() => { context.onChange({ path: '$.id', type: 'Int' }) }}>set {context.inputName}</button>
            <button onClick={() => { context.onChange(null) }}>clear {context.inputName}</button>
          </div>
        )
      }
      return <button onClick={() => { context.onExpressionChange(`jsonPath(${context.jsonOutputs[0]?.name ?? ''}, '$.id')`) }}>expression assist {context.inputName}</button>
    },
  }
  return { seen, custom }
}

describe('the projection control slot of the mapping dialog', () => {
  it('is drawn on a direct mapping from a Json output, and not on another source', () => {
    const { seen, custom } = recorder()
    open({ Count: { type: 'direct', sourceOutputName: 'Payload' }, Label: { type: 'direct', sourceOutputName: 'Name' } }, custom)

    expect(screen.getByTestId('control-Count')).toBeInTheDocument()
    expect(screen.queryByTestId('control-Label')).not.toBeInTheDocument()
    expect(seen.filter((context) => context.placement === 'wire')[0]).toMatchObject({
      placement: 'wire', edgeId: 'e1', targetNodeId: 't', inputName: 'Count', sourceNodeId: 's', sourceOutputName: 'Payload',
      sourceType: 'Json', targetType: 'Int', projection: undefined,
    })
  })

  it('is drawn for a Json? source too', () => {
    const { custom } = recorder()
    open({ Label: { type: 'direct', sourceOutputName: 'Maybe' } }, custom)
    expect(screen.getByTestId('control-Label')).toBeInTheDocument()
  })

  it('draws nothing new where the host provides no slot', () => {
    open({ Count: { type: 'direct', sourceOutputName: 'Payload' } }, {})
    expect(screen.queryByTestId('control-Count')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /set Count/ })).not.toBeInTheDocument()
  })

  it('is not drawn on a literal mapping', () => {
    const { custom } = recorder()
    open({ Count: { type: 'literal', literalValue: 3 } }, custom)
    expect(screen.queryByTestId('control-Count')).not.toBeInTheDocument()
  })

  it('writes the stored projection key when the author saves, and clears it', async () => {
    const user = userEvent.setup()
    const { custom } = recorder()
    const view = open({ Count: { type: 'direct', sourceOutputName: 'Payload' } }, custom)

    await user.click(screen.getByRole('button', { name: 'set Count' }))
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(stored(view)).toEqual({ Count: { type: 'direct', sourceOutputName: 'Payload', projection: { path: '$.id', type: 'Int' } } })
  })

  it('hands the stored projection back, and clears it on null', async () => {
    const user = userEvent.setup()
    const { seen, custom } = recorder()
    const projection: MappingProjection = { path: '$.items', mode: 'jsonText' }
    const view = open({ Count: { type: 'direct', sourceOutputName: 'Payload', projection } }, custom)

    expect(seen.some((context) => context.placement === 'wire' && context.projection?.path === '$.items')).toBe(true)
    await user.click(screen.getByRole('button', { name: 'clear Count' }))
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(stored(view)).toEqual({ Count: { type: 'direct', sourceOutputName: 'Payload' } })
  })

  it('keeps a stored projection through an unrelated edit and opens its control on a source that is not Json', async () => {
    const user = userEvent.setup()
    const { custom } = recorder()
    const view = open({ Count: { type: 'direct', sourceOutputName: 'Payload', projection: { path: '$.n' } }, Label: { type: 'direct', sourceOutputName: 'Name', projection: { path: '$.stale' } } }, custom)

    expect(screen.getByTestId('control-Label')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(stored(view)).toEqual({
      Count: { type: 'direct', sourceOutputName: 'Payload', projection: { path: '$.n' } },
      Label: { type: 'direct', sourceOutputName: 'Name', projection: { path: '$.stale' } },
    })
  })

  it('hands the term of a source that is not Json, and no term where the source declares no such output', () => {
    const { seen, custom } = recorder()
    open({ Label: { type: 'direct', sourceOutputName: 'Name', projection: { path: '$.a' } }, Count: { type: 'direct', sourceOutputName: 'Gone', projection: { path: '$.b' } } }, custom)

    const wires = seen.filter((context) => context.placement === 'wire')
    expect(wires.find((context) => context.inputName === 'Label')?.sourceType).toBe('String')
    expect(wires.find((context) => context.inputName === 'Count')?.sourceType).toBeUndefined()
  })

  it('drops the projection when the author switches the mapping to a literal', async () => {
    const user = userEvent.setup()
    const { custom } = recorder()
    const view = open({ Label: { type: 'direct', sourceOutputName: 'Payload', projection: { path: '$.n' } } }, custom)

    await user.click(screen.getByLabelText('Label mapping type'))
    await user.click(screen.getByRole('option', { name: 'Literal' }))
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(stored(view)?.['Label']).not.toHaveProperty('projection')
  })

  it('is offered to an expression mapping whose wire has Json outputs, and writes the expression', async () => {
    const user = userEvent.setup()
    const { seen, custom } = recorder()
    const view = open({ Count: { type: 'literal', literalValue: 'expr:Payload' } }, custom)

    expect(seen.find((context) => context.placement === 'expression')).toMatchObject({
      placement: 'expression', edgeId: 'e1', targetNodeId: 't', inputName: 'Count', sourceNodeId: 's',
      jsonOutputs: [{ name: 'Payload', type: 'Json' }, { name: 'Maybe', type: 'Json?' }], expressionBody: 'Payload', targetType: 'Int',
    })
    await user.click(screen.getByRole('button', { name: 'expression assist Count' }))
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(stored(view)).toEqual({ Count: { type: 'literal', literalValue: "expr:jsonPath(Payload, '$.id')" } })
  })

  it('offers the _error output of a node under EmitError as a Json source, and a projection on it is stored', async () => {
    const user = userEvent.setup()
    const { seen, custom } = recorder()
    const view = open({}, custom, 'EmitError')

    await user.click(screen.getByRole('combobox', { name: 'Count source' }))
    await user.click(screen.getByRole('option', { name: 'Error (Json)' }))
    expect(seen.filter((context) => context.placement === 'wire').at(-1)).toMatchObject({ inputName: 'Count', sourceOutputName: '_error', sourceType: 'Json' })
    await user.click(screen.getByRole('button', { name: 'set Count' }))
    await user.click(screen.getByRole('button', { name: 'Save Mappings' }))
    expect(stored(view)).toEqual({ Count: { type: 'direct', sourceOutputName: '_error', projection: { path: '$.id', type: 'Int' } } })
  })

  it('offers no _error output to a node whose policy is not EmitError', async () => {
    const user = userEvent.setup()
    const { custom } = recorder()
    open({}, custom, 'Skip')

    await user.click(screen.getByRole('combobox', { name: 'Count source' }))
    expect(screen.getByRole('option', { name: 'Payload (Json)' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Error (Json)' })).not.toBeInTheDocument()
  })

  it('names _error among the Json outputs an expression mapping can read', () => {
    const { seen, custom } = recorder()
    open({ Count: { type: 'literal', literalValue: 'expr:Payload' } }, custom, 'EmitError')

    expect(seen.find((context) => context.placement === 'expression')).toMatchObject({
      jsonOutputs: [{ name: 'Payload', type: 'Json' }, { name: 'Maybe', type: 'Json?' }, { name: '_error', type: 'Json' }],
    })
  })

  it('offers one _error output where the node already declares an output of that name', async () => {
    const user = userEvent.setup()
    const declaring: BlockTypeDescriptor = {
      ...source, outputs: [...source.outputs, { name: '_error', displayName: 'Failure', isRequired: false, type: 'Json' }],
    }
    renderInEditor(
      <MappingCustomizationContext.Provider value={{}}>
        <MappingDialog edgeId="e1" onClose={vi.fn()} />
      </MappingCustomizationContext.Provider>,
      { preload: (store) => { store.getState().setBlockTypes([declaring, target]); store.getState().loadWorkflow(definition({}, 'EmitError')) } },
    )

    await user.click(screen.getByRole('combobox', { name: 'Count source' }))
    expect(screen.getByRole('option', { name: 'Failure (Json)' })).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: 'Error (Json)' })).not.toBeInTheDocument()
  })
})
