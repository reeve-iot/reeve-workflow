import { createElement } from 'react'
import type { ReactNode } from 'react'
import { renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { WorkflowClient } from '@reeve-iot/workflow-client'
import { BUILTIN_PORT_TYPES, PortTypeRegistryContext, createPortTypeRegistry } from '@reeve-iot/workflow-core'
import { WorkflowReferenceUnreadableError, createWorkflowReferenceSource, readDeclaredPorts } from './workflowReference.js'

const portTypes = createPortTypeRegistry([...BUILTIN_PORT_TYPES, { name: 'Device' }])

const wrapper = ({ children }: { children: ReactNode }) =>
  createElement(PortTypeRegistryContext.Provider, { value: portTypes }, children)

function clientAnswering(
  workflows: Readonly<Record<string, { inputParametersJson: string | null; outputParametersJson: string | null }>>,
  calls?: { count: number },
): WorkflowClient {
  return {
    getWorkflow: async ({ id }: { id: string }) => {
      if (calls) calls.count += 1
      const workflow = workflows[id]
      if (workflow === undefined) throw new Error(`404 ${id}`)
      return workflow
    },
  } as unknown as WorkflowClient
}

const ports = (json: string | null) => {
  const read = readDeclaredPorts(json, portTypes)
  if (!read.ok) throw new Error(`refused: ${read.fault.detail}`)
  return read.ports
}

describe('readDeclaredPorts', () => {
  it('reads name, display name, requiredness and type', () => {
    expect(ports(JSON.stringify([{ name: 'Room', displayName: 'Which room', isRequired: true, type: 'device' }])))
      .toEqual([{ name: 'Room', displayName: 'Which room', isRequired: true, type: 'Device' }])
  })

  it('falls back to the name when no display name was authored', () => {
    expect(ports(JSON.stringify([{ name: 'Room', displayName: '' }]))[0]?.displayName).toBe('Room')
  })

  it('reads an untyped parameter as blank rather than inventing the wildcard', () => {
    expect(ports(JSON.stringify([{ name: 'X' }, { name: 'Y', type: null }])).map((p) => p.type)).toEqual(['', ''])
  })

  it('keeps a malformed type as written rather than repairing it', () => {
    expect(ports(JSON.stringify([{ name: 'X', type: 'CollectionRoom' }]))[0]?.type).toBe('CollectionRoom')
  })

  it('reads an absent list and [] as no parameters', () => {
    expect(ports(null)).toEqual([])
    expect(ports('[]')).toEqual([])
  })

  it.each([
    ['', 'a parameter list is blank'],
    [' \t\r\n', 'a parameter list is blank'],
    ['null', 'a parameter list is the JSON null'],
  ])('refuses the list %j as %s', (json, detail) => {
    expect(readDeclaredPorts(json, portTypes)).toMatchObject({ ok: false, fault: { rule: 'L7', location: '', detail: expect.stringContaining(detail) as unknown } })
  })

  it.each([
    ['{not json', 'L1', ''],
    [JSON.stringify({ name: 'X' }), 'L7', ''],
    [JSON.stringify([{ name: '' }, { name: 'Ok' }]), 'L7', '/0/name'],
    [JSON.stringify([{ name: 42 }]), 'L7', '/0/name'],
    [JSON.stringify([null, { name: 'Ok' }]), 'L7', '/0'],
    [JSON.stringify([{ name: 'A', isRequired: 'yes' }]), 'L7', '/0/isRequired'],
    [JSON.stringify([{ name: 'A', type: 5 }]), 'L7', '/0/type'],
    [JSON.stringify([{ Name: 'A' }]), 'L5', '/0/Name'],
    ['[{"name":"A","name":"B"}]', 'L3', '/0/name'],
  ])('refuses %s whole, under %s at %j', (json, rule, location) => {
    expect(readDeclaredPorts(json, portTypes)).toMatchObject({ ok: false, fault: { rule, location } })
  })
})

describe('createWorkflowReferenceSource', () => {
  it('answers no ports for a node that references nothing, without asking the server', () => {
    const calls = { count: 0 }
    const { resolver: useReference } = createWorkflowReferenceSource(clientAnswering({}, calls))

    const { result } = renderHook(() => useReference(null), { wrapper })

    expect(result.current).toEqual({ inputs: [], outputs: [] })
    expect(calls.count).toBe(0)
  })

  it('resolves both parameter fields of the referenced workflow', async () => {
    const { resolver: useReference } = createWorkflowReferenceSource(
      clientAnswering({
        child: {
          inputParametersJson: JSON.stringify([{ name: 'In', type: 'String' }]),
          outputParametersJson: JSON.stringify([{ name: 'Out', type: 'Boolean' }]),
        },
      }),
    )

    const { result } = renderHook(() => useReference('child'), { wrapper })
    await waitFor(() => { expect(result.current.inputs).toHaveLength(1) })

    expect(result.current.inputs[0]?.name).toBe('In')
    expect(result.current.outputs[0]?.name).toBe('Out')
  })

  it('fetches one workflow once, however many nodes reference it', async () => {
    const calls = { count: 0 }
    const { resolver: useReference } = createWorkflowReferenceSource(
      clientAnswering({ child: { inputParametersJson: '[]', outputParametersJson: '[]' } }, calls),
    )

    const first = renderHook(() => useReference('child'), { wrapper })
    const second = renderHook(() => useReference('child'), { wrapper })
    await waitFor(() => { expect(calls.count).toBe(1) })
    first.rerender()
    second.rerender()

    expect(calls.count).toBe(1)
  })

  it('draws no ports for an unreadable workflow and does not ask again', async () => {
    const calls = { count: 0 }
    const onError = vi.fn()
    const { resolver: useReference } = createWorkflowReferenceSource(clientAnswering({}, calls), { onError })

    const { result, rerender } = renderHook(() => useReference('gone'), { wrapper })
    await waitFor(() => { expect(onError).toHaveBeenCalledWith('gone', expect.any(Error)) })
    rerender()

    expect(result.current).toEqual({ inputs: [], outputs: [] })
    expect(calls.count).toBe(1)
  })

  it('draws no ports for a child whose parameter list cannot be read, and says why once', async () => {
    const calls = { count: 0 }
    const onError = vi.fn()
    const { resolver: useReference } = createWorkflowReferenceSource(
      clientAnswering({ child: { inputParametersJson: '[{"name":"Ok"},{"name":7}]', outputParametersJson: '[]' } }, calls),
      { onError },
    )

    const { result, rerender } = renderHook(() => useReference('child'), { wrapper })
    await waitFor(() => { expect(onError).toHaveBeenCalledTimes(1) })
    rerender()

    const error = onError.mock.calls[0]?.[1] as unknown
    expect(error).toBeInstanceOf(WorkflowReferenceUnreadableError)
    expect(error).toMatchObject({ workflowId: 'child', fault: { rule: 'L7', location: '/1/name' } })
    expect(result.current).toEqual({ inputs: [], outputs: [] })
    expect(calls.count).toBe(1)
  })

  it('draws no ports for a child whose output list cannot be read', async () => {
    const onError = vi.fn()
    const { resolver: useReference } = createWorkflowReferenceSource(
      clientAnswering({ child: { inputParametersJson: '[{"name":"In","type":"Int"}]', outputParametersJson: '[{"name":7}]' } }),
      { onError },
    )

    const { result } = renderHook(() => useReference('child'), { wrapper })
    await waitFor(() => { expect(onError).toHaveBeenCalledTimes(1) })

    expect(onError.mock.calls[0]?.[1]).toMatchObject({ workflowId: 'child', fault: { rule: 'L7', location: '/0/name' } })
    expect(result.current).toEqual({ inputs: [], outputs: [] })
  })

  it('reads a child again after refresh, which is how a new parameter reaches its parent', async () => {
    let declared = JSON.stringify([{ name: 'Brightness' }])
    const getWorkflow = vi.fn(async () => ({ inputParametersJson: declared, outputParametersJson: '[]' }))
    const { resolver: useReference, refresh } = createWorkflowReferenceSource(
      { getWorkflow } as unknown as WorkflowClient,
    )

    const { result } = renderHook(() => useReference('child'), { wrapper })
    await waitFor(() => { expect(result.current.inputs).toHaveLength(1) })
    declared = JSON.stringify([{ name: 'Brightness' }, { name: 'Warmth' }])
    refresh()

    await waitFor(() => { expect(result.current.inputs.map((p) => p.name)).toEqual(['Brightness', 'Warmth']) })
  })

  it('asks again after refresh for a child that could not be read', async () => {
    let reachable = false
    const getWorkflow = vi.fn(async () => {
      if (!reachable) throw new Error('502')
      return { inputParametersJson: JSON.stringify([{ name: 'In' }]), outputParametersJson: '[]' }
    })
    const { resolver: useReference, refresh } = createWorkflowReferenceSource(
      { getWorkflow } as unknown as WorkflowClient,
    )

    const { result } = renderHook(() => useReference('child'), { wrapper })
    await waitFor(() => { expect(getWorkflow).toHaveBeenCalledTimes(1) })
    reachable = true
    refresh()

    await waitFor(() => { expect(result.current.inputs).toHaveLength(1) })
  })

  it('returns the same ports across renders, so they are safe in a dependency array', async () => {
    const { resolver: useReference } = createWorkflowReferenceSource(
      clientAnswering({ child: { inputParametersJson: JSON.stringify([{ name: 'In' }]), outputParametersJson: '[]' } }),
    )

    const { result, rerender } = renderHook(() => useReference('child'), { wrapper })
    await waitFor(() => { expect(result.current.inputs).toHaveLength(1) })
    const first = result.current
    rerender()

    expect(result.current).toBe(first)
  })
})
