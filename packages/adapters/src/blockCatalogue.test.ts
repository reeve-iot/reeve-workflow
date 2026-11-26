import { StrictMode, createElement, useEffect } from 'react'
import type { ReactNode } from 'react'
import { act, render, renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { BlockPortResponse, WorkflowBlockTypeResponse, WorkflowClient } from '@reeve-iot/workflow-client'
import {
  BUILTIN_PORT_TYPES,
  BlockPortDeclarationInvalidError,
  PortTypeRegistryContext,
  createPortTypeRegistry,
  createWorkflowEditorStore,
} from '@reeve-iot/workflow-core'
import { createBlockCatalogueSource, readBlockType } from './blockCatalogue.js'

const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Device' }])

const wrapper = ({ children }: { children: ReactNode }) =>
  createElement(PortTypeRegistryContext.Provider, { value: portTypes }, children)

function block(overrides: Partial<WorkflowBlockTypeResponse> = {}): WorkflowBlockTypeResponse {
  return {
    blockTypeId: 'Delay',
    displayName: 'Delay',
    description: 'Waits',
    category: 'Flow',
    inputs: [],
    outputs: [],
    configurationSchema: [],
    subCanvasNames: [],
    subCanvasInputs: {},
    subCanvasOutputs: {},
    hasDynamicPorts: false,
    supportsDeclaredOutputs: false,
    declaredOutputsCollect: false,
    runsWorkflowFrom: null,
    typeParameters: [],
    ...overrides,
  }
}

function clientAnswering(blocks: readonly WorkflowBlockTypeResponse[], calls?: { count: number }): WorkflowClient {
  return {
    getBlockTypes: async () => {
      if (calls) calls.count += 1
      return blocks
    },
  } as unknown as WorkflowClient
}

describe('readBlockType', () => {
  it('executes the shared declaration vectors through the catalogue reader, which refuses only a term that does not parse', () => {
    interface Vector {
      name: string
      typeParameters?: { name: string; constraints: string[] }[]
      inputs?: Record<string, string>
      outputs?: Record<string, string>
      scopeVariables?: Record<string, { name: string; type: string }[]>
      subCanvases?: Record<string, { inputs: Record<string, string>; outputs: Record<string, string> }>
      hasDynamicPorts?: boolean
      supportsDeclaredOutputs?: boolean
      declaredOutputsCollect?: boolean
      expect: true | string
      expectEditor?: string
      names?: string[]
    }
    const contract = JSON.parse(readFileSync(resolve(__dirname, '../../../contracts/registration-agreement.json'), 'utf8')) as {
      declarations: Vector[]
    }
    const ports = (types: Record<string, string> | undefined, variables?: Record<string, { name: string; type: string }[]>): BlockPortResponse[] =>
      Object.entries(types ?? {}).map(([name, type]) =>
        ({ name, displayName: name, type, isRequired: false, allowedValues: null, secretValues: false, scopeVariables: variables?.[name] ?? [] }))
    expect(contract.declarations.length).toBeGreaterThan(40)
    expect(contract.declarations.filter((v) => v.expectEditor !== undefined).length).toBeGreaterThan(5)
    for (const vector of contract.declarations) {
      const branches = Object.keys(vector.subCanvases ?? {})
      const descriptor = block({
        blockTypeId: vector.name,
        typeParameters: vector.typeParameters ?? [],
        hasDynamicPorts: vector.hasDynamicPorts ?? false,
        supportsDeclaredOutputs: vector.supportsDeclaredOutputs ?? false,
        declaredOutputsCollect: vector.declaredOutputsCollect ?? false,
        inputs: ports(vector.inputs, vector.scopeVariables),
        outputs: ports(vector.outputs),
        subCanvasNames: branches,
        subCanvasInputs: Object.fromEntries(branches.map((b) => [b, ports(vector.subCanvases?.[b]?.inputs)])),
        subCanvasOutputs: Object.fromEntries(branches.map((b) => [b, ports(vector.subCanvases?.[b]?.outputs)])),
      })
      if (vector.expectEditor === undefined) {
        expect(() => readBlockType(descriptor, portTypes), vector.name).not.toThrow()
        continue
      }
      let refusal: unknown
      try {
        readBlockType(descriptor, portTypes)
      } catch (error) {
        refusal = error
      }
      expect(refusal, vector.name).toBeInstanceOf(BlockPortDeclarationInvalidError)
      const message = (refusal as Error).message
      expect(message.startsWith(`${vector.expectEditor}:`), vector.name).toBe(true)
      expect(message, vector.name).toContain(`'${vector.name}'`)
      for (const named of vector.names ?? []) expect(message, `${vector.name}: names ${named}`).toContain(named)
    }
  })

  it('carries the scope variables of an Expression input, each type normalised as a port type is, and none on any other port', () => {
    const descriptor = readBlockType(
      block({
        typeParameters: [{ name: 'T', constraints: [] }],
        inputs: [
          { name: 'Items', displayName: 'Items', type: 'Collection<$T>', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] },
          {
            name: 'Predicate', displayName: 'Predicate', type: 'Expression<Boolean>', isRequired: true, allowedValues: null,
            secretValues: false, scopeVariables: [{ name: 'item', type: '$T' }, { name: 'value', type: 'device' }],
          },
        ],
      }),
      portTypes,
    )

    expect(descriptor.inputs[0]).not.toHaveProperty('scopeVariables')
    expect(descriptor.inputs[1]?.scopeVariables).toEqual([{ name: 'item', type: '$T' }, { name: 'value', type: 'Device' }])
  })

  it('carries the SecretValues declaration of an input, and none on a port that does not declare it', () => {
    const descriptor = readBlockType(
      block({
        inputs: [
          { name: 'Headers', displayName: 'Headers', type: 'Map<String,String>', isRequired: false, allowedValues: null, secretValues: true, scopeVariables: [] },
          { name: 'Tags', displayName: 'Tags', type: 'Map<String,String>', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] },
        ],
      }),
      portTypes,
    )

    expect(descriptor.inputs[0]?.secretValues).toBe(true)
    expect(descriptor.inputs[1]).not.toHaveProperty('secretValues')
  })

  it('carries a block\'s type parameters and their constraints, and a declaration term as the type of a port', () => {
    const descriptor = readBlockType(
      block({
        typeParameters: [{ name: 'T', constraints: ['Numeric'] }, { name: 'R', constraints: [] }],
        inputs: [{ name: 'Items', displayName: 'Items', type: 'Collection<$T>', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] }],
        outputs: [{ name: 'First', displayName: 'First', type: '$R?', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }],
      }),
      portTypes,
    )

    expect(descriptor.typeParameters).toEqual([{ name: 'T', constraints: ['Numeric'] }, { name: 'R', constraints: [] }])
    expect(descriptor.inputs[0]?.type).toBe('Collection<$T>')
    expect(descriptor.outputs[0]?.type).toBe('$R?')
  })

  it('reports a declared term that does not parse as a finding naming the block and the port, and leaves the block out of the palette', async () => {
    const bad = block({
      blockTypeId: 'bad',
      inputs: [{ name: 'A', displayName: 'A', type: 'Int??', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }],
    })
    const errors: unknown[] = []
    const source = createBlockCatalogueSource(clientAnswering([bad, block({ blockTypeId: 'good' })]), { onError: (e) => { errors.push(e) } })

    const { result } = renderHook(() => source.useBlockTypes(), { wrapper })
    await waitFor(() => { expect(result.current.map((b) => b.blockTypeId)).toEqual(['good']) })

    await waitFor(() => { expect(errors).toHaveLength(1) })
    expect(errors[0]).toMatchObject({
      code: 'block-port-declaration-invalid',
      params: { blockTypeId: 'bad', where: "port 'A'" },
      message: expect.stringContaining("Block 'bad' port 'A' has the type 'Int??', which is not a declaration term") as unknown,
    })
  })

  it('normalises every port type through the registry', () => {
    const descriptor = readBlockType(
      block({
        inputs: [{ name: 'On', displayName: 'On', type: 'device', isRequired: true, allowedValues: null, secretValues: false, scopeVariables: [] }],
        outputs: [{ name: 'Out', displayName: 'Out', type: 'Collection<String>', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }],
      }),
      portTypes,
    )

    expect(descriptor.inputs[0]?.type).toBe('Device')
    expect(descriptor.outputs[0]?.type).toBe('Collection<String>')
  })

  it('carries allowed values through, because they are what a port picker offers', () => {
    const descriptor = readBlockType(
      block({ inputs: [{ name: 'Mode', displayName: 'Mode', type: 'String', isRequired: true, allowedValues: ['On', 'Off'], secretValues: false, scopeVariables: [] }] }),
      portTypes,
    )

    expect(descriptor.inputs[0]?.allowedValues).toEqual(['On', 'Off'])
  })

  it('carries both sub-canvas maps, keyed by the names the block declares', () => {
    const descriptor = readBlockType(
      block({
        subCanvasNames: ['Try', 'Catch'],
        subCanvasInputs: { Try: [{ name: 'In', displayName: 'In', type: 'String', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }], Catch: [] },
        subCanvasOutputs: { Try: [], Catch: [{ name: 'Err', displayName: 'Err', type: 'String', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }] },
      }),
      portTypes,
    )

    expect(descriptor.subCanvasNames).toEqual(['Try', 'Catch'])
    expect(descriptor.subCanvasInputs?.['Try']?.map((p) => p.name)).toEqual(['In'])
    expect(descriptor.subCanvasOutputs?.['Catch']?.map((p) => p.name)).toEqual(['Err'])
  })

  it('carries the two capability flags separately', () => {
    const descriptor = readBlockType(block({ hasDynamicPorts: true, supportsDeclaredOutputs: false }), portTypes)

    expect(descriptor.hasDynamicPorts).toBe(true)
    expect(descriptor.supportsDeclaredOutputs).toBe(false)
  })

  it('carries the configuration schema, which is what draws the node config panel', () => {
    const descriptor = readBlockType(
      block({ configurationSchema: [{ name: 'Ms', label: 'Milliseconds', type: 'Number', required: true, defaultValue: '1000', description: null, allowedValues: null }] }),
      portTypes,
    )

    expect(descriptor.configurationSchema).toEqual([
      { name: 'Ms', label: 'Milliseconds', type: 'Number', required: true, defaultValue: '1000', description: null, allowedValues: null },
    ])
  })

  it('carries an Enum field\'s names in the order the server lists them', () => {
    const descriptor = readBlockType(
      block({ configurationSchema: [{ name: 'Kind', label: 'Kind', type: 'Enum', required: false, defaultValue: null, description: null, allowedValues: ['Toggle', 'Dim', 'Scene'] }] }),
      portTypes,
    )

    expect(descriptor.configurationSchema?.[0]?.type).toBe('Enum')
    expect(descriptor.configurationSchema?.[0]?.allowedValues).toEqual(['Toggle', 'Dim', 'Scene'])
  })

  it.each([
    ['writes null', { allowedValues: null }],
    ['leaves the key out', {}],
  ])('reads a non-Enum field whose wire %s as having no names', (_, names) => {
    const field = { name: 'Ms', label: 'Ms', type: 'Number', required: false, defaultValue: null, description: null, ...names }
    const descriptor = readBlockType(block({ configurationSchema: [field as never] }), portTypes)

    expect(descriptor.configurationSchema?.[0]?.allowedValues).toBeNull()
  })

  it('refuses a port or a field that carries a text policy', () => {
    const port = { name: 'Ms', displayName: 'Ms', type: 'Int', isRequired: false, allowedValues: null, textPolicy: 'Parse' }
    const field = { name: 'Ms', label: 'Ms', type: 'Number', required: false, defaultValue: null, description: null, textPolicy: 'Parse', allowedValues: null }
    expect(() => readBlockType(block({ inputs: [port as never] }), portTypes)).toThrow(/'Ms' declares a text policy, which this editor's block-catalogue format does not define/)
    expect(() => readBlockType(block({ configurationSchema: [field as never] }), portTypes)).toThrow(/'Ms' declares a text policy, which this editor's block-catalogue format does not define/)
  })

  it('carries the input a block runs a workflow from, and leaves it unset for a block that runs none', () => {
    expect(readBlockType(block({ runsWorkflowFrom: 'WorkflowId' }), portTypes).runsWorkflowFrom).toBe('WorkflowId')
    expect('runsWorkflowFrom' in readBlockType(block(), portTypes)).toBe(false)
  })

  it('refuses a port that declares a relation, and reads one whose relation is null', () => {
    const port = (typeFrom: unknown) => ({ name: 'Out', displayName: 'Out', type: 'String', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [], typeFrom })

    expect(() => readBlockType(block({ outputs: [port({ relation: 'same', portName: 'In', branchName: null }) as never] }), portTypes))
      .toThrow(/Port 'Out' declares a type relation, which this editor's block-catalogue format does not define/)
    expect(readBlockType(block({ outputs: [port(null) as never] }), portTypes).outputs[0]).not.toHaveProperty('typeFrom')
  })

  it.each(['inputs', 'outputs', 'subCanvasInputs', 'subCanvasOutputs'] as const)('refuses dotted names in %s', (side) => {
    const port: BlockPortResponse = { name: 'result.value', displayName: 'Result', type: 'String', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }
    const response = block(side === 'inputs' || side === 'outputs'
      ? { [side]: [port] }
      : { subCanvasNames: ['Body'], [side]: { Body: [port] } })

    expect(() => readBlockType(response, portTypes)).toThrow(/result\.value.*dotted-name/)
  })
})

describe('createBlockCatalogueSource', () => {
  it('is empty on the first render and holds the catalogue once it arrives', async () => {
    const source = createBlockCatalogueSource(clientAnswering([block()]))

    const { result } = renderHook(() => source.useBlockTypes(), { wrapper })
    expect(result.current).toEqual([])

    await waitFor(() => { expect(result.current).toHaveLength(1) })
    expect(result.current[0]?.blockTypeId).toBe('Delay')
  })

  it('fetches once for every consumer', async () => {
    const calls = { count: 0 }
    const source = createBlockCatalogueSource(clientAnswering([block()], calls))

    const first = renderHook(() => source.useBlockTypes(), { wrapper })
    const second = renderHook(() => source.useBlockTypes(), { wrapper })
    await waitFor(() => { expect(first.result.current).toHaveLength(1) })
    await waitFor(() => { expect(second.result.current).toHaveLength(1) })

    expect(calls.count).toBe(1)
  })

  it('returns the same array while nothing changes, so a store setter is not written per render', async () => {
    const source = createBlockCatalogueSource(clientAnswering([block()]))

    const { result, rerender } = renderHook(() => source.useBlockTypes(), { wrapper })
    await waitFor(() => { expect(result.current).toHaveLength(1) })
    const first = result.current
    rerender()

    expect(result.current).toBe(first)
  })

  it('loads again after refresh', async () => {
    const calls = { count: 0 }
    const source = createBlockCatalogueSource(clientAnswering([block()], calls))

    const { result } = renderHook(() => source.useBlockTypes(), { wrapper })
    await waitFor(() => { expect(result.current).toHaveLength(1) })
    source.refresh()
    await waitFor(() => { expect(calls.count).toBe(2) })

    expect(result.current).toHaveLength(1)
  })

  it('leaves out a block type it cannot read, reports it once, and loads the rest of the palette', async () => {
    const onError = vi.fn()
    const newer = block({
      blockTypeId: 'Newer',
      outputs: [{ name: 'Out', displayName: 'Out', type: 'String', isRequired: false, allowedValues: null,
        typeFrom: { relation: 'firstOf', portName: 'In', branchName: null } } as never],
    })
    const source = createBlockCatalogueSource(clientAnswering([block(), newer]), { onError })

    const { result, rerender } = renderHook(() => source.useBlockTypes(), { wrapper })
    const second = renderHook(() => source.useBlockTypes(), { wrapper })
    await waitFor(() => { expect(result.current.map((b) => b.blockTypeId)).toEqual(['Delay']) })
    await waitFor(() => { expect(second.result.current).toHaveLength(1) })
    rerender()

    expect(onError).toHaveBeenCalledTimes(1)
    const reported: unknown = onError.mock.calls[0]?.[0]
    expect(reported).toBeInstanceOf(Error)
    expect((reported as Error).message).toMatch(/Block type 'Newer' is left out of the palette: Port 'Out' declares a type relation/)
  })

  it('reports every block type a load leaves out, once per load, under StrictMode and a store written from an effect', async () => {
    const onError = vi.fn()
    const relating = (id: string, relation: string | null) => block({
      blockTypeId: id,
      outputs: [{ name: 'Out', displayName: 'Out', type: 'String', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [],
        typeFrom: relation === null ? null : { relation, portName: 'In', branchName: null } } as never],
    })
    let answer = [block({ blockTypeId: 'A' }), relating('B', 'firstOf'), relating('C', 'lastOf')]
    const client = { getBlockTypes: async () => answer } as unknown as WorkflowClient
    const source = createBlockCatalogueSource(client, { onError })
    const store = createWorkflowEditorStore({ portTypes })
    function Editor() {
      const types = source.useBlockTypes()
      useEffect(() => { store.getState().setBlockTypes([...types]) }, [types])
      return null
    }
    render(createElement(StrictMode, null,
      createElement(PortTypeRegistryContext.Provider, { value: portTypes }, createElement(Editor), createElement(Editor))))

    await waitFor(() => { expect(store.getState().blockTypes.map((b) => b.blockTypeId)).toEqual(['A']) })
    expect(onError.mock.calls.map((c) => (c[0] as Error).message.match(/'(\w+)'/)?.[1])).toEqual(['B', 'C'])

    answer = [block({ blockTypeId: 'A' }), relating('B', null), relating('C', 'lastOf')]
    act(() => { source.refresh() })
    await waitFor(() => { expect(store.getState().blockTypes.map((b) => b.blockTypeId)).toEqual(['A', 'B']) })
    expect(onError).toHaveBeenCalledTimes(3)
    expect((onError.mock.calls[2]?.[0] as Error).message).toMatch(/Block type 'C'/)
  })

  it('reports a failure and leaves the catalogue empty rather than throwing into render', async () => {
    const onError = vi.fn()
    const client = { getBlockTypes: async () => { throw new Error('502') } } as unknown as WorkflowClient
    const source = createBlockCatalogueSource(client, { onError })

    const { result } = renderHook(() => source.useBlockTypes(), { wrapper })
    await waitFor(() => { expect(onError).toHaveBeenCalledWith(expect.any(Error)) })

    expect(result.current).toEqual([])
  })

  it('leaves out a dotted descriptor, reports it once, and registers the rest of the palette', async () => {
    const onError = vi.fn()
    const dotted = block({ blockTypeId: 'Dotted', inputs: [{ name: 'input.value', displayName: 'Input', type: 'String', isRequired: false, allowedValues: null, secretValues: false, scopeVariables: [] }] })
    const source = createBlockCatalogueSource(clientAnswering([block(), dotted]), { onError })
    const store = createWorkflowEditorStore({ portTypes })
    function Editor() {
      const types = source.useBlockTypes()
      useEffect(() => { store.getState().setBlockTypes([...types]) }, [types])
      return null
    }
    render(createElement(StrictMode, null,
      createElement(PortTypeRegistryContext.Provider, { value: portTypes }, createElement(Editor), createElement(Editor))))

    await waitFor(() => { expect(store.getState().blockTypes.map((b) => b.blockTypeId)).toEqual(['Delay']) })
    expect(onError).toHaveBeenCalledTimes(1)
    expect((onError.mock.calls[0]?.[0] as Error).message).toMatch(/Block type 'Dotted'.*input\.value.*dotted-name/)
  })
})
